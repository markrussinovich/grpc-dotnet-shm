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

using System.Net;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Server-side gRPC interceptor implementing gRFC G3 Transport Discovery.
/// <para>
/// When the server receives an RPC with the <c>shm-offer</c> metadata key,
/// it verifies the client is on the same host (loopback peer address) and,
/// if so, returns the control segment name in the <c>shm-ctl</c> trailing
/// metadata.
/// </para>
/// <para>
/// The negotiation is backward compatible. A client that does not implement
/// SHM transport never sends <c>shm-offer</c>; the server does not push SHM.
/// A client that receives <c>shm-ctl</c> but cannot open the control segment
/// continues using HTTP/2.
/// </para>
/// </summary>
/// <example>
/// <code>
/// // ASP.NET Core server with dual TCP + SHM listening
/// builder.Services.AddGrpc(o =&gt;
///     o.Interceptors.Add&lt;ShmDiscoveryInterceptor&gt;());
/// builder.Services.AddSingleton(
///     new ShmDiscoveryInterceptor("myservice_ctl"));
/// builder.WebHost.UseSharedMemory("myservice");
/// </code>
/// </example>
/// <remarks>
/// Corresponds to Go's <c>grpc.ShmDiscoveryServerInterceptors()</c>.
/// </remarks>
public class ShmDiscoveryInterceptor : Interceptor
{
    /// <summary>
    /// Metadata key sent by the client to offer SHM transport.
    /// Per G3 spec, the value is empty.
    /// </summary>
    public const string ShmOfferKey = "shm-offer";

    /// <summary>
    /// Metadata key returned by the server in trailing metadata.
    /// Its value is the name of the SHM control segment.
    /// </summary>
    public const string ShmCtlKey = "shm-ctl";

    private readonly string _shmCtlSegment;

    /// <summary>
    /// Creates a new <see cref="ShmDiscoveryInterceptor"/>.
    /// </summary>
    /// <param name="shmCtlSegment">
    /// The control segment name that the SHM listener is serving on.
    /// Must match the name used by <see cref="ShmControlListener"/>
    /// or <see cref="ShmGrpcServer"/>.
    /// </param>
    public ShmDiscoveryInterceptor(string shmCtlSegment)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(shmCtlSegment);
        _shmCtlSegment = shmCtlSegment;
    }

    /// <inheritdoc/>
    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        MaybeSetShmCtl(context);
        return continuation(request, context);
    }

    /// <inheritdoc/>
    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        MaybeSetShmCtl(context);
        return continuation(requestStream, context);
    }

    /// <inheritdoc/>
    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        MaybeSetShmCtl(context);
        return continuation(request, responseStream, context);
    }

    /// <inheritdoc/>
    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        MaybeSetShmCtl(context);
        return continuation(requestStream, responseStream, context);
    }

    /// <summary>
    /// Checks if the incoming RPC carries <c>shm-offer</c> and if the peer
    /// is on the same host. If both conditions are met, sets <c>shm-ctl</c>
    /// in the trailing metadata.
    /// </summary>
    /// <remarks>
    /// Corresponds to Go's <c>maybeSetShmCtl()</c>.
    /// </remarks>
    private void MaybeSetShmCtl(ServerCallContext context)
    {
        // 1. Check for shm-offer in request metadata
        var offer = context.RequestHeaders.Get(ShmOfferKey);
        if (offer == null)
            return;

        // 2. Check same-host (loopback / UDS)
        if (!IsSameHost(context))
            return;

        // 3. Set shm-ctl in trailing metadata
        context.ResponseTrailers.Add(ShmCtlKey, _shmCtlSegment);
    }

    /// <summary>
    /// Checks whether the peer address is a loopback address, indicating
    /// the client is on the same host as the server.
    /// </summary>
    /// <remarks>
    /// Corresponds to Go's <c>isSameHostPeer()</c>.
    /// <para>
    /// For ASP.NET Core servers, this reads the remote IP from the HTTP
    /// context. For standalone <see cref="ShmGrpcServer"/>, this always
    /// returns true (SHM is inherently same-host).
    /// </para>
    /// </remarks>
    private static bool IsSameHost(ServerCallContext context)
    {
        // ShmServerCallContext is always same-host by definition
        if (context is ShmServerCallContext)
            return true;

        // For ASP.NET Core, check the Peer property which contains the
        // remote address in "ipvN:address:port" or "unix:path" format.
        var peer = context.Peer;
        if (string.IsNullOrEmpty(peer))
            return false;

        // Unix domain socket — same host by definition
        if (peer.StartsWith("unix:", StringComparison.OrdinalIgnoreCase))
            return true;

        // Parse "ipv4:127.0.0.1:port" or "ipv6:[::1]:port" or "127.0.0.1:port"
        try
        {
            // Strip scheme prefix if present (e.g., "ipv4:" or "ipv6:")
            var addrPart = peer;
            var colonIdx = peer.IndexOf(':');
            if (colonIdx >= 0 && !peer.StartsWith('['))
            {
                var prefix = peer[..colonIdx];
                if (prefix is "ipv4" or "ipv6")
                    addrPart = peer[(colonIdx + 1)..];
            }

            // Remove port
            string host;
            if (addrPart.StartsWith('['))
            {
                var bracketEnd = addrPart.IndexOf(']');
                host = bracketEnd > 0 ? addrPart[1..bracketEnd] : addrPart;
            }
            else
            {
                var lastColon = addrPart.LastIndexOf(':');
                host = lastColon > 0 ? addrPart[..lastColon] : addrPart;
            }

            if (IPAddress.TryParse(host, out var ip))
                return IPAddress.IsLoopback(ip);
        }
        catch
        {
            // Parse failure — conservative
        }

        return false;
    }
}
