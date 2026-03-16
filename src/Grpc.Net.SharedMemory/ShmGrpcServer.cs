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
[SupportedOSPlatform("windows")]
public sealed class ShmGrpcServer : IAsyncDisposable
{
    private readonly string _segmentName;
    private readonly ulong _ringCapacity;
    private readonly uint _maxStreams;
    private readonly Dictionary<string, IMethodHandler> _methods = new(StringComparer.Ordinal);
    private ShmControlListener? _listener;
    private readonly CancellationTokenSource _shutdownCts = new();
    private bool _disposed;

    /// <summary>
    /// Creates a new SHM gRPC server.
    /// </summary>
    /// <param name="segmentName">The shared memory segment name clients will connect to.</param>
    /// <param name="ringCapacity">Ring buffer capacity per connection (default: 64MB).</param>
    /// <param name="maxStreams">Maximum concurrent streams per connection (default: 100).</param>
    public ShmGrpcServer(string segmentName, ulong ringCapacity = 64 * 1024 * 1024, uint maxStreams = 100)
    {
        _segmentName = segmentName ?? throw new ArgumentNullException(nameof(segmentName));
        _ringCapacity = ringCapacity;
        _maxStreams = maxStreams;
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
        ObjectDisposedException.ThrowIf(_disposed, this);

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
                await SendErrorTrailersAsync(stream, ex.StatusCode, ex.Status.Detail);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await SendErrorTrailersAsync(stream, StatusCode.Cancelled, "Server shutting down");
            }
            catch (Exception ex)
            {
                await SendErrorTrailersAsync(stream, StatusCode.Internal, ex.Message);
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

    private static async Task SendErrorTrailersAsync(ShmGrpcStream stream, StatusCode code, string? message)
    {
        try
        {
            // Ensure response headers are sent (required before trailers)
            if (stream.ResponseHeaders == null)
            {
                await stream.SendResponseHeadersAsync();
            }
            await stream.SendTrailersAsync(code, message);
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
    private static async Task SendProtobufMessageAsync(
        ShmGrpcStream stream, IMessage message, CancellationToken ct)
    {
        var size = message.CalculateSize();
        if (size == 0)
        {
            await stream.SendMessageAsync(ReadOnlyMemory<byte>.Empty, ct);
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            // Serialize directly into the rented buffer — no intermediate byte[].
            using (var cos = new CodedOutputStream(buffer))
            {
                message.WriteTo(cos);
            }
            await stream.SendMessageAsync(buffer.AsMemory(0, size), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _shutdownCts.Cancel();

            if (_listener != null)
            {
                await _listener.DisposeAsync();
            }

            _shutdownCts.Dispose();
        }
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

        public UnaryHandler(Func<TReq, ServerCallContext, Task<TResp>> handler) => _handler = handler;

        public async Task HandleAsync(ShmGrpcStream stream, ShmServerCallContext context, CancellationToken ct)
        {
            // Read single request message
            var request = await ReadSingleMessageAsync(stream, _parser, ct);

            // Send response headers
            await context.EnsureResponseHeadersSentAsync();

            // Call handler
            var response = await _handler(request, context);

            // Send response using pooled buffer (avoids LOH allocation)
            await SendProtobufMessageAsync(stream, response, ct);

            // Send trailers
            await stream.SendTrailersAsync(
                context.Status.StatusCode,
                context.Status.Detail);
        }
    }

    private sealed class ServerStreamingHandler<TReq, TResp> : IMethodHandler
        where TReq : class, IMessage<TReq>, new()
        where TResp : class, IMessage<TResp>
    {
        private readonly Func<TReq, IServerStreamWriter<TResp>, ServerCallContext, Task> _handler;
        private readonly MessageParser<TReq> _parser = new(() => new TReq());

        public ServerStreamingHandler(Func<TReq, IServerStreamWriter<TResp>, ServerCallContext, Task> handler) => _handler = handler;

        public async Task HandleAsync(ShmGrpcStream stream, ShmServerCallContext context, CancellationToken ct)
        {
            // Read single request message
            var request = await ReadSingleMessageAsync(stream, _parser, ct);

            // Send response headers
            await context.EnsureResponseHeadersSentAsync();

            // Create writer and call handler
            var writer = new ShmServerStreamWriter<TResp>(stream, context);
            await _handler(request, writer, context);

            // Send trailers
            await stream.SendTrailersAsync(
                context.Status.StatusCode,
                context.Status.Detail);
        }
    }

    private sealed class ClientStreamingHandler<TReq, TResp> : IMethodHandler
        where TReq : class, IMessage<TReq>, new()
        where TResp : class, IMessage<TResp>
    {
        private readonly Func<IAsyncStreamReader<TReq>, ServerCallContext, Task<TResp>> _handler;

        public ClientStreamingHandler(Func<IAsyncStreamReader<TReq>, ServerCallContext, Task<TResp>> handler) => _handler = handler;

        public async Task HandleAsync(ShmGrpcStream stream, ShmServerCallContext context, CancellationToken ct)
        {
            // Send response headers
            await context.EnsureResponseHeadersSentAsync();

            // Create reader and call handler
            using var reader = new ShmAsyncStreamReader<TReq>(stream);
            var response = await _handler(reader, context);

            // Send response using pooled buffer (avoids LOH allocation)
            await SendProtobufMessageAsync(stream, response, ct);

            // Send trailers
            await stream.SendTrailersAsync(
                context.Status.StatusCode,
                context.Status.Detail);
        }
    }

    private sealed class DuplexStreamingHandler<TReq, TResp> : IMethodHandler
        where TReq : class, IMessage<TReq>, new()
        where TResp : class, IMessage<TResp>
    {
        private readonly Func<IAsyncStreamReader<TReq>, IServerStreamWriter<TResp>, ServerCallContext, Task> _handler;

        public DuplexStreamingHandler(Func<IAsyncStreamReader<TReq>, IServerStreamWriter<TResp>, ServerCallContext, Task> handler) => _handler = handler;

        public async Task HandleAsync(ShmGrpcStream stream, ShmServerCallContext context, CancellationToken ct)
        {
            // Send response headers
            await context.EnsureResponseHeadersSentAsync();

            // Create reader, writer, and call handler
            using var reader = new ShmAsyncStreamReader<TReq>(stream);
            var writer = new ShmServerStreamWriter<TResp>(stream, context);
            await _handler(reader, writer, context);

            // Send trailers
            await stream.SendTrailersAsync(
                context.Status.StatusCode,
                context.Status.Detail);
        }
    }

    #endregion

    #region Stream Adapters

    private static async Task<TReq> ReadSingleMessageAsync<TReq>(
        ShmGrpcStream stream, MessageParser<TReq> parser, CancellationToken ct)
        where TReq : class, IMessage<TReq>, new()
    {
        // Use ReceiveMessageBuffersAsync to avoid the extra byte[] allocation
        // that ReceiveMessagesAsync performs via ToArray().  The buffer is
        // valid until the next MoveNextAsync — parsing completes before that.
        await foreach (var msg in stream.ReceiveMessageBuffersAsync(ct))
        {
            return parser.ParseFrom(new ReadOnlySequence<byte>(msg));
        }

        throw new RpcException(new Status(StatusCode.Internal, "No request message received"));
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

        public ShmAsyncStreamReader(ShmGrpcStream stream) => _stream = stream;

        public T Current => _current ?? throw new InvalidOperationException("No current message");

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            // Use ReceiveNextMessageBufferAsync with the caller's per-call
            // cancellation token. Unlike the enumerator-based approach, this
            // ensures every MoveNext call respects the current token — not
            // the one from the first call.
            var (mem, frame, eos) = await _stream.ReceiveNextMessageBufferAsync(
                _previousFrame, cancellationToken).ConfigureAwait(false);

            if (eos)
            {
                _previousFrame = default;
                _current = default;
                return false;
            }

            _previousFrame = frame;
            _current = _parser.ParseFrom(new ReadOnlySequence<byte>(mem));
            return true;
        }

        public void Dispose()
        {
            // Release any held pooled buffer from the last received message.
            // Without this, a handler that short-circuits (reads one message
            // then returns) would leak the rented ArrayPool buffer.
            _previousFrame.ReturnToPool();
            _previousFrame = default;
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

        public ShmServerStreamWriter(ShmGrpcStream stream, ShmServerCallContext context)
        {
            _stream = stream;
            _context = context;
        }

        public WriteOptions? WriteOptions { get; set; }

        public async Task WriteAsync(T message)
        {
            await _context.EnsureResponseHeadersSentAsync();
            await SendProtobufMessageAsync(_stream, message, default);
        }
    }

    #endregion
}
