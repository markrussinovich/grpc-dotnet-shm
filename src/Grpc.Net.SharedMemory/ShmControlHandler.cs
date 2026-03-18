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

using System.Net;
using System.Net.Http.Headers;
using Grpc.Core;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// An HttpMessageHandler that routes gRPC requests over shared memory
/// using the grpc-go-shmem compatible control segment protocol.
/// Use with GrpcChannel.ForAddress() by setting GrpcChannelOptions.HttpHandler.
/// </summary>
/// <example>
/// <code>
/// var handler = new ShmControlHandler("my_grpc_segment");
/// var channel = GrpcChannel.ForAddress("shm://localhost", new GrpcChannelOptions
/// {
///     HttpHandler = handler
/// });
/// var client = new Greeter.GreeterClient(channel);
/// </code>
/// </example>
public sealed class ShmControlHandler : HttpMessageHandler
{
    private readonly string _baseName;
    private readonly ShmClientTransportOptions _options;
    private readonly ShmConnectionPool? _pool;
    private bool _disposed;

    // --- Pool-bypass mode (EnableMultipleConnections = false) ---
    // Holds a single direct connection, lazily initialized on first use.
    private readonly SemaphoreSlim? _directConnectLock;
    private volatile ShmConnection? _directConnection;

    /// <summary>
    /// Creates a new ShmControlHandler that connects to the specified shared memory segment
    /// using the grpc-go-shmem control segment protocol.
    /// </summary>
    /// <param name="baseName">The base name of the shared memory segment (without _ctl suffix).</param>
    /// <param name="options">
    /// Optional transport options. When <c>null</c>, default options are used
    /// (multiple connections enabled, 64 MB ring, 30s connect timeout).
    /// </param>
    public ShmControlHandler(string baseName, ShmClientTransportOptions? options = null)
    {
        _baseName = baseName ?? throw new ArgumentNullException(nameof(baseName));
        _options = options ?? new ShmClientTransportOptions();

        if (_options.EnableMultipleConnections)
        {
            _pool = new ShmConnectionPool(_options, ConnectViaControlSegmentAsync);
        }
        else
        {
            // Single-connection bypass mode: lazy-init on first request.
            _directConnectLock = new SemaphoreSlim(1, 1);
        }
    }

    /// <summary>
    /// Creates a new ShmControlHandler with a legacy-compatible connect timeout parameter.
    /// Equivalent to passing <c>new ShmClientTransportOptions { ConnectTimeout = connectTimeout }</c>.
    /// </summary>
    /// <param name="baseName">The base name of the shared memory segment (without _ctl suffix).</param>
    /// <param name="connectTimeout">Timeout for connection establishment. <c>null</c> uses the default (30s).</param>
    public ShmControlHandler(string baseName, TimeSpan? connectTimeout)
        : this(baseName, connectTimeout.HasValue
            ? new ShmClientTransportOptions { ConnectTimeout = connectTimeout.Value }
            : null)
    {
    }

    /// <summary>
    /// Gets the base segment name this handler connects to.
    /// </summary>
    public string BaseName => _baseName;

    /// <summary>
    /// Gets the connection pool used by this handler, or <c>null</c> when
    /// <see cref="ShmClientTransportOptions.EnableMultipleConnections"/> is <c>false</c>.
    /// Exposed for diagnostics.
    /// </summary>
    internal ShmConnectionPool? Pool => _pool;

    /// <summary>
    /// Gets whether connection pooling is enabled for this handler.
    /// Equivalent to <see cref="ShmClientTransportOptions.EnableMultipleConnections"/>.
    /// </summary>
    internal bool IsPoolingEnabled => _pool != null;

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ShmGrpcStream stream;

