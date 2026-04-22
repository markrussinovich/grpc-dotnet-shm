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
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;
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
    private int _disposed;

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
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

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

    private static async Task<HttpResponseMessage> SendOnStreamAsync(
        ShmGrpcStream stream, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var method = request.RequestUri?.AbsolutePath ?? "/";
        var authority = request.RequestUri?.Authority ?? "localhost";
        var metadata = ExtractMetadata(request.Headers);
        var deadline = ExtractDeadline(request.Headers);

        await stream.SendRequestHeadersAsync(method, authority, metadata, deadline).ConfigureAwait(false);

        if (request.Content != null)
        {
            var writeStream = new ShmGrpcRequestStream(stream);
            _ = SendBodyAsync(writeStream, request.Content, stream, cancellationToken);
        }
        else
        {
            await stream.SendHalfCloseAsync().ConfigureAwait(false);
        }

        var responseHeaders = await stream.ReceiveResponseHeadersAsync(cancellationToken).ConfigureAwait(false);

        var responseContent = new ShmControlResponseContent(stream);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = responseContent,
            Version = new Version(2, 0)
        };
        responseContent.SetTrailingHeaders(response.TrailingHeaders);

        // Add response headers
        if (responseHeaders.Metadata != null)
        {
            foreach (var kv in responseHeaders.Metadata)
            {
                AddMetadataToHeaders(response.Headers, kv);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Store the real exception so ReceiveResponseHeadersAsync can
            // surface it as InnerException instead of generic "Stream closed".
            stream.SetSendFailure(ex);
            System.Diagnostics.Debug.WriteLine(
                $"ShmControlHandler.SendBodyAsync failed: {ex}");
            try { await stream.CancelAsync().ConfigureAwait(false); }
            catch { /* best effort */ }
            stream.CompleteInbound();
        }
        catch (OperationCanceledException ex)
        {
            stream.SetSendFailure(ex);
            try { await stream.CancelAsync().ConfigureAwait(false); }
            catch { /* best effort */ }
            stream.CompleteInbound();
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
                ControlWire.EncodeConnectRequest(preferredRing, preferredRing, _options.SingleStreamMode), ct).ConfigureAwait(false);

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
                        var conn = ShmConnection.FromClientSegment(dataSegmentName, dataSegment);
                        if (_options.SingleStreamMode)
                        {
                            conn.ZeroCopyRead = true;
                            conn.FrameWriter?.EnableSingleStreamMode();
                        }
                        return conn;
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

    internal static void AddMetadataToHeaders(HttpHeaders headers, MetadataKV kv)
    {
        var isBin = kv.Key.EndsWith("-bin", StringComparison.OrdinalIgnoreCase);
        foreach (var v in kv.Values)
        {
            headers.TryAddWithoutValidation(kv.Key,
                isBin ? Convert.ToBase64String(v) : System.Text.Encoding.UTF8.GetString(v));
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
                    // Binary metadata — skip malformed base64 instead of crashing.
                    try
                    {
                        metadata.Add(new Metadata.Entry(header.Key, Convert.FromBase64String(value)));
                    }
                    catch (FormatException) { /* malformed base64 — skip */ }
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
            ObjectDisposedException.ThrowIf(_disposed != 0, this);

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
            if (_disposed != 0)
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            base.Dispose(disposing);
            return;
        }

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
internal sealed class ShmGrpcRequestStream : Stream, Grpc.Net.Client.IDirectMessageWriter
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

    /// <summary>
    /// IDirectMessageWriter: serialize into a transport-owned pooled buffer
    /// and enqueue non-blocking, bypassing gRPC framing and the framework's
    /// SerializationContext buffer. The pooled buffer is returned to
    /// ArrayPool by the WriterLoop after the ring write completes.
    /// </summary>
    public Task WriteSerializedMessageAsync<TMessage>(
        TMessage message,
        Action<TMessage, Grpc.Core.SerializationContext> serializer,
        CancellationToken cancellationToken)
    {
        // Fast path: for protobuf IMessage types in singleStreamMode,
        // serialize directly into the ring buffer (zero intermediate buffer)
        // using IMessage.WriteTo. This bypasses the provided serializer
        // delegate entirely — IMessage.WriteTo produces the canonical
        // protobuf binary encoding, identical to the default
        // ContextualSerializer generated by protoc/Grpc.Tools.
        //
        // Trade-off: if a user registers a custom contextual serializer on
        // an IMessage type (e.g., an encryption or compression layer that
        // modifies the wire payload), it will NOT be called on this path.
        // This is an intentional design choice: custom marshallers on
        // IMessage types are extremely rare in practice, and the
        // performance benefit (eliminating one full-message copy) justifies
        // the specialization. Non-IMessage types always go through the
        // serializer delegate below.
        if (_shmStream.Connection.SingleStreamMode
            && _shmStream.Connection.ActiveStreamCount <= 1
            && message is Google.Protobuf.IMessage protoMsg)
        {
            var writer = _shmStream.Connection.FrameWriter;
            if (writer != null)
            {
                var size = protoMsg.CalculateSize();
                if (size > 0 && writer.TryPauseWriterLoop())
                {
                    try
                    {
                        writer.WriteInlineDirectMultiFrame(_shmStream.StreamId, size, protoMsg, 0, default);
                        return Task.CompletedTask;
                    }
                    finally
                    {
                        writer.ResumeWriterLoop();
                    }
                }
            }
        }

        // Standard path: serialize via the provided marshaller delegate
        // into a pooled buffer, then send via TryPause/ExecuteInline/queue.
        var ctx = new DirectWriteSerializationContext(_shmStream);
        serializer(message, ctx);
        return ctx.SendResult(cancellationToken);
    }

    /// <summary>
    /// Minimal SerializationContext that writes directly into a pooled buffer
    /// with a 5-byte gRPC LPM header reserved at offset 0. Implements
    /// IBufferWriter so protobuf can serialize using the fast WriteContext path.
    /// </summary>
    private sealed class DirectWriteSerializationContext : Grpc.Core.SerializationContext, IBufferWriter<byte>
    {
        private readonly ShmGrpcStream _stream;
        private byte[]? _buffer;
        private int _position;
        private int _payloadLength;

        public DirectWriteSerializationContext(ShmGrpcStream stream) => _stream = stream;

        public override void SetPayloadLength(int payloadLength)
        {
            _payloadLength = payloadLength;
        }

        public override IBufferWriter<byte> GetBufferWriter()
        {
            if (_buffer == null && _payloadLength > 0)
            {
                _buffer = ArrayPool<byte>.Shared.Rent(_payloadLength);
            }

            return this;
        }

        public override void Complete(byte[] payload)
        {
            // Old-style Complete(byte[]): copy into our pooled buffer.
            // Return any previously-rented buffer from GetBufferWriter().
            if (_buffer != null)
                ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = ArrayPool<byte>.Shared.Rent(payload.Length);
            payload.AsSpan().CopyTo(_buffer);
            _position = payload.Length;
        }

        public override void Complete()
        {
        }

        public void Advance(int count) => _position += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureBuffer(sizeHint);
            return _buffer.AsMemory(_position);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureBuffer(sizeHint);
            return _buffer.AsSpan(_position);
        }

        private void EnsureBuffer(int sizeHint)
        {
            var needed = _position + Math.Max(sizeHint, 1);
            if (_buffer == null)
            {
                _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(needed, 256));
            }
            else if (needed > _buffer.Length)
            {
                var newBuf = ArrayPool<byte>.Shared.Rent(needed);
                _buffer.AsSpan(0, _position).CopyTo(newBuf);
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = newBuf;
            }
        }

        internal Task SendResult(CancellationToken cancellationToken)
        {
            if (_buffer == null || _position == 0)
            {
                return _stream.SendMessageAsync(ReadOnlyMemory<byte>.Empty, cancellationToken);
            }

            // In singleStreamMode with one active stream, bypass the queue.
            // - ≤ ringCapacity: TryPauseWriterLoop or ExecuteInline
            //   (handler writes ring directly or via WriterLoop callback)
            // - > ringCapacity: falls through to queued SendMessageZeroCopyAsync
            if (_stream.Connection.SingleStreamMode && _stream.Connection.ActiveStreamCount <= 1)
            {
                var writer = _stream.Connection.FrameWriter;
                if (writer != null)
                {
                    var ringCap = (long)_stream.Connection.TxRing.Capacity;
                    if (_position <= ringCap && writer.TryPauseWriterLoop())
                    {
                        var buf = _buffer;
                        _buffer = null;
                        try
                        {
                            writer.WriteInline(_stream.StreamId, buf.AsSpan(0, _position), 0, default);
                        }
                        finally
                        {
                            writer.ResumeWriterLoop();
                            ArrayPool<byte>.Shared.Return(buf);
                        }
                        return Task.CompletedTask;
                    }

                    // Large message or TryPause failed: ExecuteInline.
                    if (_position <= ringCap)
                    {
                        var buf = _buffer;
                        var bufLen = _position;
                        var streamId = _stream.StreamId;
                        _buffer = null;
                        try
                        {
                            writer.ExecuteInline(() =>
                            {
                                writer.WriteInline(streamId, buf.AsSpan(0, bufLen), 0, default);
                            });
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(buf);
                        }
                        return Task.CompletedTask;
                    }
                }
            }

            // Fallback: transfer buffer ownership to SendMessageZeroCopyAsync —
            // WriterLoop returns it to ArrayPool after ring write.
            return _stream.SendMessageZeroCopyAsync(
                _buffer.AsMemory(0, _position), _buffer, cancellationToken);
        }
    }

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
internal sealed class ShmControlResponseContent : HttpContent, Grpc.Net.Client.IDirectMessageReader
{
    private readonly ShmGrpcStream _stream;
    private HttpHeaders? _trailingHeaders;
    private InboundFrame _currentFrame;
    // Multi-frame accumulation: connection-level cached buffer.
    // Borrowed from ShmConnection.CachedReadBuffer on construction,
    // returned on Dispose to avoid LOH churn on repeated Unary calls.
    private byte[]? _assembled;
    private int _assembledPos;

