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
    /// Reads a frame and returns a payload wrapper that can either borrow ring memory
    /// (when contiguous and allowed) or use a pooled buffer fallback.
    /// </summary>
    public static (FrameHeader Header, FramePayload Payload) ReadFramePayload(
        ShmRing ring,
        bool allowBorrowed,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            // Read frame header
            var headerReservation = ring.ReserveRead(ShmConstants.FrameHeaderSize, cancellationToken);

            Span<byte> headerBytes = stackalloc byte[ShmConstants.FrameHeaderSize];
            CopyFromReservation(headerReservation, headerBytes);
            ring.CommitRead(headerReservation, ShmConstants.FrameHeaderSize);

            var header = FrameHeader.DecodeFrom(headerBytes);

            // Guard against corrupted frame headers that could cause huge
            // allocations or block forever trying to read from the ring.
            if (header.Length > MaxFramePayloadSize)
            {
                throw new InvalidDataException(
                    $"Frame payload length {header.Length} exceeds maximum {MaxFramePayloadSize}. " +
                    "This may indicate data corruption in the shared memory ring buffer.");
            }

            if (!Enum.IsDefined(header.Type) && header.Type != FrameType.Pad)
            {
                throw new InvalidDataException(
                    $"Unknown frame type 0x{(byte)header.Type:X2} with length {header.Length}. " +
                    "This may indicate data corruption in the shared memory ring buffer.");
            }

            // Skip PAD frames
            if (header.Type == FrameType.Pad)
            {
                if (header.Length > 0)
                {
                    var padReservation = ring.ReserveRead((int)header.Length, cancellationToken);
                    ring.CommitRead(padReservation, (int)header.Length);
                }
                continue;
            }

            if (header.Length == 0)
            {
                return (header, FramePayload.Empty);
            }

            var payloadLength = (int)header.Length;
            var payloadReservation = ring.ReserveRead(payloadLength, cancellationToken);

            if (allowBorrowed && payloadReservation.Second.IsEmpty)
            {
                return (header, FramePayload.FromReservation(payloadReservation, payloadLength));
            }

            var payload = ArrayPool<byte>.Shared.Rent(payloadLength);
            CopyFromReservation(payloadReservation, payload.AsSpan(0, payloadLength));
            ring.CommitRead(payloadReservation, payloadLength);
            return (header, FramePayload.FromPooled(payload, payloadLength));
        }
    }
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
    {
        var totalPayloadSize = payload1.Length + payload2.Length;
        header.Length = (uint)totalPayloadSize;
        header.Reserved = 0;
        header.Reserved2 = 0;

        var totalSize = ShmConstants.FrameHeaderSize + totalPayloadSize;

        // Reserve space for the entire frame atomically
        var reservation = ring.ReserveWrite(totalSize, cancellationToken);

        // Encode header
        Span<byte> headerBytes = stackalloc byte[ShmConstants.FrameHeaderSize];
        header.EncodeTo(headerBytes);

        // We need to write 3 parts into potentially 2 slices (First/Second).
        // Use a helper approach: treat the reservation as a linear span and write sequentially.
        var firstSpan = reservation.First.Span;
        var secondSpan = reservation.Second.Span;
        var written = 0;

        // Write header
        written = WriteToReservation(firstSpan, secondSpan, written, headerBytes);

        // Write payload1
        if (payload1.Length > 0)
        {
            written = WriteToReservation(firstSpan, secondSpan, written, payload1);
        }

        // Write payload2
        if (payload2.Length > 0)
        {
            written = WriteToReservation(firstSpan, secondSpan, written, payload2);
        }

        // Commit the write
        ring.CommitWrite(reservation, written);
    }

    /// <summary>
    /// Writes data to a two-part reservation (First/Second spans) starting at the given offset.
    /// Returns the new offset after writing.
    /// </summary>
    private static int WriteToReservation(Span<byte> first, Span<byte> second, int offset, ReadOnlySpan<byte> data)
    {
        var remaining = data.Length;
        var dataOffset = 0;

        // Write to First span if we haven't passed it yet
        if (offset < first.Length && remaining > 0)
        {
            var available = first.Length - offset;
            var toCopy = Math.Min(remaining, available);
            data.Slice(dataOffset, toCopy).CopyTo(first.Slice(offset));
            offset += toCopy;
            dataOffset += toCopy;
            remaining -= toCopy;
        }

        // Write to Second span for anything remaining
        if (remaining > 0)
        {
            var secondOffset = offset - first.Length;
            data.Slice(dataOffset, remaining).CopyTo(second.Slice(secondOffset));
            offset += remaining;
        }

        return offset;
    }

    /// <summary>
    /// Reads a frame from the ring buffer, skipping PAD frames.
    /// Blocks until a frame is available.
    /// </summary>
    /// <param name="ring">The ring buffer to read from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The frame header and payload.</returns>
    public static (FrameHeader Header, byte[] Payload) ReadFrame(ShmRing ring, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            // Read frame header
            var headerReservation = ring.ReserveRead(ShmConstants.FrameHeaderSize, cancellationToken);

            Span<byte> headerBytes = stackalloc byte[ShmConstants.FrameHeaderSize];
            CopyFromReservation(headerReservation, headerBytes);
            ring.CommitRead(headerReservation, ShmConstants.FrameHeaderSize);

            var header = FrameHeader.DecodeFrom(headerBytes);

            // Guard against corrupted frame headers
            if (header.Length > MaxFramePayloadSize)
            {
                throw new InvalidDataException(
                    $"Frame payload length {header.Length} exceeds maximum {MaxFramePayloadSize}.");
            }

            if (!Enum.IsDefined(header.Type) && header.Type != FrameType.Pad)
            {
                throw new InvalidDataException(
                    $"Unknown frame type 0x{(byte)header.Type:X2} with length {header.Length}.");
            }

            // Skip PAD frames
            if (header.Type == FrameType.Pad)
            {
                if (header.Length > 0)
                {
                    // Skip the padding payload
                    var padReservation = ring.ReserveRead((int)header.Length, cancellationToken);
                    ring.CommitRead(padReservation, (int)header.Length);
                }
                continue;
            }

            // Read payload if present
            byte[] payload;
            if (header.Length > 0)
            {
                payload = new byte[header.Length];
                var payloadReservation = ring.ReserveRead((int)header.Length, cancellationToken);
                CopyFromReservation(payloadReservation, payload);
                ring.CommitRead(payloadReservation, (int)header.Length);
            }
            else
            {
                payload = Array.Empty<byte>();
            }

            return (header, payload);
        }
    }

    /// <summary>
    /// Reads a frame from the ring buffer using ArrayPool to avoid per-frame heap allocation.
    /// The caller is responsible for returning the payload array to <see cref="ArrayPool{T}.Shared"/>
    /// when done (unless PayloadLength is 0, in which case Payload is <see cref="Array.Empty{T}"/>).
    /// </summary>
    /// <param name="ring">The ring buffer to read from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The frame header, pooled payload buffer, and actual payload length.</returns>
    public static (FrameHeader Header, byte[] Payload, int PayloadLength) ReadFramePooled(ShmRing ring, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            // Read frame header
            var headerReservation = ring.ReserveRead(ShmConstants.FrameHeaderSize, cancellationToken);

            Span<byte> headerBytes = stackalloc byte[ShmConstants.FrameHeaderSize];
            CopyFromReservation(headerReservation, headerBytes);
            ring.CommitRead(headerReservation, ShmConstants.FrameHeaderSize);

            var header = FrameHeader.DecodeFrom(headerBytes);

            // Guard against corrupted frame headers
            if (header.Length > MaxFramePayloadSize)
            {
                throw new InvalidDataException(
                    $"Frame payload length {header.Length} exceeds maximum {MaxFramePayloadSize}.");
            }

            if (!Enum.IsDefined(header.Type) && header.Type != FrameType.Pad)
            {
                throw new InvalidDataException(
                    $"Unknown frame type 0x{(byte)header.Type:X2} with length {header.Length}.");
            }

            // Skip PAD frames
            if (header.Type == FrameType.Pad)
            {
                if (header.Length > 0)
                {
                    var padReservation = ring.ReserveRead((int)header.Length, cancellationToken);
                    ring.CommitRead(padReservation, (int)header.Length);
                }
                continue;
            }

            // Read payload into a pooled buffer if present
            if (header.Length > 0)
            {
                var payloadLength = (int)header.Length;
                var payload = ArrayPool<byte>.Shared.Rent(payloadLength);
                var payloadReservation = ring.ReserveRead(payloadLength, cancellationToken);
                CopyFromReservation(payloadReservation, payload.AsSpan(0, payloadLength));
                ring.CommitRead(payloadReservation, payloadLength);
                return (header, payload, payloadLength);
            }

            return (header, Array.Empty<byte>(), 0);
        }
    }

    /// <summary>
    /// Reads a frame without allocating a new payload array.
    /// The payload is written to the provided buffer.
    /// </summary>
    /// <param name="ring">The ring buffer to read from.</param>
    /// <param name="payloadBuffer">Buffer to receive the payload. Must be large enough.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The frame header and actual payload length.</returns>
    public static (FrameHeader Header, int PayloadLength) ReadFrameInto(
        ShmRing ring,
        Span<byte> payloadBuffer,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            // Read frame header
            var headerReservation = ring.ReserveRead(ShmConstants.FrameHeaderSize, cancellationToken);

            Span<byte> headerBytes = stackalloc byte[ShmConstants.FrameHeaderSize];
            CopyFromReservation(headerReservation, headerBytes);
            ring.CommitRead(headerReservation, ShmConstants.FrameHeaderSize);

            var header = FrameHeader.DecodeFrom(headerBytes);

            // Guard against corrupted frame headers
            if (header.Length > MaxFramePayloadSize)
            {
                throw new InvalidDataException(
                    $"Frame payload length {header.Length} exceeds maximum {MaxFramePayloadSize}.");
            }

            if (!Enum.IsDefined(header.Type) && header.Type != FrameType.Pad)
            {
                throw new InvalidDataException(
                    $"Unknown frame type 0x{(byte)header.Type:X2} with length {header.Length}.");
            }

            // Skip PAD frames
            if (header.Type == FrameType.Pad)
            {
                if (header.Length > 0)
                {
                    var padReservation = ring.ReserveRead((int)header.Length, cancellationToken);
                    ring.CommitRead(padReservation, (int)header.Length);
                }
                continue;
            }

            // Read payload if present
            if (header.Length > 0)
            {
                if (payloadBuffer.Length < header.Length)
                {
                    throw new ArgumentException($"Payload buffer too small: need {header.Length}, have {payloadBuffer.Length}");
                }

                var payloadReservation = ring.ReserveRead((int)header.Length, cancellationToken);
                CopyFromReservation(payloadReservation, payloadBuffer[..(int)header.Length]);
                ring.CommitRead(payloadReservation, (int)header.Length);
            }

            return (header, (int)header.Length);
        }
    }

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
    public static void WriteMessage(ShmRing ring, uint streamId, ReadOnlySpan<byte> data, bool isLast, CancellationToken cancellationToken = default)
    {
        var flags = isLast ? (byte)0 : MessageFlags.More;

        // Calculate max payload per frame: half of ring capacity minus header, min 1KB
        var cap = (int)ring.Capacity;
        var maxFramePayload = cap / 2 - ShmConstants.FrameHeaderSize;
        if (maxFramePayload < 1024)
        {
            maxFramePayload = 1024;
        }

        // Fast path: payload fits in a single frame
        if (data.Length <= maxFramePayload)
        {
            var header = new FrameHeader(FrameType.Message, streamId, (uint)data.Length, flags);
            WriteFrame(ring, header, data, cancellationToken);
            return;
        }

        // Slow path: chunk the payload into multiple frames with MORE flag
        var remaining = data;
        while (remaining.Length > 0)
        {
            var chunkSize = Math.Min(maxFramePayload, remaining.Length);
            var chunk = remaining[..chunkSize];
            remaining = remaining[chunkSize..];

            byte chunkFlags;
            if (remaining.Length > 0)
            {
                // More chunks follow
                chunkFlags = MessageFlags.More;
            }
            else
            {
                // Last chunk - use the original flags
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
