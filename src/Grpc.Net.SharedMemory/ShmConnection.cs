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
    private bool _goAwaySent;
    private bool _goAwayReceived;
    private bool _draining;
    private uint _maxConcurrentStreams;

    // Atomic counter for client-side max-stream enforcement.
    // Incremented in CreateStream BEFORE adding to _streams, decremented in
    // RemoveStream AFTER removing. This eliminates the TOCTOU race where
    // N threads pass a _streams.Count check simultaneously and all succeed
    // in creating streams, exceeding the server's maxConcurrentStreams limit.
    private int _clientStreamCount;

    // Accumulated connection-level window update bytes. Sent when threshold
    // is reached to reduce frame overhead. Uses Min(1MB, InitialWindowSize/4)
    // to maintain the 25% window replenishment ratio from the original design.
    private long _connWindowDebt;
    private static readonly long ConnWindowUpdateThreshold =
        Math.Min(1_048_576, ShmConstants.InitialWindowSize / 4);

    // Write lock: the SPSC ring buffer requires single-producer semantics.
    // All writes to TxRing are serialised through the ShmFrameWriter's
    // dedicated consumer thread (Channel SingleReader=true).
    private ShmFrameWriter? _frameWriter;

    // Keepalive (A73 RFC)
    private readonly ShmKeepaliveOptions _keepaliveOptions;
    private readonly ShmKeepaliveEnforcementPolicy? _enforcementPolicy;
    private Task? _keepaliveTask;
    private DateTime _lastPingAt;
    private DateTime _lastPingSentAt;
    private bool _pendingPing;
    private int _pingStrikes;

    /// <summary>
    /// Gets the connection name (shared memory segment name).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets whether this is a client-side connection.
    /// </summary>
    public bool IsClient => _isClient;

    /// <summary>
    /// Gets whether the connection has been closed.
    /// </summary>
    public bool IsClosed => Volatile.Read(ref _disposed) != 0 || _goAwaySent || _goAwayReceived;

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
    public int ActiveStreamCount => _isClient
        ? Volatile.Read(ref _clientStreamCount)
        : _streams.Count;

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
    [SupportedOSPlatform("windows")]
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
        _frameWriter = new ShmFrameWriter(TxRing, _disposeCts);

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
        if (_goAwaySent) return;
        _goAwaySent = true;

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

    internal void SendStreamWindowUpdate(uint streamId, uint increment)
    {
        if (increment > 0)
        {
            Span<byte> payload = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload, increment);
            SendFrame(FrameType.WindowUpdate, streamId, 0, payload);
        }
    }

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
                        RxRing, allowBorrowed: true, _disposeCts.Token);
                    ProcessFrame(header, payload);
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
                // Route to stream — transfer pooled buffer ownership
                if (_streams.TryGetValue(header.StreamId, out var stream))
                {
                    var frame = new InboundFrame(header.Type, payload, header.Flags);
                    stream.OnFrameReceived(frame);
                }
                else
                {
                    payload.Release();
                }

                // Batch connection-level window update: accumulate bytes and
                // send when threshold is reached. Uses the smaller of 1 MB and
                // InitialWindowSize/4 as threshold, matching the original 25%
                // window replenishment ratio while bounding the absolute delay.
                if (header.Type == FrameType.Message && payloadLength > 0)
                {
                    var debt = Interlocked.Add(ref _connWindowDebt, payloadLength);
                    if (debt >= ConnWindowUpdateThreshold)
                    {
                        var toSend = Interlocked.Exchange(ref _connWindowDebt, 0);
                        if (toSend > 0)
                        {
                            SendConnectionWindowUpdate((uint)Math.Min(toSend, uint.MaxValue));
                        }
                    }
                }
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
                GoAwayReceived?.Invoke(this, new GoAwayEventArgs(header.Flags, message));
                payload.Release();
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
            if (_draining || _goAwaySent || _goAwayReceived)
            {
                RejectStream(streamId, "transport is draining");
                payload.Release();
                return;
            }

            // Check max concurrent streams
            if (_streams.Count >= (int)_maxConcurrentStreams)
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

            // Publish to incoming streams channel.
            // Channel capacity is 2x maxConcurrentStreams to absorb the window
            // between RemoveStream (which decrements _streams.Count, allowing
            // new streams past the count check) and the consumer draining the
            // channel. TryWrite should always succeed with this capacity.
            if (!_incomingStreamsChannel.Writer.TryWrite(newStream))
            {
                // Should not happen with 2x capacity. If it does, wait briefly
                // then fail-fast: reject the stream so the client gets an error
                // instead of hanging in an orphaned state.
                // Use a dedicated CTS with timeout so the WriteAsync is cancelled
                // on timeout — not left orphaned. An uncancelled WriteAsync could
                // enqueue the stream later, after we've already rejected it.
                var written = false;
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
                    timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(500));
                    _incomingStreamsChannel.Writer.WriteAsync(newStream, timeoutCts.Token)
                        .AsTask().GetAwaiter().GetResult();
                    written = true;
                }
                catch { /* disposed, cancelled, or timed out */ }

                if (!written)
                {
                    // Remove from _streams, dispose, and reject — the stream was
                    // accepted into _streams but never delivered to AcceptStreamsAsync.
                    // Dispose drains queued inbound frames and releases any borrowed
                    // ring space they may hold.
                    if (_streams.TryRemove(streamId, out var orphaned))
                    {
                        orphaned.Dispose();
                    }

                    RejectStream(streamId, "server overloaded");
                    return;
                }
            }

            StreamReceived?.Invoke(this, new StreamReceivedEventArgs(newStream));
        }
        else
        {
            payload.Release();
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
    /// Routes a WINDOW_UPDATE frame to the appropriate stream.
    /// Connection-level updates (streamId=0) are accepted but not enforced
    /// locally — the ring buffer's physical capacity provides natural
    /// backpressure. We still send connection-level WINDOW_UPDATEs to the
    /// remote side (see ProcessFrame) for interop with implementations
    /// that enforce connection-level send quota.
    /// </summary>
    internal void AddSendQuota(uint streamId, uint delta)
    {
        if (streamId == 0)
        {
            // Connection-level: accepted but not enforced locally.
            return;
        }

        // Stream-level - route to stream
        if (_streams.TryGetValue(streamId, out var stream))
        {
            stream.OnWindowUpdate(delta);
        }
    }

    /// <summary>
    /// Sends a connection-level window update to the remote side.
    /// This keeps the remote's connection send quota replenished for interop
    /// with implementations that enforce connection-level flow control.
    /// </summary>
    private void SendConnectionWindowUpdate(uint increment)
    {
        Span<byte> payload = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload, increment);
        SendFrame(FrameType.WindowUpdate, 0, 0, payload);
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
                var hasActiveStreams = _streams.Count > 0;
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
            var hasActiveStreams = _streams.Count > 0;

            // Check if ping is allowed without streams
            if (!hasActiveStreams && !_enforcementPolicy.PermitWithoutStream)
            {
                _pingStrikes++;
                if (_pingStrikes > _enforcementPolicy.MaxPingStrikes)
                {
                    SendGoAway("too many pings without streams");
                    return;
                }
            }

            // Check if ping is too frequent
            if (_lastPingAt != DateTime.MinValue && now - _lastPingAt < _enforcementPolicy.MinTime)
            {
                _pingStrikes++;
                if (_pingStrikes > _enforcementPolicy.MaxPingStrikes)
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
        if (_goAwaySent || _goAwayReceived)
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

        if (!_goAwaySent)
        {
            _goAwaySent = true;
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

        try { _frameReaderTask.Wait(TimeSpan.FromMilliseconds(500)); } catch { }

        if (_keepaliveTask != null)
        {
            try { _keepaliveTask.Wait(TimeSpan.FromMilliseconds(200)); } catch { }
        }

        foreach (var stream in _streams.Values)
        {
            try { stream.Dispose(); } catch { }
        }
        _streams.Clear();

        _segment.Dispose();
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

        if (!_goAwaySent)
        {
            _goAwaySent = true;
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

        try { await _frameReaderTask.WaitAsync(TimeSpan.FromMilliseconds(500)); } catch { }

        if (_keepaliveTask != null)
        {
            try { await _keepaliveTask.WaitAsync(TimeSpan.FromMilliseconds(200)); } catch { }
        }

        foreach (var stream in _streams.Values)
        {
            try { await stream.DisposeAsync(); } catch { }
        }
        _streams.Clear();

        _segment.Dispose();
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
