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

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// A connection listener compatible with grpc-go-shmem.
/// Uses a control segment (_ctl) for connection establishment and creates
/// per-connection data segments, matching the Go implementation.
/// </summary>
public sealed class ShmControlListener : IDisposable, IAsyncDisposable
{
    private readonly string _baseName;
    private readonly Segment _controlSegment;
    private readonly ShmRing _controlRx;  // Ring A: client→server
    private readonly ShmRing _controlTx;  // Ring B: server→client
    private readonly ConcurrentDictionary<string, ShmConnection> _activeConnections;
    private readonly CancellationTokenSource _disposeCts;
    private readonly ulong _ringCapacity;
    private readonly uint _maxStreams;
    private int _connectionId;
    private bool _disposed;

    /// <summary>
    /// Gets the base segment name.
    /// </summary>
    public string BaseName => _baseName;

    /// <summary>
    /// Gets the endpoint.
    /// </summary>
    public EndPoint EndPoint { get; }

    /// <summary>
    /// Creates a new listener compatible with grpc-go-shmem.
    /// </summary>
    /// <param name="baseName">The base segment name (without _ctl suffix).</param>
    /// <param name="ringCapacity">Ring buffer capacity for data segments (default: 64MB).</param>
    /// <param name="maxStreams">Maximum concurrent streams per connection (default: 100).</param>
    public ShmControlListener(string baseName, ulong ringCapacity = 64 * 1024 * 1024, uint maxStreams = 100)
    {
        _baseName = baseName ?? throw new ArgumentNullException(nameof(baseName));
        _ringCapacity = ringCapacity;
        _maxStreams = maxStreams;
        _activeConnections = new ConcurrentDictionary<string, ShmConnection>();
        _disposeCts = new CancellationTokenSource();
        EndPoint = new ShmEndPoint(baseName);

        // Create the control segment like Go does
        _controlSegment = Segment.CreateControlSegment(baseName);
        _controlSegment.SetServerReady(true);

        // Ring A is client→server (we read from it)
        // Ring B is server→client (we write to it)
        _controlRx = _controlSegment.RingA;
        _controlTx = _controlSegment.RingB;
    }

    /// <summary>
    /// Accepts a new connection from a client.
    /// This implements the grpc-go-shmem connection handshake protocol.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A new ShmConnection for this client.</returns>
    public async Task<ShmConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        var ct = linkedCts.Token;

        while (!ct.IsCancellationRequested)
        {
            // Read a frame from the control ring
            var (frameHeader, payload) = await ReadControlFrameAsync(ct).ConfigureAwait(false);

            if (frameHeader.Type != FrameType.Connect)
            {
                payload.Release();
                // Ignore non-CONNECT frames
                continue;
            }

            // Decode and validate CONNECT request
            try
            {
                ControlWire.DecodeConnectRequest(payload.Memory.Span);
            }
            catch (Exception ex)
            {
                // Send REJECT
                await SendRejectAsync(ex.Message, ct).ConfigureAwait(false);
                continue;
            }
            finally
            {
                payload.Release();
            }

            // Create a new data segment for this connection
            var connId = Interlocked.Increment(ref _connectionId);
            var segmentName = $"{_baseName}_conn_{connId}";

            // Clean up any stale segment
            Segment.TryRemoveSegment(segmentName);

            Segment dataSegment;
            try
            {
                dataSegment = Segment.Create(segmentName, _ringCapacity, _maxStreams);
                dataSegment.SetServerReady(true);
            }
            catch (Exception ex)
            {
                await SendRejectAsync($"Failed to create segment: {ex.Message}", ct).ConfigureAwait(false);
                continue;
            }

            // Send ACCEPT with the data segment name
            await SendAcceptAsync(segmentName, ct).ConfigureAwait(false);

            // Wait for client to map the segment
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                await dataSegment.WaitForClientAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                dataSegment.Dispose();
                continue;
            }

            // Create and return the connection
            var connection = new ShmConnection(segmentName, dataSegment);
            _activeConnections[segmentName] = connection;
            return connection;
        }

        throw new OperationCanceledException(ct);
    }

    /// <summary>
    /// Accepts incoming connections as an async enumerable.
    /// </summary>
    public async IAsyncEnumerable<ShmConnection> AcceptConnectionsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            ShmConnection? connection = null;
            try
            {
                connection = await AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            if (connection != null)
            {
                yield return connection;
            }
        }
    }

    private Task<(FrameHeader header, FramePayload payload)> ReadControlFrameAsync(CancellationToken ct)
    {
        var frame = FrameProtocol.ReadFramePayload(_controlRx, allowBorrowed: true, ct);
        return Task.FromResult((frame.Header, frame.Payload));
    }

    private Task WriteControlFrameAsync(FrameType type, byte[] payload, CancellationToken ct)
    {
        var header = new FrameHeader(type, 0, 0, 0);
        FrameProtocol.WriteFrame(_controlTx, header, payload, ct);

        return Task.CompletedTask;
    }

    private Task SendAcceptAsync(string segmentName, CancellationToken ct)
    {
        var payload = ControlWire.EncodeConnectResponse(segmentName);
        return WriteControlFrameAsync(FrameType.Accept, payload, ct);
    }

    private Task SendRejectAsync(string message, CancellationToken ct)
    {
        var payload = ControlWire.EncodeConnectReject(message);
        return WriteControlFrameAsync(FrameType.Reject, payload, ct);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _disposeCts.Cancel();

        // Close control segment
        _controlRx.Dispose();
        _controlTx.Dispose();
        _controlSegment.Dispose();

        // Remove control segment file
        Segment.TryRemoveSegment(_baseName + ShmConstants.ControlSegmentSuffix);

        // Close all active connections
        foreach (var conn in _activeConnections.Values)
        {
            conn.Dispose();
        }
        _activeConnections.Clear();

        _disposeCts.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _disposeCts.Cancel();

        // Close control segment
        _controlRx.Dispose();
        _controlTx.Dispose();
        _controlSegment.Dispose();

        // Remove control segment file
        Segment.TryRemoveSegment(_baseName + ShmConstants.ControlSegmentSuffix);

        // Close all active connections
        foreach (var conn in _activeConnections.Values)
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
        _activeConnections.Clear();

        _disposeCts.Dispose();
    }
}
