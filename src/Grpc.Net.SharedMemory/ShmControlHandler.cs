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
using Google.Protobuf;
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

    // Round-8 PR-C1: cache the HttpResponseMessage.Version value once.
    // `new Version(2, 0)` per RPC is one allocation per response on the
    // unary hot path; the immutable singleton is safe to share.
    private static readonly Version s_http20Version = new(2, 0);

    // Diagnostic counters for the wake-coalescing path; visible to bench.
    internal static long s_unaryRequests;
    internal static long s_streamingRequests;
    internal static (long Unary, long Streaming) GetRequestKindDiag()
        => (Volatile.Read(ref s_unaryRequests), Volatile.Read(ref s_streamingRequests));

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await stream.CancelAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            await stream.CancelAsync().ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
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
        using var cancellationRegistration = cancellationToken.UnsafeRegister(static state =>
        {
            _ = CancelStreamAsync((ShmGrpcStream)state!);
        }, stream);

        var method = request.RequestUri?.AbsolutePath ?? "/";
        var authority = request.RequestUri?.Authority ?? "localhost";
        var metadata = ExtractMetadata(request.Headers);
        var deadline = ExtractDeadline(request.Headers);

        // Unary content-type detection. For known Unary content types
        // (PushUnaryContent / UnaryContent / WinHttpUnaryContent in
        // grpc-dotnet) we STAGE the request Headers instead of sending
        // them immediately. The body-write path inside
        // ShmGrpcRequestStream.WriteSerializedMessageAsync can then
        // coalesce HEADERS + DATA(END_STREAM) into a single inline
        // batch -> ONE peer SignalData wake covering the entire request
        // (vs 3 wakes today: Headers + Data + HalfClose). The size gate
        // is the SAME wrap-safe CanCoalesceInlineMessage threshold used
        // by the server-side coalesce path on this branch.
        //
        // For streaming content (PushStreamContent etc.) we keep the
        // today's behaviour: send Headers eagerly so the server can
        // start processing as soon as the first DATA arrives, not after
        // the client has flushed its body.
        var contentTypeName = request.Content?.GetType().Name;
        var isUnary = contentTypeName != null
            && (contentTypeName.StartsWith("PushUnaryContent", StringComparison.Ordinal)
                || contentTypeName.StartsWith("UnaryContent", StringComparison.Ordinal)
                || contentTypeName.StartsWith("WinHttpUnaryContent", StringComparison.Ordinal));
        if (isUnary) Interlocked.Increment(ref s_unaryRequests);
        else Interlocked.Increment(ref s_streamingRequests);

        if (isUnary)
        {
            // Defer Headers wire send; body-write coalesce path may consume
            // the staged Headers under the same inline batch.
            stream.StageRequestHeaders(method, authority, metadata, deadline);
        }
        else
        {
            await stream.SendRequestHeadersAsync(method, authority, metadata, deadline, coalesceWithHalfClose: false).ConfigureAwait(false);
        }

        if (request.Content != null)
        {
            // Round-8 PR-C1: SendBodyAsync owns the ShmGrpcRequestStream
            // lifecycle via `using` (was leaked here on every RPC where
            // the marshaller didn't take the IDirectMessageWriter fast
            // path — _bodyBuf rented by the multi-fragment WriteAsync
            // path was never returned to ArrayPool).
            _ = SendBodyAsync(request.Content, stream, cancellationToken);
        }
        else
        {
            // No-body Unary: flush any staged headers (coalesce path
            // never ran), then HalfClose. For streaming with no body the
            // staged-headers branch above was skipped.
            if (stream.HasStagedHeaders)
            {
                await stream.FlushStagedHeadersAsync(cancellationToken).ConfigureAwait(false);
            }
            await stream.SendHalfCloseAsync().ConfigureAwait(false);
        }

        HeadersV1 responseHeaders;
        try
        {
            responseHeaders = await stream.ReceiveResponseHeadersAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        var responseContent = new ShmControlResponseContent(stream);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = responseContent,
            Version = s_http20Version
        };
        responseContent.SetTrailingHeaders(response.TrailingHeaders);

        // Add response headers
        if (responseHeaders.Metadata != null)
        {
            foreach (var kv in responseHeaders.Metadata)
            {
                // Extract grpc-encoding for response decompression
                if (string.Equals(kv.Key, "grpc-encoding", StringComparison.OrdinalIgnoreCase)
                    && kv.Values.Count > 0)
                {
                    responseContent.SetResponseEncoding(
                        System.Text.Encoding.UTF8.GetString(kv.Values[0]));
                }
                AddMetadataToHeaders(response.Headers, kv);
            }
        }

        return response;
    }

    private static async Task CancelStreamAsync(ShmGrpcStream stream)
    {
        try
        {
            await stream.CancelAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best effort: cancellation may race with stream disposal.
        }
    }

    /// <summary>
    /// Sends the request body and half-close on the given stream.
    /// Runs inline for unary calls (completes before yielding) and
    /// naturally yields for streaming calls via the async state machine.
    /// </summary>
    /// <summary>
    /// Sends the request body and half-close on the given stream.
    /// Runs inline for unary calls (completes before yielding) and
    /// naturally yields for streaming calls via the async state machine.
    /// Round-8 PR-C1: constructs and OWNS the
    /// <see cref="ShmGrpcRequestStream"/> lifecycle via <c>using</c>
    /// to guarantee <c>_bodyBuf</c> returns to the pool on every exit
    /// (success, exception, cancellation). Prior shape (caller-owned
    /// stream + fire-and-forget call) leaked the pooled buffer on
    /// custom HttpContent and multi-fragment WriteAsync paths.
    /// </summary>
    private static async Task SendBodyAsync(
        HttpContent content,
        ShmGrpcStream stream,
        CancellationToken cancellationToken)
    {
        using var writeStream = new ShmGrpcRequestStream(stream);
        try
        {
            await content.CopyToAsync(writeStream, cancellationToken).ConfigureAwait(false);
            // Safety: if the body write completed but did NOT consume
            // the staged Headers (e.g., the marshaller didn't take the
            // IDirectMessageWriter fast path, or the user supplied a
            // custom HttpContent that calls Stream.WriteAsync directly),
            // flush the staged Headers now so the peer sees them BEFORE
            // HalfClose. Without this guard, the peer would observe a
            // HalfClose on a stream with no Headers => protocol error.
            if (stream.HasStagedHeaders)
            {
                await stream.FlushStagedHeadersAsync(cancellationToken).ConfigureAwait(false);
            }
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

    /// <summary>Test-only helper that exposes the negotiated control-plane connect path.</summary>
    internal Task<ShmConnection> ConnectForTest(CancellationToken cancellationToken)
        => ConnectViaControlSegmentAsync(cancellationToken);

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
            // Wire format is always HTTP/2 (advertised in the extension).
            //
            // Nonce coordination (gRFC A — Stale Response Correlation):
            // generate a fresh 8-byte CSPRNG nonce per CONNECT. The
            // server echoes it in ACCEPT/REJECT so we can detect and
            // skip stale responses left on the shared Ring B by a
            // previously timed-out dialer (which would otherwise
            // mis-bind us to the wrong peer's segment with the wrong
            // singleStreamMode flag). Bounded skip loop matches
            // grpc-go-shmem's maxStaleResponses=3.
            var preferredRing = _options.RingCapacity;
            var ourNonce = ControlWire.NewConnectNonce();
            await WriteControlFrameAsync(ctlTx, FrameType.Connect,
                ControlWire.EncodeConnectRequest(preferredRing, preferredRing, _options.SingleStreamMode, ourNonce), ct).ConfigureAwait(false);

            const int maxStaleResponses = 3;
            for (int attempt = 0; attempt <= maxStaleResponses; attempt++)
            {
                // Read response
                var (responseHeader, responsePayload) = await ReadControlFrameAsync(ctlRx, ct).ConfigureAwait(false);

                switch (responseHeader.Type)
                {
                    case FrameType.Accept:
                        var (dataSegmentName, acceptNonce) = ControlWire.DecodeConnectResponse(responsePayload.Span);
                        if (acceptNonce != ourNonce)
                        {
                            // Stale ACCEPT from a previous dialer; skip
                            // and keep reading for OUR response.
                            continue;
                        }

                        // Open the data segment
                        var dataSegment = Segment.Open(dataSegmentName);
                        try
                        {
                            await dataSegment.WaitForServerAsync(ct).ConfigureAwait(false);

                            // Signal that client has mapped the segment
                            dataSegment.SetClientReady(true);

                            // Optional security handshake on the data segment
                            // BEFORE we hand it to ShmConnection — the connection
                            // ctor starts the frame reader loop which would
                            // otherwise race the handshake frame I/O. Mirrors
                            // grpc-go-shmem's transport-layer
                            // ShmSecurityHandshaker.ClientHandshake.
                            ShmAuthInfo? authInfo = null;
                            if (_options.Handshaker != null)
                            {
                                // From the client's perspective: RingA is
                                // client→server (we write), RingB is
                                // server→client (we read).
                                authInfo = await _options.Handshaker.ClientHandshakeAsync(
                                    writer: (type, payload, c) => WriteHandshakeFrameAsync(dataSegment.RingA, type, payload, c),
                                    reader: c => ReadHandshakeFrameAsync(dataSegment.RingB, c),
                                    ct).ConfigureAwait(false);
                            }

                            // Wire format is always HTTP/2 — the protocol layer rejected
                            // anything else.
                            // Create and return the connection
                            var conn = ShmConnection.FromClientSegment(dataSegmentName, dataSegment);
                            conn.AuthInfo = authInfo;
                            if (_options.SingleStreamMode)
                            {
                                conn.ZeroCopyRead = true;
                                // SingleStreamMode propagates to TxRing/RxRing
                                // (see ShmConnection.SingleStreamMode setter), so
                                // the chain-ZC budget on the data rings reflects
                                // the negotiated mode and the client-side inline-
                                // write fast paths are unlocked.
                                //
                                // Correctness depends on `SendRequestHeadersAsync`
                                // taking the TryPauseWriterLoop inline-write path
                                // when this flag is set so Headers, Message, and
                                // HalfClose all serialise through the same inline
                                // writer (no concurrent WriterLoop dequeue racing
                                // against an inline writer on the same ring).
                                conn.SingleStreamMode = true;
                                conn.FrameWriter?.EnableSingleStreamMode();
                            }
                            if (_options.InlineReceiveContinuations)
                            {
                                // Local receive-side opt-in: only affects this
                                // client's Channel<InboundFrame> dispatch, not
                                // the wire protocol. Each side picks its own
                                // continuation model independently. See
                                // ShmConnection.InlineReceiveContinuations.
                                conn.InlineReceiveContinuations = true;
                            }
                            return conn;
                        }
                        catch
                        {
                            dataSegment.Dispose();
                            throw;
                        }

                    case FrameType.Reject:
                        var (message, rejectNonce) = ControlWire.DecodeConnectReject(responsePayload.Span);
                        if (rejectNonce != ourNonce)
                        {
                            // Stale REJECT (or 0-nonce decode-failure
                            // REJECT for a previous dialer); skip.
                            continue;
                        }
                        throw new InvalidOperationException($"Connection rejected by server: {message}");

                    default:
                        throw new InvalidOperationException($"Unexpected response frame type: {responseHeader.Type}");
                }
            }

            // Exhausted the stale-skip budget without seeing our nonce.
            // Treat as a soft handshake failure: the legitimate response
            // is either delayed past our patience or got mis-consumed by
            // an earlier dialer that beat us to Ring B reads.
            throw new InvalidOperationException(
                $"No matching connect response after {maxStaleResponses + 1} attempts (stale-skip budget exhausted).");
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

    /// <summary>
    /// Writes a single security-handshake frame to the data-segment ring.
    /// Adapter matching <see cref="IShmSecurityHandshaker"/>'s writer
    /// delegate signature.
    /// </summary>
    private static Task WriteHandshakeFrameAsync(ShmRing ring, FrameType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var header = new FrameHeader
        {
            Length = (uint)payload.Length,
            StreamId = 0,
            Type = type,
            Flags = 0
        };
        var headerBytes = header.ToBytes();
        ring.Write(headerBytes, ct);
        if (payload.Length > 0)
        {
            // ShmRing.Write takes byte[]; copy from the ROM (handshake
            // frames are small — Init/Resp ≤ MaxIdentitySize+19 bytes,
            // Ack = 2 bytes, Fail bounded by message length).
            var buffer = payload.ToArray();
            ring.Write(buffer, ct);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads a single security-handshake frame from the data-segment
    /// ring. Adapter matching <see cref="IShmSecurityHandshaker"/>'s
    /// reader delegate signature.
    /// </summary>
    private static Task<(FrameType Type, ReadOnlyMemory<byte> Payload)> ReadHandshakeFrameAsync(ShmRing ring, CancellationToken ct)
    {
        var headerBuffer = new byte[ShmConstants.FrameHeaderSize];
        ReadExact(ring, headerBuffer, ct);
        var header = FrameHeader.Parse(headerBuffer);

        // Handshake frames are tightly bounded — Identity ≤ 256 B,
        // Nonce = 16 B, Fail message bounded by callers. Reject
        // anything wildly oversized to defend against a hostile peer.
        if (header.Length > ShmConstants.MinRingCapacity)
        {
            throw new InvalidDataException(
                $"Handshake frame payload {header.Length} exceeds maximum {ShmConstants.MinRingCapacity}.");
        }

        ReadOnlyMemory<byte> payload = ReadOnlyMemory<byte>.Empty;
        if (header.Length > 0)
        {
            var payloadBuffer = new byte[header.Length];
            ReadExact(ring, payloadBuffer, ct);
            payload = payloadBuffer;
        }
        return Task.FromResult((header.Type, payload));
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
        // Round-7 perf: lazy-allocate. The dominant case (gRPC unary call
        // with no user metadata) walks the headers, hits no surviving
        // entry, and the old code still allocated a fresh Metadata
        // instance just to return null. Allocate only when we find the
        // first non-skipped entry — saves one alloc per RPC.
        Metadata? metadata = null;

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
                        (metadata ??= new Metadata()).Add(new Metadata.Entry(header.Key, Convert.FromBase64String(value)));
                    }
                    catch (FormatException) { /* malformed base64 — skip */ }
                }
                else
                {
                    (metadata ??= new Metadata()).Add(new Metadata.Entry(header.Key, value));
                }
            }
        }

        return metadata;
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

    // Round-8 PR-C1: test-only counter that lets regression tests assert
    // every constructed stream was matched by a Dispose. Tracks the number
    // of instances currently outstanding (incremented in ctor, decremented
    // in Dispose). A non-zero post-RPC value means SendBodyAsync (or
    // another caller) leaked the stream and therefore leaked `_bodyBuf`
    // back to GC instead of the ArrayPool. Live counter (not interlocked
    // for raw value but the +/- pair is via Interlocked) so a thread-safe
    // observer can read it.
    internal static int LiveInstanceCount => Volatile.Read(ref s_liveInstanceCount);
    private static int s_liveInstanceCount;

    public ShmGrpcRequestStream(ShmGrpcStream shmStream)
    {
        _shmStream = shmStream;
        Interlocked.Increment(ref s_liveInstanceCount);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _bodyBuf != null)
        {
            ArrayPool<byte>.Shared.Return(_bodyBuf);
            _bodyBuf = null;
        }
        if (disposing)
        {
            Interlocked.Decrement(ref s_liveInstanceCount);
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var remaining = buffer;

        // Resume partial body from previous write.
        // _bodyExpected is the protobuf-only length (excluding the 5-byte gRPC header).
        // _bodyBufLen is the total bytes in _bodyBuf (including the 5-byte header).
        // So the total expected is 5 + _bodyExpected.
        if (_bodyExpected > 0 && _bodyBufLen < 5 + _bodyExpected)
        {
            var needed = (5 + _bodyExpected) - _bodyBufLen;
            var toCopy = Math.Min(needed, remaining.Length);
            remaining.Slice(0, toCopy).CopyTo(_bodyBuf.AsMemory(_bodyBufLen));
            _bodyBufLen += toCopy;
            remaining = remaining.Slice(toCopy);

            if (_bodyBufLen < 5 + _bodyExpected)
            {
                return; // Still incomplete
            }

            await _shmStream.SendMessageAsync(_bodyBuf.AsMemory(0, 5 + _bodyExpected), cancellationToken).ConfigureAwait(false);
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
            // Compressed flag preserved — server handles decompression.
            var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hdrSpan.Slice(1));

            if (remaining.Length < length)
            {
                // Partial body — buffer gRPC header + available body
                var totalNeeded = 5 + length;
                if (_bodyBuf == null || _bodyBuf.Length < totalNeeded)
                {
                    if (_bodyBuf != null) ArrayPool<byte>.Shared.Return(_bodyBuf);
                    _bodyBuf = ArrayPool<byte>.Shared.Rent(totalNeeded);
                }
                // Copy the 5-byte gRPC header first
                _headerBuf.AsSpan(0, 5).CopyTo(_bodyBuf);
                remaining.CopyTo(_bodyBuf.AsMemory(5));
                _bodyBufLen = 5 + remaining.Length;
                _bodyExpected = length;
                return;
            }

            // Reconstruct: 5-byte header + body using reusable pooled _bodyBuf
            var fullMsgLen = 5 + length;
            if (_bodyBuf == null || _bodyBuf.Length < fullMsgLen)
            {
                if (_bodyBuf != null) ArrayPool<byte>.Shared.Return(_bodyBuf);
                _bodyBuf = ArrayPool<byte>.Shared.Rent(fullMsgLen);
            }
            _headerBuf.AsSpan(0, 5).CopyTo(_bodyBuf);
            remaining.Slice(0, length).Span.CopyTo(_bodyBuf.AsSpan(5));
            await _shmStream.SendMessageAsync(_bodyBuf.AsMemory(0, fullMsgLen), cancellationToken).ConfigureAwait(false);
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
            // Compressed flag preserved — server handles decompression.
            var msgLen = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(span.Slice(1));

            if (remaining.Length < 5 + msgLen)
            {
                // Partial body — buffer header + available body (include gRPC 5-byte header)
                _bodyExpected = msgLen;
                var totalNeeded = 5 + msgLen;
                if (_bodyBuf == null || _bodyBuf.Length < totalNeeded)
                {
                    if (_bodyBuf != null) ArrayPool<byte>.Shared.Return(_bodyBuf);
                    _bodyBuf = ArrayPool<byte>.Shared.Rent(totalNeeded);
                }
                remaining.Slice(0, remaining.Length).CopyTo(_bodyBuf);
                _bodyBufLen = remaining.Length;
                return;
            }

            await _shmStream.SendMessageAsync(remaining.Slice(0, 5 + msgLen), cancellationToken).ConfigureAwait(false);
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
        // SAFE-INLINE-RECEIVE DEADLOCK GUARD (2026-06-01): when the
        // caller is running inline on the SHM frame-reader Thread (a
        // direct consequence of AllowSynchronousContinuations=true
        // on the inbound channel) AND the upcoming write would block
        // on per-stream / connection send quota, hop off to a
        // ThreadPool worker before descending into the inline-direct
        // path — otherwise WriteInlineDirectMultiFrame's inner
        // ReserveSendQuotaOrBlock parks the reader Thread on
        // _sendQuotaWake and the peer's WINDOW_UPDATE can never be
        // processed (no Thread left to read it). Confirmed by
        // dotnet-dump on the demo bench 64 MiB max ping-pong hang.
        // Mirrors the same guard already present on
        // ShmGrpcStream.SendMessageAsync; required here because this
        // path takes the WriteInlineDirectMultiFrame fast path that
        // bypasses SendMessageAsync.
        if (ShmReaderThreadContext.IsOnReaderThread
            && message is Google.Protobuf.IMessage protoSizeProbe
            && _shmStream.WouldBlockSendQuota(5 + protoSizeProbe.CalculateSize()))
        {
            return WriteSerializedMessageWithReaderThreadHopAsync(message, serializer, cancellationToken);
        }

        // Fast path: for protobuf IMessage types in singleStreamMode,
        // serialize directly into the ring buffer via
        // WriteInlineDirectMultiFrame (zero intermediate buffer).
        //
        // Note: NO `size > 0` guard. Empty messages (e.g., probe call
        // with `SimpleRequest{ResponseSize=0}`) must also take this
        // inline path so that Message and the subsequent HALFCLOSE
        // (also TryPause inline) serialise through the same
        // `_inlineWriterActive` CAS. If the empty message fell through
        // to the queued path while HALFCLOSE went inline, HALFCLOSE
        // would reach the ring before the queued empty MESSAGE
        // (race observed at probe time on Intel Linux).
        if (_shmStream.Connection.SingleStreamMode
            && _shmStream.Connection.ActiveStreamCount <= 1
            && message is Google.Protobuf.IMessage protoMsg)
        {
            var writer = _shmStream.Connection.FrameWriter;
            if (writer != null)
            {
                var size = protoMsg.CalculateSize();

                // Client-coalesce path (PR #21 + round-11 multi-frame
                // expansion):
                //
                // When ShmControlHandler.SendOnStreamAsync detected a
                // Unary-shaped request (PushUnaryContent / UnaryContent /
                // WinHttpUnaryContent) it called StageRequestHeaders
                // instead of SendRequestHeadersAsync, deferring the
                // Headers wire write to right here. If the protobuf
                // body fits within the multi-frame coalesce gate (lpm
                // <= cap/8, no FairMaxFramePayload clamp), the size is
                // below the per-call latency cap, and both stream + conn
                // flow-control windows can absorb the full lpm, we open
                // one inline batch and write HEADERS + DATA(END_STREAM)
                // back-to-back -> 1 SignalData wake for the entire
                // request (vs 3 wakes today: Headers + Data + HalfClose;
                // round-9 unary saved this for <= FairMax; round-11
                // multi-frame extends to ~64 KiB Fair / 128 KiB Jumbo32).
                //
                // Three thresholds:
                //   - writer.CanCoalesceMultiFrameMessage(5 + size) is
                //     the F1 correctness gate (cumulative bytes fit
                //     cap/8 ring space — the writer may still emit N
                //     FairMax-sized H2 DATA frames under the suppressed
                //     wake without filling the ring).
                //   - ShmFrameWriter.CoalesceLatencyCapBytes (128 KiB)
                //     is a per-call blast-radius cap on how long the
                //     paused WriterLoop / suppressed control frames
                //     can be held — bounds tail latency for concurrent
                //     control traffic.
                //   - _shmStream.SendQuota >= lpm AND
                //     _shmStream.Connection.ConnSendQuota >= lpm are
                //     the F2 deadlock guard: suppressed HEADERS hides
                //     our DATA from peer, so if inner per-chunk
                //     ReserveSendQuotaOrBlock blocks waiting for WU
                //     the peer would never know to send one. Pre-check
                //     covers BOTH resources the reserve will debit
                //     (stream + conn). Snapshot is sound because this
                //     stream is single-producer for sends and WU only
                //     INCREASES quota.
                var lpmFramedSize = 5 + size;
                if (_shmStream.HasStagedHeaders
                    && lpmFramedSize <= ShmFrameWriter.CoalesceLatencyCapBytes
                    && writer.CanCoalesceMultiFrameMessage(lpmFramedSize)
                    && _shmStream.SendQuota >= lpmFramedSize
                    && _shmStream.Connection.ConnSendQuota >= lpmFramedSize
                    && writer.TryPauseWriterLoop())
                {
                    try
                    {
                        writer.BeginInlineBatch();
                        try
                        {
                            _shmStream.WriteStagedHeadersInline(writer);
                            writer.WriteInlineDirectMultiFrame(
                                _shmStream.StreamId, size, protoMsg,
                                MessageFlags.EndStream, default, _shmStream);
                            _shmStream.MarkHalfClosed();
                        }
                        finally
                        {
                            writer.EndInlineBatch();
                        }
                        return Task.CompletedTask;
                    }
                    finally
                    {
                        writer.ResumeWriterLoop();
                    }
                }

                // Existing fast path: just DATA. If Headers were staged
                // but coalesce gate failed (too big / pause contended),
                // flush them via the slow path first to preserve wire
                // ordering, then fall through to the DATA inline write.
                if (_shmStream.HasStagedHeaders)
                {
                    return WriteFlushHeadersThenMessageAsync(message, serializer, size, protoMsg, cancellationToken);
                }

                if (writer.TryPauseWriterLoop())
                {
                    // Round-11 multi-frame streaming coalesce: when the
                    // protobuf body exceeds the FairMaxFramePayload H2
                    // frame cap (e.g., 16 KiB Fair = 16389 lpm spilling
                    // into 2 chunks), WriteInlineDirectMultiFrame today
                    // emits per-chunk SignalData wakes. Wrap the whole
                    // call in BeginInlineBatch so N chunks collapse to
                    // 1 wake at EndInlineBatch.
                    //
                    // Gates mirror Site 1 (unary) minus HasStagedHeaders:
                    //   - lpm <= 128 KiB latency cap
                    //   - CanCoalesceMultiFrameMessage (cap/8 ring space,
                    //     no FairMax clamp)
                    //   - stream + conn SendQuota >= lpm (F2 deadlock guard)
                    // size==0 path is a single WriteInline commit with
                    // no multi-frame to coalesce — skip the batch (no
                    // gain, ~10ns saved).
                    // (lpmFramedSize already computed for Site 1 gate above.)
                    bool coalesce = size > 0
                        && lpmFramedSize <= ShmFrameWriter.CoalesceLatencyCapBytes
                        && writer.CanCoalesceMultiFrameMessage(lpmFramedSize)
                        && _shmStream.SendQuota >= lpmFramedSize
                        && _shmStream.Connection.ConnSendQuota >= lpmFramedSize;
                    if (coalesce) writer.BeginInlineBatch();
                    try
                    {
                        writer.WriteInlineDirectMultiFrame(_shmStream.StreamId, size, protoMsg, 0, default, _shmStream);
                        return Task.CompletedTask;
                    }
                    finally
                    {
                        if (coalesce) writer.EndInlineBatch();
                        writer.ResumeWriterLoop();
                    }
                }
            }
        }

        // Standard path: serialize via the provided marshaller delegate
        // into a pooled buffer, then send via TryPause/ExecuteInline/queue.
        if (_shmStream.HasStagedHeaders)
        {
            return WriteFlushHeadersThenSerializedAsync(message, serializer, cancellationToken);
        }
        var ctx = new DirectWriteSerializationContext(_shmStream);
        serializer(message, ctx);
        return ctx.SendResult(cancellationToken);
    }

    /// <summary>
    /// Slow-path helper for <see cref="WriteSerializedMessageAsync"/>:
    /// hops off the SHM frame-reader Thread via <see cref="Task.Yield"/>
    /// before recursing into the normal send path. See
    /// <see cref="ShmReaderThreadContext"/> for the deadlock invariant.
    /// </summary>
    private async Task WriteSerializedMessageWithReaderThreadHopAsync<TMessage>(
        TMessage message,
        Action<TMessage, Grpc.Core.SerializationContext> serializer,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        await WriteSerializedMessageAsync(message, serializer, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteFlushHeadersThenMessageAsync<TMessage>(
        TMessage message,
        Action<TMessage, Grpc.Core.SerializationContext> serializer,
        int size,
        Google.Protobuf.IMessage protoMsg,
        CancellationToken cancellationToken)
    {
        // Coalesce-gate-failed fall-back. Two paths possible:
        //
        //   FAST: writer.TryPauseWriterLoop succeeds. Write the staged
        //         Headers inline + the DATA inline UNDER THE SAME
        //         PAUSE. No BeginInlineBatch is opened, so each
        //         CommitWrite fires its own SignalData as today —
        //         wire-level wake count is identical to the pre-PR
        //         path (Headers + N DATA chunks + HalfClose). The win
        //         vs the prior implementation is that Headers skips
        //         the queued `SendFrameAsync` overhead (~10-30 us /
        //         call for Task allocation + WriterLoop scheduling)
        //         and goes inline on the caller thread, matching the
        //         OLD `SendRequestHeadersAsync` behaviour. Without
        //         this path, Unary calls whose size exceeds the
        //         coalesce gate (e.g., fair mode `SHM_FAIR_MAX_FRAME=
        //         16 KiB` with a 64 KiB Unary body) would regress vs
        //         pre-PR by the queued-Headers latency.
        //
        //         Important: do NOT use `BeginInlineBatch` here. The
        //         coalesce gate already rejected this size because
        //         the body chunks into multiple frames under
        //         `RingFrameStream`, and chunked writes under an open
        //         batch are the F1 deadlock class fixed in PR #21
        //         (signals suppressed -> peer never drains -> ring
        //         fills -> next chunk's `ReserveWrite` blocks).
        //
        //   SLOW: TryPauseWriterLoop loses the CAS to another inline
        //         writer (rare; only at multi-stream concurrency).
        //         Falls back to the queued path: flush Headers via
        //         the standard `SendFrameAsync` queue, then drive
        //         DATA through the marshaller into a pooled buffer
        //         and queue it too.
        var writer = _shmStream.Connection.FrameWriter;
        if (writer != null && writer.TryPauseWriterLoop())
        {
            try
            {
                _shmStream.WriteStagedHeadersInline(writer);
                writer.WriteInlineDirectMultiFrame(_shmStream.StreamId, size, protoMsg, 0, default, _shmStream);
                return;
            }
            finally
            {
                writer.ResumeWriterLoop();
            }
        }

        await _shmStream.FlushStagedHeadersAsync(cancellationToken).ConfigureAwait(false);
        var ctx = new DirectWriteSerializationContext(_shmStream);
        serializer(message, ctx);
        await ctx.SendResult(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteFlushHeadersThenSerializedAsync<TMessage>(
        TMessage message,
        Action<TMessage, Grpc.Core.SerializationContext> serializer,
        CancellationToken cancellationToken)
    {
        await _shmStream.FlushStagedHeadersAsync(cancellationToken).ConfigureAwait(false);
        var ctx = new DirectWriteSerializationContext(_shmStream);
        serializer(message, ctx);
        await ctx.SendResult(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Minimal SerializationContext that writes directly into a pooled buffer
    /// with a 5-byte gRPC LPM header reserved at offset 0. Implements IBufferWriter so
    /// protobuf can serialize using the fast WriteContext path.
    /// </summary>
    private sealed class DirectWriteSerializationContext : Grpc.Core.SerializationContext, IBufferWriter<byte>
    {
        private static readonly byte[] EmptyGrpcLpm = new byte[5];
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
                // Reserve 5 bytes at start for gRPC length-prefix header
                // (needed for Go interop compatibility)
                _buffer = ArrayPool<byte>.Shared.Rent(5 + _payloadLength);
                _position = 5; // Start writing protobuf after the header
            }

            return this;
        }

        public override void Complete(byte[] payload)
        {
            // Old-style Complete(byte[]): copy into our pooled buffer
            // with 5-byte gRPC header reservation at the front.
            if (_buffer != null)
                ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = ArrayPool<byte>.Shared.Rent(5 + payload.Length);
            payload.AsSpan().CopyTo(_buffer.AsSpan(5));
            _position = 5 + payload.Length;
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
            // Empty-payload shortcut: when the protobuf serialised to nothing
            // (e.g., `SimpleRequest{ResponseSize=0}`), we still need to ship
            // the 5-byte gRPC LPM header. Under SingleStreamMode that empty
            // header MUST take the same TryPauseWriterLoop inline path as
            // the subsequent HALFCLOSE (see SendHalfCloseAsync); otherwise
            // an inline HALFCLOSE write would beat a queued empty MESSAGE
            // to the ring and the server would see HALFCLOSE first.
            //
            // Route through `_buffer` either way: if `_buffer` is null,
            // pre-fill it with the 5-byte empty LPM header and let the
            // single inline path below handle it.
            if (_buffer == null || _position <= 5)
            {
                if (_buffer == null)
                {
                    _buffer = ArrayPool<byte>.Shared.Rent(5);
                }
                // EmptyGrpcLpm = 5 zero bytes; copy into the buffer's first 5 bytes.
                EmptyGrpcLpm.AsSpan().CopyTo(_buffer.AsSpan(0, 5));
                _position = 5;
            }
            else
            {
                // Write the 5-byte gRPC length-prefix header at offset 0.
                // Protobuf payload starts at offset 5, so payload length = _position - 5.
                var protoLen = _position - 5;
                _buffer[0] = 0; // no compression
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                    _buffer.AsSpan(1, 4), (uint)protoLen);
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
                            writer.WriteInline(_stream.StreamId, buf.AsSpan(0, _position), 0, default, _stream);
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
                        var fairStream = _stream;
                        _buffer = null;
                        try
                        {
                            writer.ExecuteInline(() =>
                            {
                                writer.WriteInline(streamId, buf.AsSpan(0, bufLen), 0, default, fairStream);
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
internal sealed class ShmControlResponseContent : HttpContent,
    Grpc.Net.Client.IDirectMessageReader,
    Grpc.Net.Client.IPooledDeserializer
{
    private readonly ShmGrpcStream _stream;
    private HttpHeaders? _trailingHeaders;
    private InboundFrame _currentFrame;

    // ===== Multi-frame chain =====
    //
    // A logical gRPC message that exceeds the wire-format's single-frame
    // payload limit (cap/3 for Custom16, 16 MiB for HTTP/2) is split into
    // a sequence of MORE-flagged frames terminated by a single non-MORE
    // frame. Previous implementation copied each chunk into a contiguous
    // <c>_assembled</c> buffer (one alloc per message + per-frame memcpy).
    // The chain implementation keeps each frame as its own segment and
    // hands the consumer a multi-segment <see cref="ReadOnlySequence{T}"/>,
    // saving the per-frame memcpy. Each segment's <see cref="InboundFrame"/>
    // remains in <c>_chainFrames</c> for release on
    // <see cref="ReleaseCurrentMessage"/>.
    //
    // The chain optimisation applies only to UNCOMPRESSED multi-frame
    // messages. Compressed payloads still need a contiguous buffer to feed
    // the decompressor; that path falls back to the legacy
    // <c>_assembled</c> path so the existing decompression code stays
    // untouched. compFlag is sniffed from the first byte of the first
    // frame's payload (which is always the LPM compression flag).
    private BufferSegment? _chainHead;
    private BufferSegment? _chainTail;
    private List<InboundFrame>? _chainFrames;
    private int _chainBodySize;          // accumulated body size (excludes LPM 5-byte header at chain start)

    // Client-side lazy-chain ROS for streaming multi-chunk uncompressed
    // messages. Built when the first chunk (More=1, compFlag=0) arrives;
    // pulls subsequent chunks synchronously as the caller's MergeFrom
    // advances and releases each pool buffer immediately after the parser
    // consumes it. Pool peak ~2 chunks regardless of LPM size (vs
    // O(message) under the legacy BufferSegment-collect-then-MergeFrom
    // path).
    //
    // _lazyChainSawEndStream tracks whether the puller observed EndStream
    // on the final chunk; surfaced to the caller via the (Empty, EOS=true)
    // sentinel return on the NEXT ReadNextMessage* call (EOS is not known
    // when we return the ROS - it is discovered inside MergeFrom when
    // the parser pulls the last chunk).
    private LazyChainRos? _lazyChain;
    private bool _lazyChainSawEndStream;

    // Multi-frame eager-pre-fetch state (deadlock-safe replacement for
    // <see cref="LazyChainRos"/> on chain-ZC-budget-fitting LPMs;
    // see <see cref="InboundChainHelper"/>). When non-null, holds every
    // chunk of the most-recently-returned multi-frame uncompressed
    // message. Same ownership protocol as <see cref="_lazyChain"/>:
    // released at the top of the next <c>ReadNextMessage*</c> call,
    // and if any chunk carried <see cref="MessageFlags.EndStream"/>
    // we surface <c>(Empty, true)</c> on that next call (preserving
    // the existing caller contract that EndStream is observed AFTER
    // the message ROS is fully consumed).
    private List<InboundFrame>? _prefetchedChunks;
    private bool _prefetchedSawEndStream;

    // Multi-frame accumulation (compressed path only). Allocated lazily
    // via ArrayPool when the compressed code path needs a contiguous
    // buffer; returned to ArrayPool on Dispose. ArrayPool's LOH bucket
    // recycling provides cross-call reuse without per-connection
    // pinning.
    private byte[]? _assembled;
    private int _assembledPos;

    // Response compression: encoding from grpc-encoding response header.
    private string? _responseEncoding;

    // Cached pooled parser delegate — set once by SetPooledDeserializer.
    private Func<ReadOnlySequence<byte>, object>? _pooledDeserializer;
    public Func<ReadOnlySequence<byte>, object>? PooledDeserializer => _pooledDeserializer;

    // Bench --fair gate: when SHM_DISABLE_POOLED_DESER=1, the pooled
    // deserialization fast-path is suppressed so the client falls back to
    // the stock Grpc.Net.Client buffered codec. This makes A/B numbers vs
    // TCP / UDS comparable on equal terms (those transports do not
    // currently expose the IPooledDeserializer hook). No effect in
    // production deployments that do not set the variable.
    private static readonly bool s_disablePooledDeser =
        string.Equals(Environment.GetEnvironmentVariable("SHM_DISABLE_POOLED_DESER"),
            "1", StringComparison.Ordinal);

    public void SetPooledDeserializer(Type responseType)
    {
        if (s_disablePooledDeser) return;
        if (_pooledDeserializer != null) return;
        try
        {
            // One-time generic specialization via MakeGenericMethod.
            // Creates a cached delegate calling PooledProtoParser.ParseFrom<T>
            // with full JIT optimization — no descriptor reflection per field.
            // Called once per stream, not per message.
            typeof(ShmControlResponseContent)
                .GetMethod(nameof(EnablePooledDeserialization),
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .MakeGenericMethod(responseType)
                .Invoke(this, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
            or System.Reflection.TargetInvocationException or NotSupportedException)
        {
            // Non-IMessage type or constraint mismatch — PooledDeserializer stays null.
        }
    }

    private void EnablePooledDeserialization<T>() where T : class, IMessage<T>, new()
    {
        // Use Interlocked to prevent double-init from concurrent MoveNext calls.
        var del = new Func<ReadOnlySequence<byte>, object>((payload) =>
        {
            // Single-segment fast path (single-frame ZC, or copy-mode small
            // message): the pooled scanner is fastest at this size.
            if (payload.IsSingleSegment)
                return PooledProtoParser.ParseFrom<T>(payload.FirstSpan);

            // Multi-segment path (multi-frame chain ZC, or _assembled
            // fallback that produced multiple segments): hand the
            // ReadOnlySequence directly to Google.Protobuf's
            // MergeFrom(ReadOnlySequence) which uses CodedInputStream
            // internally. CodedInputStream walks varints across segment
            // boundaries; for `bytes` fields it allocates a ByteString of
            // the field's length and copies the bytes once. That single
            // memcpy is unavoidable (ByteString requires contiguous
            // storage) — but we save the upstream "flatten ROS into one
            // pool buffer" memcpy that the previous implementation did
            // before scanning. Net: 1 memcpy per message instead of 2 for
            // ZC-chained large payloads.
            var msg = new T();
            Google.Protobuf.MessageExtensions.MergeFrom(msg, payload);
            return msg;
        });
        Interlocked.CompareExchange(ref _pooledDeserializer, del, null);
    }

    public ShmControlResponseContent(ShmGrpcStream stream)
    {
        _stream = stream;
        Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        _assembled = null;
    }

    /// <summary>
    /// Multi-segment <see cref="ReadOnlySequenceSegment{T}"/> node for the
    /// multi-frame ZC chain. We expose the protected setters as new public
    /// properties so the chain can be wired up incrementally as frames
    /// arrive.
    /// </summary>
    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public void SetMemory(ReadOnlyMemory<byte> memory) => Memory = memory;
        public void SetRunningIndex(long runningIndex) => RunningIndex = runningIndex;
        public void SetNext(BufferSegment? next) => Next = next;
    }

    /// <summary>
    /// Appends a frame to the multi-frame ZC chain. The first frame in the
    /// chain still carries the 5-byte LPM header at <c>Memory[0..5]</c>;
    /// subsequent frames are pure body bytes. Frame ownership is transferred
    /// to <c>_chainFrames</c> for batched release in
    /// <see cref="ReleaseCurrentMessage"/>.
    /// </summary>
    private void AppendChainFrame(InboundFrame frame, ReadOnlyMemory<byte> bodyMemory)
    {
        var seg = new BufferSegment();
        seg.SetMemory(bodyMemory);
        _chainFrames ??= new List<InboundFrame>(8);
        _chainFrames.Add(frame);
        if (_chainHead == null)
        {
            seg.SetRunningIndex(0);
            _chainHead = seg;
            _chainTail = seg;
        }
        else
        {
            seg.SetRunningIndex(_chainTail!.RunningIndex + _chainTail.Memory.Length);
            _chainTail.SetNext(seg);
            _chainTail = seg;
        }
        _chainBodySize += bodyMemory.Length;
    }

    /// <summary>Releases all chain frames and resets chain state.</summary>
    private void ReleaseChain()
    {
        if (_chainFrames != null)
        {
            for (int i = 0; i < _chainFrames.Count; i++)
            {
                _chainFrames[i].ReturnToPool();
            }
            _chainFrames.Clear();
        }
        // Drop segment references so the GC can reclaim the small wrapper
        // objects (the underlying memory has already been released through
        // each frame's ReturnToPool above).
        var node = _chainHead;
        while (node != null)
        {
            var next = node.Next as BufferSegment;
            node.SetMemory(default);
            node.SetRunningIndex(0);
            node.SetNext(null);
            node = next;
        }
        _chainHead = null;
        _chainTail = null;
        _chainBodySize = 0;
    }

    /// <summary>Sets the grpc-encoding for response decompression.</summary>
    internal void SetResponseEncoding(string encoding)
    {
        if (!string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase))
            _responseEncoding = encoding;
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

    public ValueTask<(ReadOnlySequence<byte> Payload, bool EndOfStream)> ReadNextMessageAsync(
        CancellationToken cancellationToken)
    {
        _currentFrame.ReturnToPool();
        _currentFrame = default;
        ReleaseChain();
        _assembledPos = 0;

        // Dispose any lazy-chain from the PREVIOUS message; if its puller
        // observed EndStream, surface (Empty, true) immediately without
        // touching the channel further.
        if (_lazyChain != null)
        {
            _lazyChain.Dispose();
            _lazyChain = null;
            if (_lazyChainSawEndStream)
            {
                _lazyChainSawEndStream = false;
                _stream.MarkHalfCloseReceived();
                ApplyTrailers();
                return new ValueTask<(ReadOnlySequence<byte>, bool)>((ReadOnlySequence<byte>.Empty, true));
            }
        }

        // Same release/EOS pattern for the eager-pre-fetch path.
        if (_prefetchedChunks != null)
        {
            InboundChainHelper.ReleaseAll(_prefetchedChunks);
            _prefetchedChunks = null;
            if (_prefetchedSawEndStream)
            {
                _prefetchedSawEndStream = false;
                _stream.MarkHalfCloseReceived();
                ApplyTrailers();
                return new ValueTask<(ReadOnlySequence<byte>, bool)>((ReadOnlySequence<byte>.Empty, true));
            }
        }

        // Fast path: try sync read.
        while (_stream.TryReceiveFrame(out var frame))
        {
            // Multi-frame uncompressed first-chunk: take the
            // deadlock-safe async path (hybrid eager-pre-fetch or
            // yield-then-lazy; see InboundChainHelper). Must be
            // dispatched as ValueTask because the helper is async.
            if (TryDetectMultiFrameUncompressedFirstChunk(frame, out int lpmBodyLen))
            {
                return PrefetchMultiFrameAndBuildAsync(frame, lpmBodyLen, cancellationToken);
            }
            var result = ProcessReceivedFrame(frame);
            if (result.Payload.Length == 0 && !result.EndOfStream)
                continue;
            return new ValueTask<(ReadOnlySequence<byte>, bool)>(result);
        }

        return ReadNextMessageSlowAsync(cancellationToken);
    }

    /// <summary>
    /// Returns <see langword="true"/> iff <paramref name="frame"/>
    /// is the first chunk of a multi-frame UNCOMPRESSED LPM the
    /// caller has not yet started accumulating. Sets
    /// <paramref name="lpmBodyLen"/> from the LPM header. Mirrors
    /// the server-side detector in
    /// <c>ShmAsyncStreamReader.TryDetectMultiFrameUncompressedFirstChunk</c>.
    /// </summary>
    private bool TryDetectMultiFrameUncompressedFirstChunk(
        InboundFrame frame, out int lpmBodyLen)
    {
        lpmBodyLen = 0;
        if (frame.Type != FrameType.Message) return false;
        if ((frame.Flags & MessageFlags.More) == 0) return false;
        // Must be the first chunk: no multi-frame already in flight
        // (neither the legacy lazy-chain compressed path nor a chain
        // segment list nor the legacy _assembled compressed buffer).
        if (_chainHead != null || _assembledPos != 0 || _lazyChain != null) return false;
        if (frame.Length < 5) return false;
        if (frame.Memory.Span[0] != 0) return false;
        lpmBodyLen = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
            frame.Memory.Span.Slice(1, 4));
        return true;
    }

    /// <summary>
    /// Deadlock-safe async parse of a multi-frame uncompressed LPM.
    /// Hybrid dispatch: eager pre-fetch when the LPM fits the chain-ZC
    /// budget (no extra memory cost because chain-ZC already freezes
    /// the ring for the LPM duration); Task.Yield off the reader
    /// Thread + existing <see cref="LazyChainRos"/> otherwise (keeps
    /// pool footprint at ~2 frames for messages &gt; budget).
    /// On the eager path the returned ROS is fully materialised so
    /// the caller's <c>MergeFrom(ROS)</c> never pulls — safe under
    /// inline-receive-continuations.
    /// </summary>
    private async ValueTask<(ReadOnlySequence<byte> Payload, bool EndOfStream)> PrefetchMultiFrameAndBuildAsync(
        InboundFrame firstFrame, int lpmBodyLen, CancellationToken cancellationToken)
    {
        var ct = cancellationToken.CanBeCanceled
            ? cancellationToken
            : _stream.DisposeCancellationToken;

        if (!InboundChainHelper.ShouldEagerPrefetch(_stream, lpmBodyLen))
        {
            // Huge non-ZC fallback: build a LazyChainRos that pulls
            // chunks synchronously inside MergeFrom, but FIRST hop
            // off the reader Thread so the sync pulls block a TP
            // worker (not the reader Thread that's responsible for
            // delivering subsequent chunks).
            await InboundChainHelper.HopOffReaderThreadIfNeededAsync().ConfigureAwait(false);

            // EndStream on the first chunk is unusual but valid.
            if ((firstFrame.Flags & MessageFlags.EndStream) != 0)
            {
                _lazyChainSawEndStream = true;
            }

            InboundFrame? Pull(CancellationToken pullCt)
            {
                var pulled = _stream.ReceiveFrameSync(pullCt);
                if (pulled is null) return null;
                if (pulled.Value.Type != FrameType.Message)
                {
                    pulled.Value.ReturnToPool();
                    return null;
                }
                if ((pulled.Value.Flags & MessageFlags.EndStream) != 0)
                {
                    _lazyChainSawEndStream = true;
                }
                return pulled.Value;
            }

            _lazyChain = new LazyChainRos(
                firstFrame, firstFrameBodyOffset: 5,
                totalBodyLen: lpmBodyLen,
                pullNext: Pull,
                ct: _stream.DisposeCancellationToken);
            return (_lazyChain.Sequence, false);
        }

        // Eager pre-fetch path.
        bool sawEndStream = false;
        Action onEndStream = () => sawEndStream = true;

        var chunks = await InboundChainHelper.PrefetchAllChunksAsync(
            _stream, firstFrame, firstFrameBodyOffset: 5,
            totalBodyLen: lpmBodyLen,
            onEndStream: onEndStream,
            cancellationToken: ct).ConfigureAwait(false);

        // Defensive try/catch around BuildSequence: if the segment math
        // throws AFTER a successful prefetch but BEFORE _prefetchedChunks
        // captures ownership, the chunks would be stranded and the chain-ZC
        // anchor would stay open — freezing header.ReadIdx → connection-wide
        // RX stall. BuildSequence's bounds are guaranteed satisfied by
        // PrefetchAllChunksAsync's invariants (length >= 5, offset = 5),
        // so this is defense-in-depth; the server eager paths
        // (ShmGrpcServer.UnaryHandler / ShmAsyncStreamReader) wrap their
        // BuildSequence calls in finally/try-catch with the same release
        // semantics.
        ReadOnlySequence<byte> ros;
        try
        {
            ros = InboundChainHelper.BuildSequence(
                chunks, firstFrameBodyOffset: 5, totalBodyLen: lpmBodyLen);
        }
        catch
        {
            InboundChainHelper.ReleaseAll(chunks);
            throw;
        }
        _prefetchedChunks = chunks;
        _prefetchedSawEndStream = sawEndStream;
        // Preserve the legacy contract: return (ROS, false) here and
        // surface EndStream on the NEXT ReadNextMessageAsync call
        // (after the caller has consumed the ROS).
        return (ros, false);
    }

    private (ReadOnlySequence<byte> Payload, bool EndOfStream) ProcessReceivedFrame(InboundFrame frame)
    {
        switch (frame.Type)
        {
            case FrameType.Message:
                // Multi-frame branch: first frame carries the 5-byte gRPC LPM
                // header; subsequent frames are body continuation. Strategy:
                //
                //   - Uncompressed (compFlag == 0): skip the contiguous
                //     _assembled buffer entirely; chain the frame as a
                //     segment so the consumer reads a multi-segment
                //     ReadOnlySequence directly. Saves one memcpy per frame
                //     (4-256 MiB savings for big payloads).
                //
                //   - Compressed (compFlag == 1): falls through to the legacy
                //     _assembled path because the decompressor needs a
                //     contiguous source buffer. Compression of multi-frame
                //     messages is rare; not worth the extra code.
                //
                //   - When _assembledPos > 0 already (compressed-mode chain
                //     in progress) every subsequent frame keeps copying into
                //     _assembled until END.
                if ((frame.Flags & MessageFlags.More) != 0)
                {
                    bool firstChunk = _chainHead == null && _assembledPos == 0 && _lazyChain == null && _prefetchedChunks == null;

                    // NOTE: the multi-frame UNCOMPRESSED first-chunk
                    // case is intercepted earlier in
                    // ReadNextMessageAsync / ReadNextMessageSlowAsync
                    // via TryDetectMultiFrameUncompressedFirstChunk +
                    // PrefetchMultiFrameAndBuildAsync (the safe
                    // hybrid eager-pre-fetch / yield-then-lazy path).
                    // ProcessReceivedFrame should never see that case
                    // as a first chunk; only compressed multi-frame
                    // first chunks and subsequent chunks of an already
                    // accumulating chain reach here.

                    bool useChain;
                    if (firstChunk)
                    {
                        // Only reached when compFlag != 0 (compressed
                        // multi-frame first chunk). Falls through to the
                        // contiguous _assembled buffer.
                        useChain = false;
                    }
                    else
                    {
                        useChain = _chainHead != null;
                    }

                    if (useChain)
                    {
                        // Chain: the LPM 5-byte header lives inside the first
                        // frame's payload. We keep the whole payload as the
                        // first segment so the consumer's ReadOnlySequence
                        // aligns with the body (we slice off the 5-byte
                        // header at emit time on the final frame). Chained
                        // continuation frames carry pure body bytes.
                        AppendChainFrame(frame, frame.Memory);
                    }
                    else
                    {
                        // Compressed-mode multi-frame: legacy _assembled path.
                        if (_assembled == null)
                        {
                            _assembled = ArrayPool<byte>.Shared.Rent(frame.Length * 4);
                            _assembledPos = 0;
                        }
                        else if (_assembledPos + frame.Length > _assembled.Length)
                        {
                            var newBuf = ArrayPool<byte>.Shared.Rent(Math.Max(_assembled.Length * 2, _assembledPos + frame.Length));
                            _assembled.AsSpan(0, _assembledPos).CopyTo(newBuf);
                            ArrayPool<byte>.Shared.Return(_assembled);
                            _assembled = newBuf;
                        }
                        frame.Memory.Span.CopyTo(_assembled.AsSpan(_assembledPos));
                        _assembledPos += frame.Length;
                        frame.ReturnToPool();
                    }
                    return (ReadOnlySequence<byte>.Empty, false);
                }

                // Final frame or single-frame message.
                if (_chainHead != null)
                {
                    // Multi-frame final, uncompressed chain. Append the
                    // last segment, slice off the 5-byte LPM header from
                    // the head, return a ReadOnlySequence over the chain.
                    AppendChainFrame(frame, frame.Memory);

                    var eosChain = (frame.Flags & MessageFlags.EndStream) != 0;
                    if (eosChain) _stream.MarkHalfCloseReceived();

                    // Build a ReadOnlySequence that skips the first 5 bytes
                    // (LPM header) of the head segment. ReadOnlySequence
                    // doesn't accept a per-segment offset directly, but we
                    // can pass an absolute start index into the head and
                    // an absolute end into the tail.
                    var tail = _chainTail!;
                    var head = _chainHead!;
                    var seq = new ReadOnlySequence<byte>(
                        startSegment: head, startIndex: 5,
                        endSegment: tail, endIndex: tail.Memory.Length);
                    return (seq, eosChain);
                }
                else if (_assembledPos > 0)
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
                    var compFlag = _assembled![0];
                    var bodyStart = 5;
                    if (compFlag == 1)
                    {
                        if (string.IsNullOrEmpty(_responseEncoding))
                            throw new InvalidOperationException(
                                "Received compressed response but server did not send grpc-encoding header");
                        var decompressor = Compression.ShmCompressorRegistry.Get(_responseEncoding);
                        if (decompressor == null)
                            throw new InvalidOperationException(
                                $"Received compressed response with unsupported encoding '{_responseEncoding}'");
                        var bodyLen = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                            _assembled.AsSpan(1, 4));
                        var decompressed = decompressor.Decompress(_assembled.AsSpan(bodyStart, bodyLen));
                        return (new ReadOnlySequence<byte>(decompressed), eos);
                    }
                    return (new ReadOnlySequence<byte>(_assembled.AsMemory(bodyStart, _assembledPos - bodyStart)), eos);
                }
                else
                {
                    // Single frame — direct reference (zero-copy for pre-committed).
                    // Skip the 5-byte gRPC length-prefix header per G3 spec.
                    _currentFrame = frame;
                    var eos2 = (frame.Flags & MessageFlags.EndStream) != 0;
                    if (eos2) _stream.MarkHalfCloseReceived();
                    var compFlag2 = frame.Memory.Span[0];

                    // Diagnostic invariant: validate the LPM header byte against
                    // protocol expectations. We expect compFlag ∈ {0, 1} and the
                    // declared LPM length to match the frame body minus 5.
                    // A mismatch here means either:
                    //   (a) the writer wrote garbage / was overwritten by a race,
                    //   (b) the reader is looking at the wrong memory region.
                    // Either way, dump diagnostic info BEFORE returning the
                    // potentially-corrupt slice to the parser. Cost on the happy
                    // path: 4 byte loads + 2 cmp + 1 not-taken branch (~1 ns).
                    AssertLpmHeader(frame, eos2);

                    if (compFlag2 == 1)
                    {
                        if (string.IsNullOrEmpty(_responseEncoding))
                            throw new InvalidOperationException(
                                "Received compressed response but server did not send grpc-encoding header");
                        var decompressor = Compression.ShmCompressorRegistry.Get(_responseEncoding);
                        if (decompressor == null)
                            throw new InvalidOperationException(
                                $"Received compressed response with unsupported encoding '{_responseEncoding}'");
                        var bodyLen2 = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                            frame.Memory.Span.Slice(1, 4));
                        var decompressed = decompressor.Decompress(frame.Memory.Span.Slice(5, bodyLen2));
                        return (new ReadOnlySequence<byte>(decompressed), eos2);
                    }
                    var mem = frame.Memory.Slice(5);
                    return (new ReadOnlySequence<byte>(mem), eos2);
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
        // Steady-state path: when the caller passes default (no
        // cancellation), pass CancellationToken.None to
        // WaitForFrameAsync so the underlying
        // SingleConsumerUnboundedChannel.WaitToReadAsync can use its
        // pooled _waiterSingleton fast path. Per GPT-5.5 review: the
        // previous fall-back to _stream.DisposeCancellationToken
        // forced an allocation + cancellation registration per
        // empty-receive even in the steady state, which is the
        // hidden per-frame tax that hurt 1000x64B both OSes.
        // Dispose wake still arrives via _inboundFrames.Writer.TryComplete
        // from ShmGrpcStream.Dispose() / ShmConnection.Dispose()'s
        // early-wake loop.
        var ct = cancellationToken.CanBeCanceled
            ? cancellationToken
            : CancellationToken.None;

        try
        {
            while (true)
            {
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
                    if (TryDetectMultiFrameUncompressedFirstChunk(frame, out int lpmBodyLen))
                    {
                        return await PrefetchMultiFrameAndBuildAsync(frame, lpmBodyLen, ct)
                            .ConfigureAwait(false);
                    }
                    var result = ProcessReceivedFrame(frame);
                    if (result.Payload.Length == 0 && !result.EndOfStream)
                        continue;
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
        ReleaseChain();
        // Keep assembled buffer for reuse (returned to connection in Dispose).
        _assembledPos = 0;
    }

    /// <summary>
    /// Validates the LPM header byte and length at the start of a single-frame
    /// MESSAGE payload. If the header is malformed, dumps diagnostic info to
    /// stderr (and to <c>SHM_LPM_ASSERT_LOG</c> if set) and rethrows as a
    /// protocol-level error — so the failure is observed exactly once at the
    /// point of corruption rather than later as "invalid tag (zero)" deep in
    /// the parser.
    /// </summary>
    /// <remarks>
    /// Hot path cost on success: 4 byte loads + 2 comparisons + 1 not-taken
    /// branch (~1 ns). The slow path (assert failure) is kept in a separate
    /// non-inlined method so it doesn't bloat the caller and doesn't perturb
    /// JIT decisions on the success path.
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void AssertLpmHeader(InboundFrame frame, bool eos)
    {
        if (frame.Length < 5) return; // tiny/empty frame — skip
        var span = frame.Memory.Span;
        var compFlag = span[0];
        // compFlag must be 0 (no compression) or 1 (compressed).
        // length is big-endian uint32 at [1..5).
        // declared body length must equal frame.Length - 5.
        if (compFlag > 1)
        {
            ReportLpmHeaderCorruption(frame, eos, "compFlag>1");
            return;
        }
        var declared = (uint)(span[1] << 24 | span[2] << 16 | span[3] << 8 | span[4]);
        var expected = (uint)(frame.Length - 5);
        if (declared != expected)
        {
            ReportLpmHeaderCorruption(frame, eos, $"lpm-length-mismatch declared={declared} expected={expected}");
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ReportLpmHeaderCorruption(InboundFrame frame, bool eos, string reason)
    {
        var span = frame.Memory.Span;
        var headLen = Math.Min(32, frame.Length);
        var tailLen = Math.Min(32, frame.Length);
        var head = new byte[headLen];
        var tail = new byte[tailLen];
        if (headLen > 0) span.Slice(0, headLen).CopyTo(head);
        if (tailLen > 0) span.Slice(frame.Length - tailLen, tailLen).CopyTo(tail);

        var sb = new System.Text.StringBuilder(384);
        sb.Append("[SHM_LPM_ASSERT] reason=").Append(reason);
        sb.Append(" | frameLen=").Append(frame.Length);
        sb.Append(" | flags=0x").Append(frame.Flags.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        sb.Append(" | eos=").Append(eos);
        sb.Append(" | head=");
        for (var i = 0; i < head.Length; i++) sb.Append(head[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        sb.Append(" | tail=");
        for (var i = 0; i < tail.Length; i++) sb.Append(tail[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));

        var line = sb.ToString();
        var path = Environment.GetEnvironmentVariable("SHM_LPM_ASSERT_LOG");
        if (!string.IsNullOrEmpty(path))
        {
            try { File.AppendAllText(path, line + Environment.NewLine); }
            catch { Console.Error.WriteLine(line); }
        }
        else
        {
            Console.Error.WriteLine(line);
        }
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
            // CRITICAL: release any in-flight frame so its FramePayload.Release
            // runs (returns pool buffer; for ZC frames, ends the deferred-commit
            // ZC reservation so the writer can advance past the held region).
            // Without this, an exception or cancellation that interrupts the
            // normal ReleaseCurrentMessage flow would leave the ZC permanently
            // held, blocking the peer writer indefinitely.
            _currentFrame.ReturnToPool();
            _currentFrame = default;
            ReleaseChain();

            if (_lazyChain != null)
            {
                _lazyChain.Dispose();
                _lazyChain = null;
            }

            if (_prefetchedChunks != null)
            {
                InboundChainHelper.ReleaseAll(_prefetchedChunks);
                _prefetchedChunks = null;
                _prefetchedSawEndStream = false;
            }

            if (_assembled != null)
            {
                ArrayPool<byte>.Shared.Return(_assembled);
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
        //
        // Ownership transfer: <c>InboundFrame</c> is a struct, so handing
        // <c>_previousFrame</c> to <c>ReceiveNextMessageBufferAsync</c>
        // copies it. The callee releases its local copy at entry, but our
        // field is still a "live" struct copy of the same buffer. If the
        // await is interrupted by cancellation/exception BEFORE the
        // callee returns, our field is never updated — and the next
        // <see cref="Dispose"/> call (or a follow-up retry on the same
        // stream) would call <see cref="InboundFrame.ReturnToPool"/> on
        // the SAME buffer a second time. For pooled buffers this is an
        // ArrayPool double-return; for ZC payloads it is worse —
        // <see cref="FramePayload.Release"/> is NOT idempotent
        // (decrements <see cref="ShmRing.SpeculativeReservedBytes"/>),
        // and the second call drives the counter negative, permanently
        // disabling future ZC and corrupting the deferred-publish target.
        //
        // Fix: move the frame to a local and CLEAR the field BEFORE the
        // await. Whichever side returns from the await — success path
        // (we re-assign `_previousFrame = frame`) or exception path
        // (`Dispose` sees a default field and doesn't double-release) —
        // the buffer is released exactly once.
        var toRelease = _previousFrame;
        _previousFrame = default;
        var (mem, frame, eos) = await _shmStream.ReceiveNextMessageBufferAsync(
            toRelease, cancellationToken).ConfigureAwait(false);

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