        if (_pool != null)
        {
            // === Pooled path ===
            // Try synchronous fast path first to avoid ValueTask→await overhead.
            if (!_pool.TryGetConnection(out var pooledConn))
            {
                pooledConn = await _pool.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                stream = pooledConn.CreateStream();
            }
            catch (Exception ex) when (
                !cancellationToken.IsCancellationRequested &&
                (ex is ShmStreamCapacityExceededException or ObjectDisposedException or InvalidOperationException))
            {
                // Connection closed, draining, or at capacity — retry on another connection.
                stream = await CreateStreamWithRetryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ShmStreamCapacityExceededException) when (cancellationToken.IsCancellationRequested)
            {
                // Cancelled while also hitting capacity — surface the cancellation,
                // not the transport-layer capacity exception.
                cancellationToken.ThrowIfCancellationRequested();
                throw; // unreachable, but satisfies compiler
            }
        }
        else
        {
            // === Pool-bypass path ===
            // Zero pool overhead: direct connection.CreateStream().
            var conn = _directConnection;
            if (conn == null || conn.IsClosed)
            {
                conn = await EnsureDirectConnectionAsync(cancellationToken).ConfigureAwait(false);
            }

            stream = conn.CreateStream();
        }

        try
        {
            return await SendOnStreamAsync(stream, request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await stream.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Slow retry path for CreateStream capacity races. Allocates timeout CTS
    /// only when needed (not on the fast path).
    /// </summary>
    private async Task<ShmGrpcStream> CreateStreamWithRetryAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(_options.ConnectTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        while (true)
        {
            if (!_pool!.TryGetConnection(out var pooledConn))
            {
                pooledConn = await _pool.GetConnectionAsync(linkedCts.Token).ConfigureAwait(false);
            }

            try
            {
                return pooledConn.CreateStream();
            }
            catch (Exception ex) when (
                !linkedCts.IsCancellationRequested &&
                (ex is ShmStreamCapacityExceededException or ObjectDisposedException or InvalidOperationException))
            {
                // Connection closed, draining, or at capacity — retry from pool.
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Timed out after {_options.ConnectTimeout.TotalSeconds:F0}s trying to create a stream.");
            }
        }
    }

    private async Task<HttpResponseMessage> SendOnStreamAsync(
        ShmGrpcStream stream, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Extract gRPC metadata
        var method = request.RequestUri?.AbsolutePath ?? "/";
        var authority = request.RequestUri?.Authority ?? "localhost";
        var metadata = ExtractMetadata(request.Headers);
        var deadline = ExtractDeadline(request.Headers);

        // Send request headers
        await stream.SendRequestHeadersAsync(method, authority, metadata, deadline).ConfigureAwait(false);

        // Send request body via a write-through stream that forwards each
        // gRPC frame directly to SHM.
        //
        // For unary calls: PushStreamContent writes one message and returns
        // immediately, so CopyToAsync completes inline (no ThreadPool needed).
        //
        // For streaming calls: PushStreamContent awaits CompleteTcs (blocks
        // until the client closes the request stream). CopyToAsync yields at
        // the first real await point, returning control to this method so we
        // can proceed to ReceiveResponseHeadersAsync. The send continues on
        // a ThreadPool thread via the natural async continuation.
        //
        // Calling SendBodyAsync directly (instead of Task.Run) eliminates
        // ~1-2ms ThreadPool scheduling delay that was the primary bottleneck
        // at low concurrency.
        if (request.Content != null)
        {
            var writeStream = new ShmGrpcRequestStream(stream);
            _ = SendBodyAsync(writeStream, request.Content, stream, cancellationToken);
        }
        else
        {
            await stream.SendHalfCloseAsync().ConfigureAwait(false);
        }

        // Wait for response headers (server sends these before processing messages).
        // May throw ShmStreamRefusedException if the server rejected the stream.
        var responseHeaders = await stream.ReceiveResponseHeadersAsync(cancellationToken).ConfigureAwait(false);

        // Create response with streaming content
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ShmControlResponseContent(stream),
            Version = new Version(2, 0)
        };
        ((ShmControlResponseContent)response.Content).SetTrailingHeaders(response.TrailingHeaders);

        // Add response headers
        if (responseHeaders.Metadata != null)
        {
            foreach (var kv in responseHeaders.Metadata)
            {
                var values = kv.Key.EndsWith("-bin", StringComparison.OrdinalIgnoreCase)
                    ? kv.Values.Select(Convert.ToBase64String)
                    : kv.Values.Select(v => System.Text.Encoding.UTF8.GetString(v));
                response.Headers.TryAddWithoutValidation(kv.Key, values);
            }
        }

        return response;
    }

    /// <summary>
    /// Sends the request body and half-close on the given stream.
    /// Runs inline for unary calls (completes before yielding) and
    /// naturally yields for streaming calls via the async state machine.
    /// </summary>
    private static async Task SendBodyAsync(
        ShmGrpcRequestStream writeStream,
        HttpContent content,
        ShmGrpcStream stream,
        CancellationToken cancellationToken)
    {
        try
        {
            await content.CopyToAsync(writeStream, cancellationToken).ConfigureAwait(false);
            await stream.SendHalfCloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException
            or ObjectDisposedException
            or RingClosedException
            or InvalidOperationException)
        {
            // Expected during normal cancellation, disposal, connection close,
            // or server-side stream reset. No action needed.
        }
        catch (Exception ex)
        {
            // Unexpected exception from HttpContent.CopyToAsync (user-supplied
            // content) or SendHalfCloseAsync. Cancel the stream so that
            // ReceiveResponseHeadersAsync (which may be waiting concurrently)
            // fails fast instead of hanging until timeout.
            System.Diagnostics.Debug.WriteLine(
                $"ShmControlHandler: unexpected error sending request body: {ex.GetType().Name}: {ex.Message}");
            try { await stream.CancelAsync().ConfigureAwait(false); }
            catch { /* best effort */ }
        }
    }

    private async Task<ShmConnection> ConnectViaControlSegmentAsync(CancellationToken cancellationToken)
    {
        var ct = cancellationToken;

        // Open the control segment
        var ctlName = _baseName + ShmConstants.ControlSegmentSuffix;
        Segment ctlSegment;
        try
        {
            ctlSegment = Segment.Open(ctlName);
        }
        catch (FileNotFoundException)
        {
            throw new InvalidOperationException($"Server not listening on segment '{_baseName}'. Control segment '{ctlName}' not found.");
        }

        try
        {
            // Wait for server to be ready
            await ctlSegment.WaitForServerAsync(ct).ConfigureAwait(false);

            // Control rings: Ring A is client→server (we write), Ring B is server→client (we read)
            var ctlTx = ctlSegment.RingA;
            var ctlRx = ctlSegment.RingB;

            // Send CONNECT request with preferred ring capacity from client options.
            // Server will negotiate: Min(clientPreferred, serverMax). Value 0 = use server default.
            var preferredRing = _options.RingCapacity;
            await WriteControlFrameAsync(ctlTx, FrameType.Connect,
                ControlWire.EncodeConnectRequest(preferredRing, preferredRing), ct).ConfigureAwait(false);

            // Read response
            var (responseHeader, responsePayload) = await ReadControlFrameAsync(ctlRx, ct).ConfigureAwait(false);

            switch (responseHeader.Type)
            {
                case FrameType.Accept:
                    var dataSegmentName = ControlWire.DecodeConnectResponse(responsePayload.Span);

                    // Open the data segment
                    var dataSegment = Segment.Open(dataSegmentName);
                    try
                    {
                        await dataSegment.WaitForServerAsync(ct).ConfigureAwait(false);

                        // Signal that client has mapped the segment
                        dataSegment.SetClientReady(true);

                        // Create and return the connection
                        return ShmConnection.FromClientSegment(dataSegmentName, dataSegment);
                    }
                    catch
                    {
                        dataSegment.Dispose();
                        throw;
                    }

                case FrameType.Reject:
                    var message = ControlWire.DecodeConnectReject(responsePayload.Span);
                    throw new InvalidOperationException($"Connection rejected by server: {message}");

                default:
                    throw new InvalidOperationException($"Unexpected response frame type: {responseHeader.Type}");
            }
        }
        finally
        {
            ctlSegment.Dispose();
        }
    }

    private static Task WriteControlFrameAsync(ShmRing ring, FrameType type, byte[] payload, CancellationToken ct)
    {
        var header = new FrameHeader
        {
            Length = (uint)payload.Length,
            StreamId = 0,
            Type = type,
            Flags = 0
        };

        var headerBytes = header.ToBytes();
        // Write header and payload (ring.Write blocks until space is available)
        ring.Write(headerBytes, ct);
        if (payload.Length > 0)
        {
            ring.Write(payload, ct);
        }

        return Task.CompletedTask;
    }

    private static Task<(FrameHeader header, Memory<byte> payload)> ReadControlFrameAsync(ShmRing ring, CancellationToken ct)
    {
        // Read frame header
        var headerBuffer = new byte[ShmConstants.FrameHeaderSize];
        ReadExact(ring, headerBuffer, ct);

        var header = FrameHeader.Parse(headerBuffer);

        // Read payload if any
        Memory<byte> payload = Memory<byte>.Empty;
        if (header.Length > 0)
        {
            if (header.Length > ShmConstants.MinRingCapacity)
            {
                throw new InvalidDataException($"Control frame payload {header.Length} exceeds maximum.");
            }

            var payloadBuffer = new byte[header.Length];
            ReadExact(ring, payloadBuffer, ct);
            payload = payloadBuffer;
        }

        return Task.FromResult((header, payload));
    }

    private static void ReadExact(ShmRing ring, Span<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            read += ring.Read(buffer[read..], ct);
        }
    }

