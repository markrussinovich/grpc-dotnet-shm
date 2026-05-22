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

namespace Grpc.Net.SharedMemory;

using System.Buffers;

/// <summary>
/// High-level frame protocol operations for reading and writing gRPC frames
/// to the shared memory ring buffer.
/// </summary>
public static class FrameProtocol
{
    /// <summary>
    /// Maximum allowed frame payload size (128 MiB). Any frame header claiming a
    /// payload larger than this is treated as data corruption (e.g., from a SPSC
    /// ring buffer violation or stale shared memory) and will throw rather than
    /// attempting a huge allocation that would hang or OOM.
    /// </summary>
    internal const int MaxFramePayloadSize = 128 * 1024 * 1024;

    /// <summary>
    /// Reads a frame from the ring and returns a pooled-buffer payload.
    /// Always uses the HTTP/2 wire format (the only supported format).
    /// </summary>
    public static (FrameHeader Header, FramePayload Payload) ReadFramePayload(
        ShmRing ring,
        CancellationToken cancellationToken = default,
        bool zeroCopy = false)
        => Wire.Http2Codec.ReadFramePayload(ring, cancellationToken, zeroCopy);

    /// <summary>
    /// Writes a frame (header + payload) to the ring buffer atomically.
    /// Blocks until space is available.
    /// </summary>
    /// <param name="ring">The ring buffer to write to.</param>
    /// <param name="header">The frame header.</param>
    /// <param name="payload">The frame payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static void WriteFrame(ShmRing ring, FrameHeader header, ReadOnlySpan<byte> payload, CancellationToken cancellationToken = default)
    {
        // Delegate to the scatter-write overload with an empty second payload
        WriteFrame(ring, header, payload, ReadOnlySpan<byte>.Empty, cancellationToken);
    }

    /// <summary>
    /// Writes a frame with a two-part payload (scatter write) to the ring buffer atomically.
    /// This avoids an intermediate copy when the payload is logically split (e.g., gRPC prefix + data).
    /// The frame header's Length is set to payload1.Length + payload2.Length.
    /// </summary>
    /// <param name="ring">The ring buffer to write to.</param>
    /// <param name="header">The frame header.</param>
    /// <param name="payload1">The first part of the frame payload (e.g., gRPC length-prefix header).</param>
    /// <param name="payload2">The second part of the frame payload (e.g., protobuf message data).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static void WriteFrame(ShmRing ring, FrameHeader header, ReadOnlySpan<byte> payload1, ReadOnlySpan<byte> payload2, CancellationToken cancellationToken = default)
        => Wire.Http2Codec.WriteFrame(ring, header, payload1, payload2, cancellationToken);

    /// <summary>
    /// Writes a PING frame.
    /// </summary>
    public static void WritePing(ShmRing ring, byte flags, ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        var header = new FrameHeader(FrameType.Ping, 0, (uint)data.Length, flags);
        WriteFrame(ring, header, data, cancellationToken);
    }

    /// <summary>
    /// Writes a PONG frame.
    /// </summary>
    public static void WritePong(ShmRing ring, byte flags, ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        var header = new FrameHeader(FrameType.Pong, 0, (uint)data.Length, flags);
        WriteFrame(ring, header, data, cancellationToken);
    }

    /// <summary>
    /// Writes a GOAWAY frame.
    /// </summary>
    public static void WriteGoAway(ShmRing ring, byte flags, string? debugMessage = null, CancellationToken cancellationToken = default)
    {
        var payload = debugMessage != null ? System.Text.Encoding.UTF8.GetBytes(debugMessage) : Array.Empty<byte>();
        var header = new FrameHeader(FrameType.GoAway, 0, (uint)payload.Length, flags);
        WriteFrame(ring, header, payload.AsSpan(), cancellationToken);
    }

    /// <summary>
    /// Writes a CANCEL frame.
    /// </summary>
    public static void WriteCancel(ShmRing ring, uint streamId, CancellationToken cancellationToken = default)
    {
        var header = new FrameHeader(FrameType.Cancel, streamId, 0, 0);
        WriteFrame(ring, header, ReadOnlySpan<byte>.Empty, cancellationToken);
    }

