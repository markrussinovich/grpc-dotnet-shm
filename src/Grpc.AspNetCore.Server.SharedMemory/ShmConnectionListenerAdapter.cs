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
/// An <see cref="IConnectionListener"/> that accepts shared memory connections.
/// Each accepted connection wraps a pair of SHM ring buffers as a bidirectional
/// byte stream that Kestrel can use for HTTP/2 transport.
/// </summary>
internal sealed class ShmConnectionListenerAdapter : IConnectionListener
{
    private readonly string _segmentName;
    private readonly ShmTransportOptions _options;
    private readonly ShmEndPoint _endPoint;
    private readonly CancellationTokenSource _closeCts;
    private Segment? _currentSegment;
    private bool _disposed;

    public ShmConnectionListenerAdapter(string segmentName, ShmTransportOptions options)
    {
        _segmentName = segmentName;
        _options = options;
        _endPoint = new ShmEndPoint(segmentName);
        _closeCts = new CancellationTokenSource();
    }

    /// <inheritdoc/>
    public EndPoint EndPoint => _endPoint;

    /// <inheritdoc/>
    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return null;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closeCts.Token);

        try
        {
            // Create a new segment for this connection
            // Use a unique name per connection to support multiple clients
            var connId = Guid.NewGuid().ToString("N")[..8];
            var segName = $"{_segmentName}_{connId}";

            var segment = Segment.Create(segName, _options.RingCapacity, _options.MaxStreams);
            _currentSegment = segment;

            segment.SetServerReady(true);

            // Wait for a client to connect
            await segment.WaitForClientAsync(linkedCts.Token).ConfigureAwait(false);

            // Create bidirectional stream over the ring buffers
            // Server reads from RingA (client→server) and writes to RingB (server→client)
            var shmStream = new ShmStream(segment.RingA, segment.RingB);

            var connectionContext = new ShmConnectionContext(
                connectionId: connId,
                shmStream: shmStream,
                localEndPoint: _endPoint);

            return connectionContext;
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        _closeCts.Cancel();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _closeCts.Cancel();
        _currentSegment?.Dispose();
        _closeCts.Dispose();
        return ValueTask.CompletedTask;
    }
}
