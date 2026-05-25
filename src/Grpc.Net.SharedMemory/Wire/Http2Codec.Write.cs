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

using System.Buffers;
using System.Buffers.Binary;

namespace Grpc.Net.SharedMemory.Wire;

internal static partial class Http2Codec
{
    /// <summary>
    /// Translates an internal <see cref="FrameHeader"/> into HTTP/2 wire format
    /// and writes the resulting frame to <paramref name="ring"/>.
    /// </summary>
    private static void WriteFrameInternal(
        ShmRing ring,
        FrameHeader header,
        ReadOnlySpan<byte> payload1,
        ReadOnlySpan<byte> payload2,
        CancellationToken cancellationToken)
    {
        switch (header.Type)
        {
            case FrameType.Message:
                WriteH2Data(ring, header, payload1, payload2, cancellationToken);
                return;
            case FrameType.HalfClose:
                // Empty DATA with END_STREAM — equivalent to gRPC half-close.
                WriteH2DataRaw(ring, header.StreamId, Http2Flags.EndStream,
                    ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, cancellationToken);
                return;
            case FrameType.Headers:
                WriteH2Headers(ring, header, payload1, payload2, cancellationToken);
                return;
            case FrameType.Trailers:
                WriteH2Trailers(ring, header, payload1, payload2, cancellationToken);
                return;
            case FrameType.Cancel:
                WriteH2RstStream(ring, header.StreamId, Http2ErrorCode.Cancel, cancellationToken);
                return;
            case FrameType.GoAway:
                WriteH2GoAway(ring, header, payload1, payload2, cancellationToken);
                return;
            case FrameType.Ping:
                WriteH2Ping(ring, ack: false, payload1, payload2, cancellationToken);
                return;
            case FrameType.Pong:
                WriteH2Ping(ring, ack: true, payload1, payload2, cancellationToken);
                return;
            case FrameType.WindowUpdate:
                WriteH2WindowUpdate(ring, header.StreamId, payload1, payload2, cancellationToken);
                return;
            case FrameType.Pad:
                // No-op: H2 has PADDED flag but we never emit padding.
                return;
            default:
                throw new InvalidOperationException(
                    $"HTTP/2 codec: unsupported internal frame type {header.Type}");
        }
    }

    /// <summary>
    /// Writes an HTTP/2 SETTINGS frame containing the default settings
    /// (used at connection startup for spec compliance).
    /// </summary>
    public static void WriteSettings(ShmRing ring, bool ack, CancellationToken ct)
    {
        if (ack)
        {
            WriteH2FrameRaw(ring, Http2FrameType.Settings, Http2Flags.Ack, 0,
                ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, ct);
            return;
        }
        Span<byte> payload = stackalloc byte[Http2Settings.DefaultsLength];
        var written = Http2Settings.EncodeDefaults(payload);
        WriteH2FrameRaw(ring, Http2FrameType.Settings, 0, 0, payload[..written], ReadOnlySpan<byte>.Empty, ct);
    }

    private static void WriteH2Data(ShmRing ring, FrameHeader header,
        ReadOnlySpan<byte> p1, ReadOnlySpan<byte> p2, CancellationToken ct)
    {
        var isMore = (header.Flags & MessageFlags.More) != 0;
        var isEndStream = (header.Flags & MessageFlags.EndStream) != 0 && !isMore;
        byte flags = (byte)(isEndStream ? Http2Flags.EndStream : 0);
        WriteH2DataRaw(ring, header.StreamId, flags, p1, p2, ct);
    }

    private static void WriteH2DataRaw(ShmRing ring, uint streamId, byte flags,
        ReadOnlySpan<byte> p1, ReadOnlySpan<byte> p2, CancellationToken ct)
    {
        WriteH2FrameRaw(ring, Http2FrameType.Data, flags, streamId, p1, p2, ct);
    }

