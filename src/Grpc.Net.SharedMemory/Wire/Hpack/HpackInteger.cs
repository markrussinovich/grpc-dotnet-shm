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

namespace Grpc.Net.SharedMemory.Wire.Hpack;

/// <summary>
/// HPACK integer encoding/decoding (RFC 7541 §5.1).
/// Used for table indices and string lengths.
/// </summary>
internal static class HpackInteger
{
    /// <summary>
    /// Encodes <paramref name="value"/> using an N-bit prefix.
    /// <paramref name="prefixByte"/> contains the high (8-N) bits already set;
    /// the low N bits will be overwritten.
    /// Returns the number of bytes written.
    /// </summary>
    public static int Encode(uint value, int prefixBits, byte prefixByte, Span<byte> destination)
    {
        var max = (uint)((1 << prefixBits) - 1);
        var prefixMask = (byte)max;
        var clearedPrefix = (byte)(prefixByte & ~prefixMask);

        if (value < max)
        {
            destination[0] = (byte)(clearedPrefix | (byte)value);
            return 1;
        }

        destination[0] = (byte)(clearedPrefix | prefixMask);
        var v = value - max;
        var i = 1;
        while (v >= 128)
        {
            destination[i++] = (byte)((v & 0x7F) | 0x80);
            v >>= 7;
        }
        destination[i++] = (byte)v;
        return i;
    }

    /// <summary>
    /// Decodes an N-bit prefix integer starting at <paramref name="firstByte"/>
    /// (which contains the prefix in its low N bits) followed by continuation
    /// bytes from <paramref name="source"/>.
    /// Returns the decoded value and writes the number of continuation bytes
    /// consumed (excluding <paramref name="firstByte"/>) to <paramref name="bytesRead"/>.
    /// </summary>
    public static uint Decode(byte firstByte, int prefixBits, ReadOnlySpan<byte> source, out int bytesRead)
    {
        var max = (uint)((1 << prefixBits) - 1);
        var prefixMask = (byte)max;
        var value = (uint)(firstByte & prefixMask);
        bytesRead = 0;

        if (value < max)
        {
            return value;
        }

        var shift = 0;
        for (var i = 0; i < source.Length; i++)
        {
            var b = source[i];
            bytesRead++;
            value += (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }
            shift += 7;
            if (shift >= 32)
            {
                throw new InvalidDataException("HPACK integer overflow");
            }
        }

        throw new InvalidDataException("HPACK integer truncated");
    }
}