    public ShmControlResponseContent(ShmGrpcStream stream)
    {
        _stream = stream;
        Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        // Borrow cached read buffer from connection (may be null on first call).
        _assembled = stream.Connection.BorrowReadBuffer();
    }

    internal void SetTrailingHeaders(HttpHeaders trailingHeaders)
    {
        _trailingHeaders = trailingHeaders;
    }

    /// <summary>
    /// Direct message reader: returns the next complete protobuf payload
    /// without gRPC framing or Stream.ReadAsync overhead.
    /// Uses sync fast path when data is already in the channel to avoid
    /// async state machine allocation (~200ns per await).
    /// </summary>
#if SHM_TRACE
    // Profiling counters — compile with /d:SHM_TRACE to enable.
    internal static long _drSyncHit, _drSlowPath, _drSlowTicks, _drWaitTicks, _drProcessTicks;
#endif

    public ValueTask<(ReadOnlySequence<byte> Payload, bool EndOfStream)> ReadNextMessageAsync(
        CancellationToken cancellationToken)
    {
        _currentFrame.ReturnToPool();
        _currentFrame = default;
        // Reset assembled buffer position (keep buffer for reuse).
        _assembledPos = 0;

        // Fast path: try sync read.
        while (_stream.TryReceiveFrame(out var frame))
        {
#if SHM_TRACE
            Interlocked.Increment(ref _drSyncHit);
#endif
            var result = ProcessReceivedFrame(frame);
            // Empty payload + not EOS = continuation frame (More), keep reading
            if (result.Payload.Length == 0 && !result.EndOfStream)
                continue;
            return new ValueTask<(ReadOnlySequence<byte>, bool)>(result);
        }

#if SHM_TRACE
        Interlocked.Increment(ref _drSlowPath);
#endif
        return ReadNextMessageSlowAsync(cancellationToken);
    }

