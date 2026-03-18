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

using System.IO.Pipelines;
using System.Net;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// A connection listener that accepts gRPC connections over shared memory.
/// Used for ASP.NET Core server integration.
/// </summary>
public sealed class ShmConnectionListener : IDisposable, IAsyncDisposable
{
    private readonly string _segmentName;
    private readonly ShmConnection _serverConnection;
    private readonly CancellationTokenSource _disposeCts;
    private int _disposed;

    /// <summary>
    /// Gets the endpoint name for this listener.
    /// </summary>
    public EndPoint EndPoint { get; }

    /// <summary>
    /// Gets the shared memory segment name.
    /// </summary>
    public string SegmentName => _segmentName;

    /// <summary>
    /// Gets the underlying server connection.
    /// </summary>
    public ShmConnection Connection => _serverConnection;

    /// <summary>
    /// Creates a new shared memory listener with the specified segment name.
    /// </summary>
    /// <param name="segmentName">The name for the shared memory segment.</param>
    /// <param name="ringCapacity">The capacity of each ring buffer (default: 64MB).</param>
    /// <param name="maxStreams">Maximum concurrent streams (default: 100).</param>
    public ShmConnectionListener(string segmentName, ulong ringCapacity = 64 * 1024 * 1024, uint maxStreams = 100)
    {
        _segmentName = segmentName ?? throw new ArgumentNullException(nameof(segmentName));
        _serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity, maxStreams);
        _disposeCts = new CancellationTokenSource();
        EndPoint = new ShmEndPoint(segmentName);
    }

    /// <summary>
    /// Accepts incoming gRPC streams from clients.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of incoming gRPC streams.</returns>
    public async IAsyncEnumerable<ShmGrpcStream> AcceptStreamsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);

        // Use the ShmConnection's stream acceptance channel
        await foreach (var stream in _serverConnection.AcceptStreamsAsync(linkedCts.Token))
        {
            yield return stream;
        }
    }

    /// <summary>
    /// Accepts a single incoming gRPC stream from a client.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The next incoming stream, or null if the listener is closed.</returns>
    public ValueTask<ShmGrpcStream?> AcceptStreamAsync(CancellationToken cancellationToken = default)
    {
        return _serverConnection.AcceptStreamAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _disposeCts.Cancel();
        _serverConnection.Dispose();
        _disposeCts.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _disposeCts.Cancel();
        await _serverConnection.DisposeAsync();
        _disposeCts.Dispose();
    }
}

/// <summary>
/// Represents a shared memory endpoint.
/// </summary>
public sealed class ShmEndPoint : EndPoint
{
    /// <summary>
    /// Gets the shared memory segment name.
    /// </summary>
    public string SegmentName { get; }

    /// <summary>
    /// Creates a new ShmEndPoint.
    /// </summary>
    /// <param name="segmentName">The segment name.</param>
    public ShmEndPoint(string segmentName)
    {
        SegmentName = segmentName ?? throw new ArgumentNullException(nameof(segmentName));
    }

    /// <inheritdoc/>
    public override string ToString() => $"shm://{SegmentName}";

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return obj is ShmEndPoint other && SegmentName == other.SegmentName;
    }

    /// <inheritdoc/>
    public override int GetHashCode() => SegmentName.GetHashCode();
}

/// <summary>
/// Extension methods for configuring shared memory transport.
/// </summary>
public static class ShmServerExtensions
{
    /// <summary>
    /// Configures the server to listen on a shared memory segment.
    /// </summary>
    /// <param name="builder">The web application builder.</param>
    /// <param name="segmentName">The shared memory segment name.</param>
    /// <param name="ringCapacity">Ring buffer capacity (default: 64MB).</param>
    /// <param name="maxStreams">Maximum concurrent streams (default: 100).</param>
    /// <returns>The listener for use in custom server implementations.</returns>
    public static ShmConnectionListener ListenOnSharedMemory(
        string segmentName,
        ulong ringCapacity = 64 * 1024 * 1024,
        uint maxStreams = 100)
    {
        return new ShmConnectionListener(segmentName, ringCapacity, maxStreams);
    }
}
