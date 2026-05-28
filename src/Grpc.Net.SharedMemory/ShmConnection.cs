#region Copyright notice and license

// Copyright 2025 The gRPC Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

#endregion

using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Represents a gRPC connection over shared memory between a client and server.
/// Manages the underlying shared memory segment and provides stream multiplexing.
/// </summary>
public sealed class ShmConnection : IDisposable, IAsyncDisposable
{
    private readonly Segment _segment;
    private readonly bool _isClient;
    private readonly ConcurrentDictionary<uint, ShmGrpcStream> _streams;
    private readonly CancellationTokenSource _disposeCts;
    private readonly Task _frameReaderTask;
    private readonly Channel<ShmGrpcStream> _incomingStreamsChannel;
    private uint _nextStreamId;
    private int _disposed;
    private int _goAwaySent;
    private volatile bool _goAwayReceived;
    private volatile bool _draining;
    private uint _maxConcurrentStreams;

    // Atomic counter for client-side max-stream enforcement.
    // Incremented in CreateStream BEFORE adding to _streams, decremented in
    // RemoveStream AFTER removing. This eliminates the TOCTOU race where
    // N threads pass a _streams.Count check simultaneously and all succeed
    // in creating streams, exceeding the server's maxConcurrentStreams limit.
    private int _clientStreamCount;

    // Atomic counter for server-side max-stream enforcement AND for the
    // hot-path ActiveStreamCount probe in ShmGrpcServer / ShmGrpcStream
    // SingleStreamMode fast-path checks. Avoids ConcurrentDictionary.Count
    // which acquires every bucket lock — at N=1000 streams that was 65%+
    // CPU on Monitor.Enter_Slowpath in the profile (see fair-conc 1000/64B).
    // Incremented after _streams.TryAdd succeeds, decremented after
    // _streams.TryRemove succeeds. Both ops in HandleHeadersFrame /
    // DeliverStreamAsync / RemoveStream keep this in sync with _streams.
    private int _serverStreamCount;

    // HTTP/2 stream-level flow control: ACTIVE on this branch (see
    // InFlow / TrInFlow / EmitWindowUpdate). Both endpoints emit and
    // consume WINDOW_UPDATE per gRFC SHM v3.4+; per-stream send quota
    // is enforced by ShmGrpcStream.ReserveSendQuotaOrBlock.
    //
    // Connection-level FC is still elided (per-stream is sufficient
    // for SHM since the ring itself provides connection-level
    // back-pressure via WaitForSpace).
    //
    // The opt-in SHM_FAIR_MAX_FRAME env var further constrains
    // wire-format parity for benchmarking against TCP/UDS gRPC
    // (smaller per-frame DATA cap forcing multi-frame splitting).
    // SHM_INITIAL_WINDOW sets the per-stream FC advertisement
    // (default 32 MiB; set to 65535 to match the HTTP/2 spec).
    // Defaults leave the SHM transport with its native 32 MiB
    // initial window and ringCapacity/3 per-frame payload cap.
    // Write lock: the SPSC ring buffer requires single-producer semantics.
    // All writes to TxRing are serialised through the ShmFrameWriter's
    // dedicated consumer thread (Channel SingleReader=true).
    private ShmFrameWriter? _frameWriter;

    /// <summary>Gets the frame writer (for singleStreamMode direct write).</summary>
    internal ShmFrameWriter? FrameWriter => _frameWriter;

    // Receive-side fan-out dispatcher. Reader thread enqueues parsed
    // frames here; per-stripe Threads call ShmGrpcStream.OnFrameReceived
    // inline. This is the win path for the 1000-stream tiny-payload
    // cell where Channel<T>.SetResult + ThreadPool.UnsafeQueueUserWorkItem
    // dispatch costs ~0.5-1 us/frame on Linux and ~17 us/frame on
    // Windows. With 4 stripes a single stripe Thread serves
    // ~250 streams in the 1000-stream bench cell; HOL within a stripe
    // is bounded to that 250-stream subset.
    //
    // Disabled by setting SHM_RECEIVE_STRIPER=0 (default ON). When
    // null the legacy path runs: reader Thread calls
    // stream.OnFrameReceived directly, one TryWrite per frame, paying
    // the per-channel SetResult dispatch tax.
    private static readonly bool s_useReceiveStriper =
        !string.Equals(Environment.GetEnvironmentVariable("SHM_RECEIVE_STRIPER"),
            "0", StringComparison.Ordinal);
    private readonly Synchronization.ReceiveStriper? _receiveStriper;

    /// <summary>
    /// True when the receive-side striper is wired in. Read by
    /// ShmGrpcStream's ctor to decide whether the per-stream inbound
    /// channel can safely set <c>AllowSynchronousContinuations=true</c>
    /// (the stripe Thread is the only writer of that channel and is a
    /// dedicated dispatch point, so the user-code continuation can
    /// inline-run on the stripe without HOL-blocking other stripes).
    /// </summary>
    internal bool UseReceiveStriper => _receiveStriper != null;

    /// <summary>
    /// When true, ReadFramePayload returns ring memory directly (zero-copy)
    /// instead of copying to a pooled buffer. CommitRead is deferred until
    /// the consumer calls FramePayload.Release().
    /// </summary>
    internal bool ZeroCopyRead { get; set; }

    /// <summary>
    /// When true, this connection was negotiated for single-stream mode.
    /// Server handlers use ExecuteInline for atomic ring writes; the data
    /// rings raise their chain-ZC budget to <c>cap - SmallReserve</c>
    /// (see <see cref="ShmRing.ChainZcBudget"/>) so a single in-flight
    /// message can occupy almost the whole ring under ping-pong.
    /// </summary>
    internal bool SingleStreamMode
    {
        get => _singleStreamMode;
        set
        {
            _singleStreamMode = value;
            // Propagate to the data rings so the codec's chain-ZC
            // budget reflects the negotiated mode without taking a
            // dependency on ShmConnection at the codec layer.
            TxRing.SingleStreamMode = value;
            RxRing.SingleStreamMode = value;
        }
    }
    private bool _singleStreamMode;

    /// <summary>
    /// When true, the inbound <see cref="System.Threading.Channels.Channel{T}"/>
    /// of every <see cref="ShmGrpcStream"/> created on this connection
    /// is constructed with <c>AllowSynchronousContinuations=true</c>,
    /// causing the reader thread to invoke awaiting consumer
    /// continuations inline instead of dispatching them through the
    /// ThreadPool. Eliminates ~17 µs of per-receive ThreadPool dispatch
    /// latency on Windows (about 41 % of the streaming-0B round-trip).
    /// <para>
    /// SAFE ONLY when the application guarantees that (1) the
    /// connection carries at most one active stream at a time and
    /// (2) the receive-side continuations never perform a synchronous
    /// wait (e.g. <c>.Result</c> / <c>.Wait()</c> / blocking on another
    /// completion) that depends on the reader thread making further
    /// progress. Violating either property head-of-line-blocks the
    /// reader and can deadlock the connection.
    /// </para>
    /// Default <c>false</c>; opt-in via
    /// <c>ShmGrpcServer(inlineReceiveContinuations: true)</c> or
    /// <c>ShmClientTransportOptions.InlineReceiveContinuations = true</c>.
    /// Channel options are immutable after stream construction, so
    /// flipping this flag only affects streams created afterwards.
    /// </summary>
    internal bool InlineReceiveContinuations { get; set; }


