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
using Microsoft.AspNetCore.Connections;
using Grpc.Net.SharedMemory;

namespace Grpc.AspNetCore.Server.SharedMemory;

/// <summary>
/// An <see cref="IConnectionListenerFactory"/> that creates shared memory connection
/// listeners. This factory is registered with Kestrel to enable the shared memory
/// transport alongside (or instead of) TCP.
/// </summary>
internal sealed class ShmConnectionListenerFactory : IConnectionListenerFactory
{
    private readonly ShmTransportOptions _options;

    public ShmConnectionListenerFactory(ShmTransportOptions options)
    {
        _options = options;
    }

    /// <inheritdoc/>
    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        if (endpoint is not ShmEndPoint shmEndPoint)
        {
            throw new ArgumentException($"Endpoint must be an ShmEndPoint, got {endpoint.GetType().Name}", nameof(endpoint));
        }

        var listener = new ShmConnectionListenerAdapter(shmEndPoint.SegmentName, _options);
        return new ValueTask<IConnectionListener>(listener);
    }
}
