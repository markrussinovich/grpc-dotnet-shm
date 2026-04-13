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
    private readonly string _segmentName;
    private readonly ulong _ringCapacity;
    private readonly uint _maxStreams;
    private readonly bool _singleStreamMode;
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
    /// <param name="singleStreamMode">When true, enables single-stream optimizations:
    /// the server writes large response frames directly to the ring when the
    /// WriterLoop is idle, saving ~80µs wakeup latency per message.</param>
    public ShmGrpcServer(string segmentName, ulong ringCapacity = 64 * 1024 * 1024, uint maxStreams = 100, bool singleStreamMode = false)
    {
        _segmentName = segmentName ?? throw new ArgumentNullException(nameof(segmentName));
        _ringCapacity = ringCapacity;
        _maxStreams = maxStreams;
        _singleStreamMode = singleStreamMode;
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
        }

        try
        {
            await foreach (var stream in connection.AcceptStreamsAsync(ct))
            {
                // Handle each stream concurrently
                _ = HandleStreamAsync(stream, ct);
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

            try
            {
                await handler.HandleAsync(stream, context, ct);
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
        var size = message.CalculateSize();
        if (size == 0)
        {
            return stream.SendMessageAsync(ReadOnlyMemory<byte>.Empty, ct);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            // Serialize directly into the rented buffer — no intermediate byte[].
            using (var cos = new CodedOutputStream(buffer))
            {
                message.WriteTo(cos);
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
        // Transfer buffer ownership to SendMessageZeroCopyAsync — it returns
        // the buffer to ArrayPool after the ring write completes.
        return stream.SendMessageZeroCopyAsync(buffer.AsMemory(0, size), buffer, ct);
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

    private interface IMethodHandler
    {
        Task HandleAsync(ShmGrpcStream stream, ShmServerCallContext context, CancellationToken ct);
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

        public async Task HandleAsync(ShmGrpcStream stream, ShmServerCallContext context, CancellationToken ct)
        {
            var request = await ReadSingleMessageAsync(stream, _parser, ct);
            var response = await _handler(request, context);

            // In singleStreamMode with one active stream, write response
            // inline to avoid queue overhead. Three tiers:
            // 1. TryPause + TryWriteInlineDirect: serialize directly into
            //    ring (zero intermediate buffer, single-frame only)
            // 2. TryPause + WriteInline: serialize to CachedWriteBuffer,
            //    then memcpy to ring (multi-frame or wrap-around fallback)
            // 3. ExecuteInline: submit write to WriterLoop thread when
            //    TryPause fails (WriterLoop in Phase 2 spin)
            if (stream.Connection.SingleStreamMode && stream.Connection.ActiveStreamCount <= 1)
            {
                var size = ((IMessage)response).CalculateSize();
                var writer = stream.Connection.FrameWriter!;
                var ringCap = (long)stream.Connection.TxRing.Capacity;
                var msg = (IMessage)response;

                // Fast path: TryPause + TryWriteInlineDirect — serialize
                // protobuf directly into ring, zero intermediate buffer.
                var maxFramePayload = Math.Max(1, (int)stream.Connection.TxRing.Capacity / 4 - ShmConstants.FrameHeaderSize);
                if (size > 0 && size <= maxFramePayload && writer.TryPauseWriterLoop())
                {
                    try
                    {
                        if (!context.HeadersSent)
                        {
                            stream.SendResponseHeadersInline(writer);
                            context.MarkHeadersSent();
                        }
                        if (writer.TryWriteInlineDirect(stream.StreamId, size, msg, 0, default))
                        {
                            stream.SendTrailersInline(writer, context.Status.StatusCode,
                                context.Status.Detail, context.ResponseTrailers);
                            return;
                        }
                        // TryWriteInlineDirect failed (wrap-around) — fall through.
                    }
                    finally
                    {
                        writer.ResumeWriterLoop();
                    }
                }

                // Serialize to buffer (shared between TryPause and ExecuteInline).
                byte[]? serializedBuffer = null;
                int serializedSize = size;
                if (size > 0)
                {
                    var cached = stream.Connection.CachedWriteBuffer;
                    if (cached != null && cached.Length >= size)
                    {
                        serializedBuffer = cached;
                    }
                    else
                    {
                        if (cached != null) ArrayPool<byte>.Shared.Return(cached);
                        serializedBuffer = ArrayPool<byte>.Shared.Rent(size);
                        stream.Connection.CachedWriteBuffer = serializedBuffer;
                    }
                    using (var cos = new CodedOutputStream(serializedBuffer))
                        msg.WriteTo(cos);
                }

                if (size <= ringCap && writer.TryPauseWriterLoop())
                {
                    // Small-medium: direct ring write on handler thread.
                    try
                    {
                        if (!context.HeadersSent)
                        {
                            stream.SendResponseHeadersInline(writer);
                            context.MarkHeadersSent();
                        }

                        if (serializedBuffer != null)
                            writer.WriteInline(stream.StreamId,
                                serializedBuffer.AsSpan(0, serializedSize), 0, default);
                        else
                            writer.WriteInline(stream.StreamId,
                                ReadOnlySpan<byte>.Empty, 0, default);

                        stream.SendTrailersInline(writer, context.Status.StatusCode,
                            context.Status.Detail, context.ResponseTrailers);
                    }
                    finally
                    {
                        writer.ResumeWriterLoop();
                    }
                    return;
                }

                // TryPause failed or TryWriteInlineDirect unavailable: ExecuteInline.
                writer.ExecuteInline(() =>
                {
                    if (!context.HeadersSent)
                    {
                        stream.SendResponseHeadersInline(writer);
                        context.MarkHeadersSent();
                    }
                    if (serializedBuffer != null)
                        writer.WriteInline(stream.StreamId,
                            serializedBuffer.AsSpan(0, serializedSize), 0, default);
                    else
                        writer.WriteInline(stream.StreamId,
                            ReadOnlySpan<byte>.Empty, 0, default);
                    stream.SendTrailersInline(writer, context.Status.StatusCode,
                        context.Status.Detail, context.ResponseTrailers);
                });
                return;
            }

            // Fallback path: ensure headers sent, then use WriterLoop queue.
            await context.EnsureResponseHeadersSentAsync();
            await SendProtobufMessageAsync(stream, response, ct);
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

        public async Task HandleAsync(ShmGrpcStream stream, ShmServerCallContext context, CancellationToken ct)
        {
            var request = await ReadSingleMessageAsync(stream, _parser, ct);
            var singleStream = stream.Connection.SingleStreamMode;

            if (!singleStream || stream.Connection.ActiveStreamCount > 1)
                await context.EnsureResponseHeadersSentAsync();

            var writer = new ShmServerStreamWriter<TResp>(stream, context, singleStream);
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

        public async Task HandleAsync(ShmGrpcStream stream, ShmServerCallContext context, CancellationToken ct)
        {
            var singleStream = stream.Connection.SingleStreamMode;
            if (!singleStream || stream.Connection.ActiveStreamCount > 1)
                await context.EnsureResponseHeadersSentAsync();

            using var reader = new ShmAsyncStreamReader<TReq>(stream);
            var response = await _handler(reader, context);

            if (singleStream && stream.Connection.ActiveStreamCount <= 1)
            {
                var size = ((IMessage)response).CalculateSize();
                var writer = stream.Connection.FrameWriter!;
                var msg = (IMessage)response;

                // Fast path: TryPause + TryWriteInlineDirect.
                var maxFramePayload = Math.Max(1, (int)stream.Connection.TxRing.Capacity / 4 - ShmConstants.FrameHeaderSize);
                if (size > 0 && size <= maxFramePayload && writer.TryPauseWriterLoop())
                {
                    try
                    {
                        if (!context.HeadersSent)
                        {
                            stream.SendResponseHeadersInline(writer);
                            context.MarkHeadersSent();
                        }
                        if (writer.TryWriteInlineDirect(stream.StreamId, size, msg, 0, default))
                        {
                            stream.SendTrailersInline(writer, context.Status.StatusCode,
                                context.Status.Detail, context.ResponseTrailers);
                            return;
                        }
                    }
                    finally
                    {
                        writer.ResumeWriterLoop();
                    }
                }

                // Serialize to buffer.
                byte[]? serializedBuffer = null;
                if (size > 0)
                {
                    var cached = stream.Connection.CachedWriteBuffer;
                    if (cached != null && cached.Length >= size)
                        serializedBuffer = cached;
                    else
                    {
                        if (cached != null) ArrayPool<byte>.Shared.Return(cached);
                        serializedBuffer = ArrayPool<byte>.Shared.Rent(size);
                        stream.Connection.CachedWriteBuffer = serializedBuffer;
                    }
                    using (var cos = new CodedOutputStream(serializedBuffer))
                        msg.WriteTo(cos);
                }

                var ringCap = (long)stream.Connection.TxRing.Capacity;
                if (size <= ringCap && writer.TryPauseWriterLoop())
                {
                    try
                    {
                        if (!context.HeadersSent)
                        {
                            stream.SendResponseHeadersInline(writer);
                            context.MarkHeadersSent();
                        }
                        if (serializedBuffer != null)
                            writer.WriteInline(stream.StreamId, serializedBuffer.AsSpan(0, size), 0, default);
                        else
                            writer.WriteInline(stream.StreamId, ReadOnlySpan<byte>.Empty, 0, default);
                        stream.SendTrailersInline(writer, context.Status.StatusCode,
                            context.Status.Detail, context.ResponseTrailers);
                    }
                    finally
                    {
                        writer.ResumeWriterLoop();
                    }
                    return;
                }

                // Large or TryPause failed: ExecuteInline.
                var serializedSize = size;
                writer.ExecuteInline(() =>
                {
                    if (!context.HeadersSent)
                    {
                        stream.SendResponseHeadersInline(writer);
                        context.MarkHeadersSent();
                    }
                    if (serializedBuffer != null)
                        writer.WriteInline(stream.StreamId, serializedBuffer.AsSpan(0, serializedSize), 0, default);
                    else
                        writer.WriteInline(stream.StreamId, ReadOnlySpan<byte>.Empty, 0, default);
                    stream.SendTrailersInline(writer, context.Status.StatusCode,
                        context.Status.Detail, context.ResponseTrailers);
                });
                return;
            }

            await SendProtobufMessageAsync(stream, response, ct);
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

        public async Task HandleAsync(ShmGrpcStream stream, ShmServerCallContext context, CancellationToken ct)
        {
            var singleStream = stream.Connection.SingleStreamMode;
            // Headers: if truly single stream (1 active), WriteAsync sends
            // headers inline. Otherwise send eagerly via queue.
            if (!singleStream || stream.Connection.ActiveStreamCount > 1)
                await context.EnsureResponseHeadersSentAsync();

            using var reader = new ShmAsyncStreamReader<TReq>(stream);
            var writer = new ShmServerStreamWriter<TResp>(stream, context, singleStream);
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

    private static async Task<TReq> ReadSingleMessageAsync<TReq>(
        ShmGrpcStream stream, MessageParser<TReq> parser, CancellationToken ct)
        where TReq : class, IMessage<TReq>, new()
    {
        // Accumulator for multi-frame messages: single pre-allocated buffer
        // that each frame copies into at the correct offset. Eliminates
        // per-chunk ArrayPool.Rent and ReadOnlySequence chain overhead.
        byte[]? assembled = null;
        int assembledPos = 0;

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
                        // Multi-frame: copy directly into assembled buffer.
                        if (assembled == null)
                        {
                            // First frame — estimate total size.
                            // Start with 4× first frame as initial guess.
                            // Will grow if needed (rare for well-sized rings).
                            var initialSize = f.Length * 4;
                            assembled = ArrayPool<byte>.Shared.Rent(initialSize);
                        }
                        else if (assembledPos + f.Length > assembled.Length)
                        {
                            // Grow buffer (double).
                            var newBuf = ArrayPool<byte>.Shared.Rent(assembled.Length * 2);
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
                    ReadOnlySequence<byte> sequence;

                    if (assembled != null)
                    {
                        // Multi-frame final: copy last frame into assembled.
                        if (assembledPos + f.Length > assembled.Length)
                        {
                            var newBuf = ArrayPool<byte>.Shared.Rent(assembledPos + f.Length);
                            assembled.AsSpan(0, assembledPos).CopyTo(newBuf);
                            ArrayPool<byte>.Shared.Return(assembled);
                            assembled = newBuf;
                        }
                        f.Memory.Span.CopyTo(assembled.AsSpan(assembledPos));
                        assembledPos += f.Length;
                        f.ReturnToPool();

                        sequence = new ReadOnlySequence<byte>(assembled.AsMemory(0, assembledPos));
                    }
                    else
                    {
                        // Single frame — ParseFrom directly from frame memory.
                        // For pre-committed ZeroCopy frames this is truly zero-copy.
                        sequence = new ReadOnlySequence<byte>(f.Memory);
                    }

                    var result = parser.ParseFrom(sequence);

                    // Release single-frame ref (no-op for pre-committed).
                    if (assembled == null)
                        f.ReturnToPool();

                    return result;
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
            if (assembled != null)
                ArrayPool<byte>.Shared.Return(assembled);
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
        private readonly MessageParser<T> _parser = new(() => new T());
        private InboundFrame _previousFrame;
        private T? _current;
        private bool _endOfStream;
        // Multi-frame accumulation: single pre-allocated buffer.
        private byte[]? _assembled;
        private int _assembledPos;

        public ShmAsyncStreamReader(ShmGrpcStream stream)
        {
            _stream = stream;
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
                if (_assembled == null)
                    return false; // real end-of-stream
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
                        if (_assembled == null)
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
                            var newBuf = ArrayPool<byte>.Shared.Rent(_assembled.Length * 2);
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
                    ReadOnlySequence<byte> sequence;
                    if (_assembled != null)
                    {
                        // Multi-frame final: copy last frame into assembled.
                        if (_assembledPos + frame.Length > _assembled.Length)
                        {
                            var newBuf = ArrayPool<byte>.Shared.Rent(_assembledPos + frame.Length);
                            _assembled.AsSpan(0, _assembledPos).CopyTo(newBuf);
                            ArrayPool<byte>.Shared.Return(_assembled);
                            _assembled = newBuf;
                        }
                        frame.Memory.Span.CopyTo(_assembled.AsSpan(_assembledPos));
                        _assembledPos += frame.Length;
                        frame.ReturnToPool();
                        sequence = new ReadOnlySequence<byte>(_assembled.AsMemory(0, _assembledPos));
                    }
                    else
                    {
                        // Single frame — ParseFrom directly (zero-copy for pre-committed).
                        sequence = new ReadOnlySequence<byte>(frame.Memory);
                    }

                    _current = _parser.ParseFrom(sequence);

                    if (_assembled != null)
                    {
                        // Keep assembled buffer for reuse across messages
                        // (streaming with same-sized messages avoids re-allocation).
                        _assembledPos = 0;
                    }
                    else
                    {
                        frame.ReturnToPool();
                    }
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
                    return false;

                case FrameType.Trailers:
                    _stream.SetTrailers(frame);
                    frame.ReturnToPool();
                    _stream.MarkHalfCloseReceived();
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
                ArrayPool<byte>.Shared.Return(_assembled);
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
        // Reusable write buffer for the fallback path when TryWriteInlineDirect
        // fails (ring wrap-around). Avoids per-message ArrayPool.Rent for
        // repeated large messages in streaming scenarios.
        private byte[]? _writeBuf;

        public ShmServerStreamWriter(ShmGrpcStream stream, ShmServerCallContext context, bool directRingWrite = false)
        {
            _stream = stream;
            _context = context;
            _directRingWrite = directRingWrite;
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
            // In singleStreamMode with one active stream, use ExecuteInline
            // to bypass the queue. ExecuteInline internally handles the
            // WriterLoop idle case via TryPause (see ShmFrameWriter).
            if (_directRingWrite && _stream.Connection.ActiveStreamCount <= 1)
            {
                var size = message.CalculateSize();
                var writer = _stream.Connection.FrameWriter!;
                IMessage msg = message;

                // Fast path: for single-frame messages (≤ ~16MB), try to
                // serialize directly into the ring buffer (zero intermediate
                // buffer). This requires exclusive ring access via TryPause.
                // Falls through to the standard serialize+ExecuteInline path
                // on failure (wrap-around, WriterLoop busy, multi-frame).
                var maxFramePayload = Math.Max(1, (int)_stream.Connection.TxRing.Capacity / 4 - ShmConstants.FrameHeaderSize);
                if (size > 0 && size <= maxFramePayload && writer.TryPauseWriterLoop())
                {
                    try
                    {
                        if (!_context.HeadersSent)
                        {
                            _stream.SendResponseHeadersInline(writer);
                            _context.MarkHeadersSent();
                        }
                        if (writer.TryWriteInlineDirect(_stream.StreamId, size, msg, 0, default))
                            return Task.CompletedTask;
                        // TryWriteInlineDirect failed (wrap-around) — fall through
                        // to serialize+WriteInline below while still holding pause.
                    }
                    finally
                    {
                        writer.ResumeWriterLoop();
                    }
                }

                byte[]? buf = null;
                if (size > 0)
                {
                    // Reuse write buffer if large enough.
                    if (_writeBuf != null && _writeBuf.Length >= size)
                    {
                        buf = _writeBuf;
                    }
                    else
                    {
                        if (_writeBuf != null) ArrayPool<byte>.Shared.Return(_writeBuf);
                        buf = ArrayPool<byte>.Shared.Rent(size);
                        _writeBuf = buf;
                    }
                    using (var cos = new CodedOutputStream(buf))
                        msg.WriteTo(cos);
                }

                var streamId = _stream.StreamId;
                var bufSize = size;
                var ctx = _context;
                var stream = _stream;
                writer.ExecuteInline(() =>
                {
                    if (!ctx.HeadersSent)
                    {
                        stream.SendResponseHeadersInline(writer);
                        ctx.MarkHeadersSent();
                    }
                    if (buf != null)
                        writer.WriteInline(streamId, buf.AsSpan(0, bufSize), 0, default);
                    else
                        writer.WriteInline(streamId, ReadOnlySpan<byte>.Empty, 0, default);
                });

                // Don't return buf — reused via _writeBuf across messages.
                return Task.CompletedTask;
            }

            var headersTask = _context.EnsureResponseHeadersSentAsync();
            if (!headersTask.IsCompletedSuccessfully)
                return WriteAsyncSlow(headersTask, message);

            return SendProtobufMessageAsync(_stream, message, default);
        }

        private async Task WriteAsyncSlow(Task headersTask, T message)
        {
            await headersTask.ConfigureAwait(false);
            await SendProtobufMessageAsync(_stream, message, default).ConfigureAwait(false);
        }
    }

    #endregion
}