    // Keepalive (A73 RFC)
    private readonly ShmKeepaliveOptions _keepaliveOptions;
    private readonly ShmKeepaliveEnforcementPolicy? _enforcementPolicy;
    private Task? _keepaliveTask;
    private DateTime _lastPingAt;
    private DateTime _lastPingSentAt;
    private volatile bool _pendingPing;
    private int _pingStrikes; // accessed via Interlocked from multiple threads

    /// <summary>
    /// Gets the connection name (shared memory segment name).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the authentication info produced by the security handshake,
    /// or <c>null</c> if no handshaker was configured (insecure local
    /// SHM). When non-null the <see cref="ShmAuthInfo.RemoteIdentity"/>
    /// carries the peer's verified identity token, as exchanged during
    /// the data-segment HandshakeInit/Resp/Ack sequence. Mirrors
    /// grpc-go-shmem's <c>ShmAuthInfo</c> surfaced via
    /// <c>credentials/shm</c>.
    /// </summary>
    public ShmAuthInfo? AuthInfo { get; internal set; }

    /// <summary>
    /// Gets whether this is a client-side connection.
    /// </summary>
    public bool IsClient => _isClient;

    /// <summary>
    /// Gets whether the connection has been closed.
    /// </summary>
    public bool IsClosed => Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _goAwaySent) != 0 || _goAwayReceived;

    /// <summary>
    /// Gets whether the connection is draining (not accepting new streams).
    /// </summary>
    public bool IsDraining => _draining;

    /// <summary>
    /// Raised when a GoAway frame is received from the remote side.
    /// </summary>
    public event EventHandler<GoAwayEventArgs>? GoAwayReceived;

    /// <summary>
    /// Raised when a new stream is received from a client (server-side only).
    /// </summary>
    public event EventHandler<StreamReceivedEventArgs>? StreamReceived;

    /// <summary>
    /// Raised when a stream is removed from this connection.
    /// </summary>
    public event Action<uint>? StreamRemoved;

    /// <summary>
    /// Gets the number of active streams on this connection.
    /// </summary>
    /// <remarks>
    /// Both client and server use atomic counters (NOT
    /// <c>_streams.Count</c>): <c>ConcurrentDictionary.Count</c> acquires
    /// every bucket lock and at high stream concurrency (N=1000) burned
    /// 60%+ of server CPU in <c>Monitor.Enter_Slowpath</c> per the
    /// fair-conc 1000/64B profile. The counters are kept in sync with
    /// <c>_streams</c> at every <c>TryAdd</c> / <c>TryRemove</c> site.
    /// </remarks>
    public int ActiveStreamCount => _isClient
        ? Volatile.Read(ref _clientStreamCount)
        : Volatile.Read(ref _serverStreamCount);

    /// <summary>
    /// Gets the maximum number of concurrent streams allowed.
    /// </summary>
    public uint MaxConcurrentStreams => _maxConcurrentStreams;

    /// <summary>
    /// Gets the number of additional streams that can be created.
    /// </summary>
    public int AvailableStreams => (int)_maxConcurrentStreams - ActiveStreamCount;

    /// <summary>
    /// Creates a new client-side connection by opening an existing shared memory segment.
    /// </summary>
    /// <param name="name">The name of the shared memory segment to connect to.</param>
    /// <param name="keepaliveOptions">Optional keepalive options.</param>
    /// <returns>A new client connection.</returns>
    public static ShmConnection ConnectAsClient(string name, ShmKeepaliveOptions? keepaliveOptions = null)
    {
        var segment = Segment.Open(name);
        try
        {
            return new ShmConnection(name, segment, isClient: true, keepaliveOptions);
        }
        catch
        {
            segment.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a new server-side connection by creating a shared memory segment.
    /// </summary>
    /// <param name="name">The name for the shared memory segment.</param>
    /// <param name="ringCapacity">The capacity of each ring buffer (default: 64MB).</param>
    /// <param name="maxStreams">Maximum concurrent streams (default: 100).</param>
    /// <param name="keepaliveOptions">Optional keepalive options.</param>
    /// <param name="enforcementPolicy">Optional enforcement policy for server.</param>
    /// <returns>A new server connection.</returns>
    /// <remarks>
    /// This is a low-level direct API intended for in-process tests and
    /// embedded scenarios. It does NOT perform the control-segment
    /// handshake (<c>WaitForClient</c> + <c>FinalizeDataSegWaker</c>)
    /// that <see cref="ShmControlListener"/> uses on production paths.
    /// If you cross processes with <c>SHM_EVENTFD_WAKE=1</c>, prefer the
    /// listener API — otherwise the eventfd negotiation step is bypassed
    /// and a peer with a different wake primitive may deadlock.
    /// </remarks>
    public static ShmConnection CreateAsServer(
        string name,
        ulong ringCapacity = 64 * 1024 * 1024,
        uint maxStreams = 100,
        ShmKeepaliveOptions? keepaliveOptions = null,
        ShmKeepaliveEnforcementPolicy? enforcementPolicy = null)
    {
        var segment = Segment.Create(name, ringCapacity, maxStreams);
        try
        {
            return new ShmConnection(name, segment, isClient: false, keepaliveOptions, enforcementPolicy);
        }
        catch
        {
            segment.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a server-side connection from an existing segment (used by ShmControlListener).
    /// </summary>
    internal ShmConnection(string name, Segment segment)
        : this(name, segment, isClient: false)
    {
    }

    /// <summary>
    /// Creates a client-side connection from an existing segment (used by ShmControlDialer).
    /// </summary>
    internal static ShmConnection FromClientSegment(string name, Segment segment, ShmKeepaliveOptions? keepaliveOptions = null)
    {
        return new ShmConnection(name, segment, isClient: true, keepaliveOptions);
    }

    private ShmConnection(
        string name,
        Segment segment,
        bool isClient,
        ShmKeepaliveOptions? keepaliveOptions = null,
        ShmKeepaliveEnforcementPolicy? enforcementPolicy = null)
    {
        Name = name;
        _segment = segment;
        _isClient = isClient;
        _keepaliveOptions = keepaliveOptions ?? ShmKeepaliveOptions.Default;
        _enforcementPolicy = enforcementPolicy;
        _streams = new ConcurrentDictionary<uint, ShmGrpcStream>();
        _disposeCts = new CancellationTokenSource();

        // Handle MaxStreams: 0 or max uint means unlimited - use reasonable default
        var headerMaxStreams = segment.Header.MaxStreams;
        if (headerMaxStreams == 0 || headerMaxStreams == uint.MaxValue)
        {
            _maxConcurrentStreams = 100;
        }
        else
        {
            _maxConcurrentStreams = headerMaxStreams;
        }

        // Create channel for incoming streams (server-side)
        // Use 2x maxConcurrentStreams capacity to absorb transient bursts:
        // RemoveStream decrements _streams.Count (allowing a new stream to pass
        // the count check) before the old stream is consumed from the channel.
        var channelCapacity = Math.Min((int)_maxConcurrentStreams * 2, 10000);
        _incomingStreamsChannel = Channel.CreateBounded<ShmGrpcStream>(new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true
        });

        // Client uses odd stream IDs (1, 3, 5, ...), server uses even (2, 4, 6, ...)
        _nextStreamId = isClient ? 1u : 2u;

        // Initialize the frame writer BEFORE the reader. The reader thread
        // can process inbound frames immediately (e.g., Ping) which trigger
        // SendFrame → _frameWriter.Enqueue. If the writer isn't initialized
        // yet, that's a NullReferenceException.
        //
        // StreamMap routing: passed into the writer at construction so a
        // future flow-control hook can look up per-stream state without
        // a global table. Currently unused at runtime (no fair-window
        // enforcement — see ShmGrpcStream.FairAwaitWindow no-op).
        _frameWriter = new ShmFrameWriter(TxRing, _disposeCts, _streams);

        // Initialize the receive striper BEFORE the reader so the
        // reader's ProcessFrame can route frames through it on the
        // very first dispatch. ALSO before any ShmGrpcStream is
        // constructed via CreateStream / accepted from incoming, so the
        // per-stream inbound-channel ctor sees the correct
        // UseReceiveStriper value when picking AllowSynchronousContinuations.
        // Server-side CreateStream is gated by the reader's HEADERS
        // arrival, which can only happen after _frameReaderTask starts
        // a few lines below; client-side CreateStream is user-driven
        // after the connection object is returned from CreateAsServer /
        // ConnectAsClient — also after this ctor completes.
        if (s_useReceiveStriper)
        {
            _receiveStriper = new Synchronization.ReceiveStriper(_streams);
        }

        // gRFC SHM v3.4+ HTTP/2-compatible flow control: wire the
        // per-DATA-frame hook (conn-level drip via TrInFlow.OnData +
        // per-stream over-window enforcement via InFlow.OnData) and the
        // LPM-header parse-time hook (stream-level pre-credit via
        // InFlow.MaybeAdjustAdditive — MUST per gRFC because the SHM
        // codec aggregates DATA frames into a complete LPM before
        // delivering to the application, so the app-Read-driven
        // maybeAdjust path from stock HTTP/2 is unreachable). Both
        // hooks fire on the single frame-reader thread; wiring them
        // BEFORE _frameReaderTask starts gives happens-before via the
        // task launch.
        Wire.Http2Codec.SetOnDataFrame(RxRing, OnDataFrame);
        Wire.Http2Codec.SetOnMessageStart(RxRing, OnMessageStart);

        // Start background frame reader
        _frameReaderTask = FrameReaderLoopAsync();

        // Start keepalive task if enabled
        if (_keepaliveOptions.IsEnabled)
        {
            _keepaliveTask = Task.Run(KeepaliveLoopAsync);
        }
    }

    /// <summary>
    /// Creates a new stream for a gRPC call (client-side).
    /// </summary>
    /// <returns>A new gRPC stream.</returns>
    /// <exception cref="ShmStreamCapacityExceededException">
    /// Thrown when the connection has reached <see cref="MaxConcurrentStreams"/>.
    /// The caller should retry on a different connection via the pool.
    /// </exception>
    public ShmGrpcStream CreateStream()
    {
        ThrowIfDisposed();
        ThrowIfGoAway();

        // Atomically reserve a stream slot BEFORE creating the stream object.
        // Increment-then-check eliminates the TOCTOU race where N threads all
        // read _streams.Count < max, then all TryAdd successfully, exceeding
        // the server's limit and causing REJECTs that hang streaming calls.
        var reserved = Interlocked.Increment(ref _clientStreamCount);
        if (reserved > (int)_maxConcurrentStreams)
        {
            Interlocked.Decrement(ref _clientStreamCount);
            throw new ShmStreamCapacityExceededException(
                $"Connection '{Name}' has reached max concurrent streams ({_maxConcurrentStreams})");
        }

        var streamId = Interlocked.Add(ref _nextStreamId, 2) - 2;
        var stream = new ShmGrpcStream(streamId, this, isServerStream: false);

        if (!_streams.TryAdd(streamId, stream))
        {
            Interlocked.Decrement(ref _clientStreamCount);
            stream.Dispose();
            throw new InvalidOperationException($"Stream ID {streamId} already exists");
        }

        return stream;
    }

    /// <summary>
    /// Accepts incoming streams from clients (server-side).
    /// Blocks until a stream is available or cancellation is requested.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of incoming gRPC streams.</returns>
    public async IAsyncEnumerable<ShmGrpcStream> AcceptStreamsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_isClient)
        {
            throw new InvalidOperationException("AcceptStreamsAsync is only available on server-side connections");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);

        await foreach (var stream in _incomingStreamsChannel.Reader.ReadAllAsync(linkedCts.Token))
        {
            yield return stream;
        }
    }

    /// <summary>
    /// Waits for and accepts a single incoming stream from a client (server-side).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The accepted stream, or null if the connection was closed.</returns>
    public async ValueTask<ShmGrpcStream?> AcceptStreamAsync(CancellationToken cancellationToken = default)
    {
        if (_isClient)
        {
            throw new InvalidOperationException("AcceptStreamAsync is only available on server-side connections");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);

        try
        {
            return await _incomingStreamsChannel.Reader.ReadAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the ring buffer for sending data (client→server for client, server→client for server).
    /// </summary>
    internal ShmRing TxRing => _isClient ? _segment.RingA : _segment.RingB;
    /// <summary>
    /// Gets the ring buffer for receiving data (server→client for client, client→server for server).
    /// </summary>
    internal ShmRing RxRing => _isClient ? _segment.RingB : _segment.RingA;

    /// <summary>
    /// Sends a GoAway frame to initiate graceful shutdown.
    /// </summary>
    /// <param name="message">Optional debug message.</param>
    public void SendGoAway(string? message = null)
    {
        // Atomic check-and-set: only one thread sends the GoAway frame.
        if (Interlocked.CompareExchange(ref _goAwaySent, 1, 0) != 0) return;

        try
        {
            var payload = message != null ? System.Text.Encoding.UTF8.GetBytes(message) : Array.Empty<byte>();
            SendFrame(FrameType.GoAway, 0, GoAwayFlags.Draining, payload);
        }
        catch (ObjectDisposedException)
        {
            // Connection already closing
        }
        catch (InvalidOperationException)
        {
            // Frame writer already disposed
        }
    }

    /// <summary>
    /// Sends a Ping frame.
    /// </summary>
    public void SendPing()
    {
        ThrowIfDisposed();
        var pingData = BitConverter.GetBytes(Environment.TickCount64);
        SendFrame(FrameType.Ping, 0, 0, pingData);
    }

    internal void RemoveStream(uint streamId)
    {
        if (_streams.TryRemove(streamId, out _))
        {
            if (_isClient)
            {
                Interlocked.Decrement(ref _clientStreamCount);
            }
            else
            {
                Interlocked.Decrement(ref _serverStreamCount);
            }

            StreamRemoved?.Invoke(streamId);
        }
    }

    internal void SendFrame(FrameType type, uint streamId, byte flags, ReadOnlySpan<byte> payload)
    {
        ThrowIfDisposed();
        _frameWriter!.Enqueue(type, streamId, flags, payload);
    }

    /// <summary>
    /// Enqueues a frame without copying the payload. <paramref name="pooledBuffer"/>
    /// is returned to <see cref="ArrayPool{T}"/> after the ring write completes.
    /// </summary>
    internal void SendFrameZeroCopy(FrameType type, uint streamId, byte flags,
        ReadOnlyMemory<byte> payload, byte[]? pooledBuffer)
    {
        ThrowIfDisposed();
        _frameWriter!.EnqueueZeroCopy(type, streamId, flags, payload, pooledBuffer);
    }

    /// <summary>
    /// Zero-copy enqueue + wait for ring write completion. Safe for callers
    /// that reuse the payload buffer after return (streaming RPCs).
    /// </summary>
    internal void SendFrameZeroCopyAndWait(FrameType type, uint streamId, byte flags,
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        _frameWriter!.EnqueueZeroCopyAndWait(type, streamId, flags, payload, cancellationToken);
    }

    // Diagnostic counter: incremented inside the H2 codec's
    // WriteH2WindowUpdate (the single structural emission point) so
    // tests can verify exactly when WUs are emitted. With the HTTP/2
    // FC stack active on this branch, WUs ARE emitted on the drip path
    // (per-stream send quota tracking + EmitWindowUpdate at limit/4).
    // The legacy test name 'NoWindowUpdate_EmittedInDefaultMode...' is
    // a historical artefact from the pre-FC baseline; current invariant
    // is just that the counter is observable for diagnostics.
    private static long s_wuFramesEmitted;
    internal static long WindowUpdateFramesEmittedForTest()
        => Volatile.Read(ref s_wuFramesEmitted);
    internal static void RecordWindowUpdateEmission()
        => Interlocked.Increment(ref s_wuFramesEmitted);

    private Task FrameReaderLoopAsync()
    {
        // Run the blocking ReadFramePayload loop on a dedicated thread.
        // Previous implementation used Task.Factory.StartNew(async () => {...}, LongRunning)
        // which loses the dedicated thread after the first await in the async lambda,
        // causing the continuation to run on the ThreadPool. Under high concurrency
        // (e.g., 256 sessions), ThreadPool starvation can delay the frame reader
        // indefinitely, causing hangs: the remote ring fills up, the remote writer
        // blocks, and no progress is made.
        //
        // Using a real Thread ensures the frame reader never competes with application
        // tasks for ThreadPool threads.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                while (!_disposeCts.Token.IsCancellationRequested)
                {
                    var (header, payload) = FrameProtocol.ReadFramePayload(
                        RxRing, _disposeCts.Token, zeroCopy: ZeroCopyRead);
                    try
                    {
                        ProcessFrame(header, payload);
                    }
                    catch (Exception ex)
                    {
                        // ProcessFrame exceptions (e.g., from event handlers or
                        // HandleHeadersFrame) must not kill the reader loop.
                        // Log and continue — the individual stream may be
                        // broken but other streams and control frames (Ping,
                        // GoAway) must keep flowing.
                        System.Diagnostics.Debug.WriteLine(
                            $"ShmConnection.ProcessFrame error: {ex.Message}");
                    }
                }

                tcs.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetResult(); // Normal shutdown
            }
            catch (ObjectDisposedException)
            {
                tcs.TrySetResult(); // Normal shutdown
            }
            catch (RingClosedException)
            {
                tcs.TrySetResult(); // Normal shutdown
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Frame reader error: {ex.Message}");
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = $"ShmFrameReader-{Name}"
        };
        thread.Start();
        return tcs.Task;
    }

    private void ProcessFrame(FrameHeader header, FramePayload payload)
    {
        var payloadLength = payload.Length;
        var payloadMemory = payload.Memory;

        switch (header.Type)
        {
            case FrameType.Headers:
                HandleHeadersFrame(header, payload);
                break;

            case FrameType.Message:
            case FrameType.Trailers:
            case FrameType.HalfClose:
            case FrameType.Cancel:
                // Route to stream — transfer pooled buffer ownership.
                // When the receive striper is enabled (default) the
                // dispatch is fanned out across N stripe Threads so the
                // reader Thread does NOT pay the Channel<InboundFrame>
                // SetResult + ThreadPool.UnsafeQueueUserWorkItem tax for
                // every frame. The legacy direct path remains as the
                // fallback when SHM_RECEIVE_STRIPER=0.
                if (_receiveStriper != null)
                {
                    var frame = new InboundFrame(header.Type, payload, header.Flags);
                    _receiveStriper.Enqueue(header.StreamId, frame);
                }
                else if (_streams.TryGetValue(header.StreamId, out var stream))
                {
                    var frame = new InboundFrame(header.Type, payload, header.Flags);
                    stream.OnFrameReceived(frame);
                }
                else
                {
                    payload.Release();
                }

                // Connection-level back-pressure: none (ring's
                // WaitForSpace is sufficient). Per-stream HTTP/2 FC IS
                // active on this branch via InFlow / TrInFlow, so
                // inbound WUs do affect per-stream send quota — see
                // ProcessFrame's WindowUpdate case.
                break;

            case FrameType.Ping:
                HandlePing(header, payloadMemory.Span);
                payload.Release();
                break;

            case FrameType.Pong:
                HandlePong(header, payloadMemory.Span);
                payload.Release();
                break;

            case FrameType.GoAway:
                _goAwayReceived = true;
                var message = payloadLength > 0
                    ? System.Text.Encoding.UTF8.GetString(payloadMemory.Span.Slice(0, payloadLength))
                    : null;
                payload.Release(); // Release before invoking user callback to prevent leak on throw.
                GoAwayReceived?.Invoke(this, new GoAwayEventArgs(header.Flags, message));
                break;

            case FrameType.WindowUpdate:
                if (payloadLength < 4)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Invalid WindowUpdate frame: payload length {payloadLength} < 4");
                    payload.Release();
                    break;
                }

                var increment = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    payloadMemory.Span.Slice(0, payloadLength));
                AddSendQuota(header.StreamId, increment);
                payload.Release();
                break;

            default:
                payload.Release();
                break;
        }
    }


    /// <summary>
    /// Handles an incoming HEADERS frame.
    /// For server: creates a new stream from client request.
    /// For client: routes to existing stream (response headers).
    /// </summary>
    private void HandleHeadersFrame(FrameHeader header, FramePayload payload)
    {
        var streamId = header.StreamId;

        // Check if stream already exists
        if (_streams.TryGetValue(streamId, out var existingStream))
        {
            // Route to existing stream (e.g., response headers for client)
            var frame = new InboundFrame(header.Type, payload, header.Flags);
            existingStream.OnFrameReceived(frame);
            return;
        }

        // On server: new stream from client
        if (!_isClient)
        {
            // Validate stream ID - clients use odd IDs
            if (streamId % 2 != 1)
            {
                System.Diagnostics.Debug.WriteLine($"Invalid stream ID {streamId} from client (must be odd)");
                RejectStream(streamId, "invalid stream ID");
                payload.Release();
                return;
            }

            // Check if draining
            if (_draining || Volatile.Read(ref _goAwaySent) != 0 || _goAwayReceived)
            {
                RejectStream(streamId, "transport is draining");
                payload.Release();
                return;
            }

            // Check max concurrent streams. Use the atomic counter
            // (mirrors client-side enforcement) instead of
            // _streams.Count to avoid the all-bucket Monitor.Enter that
            // dominated the server-side profile at N=1000.
            if (Volatile.Read(ref _serverStreamCount) >= (int)_maxConcurrentStreams)
            {
                RejectStream(streamId, "max concurrent streams exceeded");
                payload.Release();
                return;
            }

            // Decode headers
            HeadersV1 headersV1;
            try
            {
                headersV1 = HeadersV1.Decode(payload.Memory.Span);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to decode headers: {ex.Message}");
                RejectStream(streamId, "invalid headers");
                payload.Release();
                return;
            }

            // Return payload buffer — headers have been decoded into managed objects.
            payload.Release();

            // Create new server stream
            var newStream = new ShmGrpcStream(streamId, this, isServerStream: true);
            newStream.SetRequestHeaders(headersV1);

            if (!_streams.TryAdd(streamId, newStream))
            {
                newStream.Dispose();
                return;
            }
            Interlocked.Increment(ref _serverStreamCount);

            // Publish to incoming streams channel.
            // Channel capacity is 2x maxConcurrentStreams to absorb the window
            // between RemoveStream (which decrements _streams.Count, allowing
            // new streams past the count check) and the consumer draining the
            // channel. TryWrite should always succeed with this capacity.
            if (!_incomingStreamsChannel.Writer.TryWrite(newStream))
            {
                // Channel transiently full. Fire-and-forget async delivery
                // to avoid blocking the frame-reader thread (which would
                // stall all inbound frame processing — HOL blocking).
                // StreamReceived is raised inside DeliverStreamAsync only
                // after successful delivery, so subscribers never see
                // a stream that is later rejected.
                _ = DeliverStreamAsync(newStream, streamId);
            }
            else
            {
                StreamReceived?.Invoke(this, new StreamReceivedEventArgs(newStream));
            }
        }
        else
        {
            payload.Release();
        }
    }

    /// <summary>
    /// Async fallback for stream delivery when the channel is transiently full.
    /// Runs on the thread pool — keeps the frame-reader thread unblocked.
    /// </summary>
    private async Task DeliverStreamAsync(ShmGrpcStream newStream, uint streamId)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(500));
            await _incomingStreamsChannel.Writer.WriteAsync(newStream, timeoutCts.Token).ConfigureAwait(false);
            StreamReceived?.Invoke(this, new StreamReceivedEventArgs(newStream));
        }
        catch
        {
            // Timed out, disposed, or cancelled — reject the stream.
            if (_streams.TryRemove(streamId, out var orphaned))
            {
                Interlocked.Decrement(ref _serverStreamCount);
                orphaned.Dispose();
            }
            RejectStream(streamId, "server overloaded");
        }
    }

    /// <summary>
    /// Rejects a stream by sending TRAILERS with an error.
    /// </summary>
    private void RejectStream(uint streamId, string message)
    {
        try
        {
            var trailers = new TrailersV1
            {
                Version = 1,
                GrpcStatusCode = Grpc.Core.StatusCode.Unavailable,
                GrpcStatusMessage = message
            };
            // Use EncodeToArray + SendFrame (copy) instead of Encode + SendFrameZeroCopy
            // to avoid buffer leak: if enqueue fails (writer disposed/closed), the catch
            // swallows the exception but SendFrameZeroCopy would leave the pooled buffer
            // unreturned. SendFrame copies the payload, so there's no ownership transfer.
            var trailersPayload = trailers.EncodeToArray();
            SendFrame(FrameType.Trailers, streamId, 0, trailersPayload);
        }
        catch
        {
            // Best effort
        }
    }

    /// <summary>
    /// Initiates graceful shutdown - stops accepting new streams.
    /// </summary>
    public void Drain()
    {
        _draining = true;
        SendGoAway("draining");
    }

    #region Connection-Level Flow Control

    /// <summary>
    /// Conn-level send-window quota. Bytes the peer has granted via the
    /// initial SETTINGS plus accumulated <c>WINDOW_UPDATE(streamId=0)</c>.
    /// Drained by every outbound DATA chunk; refilled by inbound conn-level
    /// WU frames. Per HTTP/2 §6.9.1 starts at <see cref="ShmConstants.InitialWindowSize"/>;
    /// senders MUST observe both this and the per-stream window.
    /// <para>
    /// Phase A note: the inline-send path consults the per-stream quota
    /// only. Conn-level enforcement is wired but its CAS happens at the
    /// writer-task boundary (Phase B); intermediate state is correct
    /// because every outbound DATA flows through the writer task after
    /// Phase B refactor.
    /// </para>
    /// </summary>
    private long _connSendQuota = ShmConstants.InitialWindowSize;

    /// <summary>
    /// Wake signal for senders blocked on insufficient conn-level quota.
    /// Set by <see cref="AddSendQuota"/>(streamId=0, …); reset by
    /// senders before re-checking quota to avoid missed wakes.
    /// </summary>
    private readonly ManualResetEventSlim _connSendQuotaWake = new(initialState: false);

    /// <summary>
    /// Conn-level inbound flow-control bookkeeping. Tracks total DATA
    /// bytes received across all streams against the conn-level limit
    /// we advertised; paces conn-level <c>WINDOW_UPDATE(streamId=0)</c>
    /// drip emission. SHM-tuned threshold may differ from stock
    /// <c>limit/4</c>; the SHM transport may flush via
    /// <see cref="Synchronization.TrInFlow.Reset"/> at its own cadence.
    /// </summary>
    internal Synchronization.TrInFlow ConnInFlow { get; } = new(initialLimit: (uint)ShmConstants.InitialWindowSize);

    /// <summary>
    /// Pending conn-level WINDOW_UPDATE credit awaiting emission.
    /// Mirrors grpc-go-shmem's <c>t.pendingConnWU</c> in the lockless
    /// WU emission path (shm_client_transport.go:620-635). Producers
    /// (the reader Thread's drip on every inbound DATA frame)
    /// atomically <c>Add</c> their delta; a producer that crosses
    /// the <see cref="_wuThreshold"/> gate <c>Exchange(0)</c>s the
    /// accumulator to claim the full sum for a single coalesced
    /// frame. Eliminates the per-drip frame-writer Enqueue cost on
    /// 65535 windows where the previous per-call emit triggered
    /// O(frames / drip-threshold) ManualResetEventSlim.Set
    /// thundering-herd wakes on every stalled sender.
    /// </summary>
    private long _pendingConnWU;

    /// <summary>Snapshot of the current conn-level send quota.</summary>
    internal long ConnSendQuota => Volatile.Read(ref _connSendQuota);

    /// <summary>
    /// Routes an inbound <c>WINDOW_UPDATE</c> frame from the H2 codec
    /// to the appropriate quota.
    /// </summary>
    /// <param name="streamId">
    /// Frame stream ID. <c>0</c> = conn-level (refills
    /// <see cref="_connSendQuota"/>); non-zero = per-stream (looks up
    /// the <see cref="ShmGrpcStream"/> and calls
    /// <see cref="ShmGrpcStream.AddSendQuota"/>).
    /// </param>
    /// <param name="delta">Credit increment. RFC 7540 §6.9.1 forbids 0;
    /// caller (H2 codec) has already validated.</param>
    /// <remarks>
    /// gRFC SHM v3.4+ "Flow Control": SHM transports run an
    /// HTTP/2-compatible FC state machine that follows RFC 7540 §5.2 and
    /// §6.9 wire semantics exactly. WU dispatching here is the receiver-side
    /// half of the per-stream/conn quota dance. Records every emission to
    /// <c>s_wuFramesEmitted</c> so test code can verify FC traffic levels.
    /// </remarks>
    internal void AddSendQuota(uint streamId, uint delta)
    {
        if (delta == 0) return; // codec already RST'd; defensive
        var clamped = delta > int.MaxValue ? int.MaxValue : (int)delta;
        if (streamId == 0)
        {
            // Conn-level WU. Cap conn quota at int.MaxValue (H2 31-bit
            // ceiling enforced via OnReceive validation at codec layer).
            while (true)
            {
                var current = Volatile.Read(ref _connSendQuota);
                var desired = current + clamped;
                if (desired > int.MaxValue) desired = int.MaxValue;
                if (Interlocked.CompareExchange(ref _connSendQuota, desired, current) == current)
                {
                    _connSendQuotaWake.Set();
                    return;
                }
            }
        }
        // Per-stream WU. Unknown stream IDs (e.g. RST'd already) are dropped.
        if (_streams.TryGetValue(streamId, out var stream))
        {
            stream.AddSendQuota(clamped);
            // Phase B: nudge the writer task to drain any deferred
            // entries on this stream whose quota is now sufficient.
            _frameWriter?.NotifyQuotaUpdated(streamId);
        }
        else
        {
            // Stream was disposed locally (e.g. Unary server-side
            // handler returned and disposed the stream right after
            // fire-and-forget SendFrameZeroCopy of the response body)
            // but our partial-write defer may still hold the last
            // chunks parked under this streamId. Wake the writer so
            // its <see cref="TryDrainDeferredLocked"/> can take the
            // fairStream-null branch and write the remaining chunks
            // best-effort to the peer (who is still reading the
            // mid-LPM bytes and waiting on the full message).
            _frameWriter?.NotifyQuotaUpdated(streamId);
        }
    }

    /// <summary>
    /// HTTP/2 codec hook: invoked synchronously by the frame reader for
    /// every inbound DATA frame (RFC 7540 §6.9.1 — Pad Length and padding
    /// included). Updates conn-level inbound accounting, emits the
    /// conn-level <c>WINDOW_UPDATE</c> drip if the threshold is crossed,
    /// and updates the per-stream inbound accounting (closing the stream
    /// with <c>FLOW_CONTROL_ERROR</c> per RFC 7540 §5.2.2 on over-window
    /// receive).
    /// </summary>
    private void OnDataFrame(uint streamId, uint payloadLen)
    {
        // Conn-level drip-on-receive. Stock HTTP/2 cadence (limit/4).
        // Phase B may add SHM-tuned threshold + piggyback WU.
        var connWu = ConnInFlow.OnData(payloadLen);
        if (connWu > 0)
        {
            EmitWindowUpdate(streamId: 0, delta: connWu);
        }

        // Per-stream inbound accounting + over-window enforcement.
        if (_streams.TryGetValue(streamId, out var stream))
        {
            var err = stream.InFlow.OnData(payloadLen);
            if (err != null)
            {
                // RFC 7540 §5.2.2: receivers MUST treat over-window inbound
                // DATA as STREAM_ERROR with FLOW_CONTROL_ERROR. Send
                // RST_STREAM(FLOW_CONTROL_ERROR) and tear down the local
                // stream so the partial LPM is dropped.
                System.Diagnostics.Debug.WriteLine($"[shm-fc] stream {streamId} FLOW_CONTROL_ERROR: {err}");
                EmitRstStream(streamId, Wire.Http2ErrorCode.FlowControlError);
                // Local teardown — best-effort; stream may already be closing.
                try { stream.AbortForFlowControl(err); } catch { /* ignore */ }
                return;
            }

            // SHM drip-on-receive: SHM has no intermediate copy buffer
            // (the codec parses directly from the ring), so "received"
            // and "read-by-app" are effectively the same event. We
            // settle the receive-side drip here instead of waiting for
            // an OnAppRead call, which may never come for consumers
            // that don't go through ShmGrpcStream.ReceiveLpmMessagesAsync
            // (e.g. the gRPC.Net.Client HttpContent stream path).
            // Matches grpc-go-shmem v3.4+ drip-on-receive behavior.
            var streamWu = stream.InFlow.OnRead(payloadLen);
            if (streamWu > 0)
            {
                EmitWindowUpdate(streamId, streamWu);
            }
        }
    }

    /// <summary>
    /// HTTP/2 codec hook: invoked the moment the codec finishes parsing
    /// the 5-byte LPM header for an inbound message. Drives the gRFC
    /// SHM v3.4+ MUST stream-level pre-credit:
    /// <c>InFlow.MaybeAdjustAdditive(lpmSize)</c> returns the additional
    /// stream WU bytes needed to admit the announced message; we emit
    /// the WU immediately bypassing the drip threshold (force-emit) so
    /// the sender does not stall waiting for a drip that never reaches
    /// the threshold before the LPM completes.
    /// </summary>
    private void OnMessageStart(uint streamId, uint lpmSize)
    {
        if (!_streams.TryGetValue(streamId, out var stream)) return;
        var wu = stream.InFlow.MaybeAdjustAdditive(lpmSize);
        if (wu > 0)
        {
            // gRFC SHM v3.4+ additive pre-credit MUST be emitted
            // immediately so the sender does not stall waiting for
            // a drip that won't arrive before the LPM completes.
            // force=true bypasses the coalescing threshold gate.
            EmitWindowUpdate(streamId, wu, force: true);
        }
    }

    /// <summary>
    /// Emits a single <c>WINDOW_UPDATE</c> frame on the TX ring.
    /// <paramref name="streamId"/> = 0 for connection-level, &gt; 0 for
    /// per-stream. Uses the existing <c>FrameProtocol.WriteWindowUpdate</c>
    /// path which is bit-identical to the H2 codec emit path verified
    /// by <c>NoWindowUpdate_*</c> tests in PR #20 (the test will be
    /// updated to "WindowUpdate_Emitted_PerHttp2Semantics" in Phase A
    /// step A10).
    /// </summary>
    private void EmitWindowUpdate(uint streamId, uint delta)
    {
        EmitWindowUpdate(streamId, delta, force: false);
    }

    /// <summary>
    /// Accumulating WINDOW_UPDATE emit path. Mirrors grpc-go-shmem's
    /// <c>sendConnWindowUpdate</c> / <c>sendStreamWindowUpdate</c>
    /// lockless coalescing (shm_client_transport.go:620-680). Each
    /// caller atomically adds its <paramref name="delta"/> to the
    /// per-scope pending accumulator. When the pending value crosses
    /// <see cref="_wuThreshold"/> (or when <paramref name="force"/>
    /// is true, e.g. for additive pre-credit on LPM start) the
    /// caller <c>Exchange(0)</c>s the accumulator and emits one WU
    /// frame for the entire swept sum. A producer that observes
    /// <c>Exchange</c> returning 0 means a concurrent producer has
    /// already swept its delta and will emit the combined frame;
    /// bytes are never lost.
    /// </summary>
    private void EmitWindowUpdate(uint streamId, uint delta, bool force)
    {
        if (_disposed != 0 || _frameWriter == null || delta == 0) return;

        // 2026-05-28 — threshold gate disabled while we debug a hang at
        // fair_conc 10×64KB on Linux Xeon (window=65535 forces multi-
        // round-trip WU pressure that the gate failed to keep liveness
        // on). The accumulator is preserved so concurrent producers
        // still combine their deltas into one frame when they race,
        // but we now ALWAYS Exchange + emit per call, matching the
        // pre-ad029a17 emit cadence on the slow path. If a future
        // commit re-introduces a gate it MUST be paired with a
        // periodic flush so deferred bytes can never be stuck when
        // the sender stalls waiting for the deferred WU.
        long claimed;
        if (streamId == 0)
        {
            Interlocked.Add(ref _pendingConnWU, delta);
            // _ = force; // intentionally ignored: gate is off
            claimed = Interlocked.Exchange(ref _pendingConnWU, 0);
        }
        else
        {
            if (!_streams.TryGetValue(streamId, out var stream)) return;
            Interlocked.Add(ref stream.PendingWU, delta);
            claimed = Interlocked.Exchange(ref stream.PendingWU, 0);
        }

        if (claimed == 0)
        {
            // A concurrent producer already swept the accumulator and
            // will emit the combined frame carrying our delta.
            return;
        }

        EmitWindowUpdateFrame(streamId, (uint)Math.Min(claimed, int.MaxValue));
        _ = force; // silence unused-warning while gate is disabled
    }

    /// <summary>
    /// Writes a single WINDOW_UPDATE frame to the TX ring via the
    /// frame-writer queue. Called only by <see cref="EmitWindowUpdate"/>
    /// after the lockless accumulator has been swept; do not call
    /// directly from drip / pre-credit paths or coalescing is
    /// bypassed.
    /// </summary>
    private void EmitWindowUpdateFrame(uint streamId, uint delta)
    {
        if (_disposed != 0 || _frameWriter == null || delta == 0) return;
        try
        {
            Span<byte> payload = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload, delta);
            _frameWriter.Enqueue(FrameType.WindowUpdate, streamId, 0, payload);
        }
        catch (InvalidOperationException)
        {
            // Writer closed; ignore.
        }
    }

    /// <summary>
    /// Emits a single <c>RST_STREAM</c> frame with the given H2 error
    /// code. Used by FC enforcement (RFC 7540 §5.2.2 / §6.9.1) to abort
    /// an over-window stream with <c>FLOW_CONTROL_ERROR</c>. Goes through
    /// the same <see cref="ShmFrameWriter"/> queue as application frames
    /// so it serializes with in-flight DATA on the same stream.
    /// </summary>
    private void EmitRstStream(uint streamId, Wire.Http2ErrorCode error)
    {
        if (_disposed != 0 || _frameWriter == null || streamId == 0) return;
        try
        {
            Span<byte> payload = stackalloc byte[4];
            // The Cancel codec path expects 4-byte big-endian H2 error code.
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)error);
            _frameWriter.Enqueue(FrameType.Cancel, streamId, 0, payload);
        }
        catch (InvalidOperationException)
        {
            // Writer closed; ignore.
        }
    }

    /// <summary>
    /// Stream-level app-Read drip path: invoked by <see cref="ShmGrpcStream"/>
    /// whenever a complete LPM is yielded to the application. The byte
    /// count <paramref name="n"/> SHOULD match what was previously
    /// accounted via <c>InFlow.OnData</c> for the same bytes (i.e. the
    /// full LPM size in our codec: 5-byte LPM header + body). Refunds
    /// any outstanding pre-credit debt first, then accumulates toward
    /// the <c>limit/4</c> drip threshold; emits the stream-level WU
    /// when the threshold is crossed.
    /// </summary>
    internal void OnAppRead(uint streamId, int n)
    {
        if (n <= 0) return;
        if (!_streams.TryGetValue(streamId, out var stream)) return;
        var wu = stream.InFlow.OnRead((uint)n);
        if (wu > 0)
        {
            EmitWindowUpdate(streamId, wu);
        }
    }

    #endregion

    #region Keepalive

    /// <summary>
    /// Keepalive background loop that sends periodic pings.
    /// </summary>
    private async Task KeepaliveLoopAsync()
    {
        try
        {
            while (!_disposeCts.Token.IsCancellationRequested && !IsClosed)
            {
                await Task.Delay(_keepaliveOptions.Time, _disposeCts.Token);

                if (_disposeCts.Token.IsCancellationRequested || IsClosed)
                {
                    break;
                }

                // Check if we should send a ping
                var hasActiveStreams = !_streams.IsEmpty;
                if (!hasActiveStreams && !_keepaliveOptions.PermitWithoutStream)
                {
                    continue;
                }

                // Check if there's already a pending ping
                if (_pendingPing)
                {
                    // Check if timeout exceeded
                    if (DateTime.UtcNow - _lastPingSentAt > _keepaliveOptions.PingTimeout)
                    {
                        // Timeout - close connection
                        SendGoAway("keepalive timeout");
                        break;
                    }
                    continue;
                }

                // Send keepalive ping
                _pendingPing = true;
                _lastPingSentAt = DateTime.UtcNow;
                var pingData = BitConverter.GetBytes(DateTime.UtcNow.Ticks);
                SendFrame(FrameType.Ping, 0, 0, pingData);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
    }

    /// <summary>
    /// Handles an incoming PING frame.
    /// </summary>
    private void HandlePing(FrameHeader header, ReadOnlySpan<byte> payload)
    {
        // Check for BDP ping
        if ((header.Flags & PingFlags.Bdp) != 0)
        {
            // BDP ping - respond with pong and BDP flag
            SendFrame(FrameType.Pong, 0, PingFlags.Bdp, payload);
            return;
        }

        // Server-side: check ping enforcement policy
        if (!_isClient && _enforcementPolicy != null)
        {
            var now = DateTime.UtcNow;
            var hasActiveStreams = !_streams.IsEmpty;

            // Check if ping is allowed without streams
            if (!hasActiveStreams && !_enforcementPolicy.PermitWithoutStream)
            {
                if (Interlocked.Increment(ref _pingStrikes) > _enforcementPolicy.MaxPingStrikes)
                {
                    SendGoAway("too many pings without streams");
                    return;
                }
            }

            // Check if ping is too frequent
            if (_lastPingAt != DateTime.MinValue && now - _lastPingAt < _enforcementPolicy.MinTime)
            {
                if (Interlocked.Increment(ref _pingStrikes) > _enforcementPolicy.MaxPingStrikes)
                {
                    SendGoAway("too many pings");
                    return;
                }
            }

            _lastPingAt = now;
        }

        // Regular ping - respond with pong
        SendFrame(FrameType.Pong, header.StreamId, header.Flags, payload);
    }

    /// <summary>
    /// Handles an incoming PONG frame.
    /// </summary>
    private void HandlePong(FrameHeader header, ReadOnlySpan<byte> payload)
    {
        // Regular keepalive pong - clear pending ping
        _pendingPing = false;
    }

    #endregion

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private void ThrowIfGoAway()
    {
        if (Volatile.Read(ref _goAwaySent) != 0 || _goAwayReceived)
        {
            throw new InvalidOperationException("Connection is being closed due to GoAway");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _frameWriter?.Dispose();

        if (Volatile.Read(ref _goAwaySent) == 0)
        {
            Interlocked.Exchange(ref _goAwaySent, 1);
            try
            {
                using var goAwayCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                var payload = System.Text.Encoding.UTF8.GetBytes("Connection disposed");
                var header = new FrameHeader(FrameType.GoAway, 0, (uint)payload.Length, GoAwayFlags.Draining);
                FrameProtocol.WriteFrame(TxRing, header, payload, goAwayCts.Token);
            }
            catch { /* best-effort */ }
        }

        _incomingStreamsChannel.Writer.TryComplete();
        _disposeCts.Cancel();

        // Early wake all stream consumers. The per-stream Channel waits
        // now run with CancellationToken.None (so the runtime can use
        // its pooled _waiterSingleton fast path; see GPT-5.5 review
        // notes in ShmGrpcStream.ReceiveFrameSync). Without an explicit
        // TryComplete here those consumers would sleep until step ~6
        // below (foreach stream.Dispose), incurring up to the 5-second
        // reader-wait budget of dispose-latency for any consumer
        // currently parked on WaitToReadAsync. TryComplete is
        // idempotent and races safely with future writes from the
        // reader (the reader will simply get TryWrite=false and
        // release the frame, which is the correct teardown
        // behaviour).
        foreach (var stream in _streams.Values)
        {
            try { stream.CompleteInbound(); }
            catch { /* best effort */ }
        }

        // Wait for the reader thread to fully exit BEFORE disposing the
        // Segment (which unmaps the ring header pages). If the reader is
        // still inside ShmRing.WaitForDataAfter when the unmap happens it
        // will AccessViolation on its next Volatile.Read of header.WriteIdx.
        // The Linux FUTEX_WAIT path now wires the CT to FUTEX_WAKE so the
        // reader unparks within microseconds; 5 s is a generous safety
        // budget that mostly covers extremely slow CI scheduling.
        try { _frameReaderTask.Wait(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ShmConnection.Dispose: frame reader: {ex.Message}"); }
        if (!_frameReaderTask.IsCompleted)
        {
            // The reader did not honour cancellation in time. Skip the
            // Segment.Dispose (it would munmap memory the reader is
            // about to touch). This leaks the segment file/handles but
            // keeps the process alive. Log so operators notice.
            System.Diagnostics.Debug.WriteLine(
                "ShmConnection.Dispose: frame reader did not exit in 5 s; " +
                "leaking Segment to avoid use-after-free.");
        }

        // Stop the receive-side fan-out AFTER the reader Thread has
        // exited so we know no more frames will be Enqueued into the
        // stripes. Disposing the striper TryCompletes each stripe queue
        // and Joins the stripe Threads; the stripe shutdown drain
        // releases any frame whose payload was queued but never
        // dispatched (e.g., reader enqueued during the cancellation
        // window). MUST happen before the Segment is unmapped (below)
        // because the drain may still touch the payload backing buffer
        // for ring-backed zero-copy frames.
        try { _receiveStriper?.Dispose(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ShmConnection.Dispose: striper: {ex.Message}"); }

        if (_keepaliveTask != null)
        {
            try { _keepaliveTask.Wait(TimeSpan.FromMilliseconds(200)); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ShmConnection.Dispose: keepalive: {ex.Message}"); }
        }

        foreach (var stream in _streams.Values)
        {
            try { stream.Dispose(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ShmConnection.Dispose: stream {stream.StreamId}: {ex.Message}"); }
        }
        _streams.Clear();
        // Keep ActiveStreamCount accessor consistent with _streams after
        // disposal (post-dispose queries should report 0, not the last
        // pre-dispose count).
        Interlocked.Exchange(ref _clientStreamCount, 0);
        Interlocked.Exchange(ref _serverStreamCount, 0);

        // Only dispose the Segment if the reader has confirmed it is
        // outside any header.WriteIdx read.
        if (_frameReaderTask.IsCompleted)
        {
            _segment.Dispose();
        }
        _disposeCts.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _frameWriter?.Dispose();

        if (Volatile.Read(ref _goAwaySent) == 0)
        {
            Interlocked.Exchange(ref _goAwaySent, 1);
            try
            {
                using var goAwayCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                var payload = System.Text.Encoding.UTF8.GetBytes("Connection disposed");
                var header = new FrameHeader(FrameType.GoAway, 0, (uint)payload.Length, GoAwayFlags.Draining);
                FrameProtocol.WriteFrame(TxRing, header, payload, goAwayCts.Token);
            }
            catch { /* best-effort */ }
        }

        _incomingStreamsChannel.Writer.TryComplete();
        _disposeCts.Cancel();

        // Early wake all stream consumers \u2014 see sync Dispose for
        // rationale.
        foreach (var stream in _streams.Values)
        {
            try { stream.CompleteInbound(); }
            catch { /* best effort */ }
        }

        // See sync Dispose for rationale on the 5 s timeout + leak-on-
        // timeout policy.
        try { await _frameReaderTask.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ShmConnection.DisposeAsync: frame reader: {ex.Message}"); }
        var readerExited = _frameReaderTask.IsCompleted;
        if (!readerExited)
        {
            System.Diagnostics.Debug.WriteLine(
                "ShmConnection.DisposeAsync: frame reader did not exit in 5 s; " +
                "leaking Segment to avoid use-after-free.");
        }

        // See sync Dispose for rationale on striper-after-reader ordering.
        try { _receiveStriper?.Dispose(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ShmConnection.DisposeAsync: striper: {ex.Message}"); }

        if (_keepaliveTask != null)
        {
            try { await _keepaliveTask.WaitAsync(TimeSpan.FromMilliseconds(200)); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ShmConnection.DisposeAsync: keepalive: {ex.Message}"); }
        }

        foreach (var stream in _streams.Values)
        {
            try { await stream.DisposeAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ShmConnection.DisposeAsync: stream {stream.StreamId}: {ex.Message}"); }
        }
        _streams.Clear();
        // See Dispose() for rationale.
        Interlocked.Exchange(ref _clientStreamCount, 0);
        Interlocked.Exchange(ref _serverStreamCount, 0);

        // Only dispose the Segment if the reader has confirmed it is
        // outside any header.WriteIdx read.
        if (readerExited)
        {
            _segment.Dispose();
        }
        _disposeCts.Dispose();
    }
}

/// <summary>
/// Event arguments for GoAway events.
/// </summary>
public sealed class GoAwayEventArgs : EventArgs
{
    /// <summary>
    /// Gets the GoAway flags.
    /// </summary>
    public byte Flags { get; }

    /// <summary>
    /// Gets the optional debug message.
    /// </summary>
    public string? Message { get; }

    /// <summary>
    /// Creates new GoAwayEventArgs.
    /// </summary>
    public GoAwayEventArgs(byte flags, string? message)
    {
        Flags = flags;
        Message = message;
    }
}

/// <summary>
/// Event args for stream received events.
/// </summary>
public sealed class StreamReceivedEventArgs : EventArgs
{
    /// <summary>
    /// Gets the stream that was received.
    /// </summary>
    public ShmGrpcStream Stream { get; }

    /// <summary>
    /// Creates new StreamReceivedEventArgs.
    /// </summary>
    public StreamReceivedEventArgs(ShmGrpcStream stream)
    {
        Stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }
}