    private static Metadata? ExtractMetadata(HttpRequestHeaders headers)
    {
        var metadata = new Metadata();

        foreach (var header in headers)
        {
            // Skip pseudo-headers and standard HTTP headers
            if (header.Key.StartsWith(':') ||
                header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in header.Value)
            {
                if (header.Key.EndsWith("-bin", StringComparison.OrdinalIgnoreCase))
                {
                    // Binary metadata
                    metadata.Add(new Metadata.Entry(header.Key, Convert.FromBase64String(value)));
                }
                else
                {
                    metadata.Add(new Metadata.Entry(header.Key, value));
                }
            }
        }

        return metadata.Count > 0 ? metadata : null;
    }

    private static DateTime? ExtractDeadline(HttpRequestHeaders headers)
    {
        if (headers.TryGetValues("grpc-timeout", out var values))
        {
            var timeout = values.FirstOrDefault();
            if (!string.IsNullOrEmpty(timeout))
            {
                // Parse timeout format: <value><unit> where unit is H/M/S/m/u/n
                if (TryParseGrpcTimeout(timeout, out var duration))
                {
                    return DateTime.UtcNow + duration;
                }
            }
        }
        return null;
    }

    private static bool TryParseGrpcTimeout(string timeout, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrEmpty(timeout) || timeout.Length < 2)
            return false;

