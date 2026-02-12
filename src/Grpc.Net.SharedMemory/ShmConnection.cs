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
    private bool _disposed;
    private bool _goAwaySent;
    private bool _goAwayReceived;
    private bool _draining;
    private uint _maxConcurrentStreams;
    private readonly bool _zeroCopyReads;

    // Connection-level flow control (matches grpc-go-shmem)
    private long _connSendQuota;
    private uint _connInFlowLimit;
    private uint _connInFlowUnacked;
    private readonly object _flowControlLock = new();

    // Write lock: the SPSC ring buffer requires single-producer semantics.
    // All writes to TxRing must be serialized through this lock.
    private readonly object _txLock = new();
    private readonly SemaphoreSlim _quotaSignal = new(0);

    // BDP estimation (A73 RFC Phase 5)
    private readonly ShmBdpEstimator _bdpEstimator;

    // Keepalive (A73 RFC)
    private readonly ShmKeepaliveOptions _keepaliveOptions;
    private readonly ShmKeepaliveEnforcementPolicy? _enforcementPolicy;
    private Task? _keepaliveTask;
    private DateTime _lastPingAt;
    private DateTime _lastPingSentAt;
    private bool _pendingPing;
    private int _pingStrikes;

    /// <summary>
    /// Gets the connection-level send quota remaining.
    /// </summary>
    public long ConnectionSendQuota => Interlocked.Read(ref _connSendQuota);

    /// <summary>
    /// Gets the BDP estimator for this connection.
    /// </summary>
    public ShmBdpEstimator BdpEstimator => _bdpEstimator;

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
    public bool IsClosed => _disposed || _goAwaySent || _goAwayReceived;

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
    /// Creates a new client-side connection by opening an existing shared memory segment.
    /// </summary>
    /// <param name="name">The name of the shared memory segment to connect to.</param>
    /// <param name="keepaliveOptions">Optional keepalive options.</param>
    /// <returns>A new client connection.</returns>
    [SupportedOSPlatform("windows")]
    public static ShmConnection ConnectAsClient(string name, ShmKeepaliveOptions? keepaliveOptions = null)
    {
        var segment = Segment.Open(name);
        return new ShmConnection(name, segment, isClient: true, keepaliveOptions);
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
        return new ShmConnection(name, segment, isClient: false, keepaliveOptions, enforcementPolicy);
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

        // Zero-copy reads are only safe with a single active stream.
        _zeroCopyReads = _maxConcurrentStreams == 1;

        // Initialize connection-level flow control (matches grpc-go-shmem)
        _connSendQuota = ShmConstants.InitialWindowSize;
        _connInFlowLimit = (uint)ShmConstants.InitialWindowSize;
        _connInFlowUnacked = 0;

        // Initialize BDP estimator
        _bdpEstimator = new ShmBdpEstimator((uint)ShmConstants.InitialWindowSize, UpdateFlowControlWindows);

        // Create channel for incoming streams (server-side)
        // Limit to a reasonable max for the bounded channel
        var channelCapacity = Math.Min((int)_maxConcurrentStreams, 10000);
        _incomingStreamsChannel = Channel.CreateBounded<ShmGrpcStream>(new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true
        });

        // Client uses odd stream IDs (1, 3, 5, ...), server uses even (2, 4, 6, ...)
        _nextStreamId = isClient ? 1u : 2u;

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
    public ShmGrpcStream CreateStream()
    {
        ThrowIfDisposed();
        ThrowIfGoAway();

        if (_zeroCopyReads && _streams.Count > 0)
        {
            throw new InvalidOperationException("Zero-copy mode supports only one active stream");
        }

        var streamId = Interlocked.Add(ref _nextStreamId, 2) - 2; // Increment by 2, return previous value
        var stream = new ShmGrpcStream(streamId, this, isServerStream: false);

        if (!_streams.TryAdd(streamId, stream))
        {
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
            lock (_txLock)
            {
                FrameProtocol.WriteGoAway(TxRing, GoAwayFlags.Draining, message, _disposeCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Connection already closing
        }
    }

    /// <summary>
    /// Sends a Ping frame.
    /// </summary>
    public void SendPing()
    {
        ThrowIfDisposed();
        var pingData = BitConverter.GetBytes(Environment.TickCount64);
        lock (_txLock)
        {
            FrameProtocol.WritePing(TxRing, 0, pingData, _disposeCts.Token);
        }
    }

    internal void RemoveStream(uint streamId)
    {
        _streams.TryRemove(streamId, out _);
    }

    internal void SendFrame(FrameType type, uint streamId, byte flags, ReadOnlySpan<byte> payload)
    {
        ThrowIfDisposed();

        lock (_txLock)
        {
            // MESSAGE frames may exceed ring capacity and need chunking
            if (type == FrameType.Message)
            {
                var isLast = (flags & MessageFlags.More) == 0;
                FrameProtocol.WriteMessage(TxRing, streamId, payload, isLast, _disposeCts.Token);
                return;
            }

            var header = new FrameHeader(type, streamId, (uint)payload.Length, flags);
            FrameProtocol.WriteFrame(TxRing, header, payload, _disposeCts.Token);
        }
    }

    private Task FrameReaderLoopAsync()
    {
        // Run blocking ReadFramePayload on a dedicated thread.
        return Task.Factory.StartNew(async () =>
        {
            try
            {
                while (!_disposeCts.Token.IsCancellationRequested)
                {
                    var (header, payload) = FrameProtocol.ReadFramePayload(RxRing, _zeroCopyReads, _disposeCts.Token);
                    await ProcessFrameAsync(header, payload).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (ObjectDisposedException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Frame reader disposed: {ex.Message}");
            }
            catch (RingClosedException)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Frame reader error: {ex.Message}");
            }
        }, _disposeCts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    private async Task ProcessFrameAsync(FrameHeader header, FramePayload payload)
    {
        var payloadLength = payload.Length;
        var payloadMemory = payload.Memory;

        switch (header.Type)
        {
            case FrameType.Headers:
                await HandleHeadersFrameAsync(header, payload);
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

                    // BDP estimation: track bytes received
                    if (header.Type == FrameType.Message)
                    {
                        var sendBdpPing = _bdpEstimator.Add((uint)payloadLength);
                        var windowUpdate = OnConnectionDataReceived((uint)payloadLength);
                        if (windowUpdate > 0)
                        {
                            SendConnectionWindowUpdate(windowUpdate);
                        }
                        if (sendBdpPing)
                        {
                            SendBdpPing();
                        }
                    }
                }
                else
                {
                    payload.Release();
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
    private async Task HandleHeadersFrameAsync(FrameHeader header, FramePayload payload)
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
                return;
            }

            // Publish to incoming streams channel
            if (!_incomingStreamsChannel.Writer.TryWrite(newStream))
            {
                await _incomingStreamsChannel.Writer.WriteAsync(newStream, _disposeCts.Token);
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
            var trailersPayload = trailers.Encode();
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
    /// Adds send quota for a stream or connection (when receiving WINDOW_UPDATE).
    /// </summary>
    /// <param name="streamId">Stream ID (0 for connection-level).</param>
    /// <param name="delta">The window size increment.</param>
    internal void AddSendQuota(uint streamId, uint delta)
    {
        if (streamId == 0)
        {
            // Connection-level
            var oldQuota = Interlocked.Add(ref _connSendQuota, delta) - delta;
            if (oldQuota <= 0 && oldQuota + delta > 0)
            {
                // Quota became positive - signal waiters
                _quotaSignal.Release();
            }
        }
        else
        {
            // Stream-level - route to stream
            if (_streams.TryGetValue(streamId, out var stream))
            {
                stream.OnWindowUpdate(delta);
            }
        }
    }

    /// <summary>
    /// Acquires send quota from both connection and stream levels.
    /// Blocks until quota is available or cancelled.
    /// </summary>
    /// <param name="streamId">The stream ID.</param>
    /// <param name="n">Number of bytes to acquire.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal async Task AcquireSendQuotaAsync(uint streamId, int n, CancellationToken cancellationToken)
    {
        // Acquire connection-level quota first
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = Interlocked.Read(ref _connSendQuota);
            if (current >= n)
            {
                if (Interlocked.CompareExchange(ref _connSendQuota, current - n, current) == current)
                {
                    break;
                }
                continue;
            }

            // Wait for quota update
            await _quotaSignal.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Updates connection inflow accounting when data is received.
    /// Returns the window update to send (if any).
    /// </summary>
    internal uint OnConnectionDataReceived(uint size)
    {
        lock (_flowControlLock)
        {
            _connInFlowUnacked += size;
            if (_connInFlowUnacked >= _connInFlowLimit / 4)
            {
                var windowUpdate = _connInFlowUnacked;
                _connInFlowUnacked = 0;
                return windowUpdate;
            }
            return 0;
        }
    }

    /// <summary>
    /// Sends a connection-level window update.
    /// </summary>
    internal void SendConnectionWindowUpdate(uint increment)
    {
        if (increment > 0)
        {
            Span<byte> payload = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload, increment);
            SendFrame(FrameType.WindowUpdate, 0, 0, payload);
        }
    }

    /// <summary>
    /// Updates flow control windows based on BDP estimation.
    /// Called by the BDP estimator when it calculates a new estimate.
    /// </summary>
    private void UpdateFlowControlWindows(uint newBdp)
    {
        lock (_flowControlLock)
        {
            if (newBdp > _connInFlowLimit)
            {
                var delta = newBdp - _connInFlowLimit;
                _connInFlowLimit = newBdp;
                SendConnectionWindowUpdate(delta);
            }
        }

        // Also update stream-level windows
        foreach (var stream in _streams.Values)
        {
            stream.UpdateFlowControlWindow(newBdp);
        }
    }

    /// <summary>
    /// Sends a BDP ping to estimate bandwidth-delay product.
    /// </summary>
    internal void SendBdpPing()
    {
        if (_disposed || IsClosed)
        {
            return;
        }

        _bdpEstimator.Timesnap();
        SendFrame(FrameType.Ping, 0, PingFlags.Bdp, ShmBdpEstimator.BdpPingData);
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
        // Check for BDP pong
        if ((header.Flags & PingFlags.Bdp) != 0 && payload.Length == 8 &&
            payload.SequenceEqual(ShmBdpEstimator.BdpPingData))
        {
            _bdpEstimator.Calculate();
            return;
        }

        // Regular keepalive pong - clear pending ping
        _pendingPing = false;
    }

    #endregion

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
        if (!_disposed)
        {
            _disposed = true;

            // Send GoAway if not already sent
            if (!_goAwaySent)
            {
                try { SendGoAway("Connection disposed"); } catch { }
            }

            _disposeCts.Cancel();

            // Wait for reader to finish
            try { _frameReaderTask.Wait(TimeSpan.FromSeconds(1)); } catch { }

            // Dispose all streams
            foreach (var stream in _streams.Values)
            {
                try { stream.Dispose(); } catch { }
            }
            _streams.Clear();

            _segment.Dispose();
            _disposeCts.Dispose();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;

            // Send GoAway if not already sent
            if (!_goAwaySent)
            {
                try { SendGoAway("Connection disposed"); } catch { }
            }

            _disposeCts.Cancel();

            // Wait for reader to finish
            try { await _frameReaderTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }

            // Dispose all streams
            foreach (var stream in _streams.Values)
            {
                try { await stream.DisposeAsync(); } catch { }
            }
            _streams.Clear();

            _segment.Dispose();
            _disposeCts.Dispose();
        }
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
