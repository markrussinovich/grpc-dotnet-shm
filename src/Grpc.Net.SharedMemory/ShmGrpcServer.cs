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
using System.Runtime.Versioning;
using Google.Protobuf;
using Grpc.Core;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// A standalone gRPC server that uses shared memory transport directly.
/// Per RFC A73, the transport exposes gRPC semantics (headers, messages, trailers)
/// and hides HTTP/2 semantics. HTTP/2 is only used conceptually for the dialer
/// connection setup via the control segment protocol.
/// </summary>
/// <example>
/// <code>
/// var server = new ShmGrpcServer("my_segment");
/// server.MapUnary&lt;HelloRequest, HelloReply&gt;(
///     "/greet.Greeter/SayHello",
///     (request, context) => Task.FromResult(new HelloReply { Message = "Hello " + request.Name }));
/// await server.RunAsync();
/// </code>
/// </example>
public sealed class ShmGrpcServer : IAsyncDisposable
{
    /// <summary>
    /// Cached 5-byte gRPC LPM header for empty messages (compression=0, length=0).
    /// Avoids per-call allocation on the empty-message fast path.
    /// </summary>
    private static readonly byte[] EmptyGrpcLpm = new byte[5];

    private readonly string _segmentName;
    private readonly ulong _ringCapacity;
    private readonly uint _maxStreams;
    private readonly bool _singleStreamMode;
    private readonly bool _pooledDeserialization;
    private readonly int _maxReceiveMessageSize;
    private readonly int _maxSendMessageSize;
    private readonly Compression.ShmCompressionOptions? _compressionOptions;
    private readonly Dictionary<string, IMethodHandler> _methods = new(StringComparer.Ordinal);
    private ShmControlListener? _listener;
    private readonly CancellationTokenSource _shutdownCts = new();
    private int _disposed;

    /// <summary>
    /// Creates a new SHM gRPC server.
    /// </summary>
    /// <param name="segmentName">The shared memory segment name clients will connect to.</param>
    /// <param name="ringCapacity">Ring buffer capacity per connection (default: 64MB).</param>
    /// <param name="maxStreams">Maximum concurrent streams per connection (default: 100).</param>
    /// <param name="singleStreamMode">When true, enables single-stream optimizations.</param>
    /// <param name="pooledDeserialization">When true, uses ArrayPool for protobuf bytes fields.</param>
    /// <param name="maxReceiveMessageSize">Maximum receive message size in bytes (default: 4MB, 0 = unlimited).</param>
    /// <param name="maxSendMessageSize">Maximum send message size in bytes (default: unlimited).</param>
    /// <param name="compressionOptions">Optional compression options for send/receive.</param>
    public ShmGrpcServer(string segmentName, ulong ringCapacity = 64 * 1024 * 1024, uint maxStreams = 100,
        bool singleStreamMode = false, bool pooledDeserialization = false,
        int maxReceiveMessageSize = 4 * 1024 * 1024, int maxSendMessageSize = int.MaxValue,
        Compression.ShmCompressionOptions? compressionOptions = null)
    {
        _segmentName = segmentName ?? throw new ArgumentNullException(nameof(segmentName));
        _ringCapacity = ringCapacity;
        _maxStreams = maxStreams;
        _singleStreamMode = singleStreamMode;
        _pooledDeserialization = pooledDeserialization;
        _maxReceiveMessageSize = maxReceiveMessageSize;
        _maxSendMessageSize = maxSendMessageSize;
        _compressionOptions = compressionOptions;
    }

    /// <summary>
    /// Registers a unary RPC method handler.
    /// </summary>
    public ShmGrpcServer MapUnary<TReq, TResp>(
        string method,
        Func<TReq, ServerCallContext, Task<TResp>> handler)
        where TReq : class, IMessage<TReq>, new()
        where TResp : class, IMessage<TResp>
    {
        _methods[method] = new UnaryHandler<TReq, TResp>(handler);
        return this;
    }

    /// <summary>
    /// Registers a server-streaming RPC method handler.
    /// </summary>
    public ShmGrpcServer MapServerStreaming<TReq, TResp>(
        string method,
        Func<TReq, IServerStreamWriter<TResp>, ServerCallContext, Task> handler)
        where TReq : class, IMessage<TReq>, new()
        where TResp : class, IMessage<TResp>
    {
        _methods[method] = new ServerStreamingHandler<TReq, TResp>(handler);
        return this;
    }

    /// <summary>
    /// Registers a client-streaming RPC method handler.
    /// </summary>
    public ShmGrpcServer MapClientStreaming<TReq, TResp>(
        string method,
        Func<IAsyncStreamReader<TReq>, ServerCallContext, Task<TResp>> handler)
        where TReq : class, IMessage<TReq>, new()
        where TResp : class, IMessage<TResp>
    {
        _methods[method] = new ClientStreamingHandler<TReq, TResp>(handler);
        return this;
    }

    /// <summary>
    /// Registers a bidirectional-streaming RPC method handler.
    /// </summary>
    public ShmGrpcServer MapDuplexStreaming<TReq, TResp>(
        string method,
        Func<IAsyncStreamReader<TReq>, IServerStreamWriter<TResp>, ServerCallContext, Task> handler)
        where TReq : class, IMessage<TReq>, new()
        where TResp : class, IMessage<TResp>
    {
        _methods[method] = new DuplexStreamingHandler<TReq, TResp>(handler);
        return this;
    }

    /// <summary>
    /// Starts the server and blocks until cancellation is requested.
    /// </summary>
    /// <param name="cancellationToken">Token to trigger graceful shutdown.</param>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        var ct = linkedCts.Token;

