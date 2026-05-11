#region Copyright notice and license

// Copyright 2026 The gRPC Authors
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

using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Client-side gRPC interceptor implementing gRFC G3 Transport Discovery.
/// <para>
/// On the first RPC, injects the <c>shm-offer</c> metadata key and reads the
/// <c>shm-ctl</c> trailing metadata from the response. If the server returns
/// a control segment name, the <see cref="OnShmDiscovered"/> callback is
/// invoked so the caller can create an SHM channel for subsequent RPCs.
/// </para>
/// <para>
/// Discovery is attempted once per interceptor instance. If the server does
/// not return <c>shm-ctl</c>, or if the first RPC fails, the interceptor
/// becomes a pass-through for all subsequent calls.
/// </para>
/// </summary>
/// <example>
/// <code>
/// GrpcChannel? shmChannel = null;
/// var interceptor = new ShmDiscoveryClientInterceptor(segmentName =>
/// {
///     shmChannel = GrpcChannel.ForAddress("http://localhost",
///         new GrpcChannelOptions { HttpHandler = new ShmControlHandler(segmentName) });
/// });
///
/// var tcpChannel = GrpcChannel.ForAddress("https://localhost:5001",
///     new GrpcChannelOptions { Interceptors = { interceptor } });
///
/// var client = new Greeter.GreeterClient(tcpChannel);
/// await client.SayHelloAsync(new HelloRequest { Name = "World" }); // discovery RPC
///
/// // Subsequent RPCs: use shmChannel if discovered
/// if (shmChannel != null)
///     client = new Greeter.GreeterClient(shmChannel);
/// </code>
/// </example>
/// <remarks>
/// Corresponds to Go's <c>ShmOfferContext()</c> and <c>ShmCtlFromTrailer()</c>.
/// </remarks>
public class ShmDiscoveryClientInterceptor : Interceptor
{
    private readonly Action<string> _onShmDiscovered;
    private volatile bool _discoveryDone;

    /// <summary>
    /// Gets whether transport discovery has completed (regardless of result).
    /// </summary>
    public bool DiscoveryDone => _discoveryDone;

    /// <summary>
    /// Gets the discovered SHM control segment name, or null if not discovered.
    /// </summary>
    public string? DiscoveredSegment { get; private set; }

    /// <summary>
    /// Raised when an SHM control segment is discovered. The string parameter
    /// is the control segment name from the server's <c>shm-ctl</c> trailer.
    /// </summary>
    public event Action<string>? OnShmDiscovered;

    /// <summary>
    /// Creates a new <see cref="ShmDiscoveryClientInterceptor"/>.
    /// </summary>
    /// <param name="onShmDiscovered">
    /// Callback invoked when the server returns a valid <c>shm-ctl</c> segment
    /// name. The caller should use this to create an SHM channel. Called at
    /// most once.
    /// </param>
    public ShmDiscoveryClientInterceptor(Action<string> onShmDiscovered)
    {
        ArgumentNullException.ThrowIfNull(onShmDiscovered);
        _onShmDiscovered = onShmDiscovered;
    }

    /// <summary>
    /// Creates a new <see cref="ShmDiscoveryClientInterceptor"/> with event-based
    /// notification. Subscribe to <see cref="OnShmDiscovered"/> to receive the
    /// segment name.
    /// </summary>
    public ShmDiscoveryClientInterceptor()
    {
        _onShmDiscovered = segmentName => OnShmDiscovered?.Invoke(segmentName);
    }

    /// <inheritdoc/>
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        if (_discoveryDone)
            return continuation(request, context);

        context = InjectShmOffer(context);
        var call = continuation(request, context);

        return new AsyncUnaryCall<TResponse>(
            WrapResponseAsync(call),
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            call.Dispose);
    }

    /// <inheritdoc/>
    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        if (_discoveryDone)
            return continuation(context);

        context = InjectShmOffer(context);
        var call = continuation(context);

        return new AsyncClientStreamingCall<TRequest, TResponse>(
            call.RequestStream,
            WrapResponseAsync(call),
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            call.Dispose);
    }

    /// <inheritdoc/>
    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        if (_discoveryDone)
            return continuation(request, context);

        context = InjectShmOffer(context);
        var call = continuation(request, context);

        // For server streaming, extract trailer after stream completes.
        // We wrap the stream reader to detect completion.
        return new AsyncServerStreamingCall<TResponse>(
            new DiscoveryStreamReader<TResponse>(call.ResponseStream, () => TryExtractShmCtl(call.GetTrailers)),
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            call.Dispose);
    }

    /// <inheritdoc/>
    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        if (_discoveryDone)
            return continuation(context);

        context = InjectShmOffer(context);
        var call = continuation(context);

        return new AsyncDuplexStreamingCall<TRequest, TResponse>(
            call.RequestStream,
            new DiscoveryStreamReader<TResponse>(call.ResponseStream, () => TryExtractShmCtl(call.GetTrailers)),
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            call.Dispose);
    }

    private static ClientInterceptorContext<TRequest, TResponse> InjectShmOffer<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        var headers = context.Options.Headers ?? new Metadata();
        headers.Add(ShmDiscoveryInterceptor.ShmOfferKey, "");
        return new ClientInterceptorContext<TRequest, TResponse>(
            context.Method, context.Host,
            context.Options.WithHeaders(headers));
    }

    private async Task<TResponse> WrapResponseAsync<TResponse>(AsyncUnaryCall<TResponse> call)
    {
        var response = await call.ResponseAsync.ConfigureAwait(false);
        TryExtractShmCtl(call.GetTrailers);
        return response;
    }

    private async Task<TResponse> WrapResponseAsync<TRequest, TResponse>(AsyncClientStreamingCall<TRequest, TResponse> call)
        where TRequest : class
        where TResponse : class
    {
        var response = await call.ResponseAsync.ConfigureAwait(false);
        TryExtractShmCtl(call.GetTrailers);
        return response;
    }

    private void TryExtractShmCtl(Func<Metadata> getTrailers)
    {
        if (_discoveryDone) return;
        _discoveryDone = true;

        try
        {
            var trailers = getTrailers();
            var shmCtl = trailers.Get(ShmDiscoveryInterceptor.ShmCtlKey)?.Value;
            if (!string.IsNullOrEmpty(shmCtl))
            {
                DiscoveredSegment = shmCtl;
                _onShmDiscovered(shmCtl);
            }
        }
        catch
        {
            // RPC may have failed — discovery is best-effort
        }
    }

    /// <summary>
    /// Wraps an <see cref="IAsyncStreamReader{T}"/> to detect stream completion
    /// and trigger SHM discovery extraction from trailers.
    /// </summary>
    private sealed class DiscoveryStreamReader<T> : IAsyncStreamReader<T>
    {
        private readonly IAsyncStreamReader<T> _inner;
        private readonly Action _onCompleted;
        private bool _completed;

        public DiscoveryStreamReader(IAsyncStreamReader<T> inner, Action onCompleted)
        {
            _inner = inner;
            _onCompleted = onCompleted;
        }

        public T Current => _inner.Current;

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            var hasNext = await _inner.MoveNext(cancellationToken).ConfigureAwait(false);
            if (!hasNext && !_completed)
            {
                _completed = true;
                _onCompleted();
            }
            return hasNext;
        }
    }
}