    private (ReadOnlySequence<byte> Payload, bool EndOfStream) ProcessReceivedFrame(InboundFrame frame)
    {
        switch (frame.Type)
        {
            case FrameType.Message:
                // Multi-frame: copy into single assembled buffer.
                if ((frame.Flags & MessageFlags.More) != 0)
                {
                    if (_assembled == null)
                    {
                        _assembled = ArrayPool<byte>.Shared.Rent(frame.Length * 4);
                        _assembledPos = 0;
                    }
                    else if (_assembledPos + frame.Length > _assembled.Length)
                    {
                        var newBuf = ArrayPool<byte>.Shared.Rent(_assembled.Length * 2);
                        _assembled.AsSpan(0, _assembledPos).CopyTo(newBuf);
                        ArrayPool<byte>.Shared.Return(_assembled);
                        _assembled = newBuf;
                    }
                    frame.Memory.Span.CopyTo(_assembled.AsSpan(_assembledPos));
                    _assembledPos += frame.Length;
                    frame.ReturnToPool();
                    return (ReadOnlySequence<byte>.Empty, false);
                }

                // Final frame or single-frame message.
                if (_assembledPos > 0)
                {
                    // Multi-frame final: copy last chunk.
                    if (_assembledPos + frame.Length > _assembled!.Length)
                    {
                        var newBuf = ArrayPool<byte>.Shared.Rent(_assembledPos + frame.Length);
                        _assembled.AsSpan(0, _assembledPos).CopyTo(newBuf);
                        ArrayPool<byte>.Shared.Return(_assembled);
                        _assembled = newBuf;
                    }
                    frame.Memory.Span.CopyTo(_assembled.AsSpan(_assembledPos));
                    _assembledPos += frame.Length;
                    frame.ReturnToPool();

                    var eos = (frame.Flags & MessageFlags.EndStream) != 0;
                    if (eos) _stream.MarkHalfCloseReceived();
                    return (new ReadOnlySequence<byte>(_assembled.AsMemory(0, _assembledPos)), eos);
                }
                else
                {
                    // Single frame — direct reference (zero-copy for pre-committed).
                    _currentFrame = frame;
                    var eos2 = (frame.Flags & MessageFlags.EndStream) != 0;
                    if (eos2) _stream.MarkHalfCloseReceived();
                    return (new ReadOnlySequence<byte>(frame.Memory), eos2);
                }

            case FrameType.HalfClose:
                frame.ReturnToPool();
                _stream.MarkHalfCloseReceived();
                ApplyTrailers();
                return (ReadOnlySequence<byte>.Empty, true);

            case FrameType.Trailers:
                _stream.SetTrailers(frame);
                frame.ReturnToPool();
                _stream.MarkHalfCloseReceived();
                ApplyTrailers();
                return (ReadOnlySequence<byte>.Empty, true);

            default:
                frame.ReturnToPool();
                return (ReadOnlySequence<byte>.Empty, true);
        }
    }