    private static void WriteH2Headers(ShmRing ring, FrameHeader header,
        ReadOnlySpan<byte> p1, ReadOnlySpan<byte> p2, CancellationToken ct)
    {
        var v1 = DecodeHeadersV1(p1, p2);
        var (hpackBuf, hpackLen) = HpackHeadersAdapter.EncodeHeaders(v1);
        try
        {
            WriteH2FrameRaw(ring, Http2FrameType.Headers, Http2Flags.EndHeaders,
                header.StreamId, hpackBuf.AsSpan(0, hpackLen), ReadOnlySpan<byte>.Empty, ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(hpackBuf);
        }
    }

    private static void WriteH2Trailers(ShmRing ring, FrameHeader header,
        ReadOnlySpan<byte> p1, ReadOnlySpan<byte> p2, CancellationToken ct)
    {
        var v1 = DecodeTrailersV1(p1, p2);
        var (hpackBuf, hpackLen) = HpackHeadersAdapter.EncodeTrailers(v1);
        try
        {
            byte flags = (byte)(Http2Flags.EndHeaders | Http2Flags.EndStream);
            WriteH2FrameRaw(ring, Http2FrameType.Headers, flags,
                header.StreamId, hpackBuf.AsSpan(0, hpackLen), ReadOnlySpan<byte>.Empty, ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(hpackBuf);
        }
    }

    private static void WriteH2RstStream(ShmRing ring, uint streamId, Http2ErrorCode error, CancellationToken ct)
    {
        Span<byte> payload = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)error);
        WriteH2FrameRaw(ring, Http2FrameType.RstStream, 0, streamId, payload, ReadOnlySpan<byte>.Empty, ct);
    }