        _listener = new ShmControlListener(_segmentName, _ringCapacity, _maxStreams);

        Console.WriteLine($"SHM gRPC server listening on segment: {_segmentName}");

        try
        {
            await foreach (var connection in _listener.AcceptConnectionsAsync(ct))
            {
                // Handle each connection concurrently
                _ = HandleConnectionAsync(connection, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    /// <summary>
    /// Initiates graceful shutdown.
    /// </summary>
    public void Shutdown()
    {
        _shutdownCts.Cancel();
    }

    private async Task HandleConnectionAsync(ShmConnection connection, CancellationToken ct)
    {
        // Per-connection singleStreamMode negotiation:
        // Enable if BOTH server allows it AND client requested it.
        // Store the negotiated result back on the connection so handlers
        // can read it via stream.Connection.SingleStreamMode.
        var negotiated = _singleStreamMode && connection.SingleStreamMode;
        connection.SingleStreamMode = negotiated;
        if (negotiated)
        {
            connection.ZeroCopyRead = true;
            connection.FrameWriter?.EnableSingleStreamMode();
        }

        var activeHandlers = new List<Task>();

        try
        {
            await foreach (var stream in connection.AcceptStreamsAsync(ct))
            {
                // Handle each stream concurrently
                var task = HandleStreamAsync(stream, ct);
                activeHandlers.Add(task);

                // Prune completed tasks periodically to avoid unbounded list growth.
                if (activeHandlers.Count > 64)
                {
                    activeHandlers.RemoveAll(t => t.IsCompleted);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Connection error: {ex.Message}");
        }
        finally
        {
            // Wait for in-flight handlers to finish before tearing down the
            // connection. This gives handlers time to send final trailers.
            if (activeHandlers.Count > 0)
            {
                try
                {
#pragma warning disable CA2016 // Intentionally not forwarding ct — best-effort wait for handlers
                    await Task.WhenAll(activeHandlers).WaitAsync(TimeSpan.FromSeconds(5));
#pragma warning restore CA2016
                }
                catch { /* best-effort; handlers already logged their own errors */ }
            }

            await connection.DisposeAsync();
        }
    }

    private async Task HandleStreamAsync(ShmGrpcStream stream, CancellationToken ct)
    {
        try
        {
            var headers = stream.RequestHeaders;
            if (headers == null)
            {
                await SendErrorTrailersAsync(stream, StatusCode.Internal, "No request headers received");
                return;
            }

            var method = headers.Method;
            if (string.IsNullOrEmpty(method) || !_methods.TryGetValue(method, out var handler))
            {
                await SendErrorTrailersAsync(stream, StatusCode.Unimplemented, $"Method not found: {method}");
                return;
            }

            var context = new ShmServerCallContext(stream, headers, ct);

            // Extract grpc-encoding from request metadata for decompression
            string? grpcEncoding = null;
            if (headers.Metadata != null)
            {
                foreach (var kv in headers.Metadata)
                {
                    if (string.Equals(kv.Key, "grpc-encoding", StringComparison.OrdinalIgnoreCase)
                        && kv.Values.Count > 0)
                    {
                        grpcEncoding = System.Text.Encoding.UTF8.GetString(kv.Values[0]);
                        break;
                    }
                }
            }

            var cfg = new HandlerConfig(
                _pooledDeserialization,
                _maxReceiveMessageSize,
                _maxSendMessageSize,
                _compressionOptions,
                grpcEncoding);

            // If server will compress responses, set grpc-encoding on the stream
            if (_compressionOptions != null && _compressionOptions.Enabled)
            {
                var sendCompressor = _compressionOptions.GetSendCompressor();
                if (sendCompressor != null && !sendCompressor.IsIdentity)
                {
                    stream.SetResponseEncoding(sendCompressor.Name);
                }
            }

            try
            {
                await handler.HandleAsync(stream, context, cfg, context.CancellationToken);
            }
            catch (RpcException ex)
            {
                await SendErrorTrailersAsync(stream, ex.StatusCode, ex.Status.Detail,
                    ex.Trailers?.Count > 0 ? ex.Trailers : context.ResponseTrailers);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await SendErrorTrailersAsync(stream, StatusCode.Cancelled, "Server shutting down",
                    context.ResponseTrailers);
            }
            catch (OperationCanceledException)
            {
                // RPC-level cancellation (deadline exceeded or client cancel)
                var code = context.IsDeadlineExceeded
                    ? StatusCode.DeadlineExceeded
                    : StatusCode.Cancelled;
                await SendErrorTrailersAsync(stream, code,
                    code == StatusCode.DeadlineExceeded ? "Deadline exceeded" : "Cancelled",
                    context.ResponseTrailers);
            }
            catch (Exception ex)
            {
                await SendErrorTrailersAsync(stream, StatusCode.Internal, ex.Message,
                    context.ResponseTrailers);
            }
        }
        catch
        {
            // Best effort - stream may already be broken
        }
        finally
        {
            stream.Dispose();
        }
    }

    private static async Task SendErrorTrailersAsync(ShmGrpcStream stream, StatusCode code, string? message,
        Metadata? metadata = null)
    {
        try
        {
            if (stream.ResponseHeaders == null)
            {
                await stream.SendResponseHeadersAsync();
            }
            await stream.SendTrailersAsync(code, message, metadata);
        }
        catch
        {
            // Best effort
        }
    }

    /// <summary>
    /// Serialises a protobuf message into a pooled buffer and sends it over the
    /// stream.  Avoids the per-message heap allocation (and LOH pressure for
    /// payloads &ge; 85 KB) that <c>IMessage.ToByteArray()</c> causes.
    /// </summary>
    private static Task SendProtobufMessageAsync(
        ShmGrpcStream stream, IMessage message, CancellationToken ct)
    {
        return SendProtobufMessageAsync(stream, message, null, int.MaxValue, ct);
    }

    private static Task SendProtobufMessageAsync(
        ShmGrpcStream stream, IMessage message,
        Compression.ShmCompressionOptions? compression,
        int maxSendMessageSize, CancellationToken ct)
    {
        var size = message.CalculateSize();
        if (size == 0)
        {
            return stream.SendMessageAsync(EmptyGrpcLpm, ct);
        }

        if (maxSendMessageSize > 0 && maxSendMessageSize < int.MaxValue && size > maxSendMessageSize)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted,
                $"Sending message exceeds the maximum configured message size ({size} vs {maxSendMessageSize})"));
        }

        // Serialize protobuf first
        var protoBuffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            message.WriteTo(protoBuffer.AsSpan(0, size));
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(protoBuffer);
            throw;
        }

        // Optionally compress if compression is configured and payload is large enough
        var compressor = compression?.GetSendCompressor();
        if (compressor != null && !compressor.IsIdentity && compression!.ShouldCompress(size))
        {
            var compressed = compressor.Compress(protoBuffer.AsSpan(0, size));
            ArrayPool<byte>.Shared.Return(protoBuffer);

            var framedBuf = ArrayPool<byte>.Shared.Rent(5 + compressed.Length);
            framedBuf[0] = 1; // compressed
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                framedBuf.AsSpan(1, 4), (uint)compressed.Length);
            compressed.AsSpan().CopyTo(framedBuf.AsSpan(5));
            return stream.SendMessageZeroCopyAsync(
                framedBuf.AsMemory(0, 5 + compressed.Length), framedBuf, ct);
        }

        // No compression — write LPM header + protobuf
        var buffer = ArrayPool<byte>.Shared.Rent(5 + size);
        buffer[0] = 0; // no compression
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            buffer.AsSpan(1, 4), (uint)size);
        protoBuffer.AsSpan(0, size).CopyTo(buffer.AsSpan(5));
        ArrayPool<byte>.Shared.Return(protoBuffer);

