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
using Grpc.Net.Client;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// A gRPC channel that starts on HTTP/2 and automatically upgrades to shared
/// memory transport after G3 Transport Discovery completes.
/// <para>
/// On the first RPC, sends <c>shm-offer</c> metadata. If the server returns
/// <c>shm-ctl</c>, subsequent RPCs are transparently routed over shared memory.
/// If the server does not support SHM, all RPCs continue over HTTP/2.
/// </para>
/// <para>
/// Usage is identical to a normal <see cref="GrpcChannel"/> — create clients
/// directly from this channel. Discovery is fully transparent.
/// </para>
/// </summary>
/// <example>
/// <code>
/// using var channel = ShmAwareChannel.Create("https://localhost:5001");
/// var client = new Greeter.GreeterClient(channel);
///
/// // First RPC: HTTP/2 with shm-offer discovery (transparent)
/// await client.SayHelloAsync(new HelloRequest { Name = "World" });
///
/// // Subsequent RPCs: SHM if discovered, otherwise HTTP/2
/// await client.SayHelloAsync(new HelloRequest { Name = "Again" });
/// </code>
/// </example>
public sealed class ShmAwareChannel : ChannelBase, IDisposable, IAsyncDisposable
{
    private readonly GrpcChannel _tcpChannel;
    private volatile GrpcChannel? _shmChannel;
    private readonly ShmDiscoveryClientInterceptor _interceptor;
    private readonly ShmClientTransportOptions? _shmOptions;
    private CallInvoker? _cachedInvoker;

    private ShmAwareChannel(
        string address,
        GrpcChannelOptions? tcpOptions,
        ShmClientTransportOptions? shmOptions)
        : base(address)
    {
        _shmOptions = shmOptions;
        _interceptor = new ShmDiscoveryClientInterceptor(OnShmDiscovered);

        var options = tcpOptions ?? new GrpcChannelOptions();
        _tcpChannel = GrpcChannel.ForAddress(address, options);
    }

    /// <summary>
    /// Creates the <see cref="CallInvoker"/> used by gRPC clients.
    /// Routes through the discovery interceptor on TCP, then switches
    /// to SHM after discovery completes.
    /// </summary>
    public override CallInvoker CreateCallInvoker()
    {
        return _cachedInvoker ??= new ShmAwareCallInvoker(this);
    }

    /// <summary>
    /// Gets whether SHM transport has been discovered and is active.
    /// </summary>
    public bool IsShmActive => _shmChannel != null;

    /// <summary>
    /// Gets the discovered SHM control segment name, or null.
    /// </summary>
    public string? DiscoveredSegment => _interceptor.DiscoveredSegment;

    /// <summary>
    /// Creates a new <see cref="ShmAwareChannel"/> targeting the specified server address.
    /// </summary>
    /// <param name="address">The server address (e.g., "https://localhost:5001").</param>
    /// <param name="tcpOptions">Optional options for the HTTP/2 channel.</param>
    /// <param name="shmOptions">Optional options for the SHM transport.</param>
    public static ShmAwareChannel Create(
        string address,
        GrpcChannelOptions? tcpOptions = null,
        ShmClientTransportOptions? shmOptions = null)
    {
        return new ShmAwareChannel(address, tcpOptions, shmOptions);
    }

    private void OnShmDiscovered(string segmentName)
    {
        if (_shmChannel != null) return;

        try
        {
            var handler = new ShmControlHandler(segmentName, _shmOptions);
            _shmChannel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
            {
                HttpHandler = handler,
                DisposeHttpClient = true
            });
        }
        catch
        {
            // Failed to open SHM segment — stay on TCP
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _shmChannel?.Dispose();
        _tcpChannel.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_shmChannel != null)
            await _shmChannel.ShutdownAsync().ConfigureAwait(false);
        _shmChannel?.Dispose();
        await _tcpChannel.ShutdownAsync().ConfigureAwait(false);
        _tcpChannel.Dispose();
    }

    /// <summary>
    /// CallInvoker that routes to SHM after discovery, or TCP with
    /// discovery interceptor before/during discovery.
    /// </summary>
    private sealed class ShmAwareCallInvoker : CallInvoker
    {
        private readonly ShmAwareChannel _owner;
        private CallInvoker? _tcpWithDiscovery;

        public ShmAwareCallInvoker(ShmAwareChannel owner) => _owner = owner;

        private CallInvoker GetInvoker()
        {
            // After discovery: route to SHM channel
            var shm = _owner._shmChannel;
            if (shm != null)
                return shm.CreateCallInvoker();

            // Before/during discovery: route to TCP with interceptor
            return _tcpWithDiscovery ??=
                _owner._tcpChannel.Intercept(_owner._interceptor);
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host,
            CallOptions options, TRequest request)
            => GetInvoker().BlockingUnaryCall(method, host, options, request);

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host,
            CallOptions options, TRequest request)
            => GetInvoker().AsyncUnaryCall(method, host, options, request);

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host,
            CallOptions options, TRequest request)
            => GetInvoker().AsyncServerStreamingCall(method, host, options, request);

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host,
            CallOptions options)
            => GetInvoker().AsyncClientStreamingCall(method, host, options);

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host,
            CallOptions options)
            => GetInvoker().AsyncDuplexStreamingCall(method, host, options);
    }
}