        var unit = timeout[^1];
        if (!long.TryParse(timeout[..^1], out var value))
            return false;

        try
        {
            duration = unit switch
            {
                'H' => TimeSpan.FromHours(value),
                'M' => TimeSpan.FromMinutes(value),
                'S' => TimeSpan.FromSeconds(value),
                'm' => TimeSpan.FromMilliseconds(value),
                'u' => TimeSpan.FromMicroseconds(value),
                'n' => TimeSpan.FromTicks(value / 100),
                _ => TimeSpan.Zero
            };
        }
        catch (OverflowException)
        {
            return false;
        }

        return duration > TimeSpan.Zero;
    }

    /// <summary>
    /// Lazily establishes the single direct connection via the control segment.
    /// Used when <see cref="ShmClientTransportOptions.EnableMultipleConnections"/> is <c>false</c>.
    /// Serialized by <c>_directConnectLock</c> to prevent concurrent connect attempts.
    /// </summary>
    private async Task<ShmConnection> EnsureDirectConnectionAsync(CancellationToken cancellationToken)
    {
        System.Diagnostics.Debug.Assert(_directConnectLock != null, "EnsureDirectConnectionAsync called with pooling enabled");

        await _directConnectLock!.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Abort if handler was disposed while we waited for the lock.
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Double-check after acquiring the lock.
            var existing = _directConnection;
            if (existing != null && !existing.IsClosed)
            {
                return existing;
            }

            // Dispose the stale connection if it was closed.
            if (existing != null)
            {
                _directConnection = null;
                try { await existing.DisposeAsync().ConfigureAwait(false); } catch { }
            }

            var conn = await ConnectViaControlSegmentAsync(cancellationToken).ConfigureAwait(false);

            // Re-check disposed after the potentially long connect.
            if (_disposed)
            {
                await conn.DisposeAsync().ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(ShmControlHandler));
            }

            _directConnection = conn;
            return conn;
        }
        finally
        {
            // Only release if not disposed — Dispose(bool) may have already
            // disposed the semaphore. Release on a disposed SemaphoreSlim
            // throws ObjectDisposedException, which would mask the real error.
            try { _directConnectLock.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                if (_pool != null)
                {
                    // Synchronously cancel any in-flight connection factory calls
                    // so that ring reads/writes on the control segment unblock
                    // immediately. This prevents SPSC violations when a new handler
                    // is created for the same segment name while this one’s pool
                    // is still asynchronously disposing.
                    _pool.CancelPendingConnections();

                    // ShmConnectionPool.DisposeAsync is genuinely async (awaits pending
                    // connection disposes). HttpMessageHandler.Dispose is sync-only,
                    // so we schedule the async cleanup and avoid blocking the caller.
                    // The pool marks itself as disposed immediately (preventing new
                    // GetConnectionAsync calls) before the async portion runs.
                    _ = DisposePoolAsync();
                }
                else
                {
                    // Single-connection mode: dispose the direct connection.
                    var conn = _directConnection;
                    _directConnection = null;
                    if (conn != null)
                    {
                        _ = DisposeDirectConnectionAsync(conn);
                    }
                    _directConnectLock?.Dispose();
                }
            }
        }
        base.Dispose(disposing);
    }

    private async Task DisposePoolAsync()
    {
        try
        {
            await _pool!.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ShmControlHandler: pool dispose error: {ex.Message}");
        }
    }

    private static async Task DisposeDirectConnectionAsync(ShmConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ShmControlHandler: direct connection dispose error: {ex.Message}");
        }
    }
}

