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
    private int _disposed;

    /// <summary>
    /// Gets or sets the optional security handshaker. When non-null,
    /// each accepted connection performs a process-level identity
    /// handshake on its data segment immediately after the
    /// control-segment CONNECT/ACCEPT completes; the resulting
    /// <see cref="ShmAuthInfo"/> is surfaced on
    /// <see cref="ShmConnection.AuthInfo"/>. Mirrors grpc-go-shmem's
    /// transport-layer <c>ShmSecurityHandshaker</c>.
    /// <para>
    /// When <c>null</c> (default) the server skips the handshake and
    /// returns connections with <c>AuthInfo == null</c>, matching the
    /// insecure-local default. Both peers must agree (either both have
    /// a handshaker or neither does) — mixed modes deadlock the
    /// silent peer waits for the data-segment frame the handshaker
    /// peer never sends.
    /// </para>
    /// </summary>
    public IShmSecurityHandshaker? Handshaker { get; set; }

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

        // Duplicate-server detection: if another listener is already
        // serving on this base name, refuse to start. Probing the
        // existing control segment is cheaper than racing on a creation
        // error AND avoids silently unlinking a peer listener's inode
        // (mirrors grpc-go-shmem NewShmListener behaviour).
        try
        {
            using var existing = Segment.OpenControlSegment(baseName);
            if (existing.IsServerReady())
            {
                throw new InvalidOperationException(
                    $"A server is already listening on segment '{baseName}'.");
            }
            // Existing segment present but ServerReady=0 → stale; fall
            // through and overwrite it.
        }
        catch (FileNotFoundException)
        {
            // Normal first-start path: no existing control segment.
        }
        catch (DirectoryNotFoundException)
        {
            // Same as FileNotFoundException on some platforms.
        }

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
            FrameHeader frameHeader;
            Memory<byte> payload;
            try
            {
                (frameHeader, payload) = await ReadControlFrameAsync(ct).ConfigureAwait(false);
            }
            catch (MalformedControlFrameException)
            {
                // Hostile or stale peer sent a malformed control frame.
                // ReadControlFrameAsync has already best-effort-drained
                // the bytes from the ring so subsequent reads can
                // resynchronize on the next frame boundary. Skip and
                // keep accepting; mirrors grpc-go-shmem listener
                // behaviour for errMalformedCtlFrame.
                continue;
            }

            if (frameHeader.Type != FrameType.Connect)
            {
                // Ignore non-CONNECT frames
                continue;
            }

            // Decode and validate CONNECT request. Capture the
            // per-CONNECT correlation nonce so we can echo it in the
            // matching ACCEPT/REJECT (closes the stale-response
            // misbinding race — dialer correlates response to its own
            // in-flight CONNECT by nonce). On decode failure we echo 0
            // per the gRFC: a 0 nonce signals "server could not decode
            // your CONNECT" so matched-version dialers treat it as
            // stale and skip it.
            (ulong clientRingA, ulong clientRingB, bool clientSingleStream, ulong clientNonce) connectParams;
            try
            {
                connectParams = ControlWire.DecodeConnectRequest(payload.Span);
            }
            catch (Exception ex)
            {
                // Send REJECT with nonce = 0 (we couldn't decode it).
                await SendRejectAsync(ex.Message, nonce: 0, ct).ConfigureAwait(false);
                continue;
            }

            // Negotiate ring capacity: Min(clientPreferred, serverMax).
            // If client sends 0, use server default.
            var negotiatedRing = ControlWire.NegotiateRingCapacity(
                connectParams.clientRingA, _ringCapacity);

            // Purge closed connections to free resources accumulated from
            // previous test runs. Without this, _activeConnections grows
            // unboundedly and stale connection objects leak their
            // FrameReaderLoopAsync threads and ring buffer kernel events.
            PurgeClosedConnections();

            // Create a new data segment for this connection
            var connId = Interlocked.Increment(ref _connectionId);
            var segmentName = $"{_baseName}_conn_{connId}";

            // Clean up any stale segment
            Segment.TryRemoveSegment(segmentName);

            Segment? dataSegment = null;
            try
            {
                dataSegment = Segment.Create(segmentName, negotiatedRing, _maxStreams);
                dataSegment.SetServerReady(true);
            }
            catch (Exception ex)
            {
                dataSegment?.Dispose();
                Segment.TryRemoveSegment(segmentName);
                await SendRejectAsync($"Failed to create segment: {ex.Message}", connectParams.clientNonce, ct).ConfigureAwait(false);
                continue;
            }

            // Send ACCEPT with the data segment name.
            // Wire format is always HTTP/2 — the protocol layer in
            // <see cref="ControlWire.DecodeConnectRequest"/> already
            // rejected any peer that did not advertise H2. Echo the
            // CONNECT nonce so the dialer can correlate this response
            // to its in-flight request.
            await SendAcceptAsync(segmentName, connectParams.clientNonce, ct).ConfigureAwait(false);

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
                Segment.TryRemoveSegment(segmentName);
                continue;
            }

            // Linux eventfd negotiation: after the opener has signalled it
            // mapped the segment, the OpenerWakeReady flag in the segment
            // header is stable. Drop our eventfd waker if the opener did
            // not establish one (peer is using a futex-only build, or its
            // SCM_RIGHTS handoff failed) so both sides converge on the
            // same wake primitive. No-op on Windows / when eventfd wake
            // is disabled.
            dataSegment.FinalizeDataSegWaker();

            // Optional security handshake on the data segment BEFORE
            // we hand it to ShmConnection — the connection ctor starts
            // the frame reader loop which would otherwise race the
            // handshake frame I/O. Mirrors grpc-go-shmem's
            // transport-layer ShmSecurityHandshaker.ServerHandshake.
            ShmAuthInfo? authInfo = null;
            if (Handshaker != null)
            {
                try
                {
                    // From the server's perspective: RingA is
                    // client→server (we read), RingB is server→client
                    // (we write).
                    authInfo = await Handshaker.ServerHandshakeAsync(
                        writer: (type, payload, c) => WriteHandshakeFrameAsync(dataSegment.RingB, type, payload, c),
                        reader: c => ReadHandshakeFrameAsync(dataSegment.RingA, c),
                        ct).ConfigureAwait(false);
                }
                catch
                {
                    // Handshake failure: dispose the data segment and
                    // continue accepting. The client surfaces a
                    // ShmHandshakeException from its side; we just drop
                    // the half-built connection on the floor here.
                    dataSegment.Dispose();
                    Segment.TryRemoveSegment(segmentName);
                    continue;
                }
            }

            // Create and return the connection
            ShmConnection connection;
            try
            {
                connection = new ShmConnection(segmentName, dataSegment);
                connection.AuthInfo = authInfo;
                // Propagate client's singleStreamMode request.
                // Server decides in HandleConnectionAsync whether to honor it.
                connection.SingleStreamMode = connectParams.clientSingleStream;
            }
            catch
            {
                dataSegment.Dispose();
                Segment.TryRemoveSegment(segmentName);
                throw;
            }

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
        while (!cancellationToken.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
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

    private Task<(FrameHeader header, Memory<byte> payload)> ReadControlFrameAsync(CancellationToken ct)
    {
        // Read frame header
        var headerBuffer = new byte[ShmConstants.FrameHeaderSize];
        ReadExact(_controlRx, headerBuffer, ct);

        var header = FrameHeader.Parse(headerBuffer);

        // Read payload if any
        Memory<byte> payload = Memory<byte>.Empty;
        if (header.Length > 0)
        {
            if (header.Length > ShmConstants.MinRingCapacity)
            {
                // Hostile peer sent an oversize Length. Best-effort
                // drain those bytes from the ring so the next
                // ReadControlFrameAsync resynchronizes on a clean frame
                // boundary instead of consuming this frame's bytes as
                // the next header. Mirrors grpc-go-shmem's
                // readCtlFrame errMalformedCtlFrame drain.
                BestEffortDrain(_controlRx, header.Length, ct);
                throw new MalformedControlFrameException(
                    $"Control frame payload {header.Length} exceeds maximum {ShmConstants.MinRingCapacity}.");
            }

            var payloadBuffer = new byte[header.Length];
            ReadExact(_controlRx, payloadBuffer, ct);
            payload = payloadBuffer;
        }

        return Task.FromResult((header, payload));
    }

    /// <summary>
    /// Reads and discards up to <paramref name="totalBytes"/> bytes from
    /// the ring in 4 KiB chunks. Used to resynchronize the ring after a
    /// malformed frame header reports an oversize Length.
    /// </summary>
    private static void BestEffortDrain(ShmRing ring, uint totalBytes, CancellationToken ct)
    {
        const int chunk = (int)ShmConstants.MinRingCapacity; // 4096
        Span<byte> sink = stackalloc byte[chunk];
        var remaining = (long)totalBytes;
        while (remaining > 0 && !ct.IsCancellationRequested)
        {
            var take = (int)Math.Min(remaining, chunk);
            try
            {
                ReadExact(ring, sink[..take], ct);
            }
            catch
            {
                // Best-effort: if the ring closes or the read fails
                // mid-drain there is nothing useful left to do; the
                // outer Accept loop will surface the underlying failure
                // on its next iteration.
                return;
            }
            remaining -= take;
        }
    }

    /// <summary>
    /// Removes connections that have been closed or disposed from
    /// <see cref="_activeConnections"/>. This prevents unbounded accumulation
    /// of stale connection objects (and their background reader threads)
    /// across consecutive test runs on a long-lived server.
    /// </summary>
    private void PurgeClosedConnections()
    {
        foreach (var (name, conn) in _activeConnections)
        {
            // Only purge connections that are fully closed AND have no
            // in-flight streams.  IsClosed becomes true on GoAway, but
            // a draining connection may still have active RPCs.
            if (conn.IsClosed && conn.ActiveStreamCount == 0)
            {
                if (_activeConnections.TryRemove(name, out var removed))
                {
                    try { removed.Dispose(); } catch { }
                    Segment.TryRemoveSegment(name);
                }
            }
        }
    }

    private Task WriteControlFrameAsync(FrameType type, byte[] payload, CancellationToken ct)
    {
        var header = new FrameHeader
        {
            Length = (uint)payload.Length,
            StreamId = 0,
            Type = type,
            Flags = 0
        };

        var headerBytes = header.ToBytes();
        // Write header and payload (ring.Write blocks until space is available)
        _controlTx.Write(headerBytes, ct);
        if (payload.Length > 0)
        {
            _controlTx.Write(payload, ct);
        }

        return Task.CompletedTask;
    }

    private static void ReadExact(ShmRing ring, Span<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            read += ring.Read(buffer[read..], ct);
        }
    }

    /// <summary>
    /// Writes a single security-handshake frame to the data-segment ring.
    /// Adapter matching <see cref="IShmSecurityHandshaker"/>'s writer
    /// delegate signature.
    /// </summary>
    private static Task WriteHandshakeFrameAsync(ShmRing ring, FrameType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var header = new FrameHeader
        {
            Length = (uint)payload.Length,
            StreamId = 0,
            Type = type,
            Flags = 0
        };
        var headerBytes = header.ToBytes();
        ring.Write(headerBytes, ct);
        if (payload.Length > 0)
        {
            var buffer = payload.ToArray();
            ring.Write(buffer, ct);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads a single security-handshake frame from the data-segment
    /// ring. Adapter matching <see cref="IShmSecurityHandshaker"/>'s
    /// reader delegate signature.
    /// </summary>
    private static Task<(FrameType Type, ReadOnlyMemory<byte> Payload)> ReadHandshakeFrameAsync(ShmRing ring, CancellationToken ct)
    {
        var headerBuffer = new byte[ShmConstants.FrameHeaderSize];
        ReadExact(ring, headerBuffer, ct);
        var header = FrameHeader.Parse(headerBuffer);

        if (header.Length > ShmConstants.MinRingCapacity)
        {
            // Defend against a hostile peer sending an oversize handshake
            // frame. The handshake runs on the data segment which has a
            // wider ring than the control segment, but identity tokens
            // are spec-bounded to MaxIdentitySize (256 B) and Fail
            // messages are short — anything over 4 KiB is malformed.
            BestEffortDrain(ring, header.Length, ct);
            throw new InvalidDataException(
                $"Handshake frame payload {header.Length} exceeds maximum {ShmConstants.MinRingCapacity}.");
        }

        ReadOnlyMemory<byte> payload = ReadOnlyMemory<byte>.Empty;
        if (header.Length > 0)
        {
            var payloadBuffer = new byte[header.Length];
            ReadExact(ring, payloadBuffer, ct);
            payload = payloadBuffer;
        }
        return Task.FromResult((header.Type, payload));
    }

    private Task SendAcceptAsync(string segmentName, ulong nonce, CancellationToken ct)
    {
        var payload = ControlWire.EncodeConnectResponse(segmentName, nonce);
        return WriteControlFrameAsync(FrameType.Accept, payload, ct);
    }

    private Task SendRejectAsync(string message, ulong nonce, CancellationToken ct)
    {
        var payload = ControlWire.EncodeConnectReject(message, nonce);
        return WriteControlFrameAsync(FrameType.Reject, payload, ct);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

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

/// <summary>
/// Thrown when a control-segment frame has a malformed header (e.g.
/// oversize <c>Length</c>). The listener's Accept loop catches this and
/// resynchronizes on the next frame boundary instead of tearing down the
/// listener; the peer is treated as hostile or out-of-sync. Mirrors
/// grpc-go-shmem's <c>errMalformedCtlFrame</c> sentinel.
/// </summary>
public sealed class MalformedControlFrameException : Exception
{
    /// <summary>Creates the exception with the supplied message.</summary>
    public MalformedControlFrameException(string message) : base(message) { }
}