    private async ValueTask<(ReadOnlySequence<byte> Payload, bool EndOfStream)> ReadNextMessageSlowAsync(
        CancellationToken cancellationToken)
    {
        var ct = cancellationToken.CanBeCanceled
            ? cancellationToken
            : _stream.DisposeCancellationToken;

        try
        {
            while (true)
            {
#if SHM_TRACE
                var _wt0 = Stopwatch.GetTimestamp();
#endif
                if (!await _stream.WaitForFrameAsync(ct).ConfigureAwait(false))
                {
                    var sendEx = _stream.SendFailure;
                    if (sendEx != null)
                        throw new InvalidOperationException("Request body send failed during streaming", sendEx);
                    ApplyTrailers();
                    return (ReadOnlySequence<byte>.Empty, true);
                }

                if (_stream.TryReceiveFrame(out var frame))
                {
                    var result = ProcessReceivedFrame(frame);
                    if (result.Payload.Length == 0 && !result.EndOfStream)
                        continue;
#if SHM_TRACE
                    var _wt2 = Stopwatch.GetTimestamp();
                    Interlocked.Add(ref _drSlowTicks, _wt2 - _wt0);
                    Interlocked.Add(ref _drWaitTicks, _wt2 - _wt0);
                    Interlocked.Add(ref _drProcessTicks, 0);
#endif
                    return result;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return (ReadOnlySequence<byte>.Empty, true);
        }
        catch (ChannelClosedException)
        {
            var sendEx2 = _stream.SendFailure;
            if (sendEx2 != null)
                throw new InvalidOperationException("Request body send failed during streaming", sendEx2);
            ApplyTrailers();
            return (ReadOnlySequence<byte>.Empty, true);
        }
    }

    public void ReleaseCurrentMessage()
    {
        _currentFrame.ReturnToPool();
        _currentFrame = default;
        // Keep assembled buffer for reuse (returned to connection in Dispose).
        _assembledPos = 0;
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

            if (message.Length <= 65536)
            {
                var combined = System.Buffers.ArrayPool<byte>.Shared.Rent(5 + message.Length);
                try
                {
                    header.CopyTo(combined, 0);
                    message.Span.CopyTo(combined.AsSpan(5));
                    await stream.WriteAsync(combined.AsMemory(0, 5 + message.Length), cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(combined);
                }
            }
            else
            {
                await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }

        ApplyTrailers();
    }

    internal void ApplyTrailers()
    {
        if (_stream.Trailers != null && _trailingHeaders != null)
        {
            var trailers = _stream.Trailers;
            _trailingHeaders.TryAddWithoutValidation("grpc-status", ((int)trailers.GrpcStatusCode).ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(trailers.GrpcStatusMessage))
            {
                _trailingHeaders.TryAddWithoutValidation("grpc-message", Uri.EscapeDataString(trailers.GrpcStatusMessage));
            }
            if (trailers.Metadata != null)
            {
                foreach (var kv in trailers.Metadata)
                {
                    ShmControlHandler.AddMetadataToHeaders(_trailingHeaders, kv);
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
            if (_assembled != null)
            {
                // Return to connection cache instead of ArrayPool.
                _stream.Connection.ReturnReadBuffer(_assembled);
                _assembled = null;
            }
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
    private bool _completedAfterCurrentMessage;

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

        // The previous message carried EndStream — stream is done after it.
        if (_completedAfterCurrentMessage)
        {
            _previousFrame.ReturnToPool();
            _previousFrame = default;
            _completed = true;
            _content.ApplyTrailers();
            return 0;
        }

        // Receive the next complete message. Each call accepts the caller's
        // cancellation token directly — no latched enumerator token.
        var (mem, frame, eos) = await _shmStream.ReceiveNextMessageBufferAsync(
            _previousFrame, cancellationToken).ConfigureAwait(false);

        if (eos)
        {
            if (mem.Length == 0)
            {
                _previousFrame = default;
                _completed = true;
                _content.ApplyTrailers();
                return 0;
            }

            // EndStream with a final message (e.g. SendMessageAndHalfCloseAsync):
            // consume the message first, mark completed after it's fully served.
            _previousFrame = frame;
            _message = mem;
            _messageLength = mem.Length;
            _frameOffset = 0;
            _hasMessage = true;
            _completedAfterCurrentMessage = true;
            return ServeCurrentMessage(buffer.Span);
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