/// <summary>
/// Write-through stream that reassembles gRPC-framed messages from
/// arbitrary WriteAsync chunks and forwards each complete message to
/// <see cref="ShmGrpcStream.SendMessageAsync"/>.  Although grpc-dotnet
/// typically writes a full [compressed:1][length:4][data] frame per call,
/// <see cref="Stream.WriteAsync"/> does not guarantee frame alignment,
/// so this class buffers partial headers and bodies defensively.
/// </summary>
internal sealed class ShmGrpcRequestStream : Stream
{
    private readonly ShmGrpcStream _shmStream;
    private byte[]? _headerBuf;
    private int _headerBufLen;
    private byte[]? _bodyBuf;
    private int _bodyBufLen;
    private int _bodyExpected;

    public ShmGrpcRequestStream(ShmGrpcStream shmStream)
    {
        _shmStream = shmStream;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // grpc-dotnet typically writes one complete gRPC frame per WriteAsync call:
        // [compressed:1][length:4][payload].  However, Stream.WriteAsync does not
        // guarantee frame-aligned writes, so we buffer partial headers and bodies
        // defensively to handle arbitrary split points.
        var remaining = buffer;

        // Resume partial body from previous write
        if (_bodyExpected > 0 && _bodyBufLen < _bodyExpected)
        {
            var needed = _bodyExpected - _bodyBufLen;
            var toCopy = Math.Min(needed, remaining.Length);
            remaining.Slice(0, toCopy).CopyTo(_bodyBuf.AsMemory(_bodyBufLen));
            _bodyBufLen += toCopy;
            remaining = remaining.Slice(toCopy);

            if (_bodyBufLen < _bodyExpected)
            {
                return; // Still incomplete
            }

            await _shmStream.SendMessageAsync(_bodyBuf.AsMemory(0, _bodyExpected), cancellationToken).ConfigureAwait(false);
            _bodyBufLen = 0;
            _bodyExpected = 0;
        }

        // Resume partial header from previous write
        if (_headerBufLen > 0)
        {
            var needed = 5 - _headerBufLen;
            if (remaining.Length < needed)
            {
                remaining.CopyTo(_headerBuf.AsMemory(_headerBufLen));
                _headerBufLen += remaining.Length;
                return;
            }

            remaining.Slice(0, needed).CopyTo(_headerBuf.AsMemory(_headerBufLen));
            _headerBufLen = 0;
            remaining = remaining.Slice(needed);

            var hdrSpan = _headerBuf.AsSpan(0, 5);
            if (hdrSpan[0] != 0) throw new NotSupportedException("Compression not yet supported");
            var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hdrSpan.Slice(1));

            if (remaining.Length < length)
            {
                // Partial body — buffer it
                _bodyBuf ??= new byte[length];
                if (_bodyBuf.Length < length) _bodyBuf = new byte[length];
                remaining.CopyTo(_bodyBuf);
                _bodyBufLen = remaining.Length;
                _bodyExpected = length;
                return;
            }

            await _shmStream.SendMessageAsync(remaining.Slice(0, length), cancellationToken).ConfigureAwait(false);
            remaining = remaining.Slice(length);
        }