    private static void WriteH2GoAway(ShmRing ring, FrameHeader header,
        ReadOnlySpan<byte> p1, ReadOnlySpan<byte> p2, CancellationToken ct)
    {
        var debugLen = p1.Length + p2.Length;
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(head[..4], 0);
        BinaryPrimitives.WriteUInt32BigEndian(head.Slice(4, 4), (uint)Http2ErrorCode.NoError);

        var totalLen = 8 + debugLen;
        if (totalLen <= 256)
        {
            Span<byte> combined = stackalloc byte[256];
            head.CopyTo(combined);
            p1.CopyTo(combined[8..]);
            p2.CopyTo(combined[(8 + p1.Length)..]);
            WriteH2FrameRaw(ring, Http2FrameType.GoAway, 0, 0, combined[..totalLen], ReadOnlySpan<byte>.Empty, ct);
            return;
        }

        var buf = ArrayPool<byte>.Shared.Rent(totalLen);
        try
        {
            head.CopyTo(buf);
            p1.CopyTo(buf.AsSpan(8));
            p2.CopyTo(buf.AsSpan(8 + p1.Length));
            WriteH2FrameRaw(ring, Http2FrameType.GoAway, 0, 0, buf.AsSpan(0, totalLen), ReadOnlySpan<byte>.Empty, ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static void WriteH2Ping(ShmRing ring, bool ack, ReadOnlySpan<byte> p1, ReadOnlySpan<byte> p2, CancellationToken ct)
    {
        Span<byte> payload = stackalloc byte[8];
        var copy = Math.Min(p1.Length, 8);
        p1[..copy].CopyTo(payload);
        var rem = 8 - copy;
        if (rem > 0)
        {
            var copy2 = Math.Min(p2.Length, rem);
            if (copy2 > 0)
            {
                p2[..copy2].CopyTo(payload[copy..]);
            }
            if (copy + copy2 < 8)
            {
                payload[(copy + copy2)..].Clear();
            }
        }
        WriteH2FrameRaw(ring, Http2FrameType.Ping, ack ? Http2Flags.Ack : (byte)0, 0,
            payload, ReadOnlySpan<byte>.Empty, ct);
    }

    private static void WriteH2WindowUpdate(ShmRing ring, uint streamId, ReadOnlySpan<byte> p1, ReadOnlySpan<byte> p2, CancellationToken ct)
    {
        // Structural regression guard for the SHM no-WU baseline (gRFC SHM
        // alignment with grpc-go-shmem v3.4+ shmNoWU). This is the single
        // wire-level emission point for WINDOW_UPDATE; any code path that
        // reaches here must show up in the test counter so we never
        // silently re-introduce WU traffic.
        ShmConnection.RecordWindowUpdateEmission();
        // Internal payload from FrameProtocol.WriteWindowUpdate is 4 bytes
        // little-endian. Convert to H2 big-endian wire format.
        Span<byte> combined = stackalloc byte[4];
        if (p1.Length >= 4)
        {
            p1[..4].CopyTo(combined);
        }
        else if (p1.Length + p2.Length >= 4)
        {
            p1.CopyTo(combined);
            p2[..(4 - p1.Length)].CopyTo(combined[p1.Length..]);
        }
        else
        {
            combined.Clear();
        }
        // LE → BE: byte-reverse the 4-byte value.
        var increment = BinaryPrimitives.ReadUInt32LittleEndian(combined);
        BinaryPrimitives.WriteUInt32BigEndian(combined, increment);
        WriteH2FrameRaw(ring, Http2FrameType.WindowUpdate, 0, streamId,
            combined, ReadOnlySpan<byte>.Empty, ct);
    }

    /// <summary>
    /// Atomically writes one HTTP/2 frame to the ring (header + payload).
    /// </summary>
    private static void WriteH2FrameRaw(
        ShmRing ring, Http2FrameType type, byte flags, uint streamId,
        ReadOnlySpan<byte> p1, ReadOnlySpan<byte> p2, CancellationToken ct)
    {
        var payloadLen = p1.Length + p2.Length;
        if (payloadLen > Http2FrameHeader.MaxAllowedPayloadLength)
        {
            throw new ArgumentOutOfRangeException(nameof(p1),
                $"H2 frame payload {payloadLen} exceeds 24-bit max");
        }

        var totalSize = Http2FrameHeader.Size + payloadLen;
        var reservation = ring.ReserveWrite(totalSize, ct);

        Span<byte> hdr = stackalloc byte[Http2FrameHeader.Size];
        Http2FrameHeader.Encode(hdr, type, flags, streamId, payloadLen);

        var firstSpan = reservation.First.Span;
        var secondSpan = reservation.Second.Span;
        var written = 0;
        written = WriteIntoReservation(firstSpan, secondSpan, written, hdr);
        if (p1.Length > 0)
        {
            written = WriteIntoReservation(firstSpan, secondSpan, written, p1);
        }
        if (p2.Length > 0)
        {
            written = WriteIntoReservation(firstSpan, secondSpan, written, p2);
        }

        ring.CommitWrite(reservation, written);
    }

    private static int WriteIntoReservation(Span<byte> first, Span<byte> second, int offset, ReadOnlySpan<byte> data)
    {
        var remaining = data.Length;
        var dataOffset = 0;
        if (offset < first.Length && remaining > 0)
        {
            var available = first.Length - offset;
            var toCopy = Math.Min(remaining, available);
            data.Slice(dataOffset, toCopy).CopyTo(first.Slice(offset));
            offset += toCopy;
            dataOffset += toCopy;
            remaining -= toCopy;
        }
        if (remaining > 0)
        {
            var secondOffset = offset - first.Length;
            data.Slice(dataOffset, remaining).CopyTo(second.Slice(secondOffset));
            offset += remaining;
        }
        return offset;
    }

    private static HeadersV1 DecodeHeadersV1(ReadOnlySpan<byte> p1, ReadOnlySpan<byte> p2)
    {
        if (p2.IsEmpty)
        {
            return HeadersV1.Decode(p1);
        }
        var len = p1.Length + p2.Length;
        Span<byte> combined = len <= 1024 ? stackalloc byte[1024] : new byte[len];
        combined = combined[..len];
        p1.CopyTo(combined);
        p2.CopyTo(combined[p1.Length..]);
        return HeadersV1.Decode(combined);
    }

    private static TrailersV1 DecodeTrailersV1(ReadOnlySpan<byte> p1, ReadOnlySpan<byte> p2)
    {
        if (p2.IsEmpty)
        {
            return TrailersV1.Decode(p1);
        }
        var len = p1.Length + p2.Length;
        Span<byte> combined = len <= 1024 ? stackalloc byte[1024] : new byte[len];
        combined = combined[..len];
        p1.CopyTo(combined);
        p2.CopyTo(combined[p1.Length..]);
        return TrailersV1.Decode(combined);
    }
}
