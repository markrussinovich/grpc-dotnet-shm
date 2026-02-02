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

/// <summary>
/// High-level frame protocol operations for reading and writing gRPC frames
/// to the shared memory ring buffer.
/// </summary>
public static class FrameProtocol
{
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
        // Ensure header length matches payload
        header.Length = (uint)payload.Length;
        header.Reserved = 0;
        header.Reserved2 = 0;

        var totalSize = ShmConstants.FrameHeaderSize + payload.Length;

        // Reserve space for the entire frame atomically
        var reservation = ring.ReserveWrite(totalSize, cancellationToken);

        // Encode header
        Span<byte> headerBytes = stackalloc byte[ShmConstants.FrameHeaderSize];
        header.EncodeTo(headerBytes);

        // Write header + payload to reservation
        var written = 0;

        // Write to First slice
        var firstSpan = reservation.First.Span;
        var headerToFirst = Math.Min(headerBytes.Length, firstSpan.Length);
        headerBytes[..headerToFirst].CopyTo(firstSpan);
        written += headerToFirst;

        var payloadToFirst = Math.Min(payload.Length, firstSpan.Length - headerToFirst);
        if (payloadToFirst > 0)
        {
            payload[..payloadToFirst].CopyTo(firstSpan[headerToFirst..]);
            written += payloadToFirst;
        }

        // Write remaining to Second slice if needed
        if (reservation.Second.Length > 0)
        {
            var secondSpan = reservation.Second.Span;
            var secondOffset = 0;

            // Remaining header bytes
            var headerRemaining = headerBytes.Length - headerToFirst;
            if (headerRemaining > 0)
            {
                headerBytes[headerToFirst..].CopyTo(secondSpan);
                secondOffset += headerRemaining;
                written += headerRemaining;
            }

            // Remaining payload bytes
            var payloadRemaining = payload.Length - payloadToFirst;
            if (payloadRemaining > 0)
            {
                payload[payloadToFirst..].CopyTo(secondSpan[secondOffset..]);
                written += payloadRemaining;
            }
        }

        // Commit the write
        ring.CommitWrite(reservation, written);
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
    /// Writes a MESSAGE frame.
    /// </summary>
    public static void WriteMessage(ShmRing ring, uint streamId, ReadOnlySpan<byte> data, bool isLast, CancellationToken cancellationToken = default)
    {
        var flags = isLast ? (byte)0 : MessageFlags.More;
        var header = new FrameHeader(FrameType.Message, streamId, (uint)data.Length, flags);
        WriteFrame(ring, header, data, cancellationToken);
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