        // Process complete frames in the remaining buffer
        while (remaining.Length > 0)
        {
            if (remaining.Length < 5)
            {
                _headerBuf ??= new byte[5];
                remaining.CopyTo(_headerBuf);
                _headerBufLen = remaining.Length;
                return;
            }

            var span = remaining.Span;
            if (span[0] != 0) throw new NotSupportedException("Compression not yet supported");
            var msgLen = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(span.Slice(1));

            if (remaining.Length < 5 + msgLen)
            {
                // Partial body — buffer header + available body
                _bodyExpected = msgLen;
                _bodyBuf ??= new byte[msgLen];
                if (_bodyBuf.Length < msgLen) _bodyBuf = new byte[msgLen];
                var available = remaining.Length - 5;
                remaining.Slice(5, available).CopyTo(_bodyBuf);
                _bodyBufLen = available;
                return;
            }

            await _shmStream.SendMessageAsync(remaining.Slice(5, msgLen), cancellationToken).ConfigureAwait(false);
            remaining = remaining.Slice(5 + msgLen);
        }
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Use WriteAsync.");

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// HttpContent implementation that reads response messages from a ShmGrpcStream.
/// grpc-dotnet calls ReadAsStreamAsync (→ CreateContentReadStreamAsync) to get a
/// stream it can incrementally read gRPC-framed messages from.  We return a
/// lightweight wrapper that reads from ShmGrpcStream.ReceiveMessageBuffersAsync()
/// directly on the caller's thread — no Pipe, no Task.Run, no resource
/// accumulation across thousands of calls.
/// </summary>
internal sealed class ShmControlResponseContent : HttpContent
{
    private readonly ShmGrpcStream _stream;
    private HttpHeaders? _trailingHeaders;

    public ShmControlResponseContent(ShmGrpcStream stream)
    {
        _stream = stream;
        Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
    }

    internal void SetTrailingHeaders(HttpHeaders trailingHeaders)
    {
        _trailingHeaders = trailingHeaders;
    }

