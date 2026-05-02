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

using System.Buffers.Binary;

namespace Grpc.Net.SharedMemory.Wire;

/// <summary>
/// Encode/decode for the 9-byte HTTP/2 frame header (RFC 7540 §4.1).
/// Layout (network order):
/// <code>
/// +-----------------------------------------------+
/// |                 Length (24)                   |
/// +---------------+---------------+---------------+
/// |   Type (8)    |   Flags (8)   |
/// +-+-------------+---------------+-------------------------------+
/// |R|                 Stream Identifier (31)                      |
/// +=+=============================================================+
/// </code>
/// </summary>
internal static class Http2FrameHeader
{
    /// <summary>Size of the HTTP/2 frame header in bytes.</summary>
    public const int Size = 9;

    /// <summary>Maximum allowed payload length in a single frame (RFC 7540 §6.5.2: 2^24 - 1).</summary>
    public const int MaxAllowedPayloadLength = (1 << 24) - 1;

    /// <summary>Encodes a frame header to a 9-byte buffer.</summary>
    public static void Encode(Span<byte> destination, Http2FrameType type, byte flags, uint streamId, int payloadLength)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException($"Destination must be at least {Size} bytes", nameof(destination));
        }
        if ((uint)payloadLength > MaxAllowedPayloadLength)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadLength),
                $"H2 frame payload length {payloadLength} exceeds 24-bit max {MaxAllowedPayloadLength}");
        }
        if ((streamId & 0x80000000u) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(streamId), "H2 stream id must fit in 31 bits");
        }

        // 24-bit length, big-endian
        destination[0] = (byte)((payloadLength >> 16) & 0xFF);
        destination[1] = (byte)((payloadLength >> 8) & 0xFF);
        destination[2] = (byte)(payloadLength & 0xFF);
        destination[3] = (byte)type;
        destination[4] = flags;
        // 32-bit field with reserved high bit (always 0 on send)
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(5, 4), streamId & 0x7FFFFFFFu);
    }

    /// <summary>Decodes a frame header from a 9-byte buffer.</summary>
    public static (Http2FrameType Type, byte Flags, int PayloadLength, uint StreamId) Decode(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
        {
            throw new ArgumentException($"Source must be at least {Size} bytes", nameof(source));
        }

        var payloadLength = (source[0] << 16) | (source[1] << 8) | source[2];
        var type = (Http2FrameType)source[3];
        var flags = source[4];
        var streamId = BinaryPrimitives.ReadUInt32BigEndian(source.Slice(5, 4)) & 0x7FFFFFFFu;
        return (type, flags, payloadLength, streamId);
    }
}