        return stream.SendMessageZeroCopyAsync(buffer.AsMemory(0, 5 + size), buffer, ct);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdownCts.Cancel();

        if (_listener != null)
        {
            await _listener.DisposeAsync();
        }

        _shutdownCts.Dispose();
    }

    #region Method Handlers

    /// <summary>Per-RPC configuration passed from the server to handlers.</summary>
    private readonly record struct HandlerConfig(
        bool PooledDeserialization,
        int MaxReceiveMessageSize,
        int MaxSendMessageSize,
        Compression.ShmCompressionOptions? Compression,
        string? GrpcEncoding);

    private interface IMethodHandler
    {
        Task HandleAsync(ShmGrpcStream stream, ShmServerCallContext context, HandlerConfig cfg, CancellationToken ct);
    }

    private sealed class UnaryHandler<TReq, TResp> : IMethodHandler
        where TReq : class, IMessage<TReq>, new()
        where TResp : class, IMessage<TResp>
    {
        private readonly Func<TReq, ServerCallContext, Task<TResp>> _handler;
        private readonly MessageParser<TReq> _parser = new(() => new TReq());

        public UnaryHandler(Func<TReq, ServerCallContext, Task<TResp>> handler)
        {
            _handler = handler;
        }

        public async Task HandleAsync(ShmGrpcStream stream, ShmServerCallContext context, HandlerConfig cfg, CancellationToken ct)
        {
            var request = await ReadSingleMessageAsync(stream, _parser, cfg.PooledDeserialization, cfg.MaxReceiveMessageSize, cfg.Compression, cfg.GrpcEncoding, ct);

            var response = await _handler(request, context);

            // In singleStreamMode with one active stream, serialize the
            // protobuf response directly into the ring buffer — no
            // intermediate byte[] for any message size.
            // WriteInlineDirectMultiFrame handles single-frame (zero-copy
            // contiguous) and multi-frame (RingFrameStream) transparently.
            // Fallback: ExecuteInline when TryPause fails.
            // Skip inline path when compression is enabled — inline writes
            // are uncompressed (zero-copy to ring), but compressed responses
            // need the SendProtobufMessageAsync path which handles compress.
            var _responseSize = ((IMessage)response).CalculateSize();
            var _sc = cfg.Compression?.ShouldCompress(_responseSize) == true
                && cfg.Compression.GetSendCompressor()?.IsIdentity == false;
            if (!_sc && stream.Connection.SingleStreamMode && stream.Connection.ActiveStreamCount <= 1)
            {
                var size = _responseSize;
                if (cfg.MaxSendMessageSize > 0 && cfg.MaxSendMessageSize < int.MaxValue && size > cfg.MaxSendMessageSize)
                    throw new RpcException(new Status(StatusCode.ResourceExhausted,
                        $"Sending message exceeds limit ({size} vs {cfg.MaxSendMessageSize})"));
                var writer = stream.Connection.FrameWriter!;
                var msg = (IMessage)response;

                if (writer.TryPauseWriterLoop())
                {
                    try
                    {
                        if (!context.HeadersSent)
                        {
                            stream.SendResponseHeadersInline(writer);
                            context.MarkHeadersSent();
                        }
                        if (size > 0)
                            writer.WriteInlineDirectMultiFrame(stream.StreamId, size, msg, 0, default);
                        else
                            writer.WriteInline(stream.StreamId, stackalloc byte[5], 0, default);
                        stream.SendTrailersInline(writer, context.Status.StatusCode,
                            context.Status.Detail, context.ResponseTrailers);
                    }
                    finally
                    {
                        writer.ResumeWriterLoop();
                    }
                    return;
                }

                // TryPause failed: ExecuteInline with intermediate buffer.
                byte[] serializedBuffer;
                int serializedSize;
                if (size > 0)
                {
                    var cached = stream.Connection.CachedWriteBuffer;
                    if (cached != null && cached.Length >= 5 + size)
                        serializedBuffer = cached;
                    else
                    {
                        if (cached != null) ArrayPool<byte>.Shared.Return(cached);
                        serializedBuffer = ArrayPool<byte>.Shared.Rent(5 + size);
                        stream.Connection.CachedWriteBuffer = serializedBuffer;
                    }
                    serializedBuffer[0] = 0;
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                        serializedBuffer.AsSpan(1, 4), (uint)size);
                    msg.WriteTo(serializedBuffer.AsSpan(5, size));
                    serializedSize = 5 + size;
                }
                else
                {
                    serializedBuffer = EmptyGrpcLpm;
                    serializedSize = 5;
                }

                writer.ExecuteInline(() =>
                {
                    if (!context.HeadersSent)
                    {
                        stream.SendResponseHeadersInline(writer);
                        context.MarkHeadersSent();
                    }
                    writer.WriteInline(stream.StreamId,
                        serializedBuffer.AsSpan(0, serializedSize), 0, default);
                    stream.SendTrailersInline(writer, context.Status.StatusCode,
                        context.Status.Detail, context.ResponseTrailers);
                });
                return;
            }

            // Fallback path: ensure headers sent, then use WriterLoop queue.
            await context.EnsureResponseHeadersSentAsync();
            await SendProtobufMessageAsync(stream, response, cfg.Compression, cfg.MaxSendMessageSize, ct);
            await stream.SendTrailersAsync(context.Status.StatusCode, context.Status.Detail, context.ResponseTrailers);
        }
    }

    private sealed class ServerStreamingHandler<TReq, TResp> : IMethodHandler
        where TReq : class, IMessage<TReq>, new()
        where TResp : class, IMessage<TResp>
    {
        private readonly Func<TReq, IServerStreamWriter<TResp>, ServerCallContext, Task> _handler;
        private readonly MessageParser<TReq> _parser = new(() => new TReq());

        public ServerStreamingHandler(Func<TReq, IServerStreamWriter<TResp>, ServerCallContext, Task> handler)
            => _handler = handler;

        public async Task HandleAsync(ShmGrpcStream stream, ShmServerCallContext context, HandlerConfig cfg, CancellationToken ct)
        {
            var request = await ReadSingleMessageAsync(stream, _parser, cfg.PooledDeserialization, cfg.MaxReceiveMessageSize, cfg.Compression, cfg.GrpcEncoding, ct);

            var singleStream = stream.Connection.SingleStreamMode;

            if (!singleStream || stream.Connection.ActiveStreamCount > 1)
                await context.EnsureResponseHeadersSentAsync();

            var writer = new ShmServerStreamWriter<TResp>(stream, context, singleStream, cfg.Compression, cfg.MaxSendMessageSize);
            try
            {
                await _handler(request, writer, context);
            }
            finally
            {
                writer.ReturnWriteBuffer();
            }

            // In singleStreamMode, inline trailers to avoid queue overhead.
            if (singleStream && stream.Connection.ActiveStreamCount <= 1)
            {
                var fw = stream.Connection.FrameWriter!;
                if (fw.TryPauseWriterLoop())
                {
                    try
                    {
                        stream.SendTrailersInline(fw, context.Status.StatusCode,
                            context.Status.Detail, context.ResponseTrailers);
                    }
                    finally
                    {
                        fw.ResumeWriterLoop();
                    }
                    return;
                }
            }

            // Send trailers
            await stream.SendTrailersAsync(
                context.Status.StatusCode,
                context.Status.Detail,
                context.ResponseTrailers);
        }
    }

    private sealed class ClientStreamingHandler<TReq, TResp> : IMethodHandler
        where TReq : class, IMessage<TReq>, new()
        where TResp : class, IMessage<TResp>
    {
        private readonly Func<IAsyncStreamReader<TReq>, ServerCallContext, Task<TResp>> _handler;

        public ClientStreamingHandler(Func<IAsyncStreamReader<TReq>, ServerCallContext, Task<TResp>> handler)
            => _handler = handler;

        public async Task HandleAsync(ShmGrpcStream stream, ShmServerCallContext context, HandlerConfig cfg, CancellationToken ct)
        {
            var singleStream = stream.Connection.SingleStreamMode;
            if (!singleStream || stream.Connection.ActiveStreamCount > 1)
                await context.EnsureResponseHeadersSentAsync();

            using var reader = new ShmAsyncStreamReader<TReq>(stream, cfg);
            var response = await _handler(reader, context);

            var _responseSize2 = ((IMessage)response).CalculateSize();
            var _sc2 = cfg.Compression?.ShouldCompress(_responseSize2) == true
                && cfg.Compression.GetSendCompressor()?.IsIdentity == false;
            if (!_sc2 && singleStream && stream.Connection.ActiveStreamCount <= 1)
            {
                var size = _responseSize2;
                if (cfg.MaxSendMessageSize > 0 && cfg.MaxSendMessageSize < int.MaxValue && size > cfg.MaxSendMessageSize)
                    throw new RpcException(new Status(StatusCode.ResourceExhausted,
                        $"Sending message exceeds limit ({size} vs {cfg.MaxSendMessageSize})"));
                var writer = stream.Connection.FrameWriter!;
                var msg = (IMessage)response;

                if (writer.TryPauseWriterLoop())
                {
                    try
                    {
                        if (!context.HeadersSent)
                        {
                            stream.SendResponseHeadersInline(writer);
                            context.MarkHeadersSent();
                        }
                        if (size > 0)
                            writer.WriteInlineDirectMultiFrame(stream.StreamId, size, msg, 0, default);
                        else
                            writer.WriteInline(stream.StreamId, stackalloc byte[5], 0, default);
                        stream.SendTrailersInline(writer, context.Status.StatusCode,
                            context.Status.Detail, context.ResponseTrailers);
                    }
                    finally
                    {
                        writer.ResumeWriterLoop();
                    }
                    return;
                }

                // TryPause failed: ExecuteInline with intermediate buffer.
                byte[] serializedBuffer;
                int serializedSize;
                if (size > 0)
                {
                    var cached = stream.Connection.CachedWriteBuffer;
                    if (cached != null && cached.Length >= 5 + size)
                        serializedBuffer = cached;
                    else
                    {
                        if (cached != null) ArrayPool<byte>.Shared.Return(cached);
                        serializedBuffer = ArrayPool<byte>.Shared.Rent(5 + size);
                        stream.Connection.CachedWriteBuffer = serializedBuffer;
                    }
                    serializedBuffer[0] = 0;
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                        serializedBuffer.AsSpan(1, 4), (uint)size);
                    msg.WriteTo(serializedBuffer.AsSpan(5, size));
                    serializedSize = 5 + size;
                }
                else
                {
                    serializedBuffer = EmptyGrpcLpm;
                    serializedSize = 5;
                }

                writer.ExecuteInline(() =>
                {
                    if (!context.HeadersSent)
                    {
                        stream.SendResponseHeadersInline(writer);
                        context.MarkHeadersSent();
                    }
                    writer.WriteInline(stream.StreamId, serializedBuffer.AsSpan(0, serializedSize), 0, default);
                    stream.SendTrailersInline(writer, context.Status.StatusCode,
                        context.Status.Detail, context.ResponseTrailers);
                });
                return;
            }

            await SendProtobufMessageAsync(stream, response, cfg.Compression, cfg.MaxSendMessageSize, ct);
            await stream.SendTrailersAsync(
                context.Status.StatusCode,
                context.Status.Detail,
                context.ResponseTrailers);
        }
    }

    private sealed class DuplexStreamingHandler<TReq, TResp> : IMethodHandler
        where TReq : class, IMessage<TReq>, new()
        where TResp : class, IMessage<TResp>
    {
        private readonly Func<IAsyncStreamReader<TReq>, IServerStreamWriter<TResp>, ServerCallContext, Task> _handler;

        public DuplexStreamingHandler(Func<IAsyncStreamReader<TReq>, IServerStreamWriter<TResp>, ServerCallContext, Task> handler)
            => _handler = handler;

        public async Task HandleAsync(ShmGrpcStream stream, ShmServerCallContext context, HandlerConfig cfg, CancellationToken ct)
        {
            var singleStream = stream.Connection.SingleStreamMode;
            // Headers: if truly single stream (1 active), WriteAsync sends
            // headers inline. Otherwise send eagerly via queue.
            if (!singleStream || stream.Connection.ActiveStreamCount > 1)
                await context.EnsureResponseHeadersSentAsync();

            using var reader = new ShmAsyncStreamReader<TReq>(stream, cfg);
            var writer = new ShmServerStreamWriter<TResp>(stream, context, singleStream, cfg.Compression, cfg.MaxSendMessageSize);
            try
            {
                await _handler(reader, writer, context);
            }
            finally
            {
                writer.ReturnWriteBuffer();
            }

            // In singleStreamMode, inline trailers to avoid queue overhead.
            if (singleStream && stream.Connection.ActiveStreamCount <= 1)
            {
                var fw = stream.Connection.FrameWriter!;
                if (fw.TryPauseWriterLoop())
                {
                    try
                    {
                        stream.SendTrailersInline(fw, context.Status.StatusCode,
                            context.Status.Detail, context.ResponseTrailers);
                    }
                    finally
                    {
                        fw.ResumeWriterLoop();
                    }
                    return;
                }
            }

            await stream.SendTrailersAsync(
                context.Status.StatusCode,
                context.Status.Detail,
                context.ResponseTrailers);
        }
    }

    #endregion

    #region Stream Adapters

    /// <summary>
    /// Decompresses gRPC LPM payload if the compressed flag is set.
    /// Returns the protobuf bytes (without the 5-byte header).
    /// </summary>
    private static ReadOnlySpan<byte> DecompressLpm(
        ReadOnlySpan<byte> lpmPayload,
        Compression.ShmCompressionOptions? compression,
        string? grpcEncoding = null)
    {
        if (lpmPayload.Length < 5)
            throw new RpcException(new Status(StatusCode.Internal,
                $"Malformed gRPC LPM frame: expected at least 5 bytes, got {lpmPayload.Length}"));

        var compressedFlag = lpmPayload[0];
        var bodyLen = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(lpmPayload.Slice(1, 4));

        if (5 + bodyLen > lpmPayload.Length)
            throw new RpcException(new Status(StatusCode.Internal,
                $"Malformed gRPC LPM frame: declared length {bodyLen} exceeds payload ({lpmPayload.Length - 5} bytes available)"));

        var body = lpmPayload.Slice(5, bodyLen);

        if (compressedFlag == 0)
            return body;

        // Decompress using the encoding declared in request/response headers
        if (compression == null)
            throw new RpcException(new Status(StatusCode.Unimplemented,
                "Received compressed message but no compression options configured"));

        var encoding = grpcEncoding ?? "gzip";
        var decompressor = compression.GetDecompressor(encoding);
        if (decompressor == null)
            throw new RpcException(new Status(StatusCode.Unimplemented,
                $"Received compressed message with encoding '{encoding}' but no matching decompressor found"));

        return decompressor.Decompress(body);
    }

    private static async Task<TReq> ReadSingleMessageAsync<TReq>(
        ShmGrpcStream stream, MessageParser<TReq> parser, bool pooledDeserialization,
        int maxReceiveMessageSize, CancellationToken ct)
        where TReq : class, IMessage<TReq>, new()
    {
        return await ReadSingleMessageAsync(stream, parser, pooledDeserialization,
            maxReceiveMessageSize, null, null, ct);
    }

    private static async Task<TReq> ReadSingleMessageAsync<TReq>(
        ShmGrpcStream stream, MessageParser<TReq> parser, bool pooledDeserialization,
        int maxReceiveMessageSize, Compression.ShmCompressionOptions? compression,
        string? grpcEncoding, CancellationToken ct)
        where TReq : class, IMessage<TReq>, new()
    {
        // Use connection-level cached read buffer to avoid LOH churn.
        // For multi-frame messages (>16MB), this eliminates per-call
        // ArrayPool.Rent/Return of 64MB+ buffers.
        var conn = stream.Connection;
        byte[]? assembled = conn.BorrowReadBuffer();
        int assembledPos = 0;
        bool usedAssembled = false;

        try
        {
            while (true)
            {
                var frame = await stream.ReceiveFrameAsync(ct).ConfigureAwait(false);
                if (frame == null)
                    throw new RpcException(new Status(StatusCode.Internal, "No request message received"));

                var f = frame.Value;
                if (f.Type == FrameType.Message)
                {
                    if ((f.Flags & MessageFlags.More) != 0)
                    {
                        usedAssembled = true;
                        // Multi-frame: copy directly into assembled buffer.
                        if (assembled == null)
                        {
                            var initialSize = f.Length * 4;
                            assembled = ArrayPool<byte>.Shared.Rent(initialSize);
                        }
                        else if (assembledPos + f.Length > assembled!.Length)
                        {
                            var newBuf = ArrayPool<byte>.Shared.Rent(Math.Max(assembled.Length * 2, assembledPos + f.Length));
                            if (assembledPos > 0)
                                assembled.AsSpan(0, assembledPos).CopyTo(newBuf);
                            ArrayPool<byte>.Shared.Return(assembled);
                            assembled = newBuf;
                        }

                        f.Memory.Span.CopyTo(assembled.AsSpan(assembledPos));
                        assembledPos += f.Length;
                        f.ReturnToPool();
                        continue;
                    }

                    // Final frame or single-frame message.
                    if (usedAssembled)
                    {
                        // Multi-frame final: copy last frame into assembled.
                        if (assembledPos + f.Length > assembled!.Length)
                        {
                            var newBuf = ArrayPool<byte>.Shared.Rent(assembledPos + f.Length);
                            assembled.AsSpan(0, assembledPos).CopyTo(newBuf);
                            ArrayPool<byte>.Shared.Return(assembled);
                            assembled = newBuf;
                        }
                        f.Memory.Span.CopyTo(assembled.AsSpan(assembledPos));
                        assembledPos += f.Length;
                        f.ReturnToPool();

                        var protoData = DecompressLpm(assembled.AsSpan(0, assembledPos), compression, grpcEncoding);
                        // Check decompressed size (not wire size) against limit
                        if (maxReceiveMessageSize > 0 && protoData.Length > maxReceiveMessageSize)
                            throw new RpcException(new Status(StatusCode.ResourceExhausted,
                                $"Received message exceeds the maximum configured message size ({protoData.Length} vs {maxReceiveMessageSize})"));
                        if (pooledDeserialization)
                            return PooledProtoParser.ParseFrom<TReq>(protoData);
                        return parser.ParseFrom(protoData);
                    }
                    else
                    {
                        // Single frame — decompress then check size
                        var protoSpan = DecompressLpm(f.Memory.Span, compression, grpcEncoding);
                        if (maxReceiveMessageSize > 0 && protoSpan.Length > maxReceiveMessageSize)
                        {
                            f.ReturnToPool();
                            throw new RpcException(new Status(StatusCode.ResourceExhausted,
                                $"Received message exceeds the maximum configured message size ({protoSpan.Length} vs {maxReceiveMessageSize})"));
                        }
                        var result = pooledDeserialization
                            ? PooledProtoParser.ParseFrom<TReq>(protoSpan)
                            : parser.ParseFrom(protoSpan);
                        f.ReturnToPool();
                        return result;
                    }
                }
                else if (f.Type == FrameType.HalfClose || f.Type == FrameType.Cancel || f.Type == FrameType.Trailers)
                {
                    f.ReturnToPool();
                    throw new RpcException(new Status(StatusCode.Internal, "No request message received"));
                }
                else
                {
                    f.ReturnToPool();
                }
            }
        }
        finally
        {
            // Return assembled buffer to connection cache (not ArrayPool).
            if (assembled != null)
                conn.ReturnReadBuffer(assembled);
        }
    }

    /// <summary>
    /// Adapts <see cref="ShmGrpcStream"/> to <see cref="IAsyncStreamReader{T}"/> for service methods.
    /// Implements <see cref="IDisposable"/> to release any held pooled buffer
    /// when the handler stops reading early (e.g., returns after one message
    /// in a client-streaming or duplex call).
    /// </summary>
    private sealed class ShmAsyncStreamReader<T> : IAsyncStreamReader<T>, IDisposable
        where T : class, IMessage<T>, new()
    {
        private readonly ShmGrpcStream _stream;
        private readonly bool _pooledDeserialization;
        private readonly int _maxReceiveMessageSize;
        private readonly Compression.ShmCompressionOptions? _compression;
        private readonly string? _grpcEncoding;
        private readonly MessageParser<T> _parser = new(() => new T());
        private InboundFrame _previousFrame;
        private T? _current;
        private bool _endOfStream;
        // Multi-frame accumulation: single pre-allocated buffer.
        private byte[]? _assembled;
        private int _assembledPos;

        public ShmAsyncStreamReader(ShmGrpcStream stream, HandlerConfig cfg)
        {
            _stream = stream;
            _pooledDeserialization = cfg.PooledDeserialization;
            _maxReceiveMessageSize = cfg.MaxReceiveMessageSize;
            _compression = cfg.Compression;
            _grpcEncoding = cfg.GrpcEncoding;
            _assembled = stream.Connection.BorrowReadBuffer();
        }

        public T Current => _current ?? throw new InvalidOperationException("No current message");

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (_endOfStream)
            {
                _previousFrame.ReturnToPool();
                _previousFrame = default;
                _current = default;
                return false;
            }

            // Release previous frame
            _previousFrame.ReturnToPool();
            _previousFrame = default;

            // Fast path: try sync read from channel
            InboundFrame frame;
            while (_stream.TryReceiveFrame(out frame))
            {
                if (ProcessFrame(frame))
                    return true;
                if (_endOfStream || _assembledPos == 0)
                    return false;
            }

            // Slow path: wait for frame with minimal async layers
            var ct = cancellationToken.CanBeCanceled ? cancellationToken : _stream.DisposeCancellationToken;
            try
            {
                while (true)
                {
                    if (!await _stream.WaitForFrameAsync(ct).ConfigureAwait(false))
                        return false;

                    while (_stream.TryReceiveFrame(out frame))
                    {
                        if (ProcessFrame(frame))
                            return true;
                        if (_endOfStream || _assembledPos == 0)
                            return false;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
                return false;
            }
        }

        private bool ProcessFrame(InboundFrame frame)
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
                            var newBuf = ArrayPool<byte>.Shared.Rent(Math.Max(_assembled.Length * 2, _assembledPos + frame.Length));
                            _assembled.AsSpan(0, _assembledPos).CopyTo(newBuf);
                            ArrayPool<byte>.Shared.Return(_assembled);
                            _assembled = newBuf;
                        }
                        frame.Memory.Span.CopyTo(_assembled.AsSpan(_assembledPos));
                        _assembledPos += frame.Length;
                        frame.ReturnToPool();
                        return false; // keep reading
                    }

                    // Final frame or single-frame message.
                    if (_assembledPos > 0)
                    {
                        // Multi-frame final: copy last frame into assembled.
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

                        var protoData = DecompressLpm(_assembled.AsSpan(0, _assembledPos), _compression, _grpcEncoding);
                        if (_maxReceiveMessageSize > 0 && protoData.Length > _maxReceiveMessageSize)
                            throw new RpcException(new Status(StatusCode.ResourceExhausted,
                                $"Received message exceeds the maximum configured message size ({protoData.Length} vs {_maxReceiveMessageSize})"));
                        _current = _pooledDeserialization
                            ? PooledProtoParser.ParseFrom<T>(protoData)
                            : _parser.ParseFrom(protoData);
                    }
                    else
                    {
                        // Single frame — skip 5-byte gRPC LPM header per G3 spec.
                        var protoSpan = DecompressLpm(frame.Memory.Span, _compression, _grpcEncoding);
                        if (_maxReceiveMessageSize > 0 && protoSpan.Length > _maxReceiveMessageSize)
                        {
                            frame.ReturnToPool();
                            throw new RpcException(new Status(StatusCode.ResourceExhausted,
                                $"Received message exceeds the maximum configured message size ({protoSpan.Length} vs {_maxReceiveMessageSize})"));
                        }
                        _current = _pooledDeserialization
                            ? PooledProtoParser.ParseFrom<T>(protoSpan)
                            : _parser.ParseFrom(protoSpan);

                        _previousFrame = frame;
                        var eosSingle = (frame.Flags & MessageFlags.EndStream) != 0;
                        if (eosSingle)
                        {
                            _stream.MarkHalfCloseReceived();
                            _endOfStream = true;
                        }
                        return true;
                    }

                    // Multi-frame: keep assembled buffer for reuse.
                    _assembledPos = 0;
                    _previousFrame = default;

                    var eos = (frame.Flags & MessageFlags.EndStream) != 0;
                    if (eos)
                    {
                        _stream.MarkHalfCloseReceived();
                        _endOfStream = true;
                    }
                    return true;

                case FrameType.HalfClose:
                    frame.ReturnToPool();
                    _stream.MarkHalfCloseReceived();
                    _endOfStream = true;
                    return false;

                case FrameType.Trailers:
                    _stream.SetTrailers(frame);
                    frame.ReturnToPool();
                    _stream.MarkHalfCloseReceived();
                    _endOfStream = true;
                    return false;

                default:
                    frame.ReturnToPool();
                    return false;
            }
        }

        public void Dispose()
        {
            _previousFrame.ReturnToPool();
            _previousFrame = default;
            if (_assembled != null)
            {
                // Return to connection cache instead of ArrayPool
                // to avoid LOH churn across stream lifecycles.
                _stream.Connection.ReturnReadBuffer(_assembled);
                _assembled = null;
                _assembledPos = 0;
            }
        }
    }

    /// <summary>
    /// Adapts <see cref="ShmGrpcStream"/> to <see cref="IServerStreamWriter{T}"/> for service methods.
    /// </summary>
    private sealed class ShmServerStreamWriter<T> : IServerStreamWriter<T>
        where T : class, IMessage<T>
    {
        private readonly ShmGrpcStream _stream;
        private readonly ShmServerCallContext _context;
        private readonly bool _directRingWrite;
        private readonly Compression.ShmCompressionOptions? _compression;
        private readonly int _maxSendMessageSize;
        // Reusable write buffer for the ExecuteInline fallback path.
        private byte[]? _writeBuf;

        public ShmServerStreamWriter(ShmGrpcStream stream, ShmServerCallContext context,
            bool directRingWrite = false, Compression.ShmCompressionOptions? compression = null,
            int maxSendMessageSize = int.MaxValue)
        {
            _stream = stream;
            _context = context;
            _directRingWrite = directRingWrite;
            _compression = compression;
            _maxSendMessageSize = maxSendMessageSize;
        }

        public WriteOptions? WriteOptions { get; set; }

        /// <summary>Returns the reusable fallback buffer to ArrayPool.</summary>
        internal void ReturnWriteBuffer()
        {
            if (_writeBuf != null)
            {
                ArrayPool<byte>.Shared.Return(_writeBuf);
                _writeBuf = null;
            }
        }

        public Task WriteAsync(T message)
        {
            // In singleStreamMode with one active stream, serialize directly
            // into the ring buffer — no intermediate byte[] for any size.
            // Skip inline when compression is enabled (inline writes are uncompressed).
            var _msgSize = message.CalculateSize();
            var _sc3 = _compression?.ShouldCompress(_msgSize) == true
                && _compression.GetSendCompressor()?.IsIdentity == false;
            if (!_sc3 && _directRingWrite && _stream.Connection.ActiveStreamCount <= 1)
            {
                var size = _msgSize;
                if (_maxSendMessageSize > 0 && _maxSendMessageSize < int.MaxValue && size > _maxSendMessageSize)
                    throw new RpcException(new Status(StatusCode.ResourceExhausted,
                        $"Sending message exceeds limit ({size} vs {_maxSendMessageSize})"));
                var writer = _stream.Connection.FrameWriter!;
                IMessage msg = message;

                // TryPause + WriteInlineDirectMultiFrame: serialize protobuf
                // directly into ring, handling single-frame and multi-frame.
                if (writer.TryPauseWriterLoop())
                {
                    try
                    {
                        if (!_context.HeadersSent)
                        {
                            _stream.SendResponseHeadersInline(writer);
                            _context.MarkHeadersSent();
                        }
                        if (size > 0)
                            writer.WriteInlineDirectMultiFrame(_stream.StreamId, size, msg, 0, default);
                        else
                            writer.WriteInline(_stream.StreamId, EmptyGrpcLpm, 0, default);
                    }
                    finally
                    {
                        writer.ResumeWriterLoop();
                    }
                    return Task.CompletedTask;
                }

                // TryPause failed: ExecuteInline with intermediate buffer.
                byte[] buf;
                int bufSize;
                if (size > 0)
                {
                    if (_writeBuf != null && _writeBuf.Length >= 5 + size)
                        buf = _writeBuf;
                    else
                    {
                        if (_writeBuf != null) ArrayPool<byte>.Shared.Return(_writeBuf);
                        buf = ArrayPool<byte>.Shared.Rent(5 + size);
                        _writeBuf = buf;
                    }
                    buf[0] = 0;
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                        buf.AsSpan(1, 4), (uint)size);
                    msg.WriteTo(buf.AsSpan(5, size));
                    bufSize = 5 + size;
                }
                else
                {
                    buf = EmptyGrpcLpm;
                    bufSize = 5;
                }

                {
                    var streamId = _stream.StreamId;
                    var ctx = _context;
                    var stream = _stream;
                    writer.ExecuteInline(() =>
                    {
                        if (!ctx.HeadersSent)
                        {
                            stream.SendResponseHeadersInline(writer);
                            ctx.MarkHeadersSent();
                        }
                        writer.WriteInline(streamId, buf.AsSpan(0, bufSize), 0, default);
                    });
                }

                return Task.CompletedTask;
            }

            var headersTask = _context.EnsureResponseHeadersSentAsync();
            if (!headersTask.IsCompletedSuccessfully)
                return WriteAsyncSlow(headersTask, message);

            return SendProtobufMessageAsync(_stream, message, _compression, _maxSendMessageSize, default);
        }

        private async Task WriteAsyncSlow(Task headersTask, T message)
        {
            await headersTask.ConfigureAwait(false);
            await SendProtobufMessageAsync(_stream, message, _compression, _maxSendMessageSize, default).ConfigureAwait(false);
        }
    }

    #endregion
}
