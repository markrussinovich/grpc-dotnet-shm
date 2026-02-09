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

using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Connections;
using Grpc.Net.SharedMemory;

namespace Grpc.AspNetCore.Server.SharedMemory;

/// <summary>
/// An <see cref="IConnectionListener"/> that accepts shared memory connections
/// using the grpc-go-shmem compatible control segment protocol.
/// Each accepted connection wraps a pair of SHM ring buffers as a bidirectional
/// byte stream that Kestrel can run HTTP/2 over.
/// </summary>
internal sealed class ShmConnectionListenerAdapter : IConnectionListener
{
    private readonly string _baseName;
    private readonly ShmTransportOptions _options;
    private readonly ShmEndPoint _endPoint;
    private readonly CancellationTokenSource _closeCts;
    private readonly Segment _controlSegment;
    private readonly ShmRing _controlRx;  // Ring A: client→server
    private readonly ShmRing _controlTx;  // Ring B: server→client
    private readonly ConcurrentDictionary<string, Segment> _activeSegments;
    private int _connectionId;
    private bool _disposed;

    public ShmConnectionListenerAdapter(string baseName, ShmTransportOptions options)
    {
        _baseName = baseName;
        _options = options;
        _endPoint = new ShmEndPoint(baseName);
        _closeCts = new CancellationTokenSource();
        _activeSegments = new ConcurrentDictionary<string, Segment>();

        // Create the control segment (grpc-go-shmem compatible)
        _controlSegment = Segment.CreateControlSegment(baseName);
        _controlSegment.SetServerReady(true);

        // Ring A = client→server (we read), Ring B = server→client (we write)
        _controlRx = _controlSegment.RingA;
        _controlTx = _controlSegment.RingB;
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

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _closeCts.Token);
        var ct = linkedCts.Token;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Read a frame from the control ring
                var (frameHeader, payload) = await ReadControlFrameAsync(ct).ConfigureAwait(false);

                if (frameHeader.Type != FrameType.Connect)
                {
                    continue;
                }

                // Validate CONNECT request
                try
                {
                    ControlWire.DecodeConnectRequest(payload.Span);
                }
                catch (Exception ex)
                {
                    WriteControlFrame(FrameType.Reject,
                        ControlWire.EncodeConnectReject(ex.Message));
                    continue;
                }

                // Create a data segment for this connection
                var connId = Interlocked.Increment(ref _connectionId);
                var segName = $"{_baseName}_conn_{connId}";
                Segment.TryRemoveSegment(segName);

                Segment dataSegment;
                try
                {
                    dataSegment = Segment.Create(segName, _options.RingCapacity, _options.MaxStreams);
                    dataSegment.SetServerReady(true);
                }
                catch (Exception ex)
                {
                    WriteControlFrame(FrameType.Reject,
                        ControlWire.EncodeConnectReject($"Failed to create segment: {ex.Message}"));
                    continue;
                }

                // Send ACCEPT with the data segment name
                WriteControlFrame(FrameType.Accept,
                    ControlWire.EncodeConnectResponse(segName));

                // Wait for client to map the data segment
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

                _activeSegments[segName] = dataSegment;

                // Server reads from RingA (client→server), writes to RingB (server→client)
                var shmStream = new ShmStream(dataSegment.RingA, dataSegment.RingB);

                return new ShmConnectionContext(
                    connectionId: segName,
                    shmStream: shmStream,
                    localEndPoint: _endPoint);
            }
            catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
            {
                return null;
            }
        }

        return null;
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
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }
        _disposed = true;
        _closeCts.Cancel();

        // Clean up control segment
        _controlRx.Dispose();
        _controlTx.Dispose();
        _controlSegment.Dispose();
        Segment.TryRemoveSegment(_baseName + ShmConstants.ControlSegmentSuffix);

        // Clean up all data segments
        foreach (var (name, segment) in _activeSegments)
        {
            segment.Dispose();
            Segment.TryRemoveSegment(name);
        }
        _activeSegments.Clear();

        _closeCts.Dispose();
        return ValueTask.CompletedTask;
    }

    private void WriteControlFrame(FrameType type, byte[] payload)
    {
        var header = new FrameHeader
        {
            Length = (uint)payload.Length,
            StreamId = 0,
            Type = type,
            Flags = 0
        };

        var headerBytes = header.ToBytes();
        _controlTx.Write(headerBytes);
        if (payload.Length > 0)
        {
            _controlTx.Write(payload);
        }
    }

    private async Task<(FrameHeader header, Memory<byte> payload)> ReadControlFrameAsync(
        CancellationToken ct)
    {
        // Wait for frame header
        while (!_controlRx.TryPeek(ShmConstants.FrameHeaderSize, out _))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1, ct).ConfigureAwait(false);
        }

        var headerBuffer = new byte[ShmConstants.FrameHeaderSize];
        if (!_controlRx.TryRead(headerBuffer))
        {
            throw new InvalidOperationException("Failed to read control frame header");
        }

        var header = FrameHeader.Parse(headerBuffer);

        // Read payload
        Memory<byte> payload = Memory<byte>.Empty;
        if (header.Length > 0)
        {
            while (!_controlRx.TryPeek((int)header.Length, out _))
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(1, ct).ConfigureAwait(false);
            }

            var payloadBuffer = new byte[header.Length];
            if (!_controlRx.TryRead(payloadBuffer))
            {
                throw new InvalidOperationException("Failed to read control frame payload");
            }
            payload = payloadBuffer;
        }

        return (header, payload);
    }
}
