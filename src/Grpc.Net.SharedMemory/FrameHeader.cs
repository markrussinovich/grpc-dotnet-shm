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
using System.Runtime.InteropServices;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Represents the 16-byte on-wire frame header.
/// Layout (little-endian):
/// - Offset 0-3:   Length (uint32) - payload length in bytes, excludes 16-byte header
/// - Offset 4-7:   StreamID (uint32) - client uses odd IDs, server uses even
/// - Offset 8:     Type (byte) - FrameType enum
/// - Offset 9:     Flags (byte) - per-type flags
/// - Offset 10-11: Reserved (uint16) - set to zero
/// - Offset 12-15: Reserved2 (uint32) - set to zero
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FrameHeader
{
    /// <summary>Payload length in bytes (excludes 16-byte header).</summary>
    public uint Length;

    /// <summary>Stream identifier (client=odd, server=even).</summary>
    public uint StreamId;

    /// <summary>Frame type.</summary>
    public FrameType Type;

    /// <summary>Per-type flags.</summary>
    public byte Flags;

    /// <summary>Reserved field (set to zero).</summary>
    public ushort Reserved;

    /// <summary>Reserved field 2 (set to zero).</summary>
    public uint Reserved2;

    /// <summary>
    /// Creates a new FrameHeader with the specified values.
    /// </summary>
    public FrameHeader(FrameType type, uint streamId, uint length = 0, byte flags = 0)
    {
        Type = type;
        StreamId = streamId;
        Length = length;
        Flags = flags;
        Reserved = 0;
        Reserved2 = 0;
    }

    /// <summary>
    /// Encodes this frame header into a 16-byte buffer.
    /// </summary>
    /// <param name="destination">The destination buffer (must be at least 16 bytes).</param>
    public readonly void EncodeTo(Span<byte> destination)
    {
        if (destination.Length < ShmConstants.FrameHeaderSize)
        {
            throw new ArgumentException($"Destination must be at least {ShmConstants.FrameHeaderSize} bytes", nameof(destination));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination[0..4], Length);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..8], StreamId);
        destination[8] = (byte)Type;
        destination[9] = Flags;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[10..12], 0); // Reserved
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..16], 0); // Reserved2
    }

    /// <summary>
    /// Decodes a frame header from a 16-byte buffer.
    /// </summary>
    /// <param name="source">The source buffer (must be at least 16 bytes).</param>
    /// <returns>The decoded frame header.</returns>
    public static FrameHeader DecodeFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < ShmConstants.FrameHeaderSize)
        {
            throw new ArgumentException($"Source must be at least {ShmConstants.FrameHeaderSize} bytes", nameof(source));
        }

        return new FrameHeader
        {
            Length = BinaryPrimitives.ReadUInt32LittleEndian(source[0..4]),
            StreamId = BinaryPrimitives.ReadUInt32LittleEndian(source[4..8]),
            Type = (FrameType)source[8],
            Flags = source[9],
            Reserved = BinaryPrimitives.ReadUInt16LittleEndian(source[10..12]),
            Reserved2 = BinaryPrimitives.ReadUInt32LittleEndian(source[12..16])
        };
    }

    /// <summary>
    /// Returns a string representation of this frame header for debugging.
    /// </summary>
    public override readonly string ToString()
    {
        return $"FrameHeader(Type={Type}, StreamId={StreamId}, Length={Length}, Flags=0x{Flags:X2})";
    }
}