    /// <summary>
    /// Writes a WINDOW_UPDATE frame.
    /// </summary>
    public static void WriteWindowUpdate(ShmRing ring, uint streamId, uint windowSizeIncrement, CancellationToken cancellationToken = default)
    {
        Span<byte> payload = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload, windowSizeIncrement);
        var header = new FrameHeader(FrameType.WindowUpdate, streamId, 4, 0);
        WriteFrame(ring, header, payload, cancellationToken);
    }

    /// <summary>
    /// Writes a MESSAGE frame, automatically chunking if the payload exceeds
    /// the ring capacity. Matches grpc-go-shmem's writeFrameBuffersChunked.
    /// </summary>
    public static void WriteMessage(ShmRing ring, uint streamId, ReadOnlySpan<byte> data, bool isLast, CancellationToken cancellationToken = default, byte extraFlags = 0)
    {
        var flags = (byte)((isLast ? 0 : MessageFlags.More) | extraFlags);

        var cap = (int)ring.Capacity;
        // Max frame payload = ringCap/3. Chosen so that:
        // 1. Common payloads (4MB, 16MB) fit in a single frame →
        //    speculative zero-copy read path (no More flag).
        //    16MB protobuf CalculateSize ≈ 16.8MB < 64MB/3 = 22.4MB.
        // 2. Speculative safety: N=2 in-flight × cap/3 = 2cap/3 < cap,
        //    so writer needs cap/3 (~22MB) to reach oldest frame.
        // 3. Pipeline: 3 frames fit simultaneously in the ring,
        //    providing good overlap for streaming workloads.
        var maxFramePayload = Math.Max(1, cap / 3);

        // HTTP/2 has an absolute hard cap on per-frame payload length
        // (24-bit field; RFC 7540 §6.5.2 SETTINGS_MAX_FRAME_SIZE upper bound
        // is 2^24 - 1). Cap our chunk size below that so the H2 codec can
        // encode every frame.
        if (maxFramePayload > Wire.Http2FrameHeader.MaxAllowedPayloadLength)
        {
            maxFramePayload = Wire.Http2FrameHeader.MaxAllowedPayloadLength;
        }

        if (data.Length <= maxFramePayload)
        {
            var header = new FrameHeader(FrameType.Message, streamId, (uint)data.Length, flags);
            WriteFrame(ring, header, data, cancellationToken);
            return;
        }

        var remaining = data;
        while (remaining.Length > 0)
        {
            var chunkSize = Math.Min(maxFramePayload, remaining.Length);
            var chunk = remaining[..chunkSize];
            remaining = remaining[chunkSize..];

            byte chunkFlags;
            if (remaining.Length > 0)
            {
                chunkFlags = MessageFlags.More;
            }
            else
            {
                chunkFlags = flags;
            }

            var header = new FrameHeader(FrameType.Message, streamId, (uint)chunkSize, chunkFlags);
            WriteFrame(ring, header, chunk, cancellationToken);
        }
    }

    /// <summary>
    /// Writes a HALF_CLOSE frame.
    /// </summary>
    public static void WriteHalfClose(ShmRing ring, uint streamId, CancellationToken cancellationToken = default)
    {
        var header = new FrameHeader(FrameType.HalfClose, streamId, 0, 0);
        WriteFrame(ring, header, ReadOnlySpan<byte>.Empty, cancellationToken);
    }

    private static void CopyFromReservation(ReadReservation reservation, Span<byte> destination)
    {
        var copied = 0;
        if (reservation.First.Length > 0)
        {
            var toCopy = Math.Min(reservation.First.Length, destination.Length);
            reservation.First.Span[..toCopy].CopyTo(destination);
            copied += toCopy;
        }
        if (reservation.Second.Length > 0 && copied < destination.Length)
        {
            var toCopy = Math.Min(reservation.Second.Length, destination.Length - copied);
            reservation.Second.Span[..toCopy].CopyTo(destination[copied..]);
        }
    }
}