    protected override Task<Stream> CreateContentReadStreamAsync()
    {
        return Task.FromResult<Stream>(new ShmGrpcResponseStream(_stream, this));
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        var header = new byte[5];
        header[0] = 0;

        await foreach (var message in _stream.ReceiveMessageBuffersAsync(cancellationToken))
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1), (uint)message.Length);
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }

        ApplyTrailers();
    }

    internal void ApplyTrailers()
    {
        if (_stream.Trailers != null && _trailingHeaders != null)
        {
            var trailers = _stream.Trailers;
            _trailingHeaders.TryAddWithoutValidation("grpc-status", ((int)trailers.GrpcStatusCode).ToString());
            if (!string.IsNullOrEmpty(trailers.GrpcStatusMessage))
            {
                _trailingHeaders.TryAddWithoutValidation("grpc-message", Uri.EscapeDataString(trailers.GrpcStatusMessage));
            }
            if (trailers.Metadata != null)
            {
                foreach (var kv in trailers.Metadata)
                {
                    var values = kv.Key.EndsWith("-bin", StringComparison.OrdinalIgnoreCase)
                        ? kv.Values.Select(Convert.ToBase64String)
                        : kv.Values.Select(v => System.Text.Encoding.UTF8.GetString(v));
                    _trailingHeaders.TryAddWithoutValidation(kv.Key, values);
                }
            }
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stream.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// A read-only stream that yields gRPC-framed messages from a ShmGrpcStream
/// on the caller's thread (no background pump, no Pipe).  Each ReadAsync call
/// writes the gRPC 5-byte header + message data directly into the caller's
/// buffer — zero intermediate allocations on the hot path.
///
/// Previous implementation allocated <c>new byte[5 + message.Length]</c> per
/// message, causing LOH allocations (and Gen2 GC pressure) at ≥85 KB payloads.
/// </summary>
internal sealed class ShmGrpcResponseStream : Stream
{
    private readonly ShmGrpcStream _shmStream;
    private readonly ShmControlResponseContent _content;
    // Current message being served (raw payload from SHM ring, pooled buffer).
    private ReadOnlyMemory<byte> _message;
    private int _messageLength;
    // How many bytes of the *logical* gRPC frame (5-byte header + message) have been served.
    private int _frameOffset;
    private bool _hasMessage;
    private bool _completed;

    // State for deferred buffer release across calls to ReceiveNextMessageBufferAsync.
    private InboundFrame _previousFrame;

    public ShmGrpcResponseStream(ShmGrpcStream shmStream, ShmControlResponseContent content)
    {
        _shmStream = shmStream;
        _content = content;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;
        if (_completed) return 0;

        // If we're mid-message, continue serving it.
        if (_hasMessage && _frameOffset < 5 + _messageLength)
        {
            return ServeCurrentMessage(buffer.Span);
        }

        // Receive the next complete message. Each call accepts the caller's
        // cancellation token directly — no latched enumerator token.
        var (mem, frame, eos) = await _shmStream.ReceiveNextMessageBufferAsync(
            _previousFrame, cancellationToken).ConfigureAwait(false);

        if (eos)
        {
            _previousFrame = default;
            _completed = true;
            _content.ApplyTrailers();
            return 0;
        }

        _previousFrame = frame;
        _message = mem;
        _messageLength = mem.Length;
        _frameOffset = 0;
        _hasMessage = true;

        return ServeCurrentMessage(buffer.Span);
    }

    /// <summary>
    /// Writes portions of the logical gRPC frame [compressed:1][length:4][data]
    /// directly into <paramref name="dest"/> without any intermediate allocation.
    /// </summary>
    private int ServeCurrentMessage(Span<byte> dest)
    {
        var totalFrameLen = 5 + _messageLength;
        int written = 0;

        // --- Serve the 5-byte gRPC header ---
        if (_frameOffset < 5)
        {
            Span<byte> hdr = stackalloc byte[5];
            hdr[0] = 0; // not compressed
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(hdr.Slice(1), (uint)_messageLength);

            int hdrStart = _frameOffset;
            int hdrRemaining = 5 - hdrStart;
            int hdrToCopy = Math.Min(hdrRemaining, dest.Length);
            hdr.Slice(hdrStart, hdrToCopy).CopyTo(dest);
            written += hdrToCopy;
            _frameOffset += hdrToCopy;

            if (written >= dest.Length)
                return written;
        }

        // --- Serve message data ---
        if (_frameOffset >= 5 && _frameOffset < totalFrameLen)
        {
            int msgStart = _frameOffset - 5;
            int msgRemaining = _messageLength - msgStart;
            int toCopy = Math.Min(msgRemaining, dest.Length - written);
            _message.Span.Slice(msgStart, toCopy).CopyTo(dest.Slice(written));
            written += toCopy;
            _frameOffset += toCopy;
        }

        return written;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Use ReadAsync.");

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Release any held pooled buffer from the last received message.
            _previousFrame.ReturnToPool();
            _previousFrame = default;
        }
        base.Dispose(disposing);
    }
}
