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
using System.Text;

namespace Grpc.Net.SharedMemory.Wire.Hpack;

/// <summary>
/// Minimal HPACK encoder (RFC 7541) for the SHM transport.
/// </summary>
/// <remarks>
/// Emits one of two representations per header:
/// <list type="bullet">
///   <item><description><b>Indexed Header Field</b> (1xxxxxxx) — when name+value matches the static table.</description></item>
///   <item><description><b>Literal Header Field without Indexing</b> (0000xxxx) — otherwise. Name may be referenced by static-table index, or sent as literal. Strings are sent as plain (non-Huffman) bytes for simplicity.</description></item>
/// </list>
/// The dynamic table is never used (peer must advertise <c>SETTINGS_HEADER_TABLE_SIZE = 0</c>).
/// </remarks>
internal static class HpackEncoder
{
    /// <summary>
    /// Encodes a list of header name/value pairs into a pooled buffer.
    /// Returns the buffer (caller must return to <see cref="ArrayPool{T}.Shared"/>) and the encoded length.
    /// </summary>
    public static (byte[] Buffer, int Length) Encode(IReadOnlyList<(string Name, byte[] Value)> headers)
    {
        // Conservative size estimate: 4 bytes overhead per entry + name + value
        var sizeEstimate = 0;
        for (var i = 0; i < headers.Count; i++)
        {
            sizeEstimate += 8 + Encoding.UTF8.GetByteCount(headers[i].Name) + headers[i].Value.Length;
        }
        if (sizeEstimate < 64) sizeEstimate = 64;

        var buffer = ArrayPool<byte>.Shared.Rent(sizeEstimate);
        var offset = 0;

        try
        {
            for (var i = 0; i < headers.Count; i++)
            {
                var (name, value) = headers[i];
                offset = EncodeHeader(buffer, offset, name, value);
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }

        return (buffer, offset);
    }

    /// <summary>
    /// Encodes a single header into <paramref name="buffer"/> starting at <paramref name="offset"/>.
    /// Grows the buffer if necessary (replacing the buffer reference is not supported here;
    /// caller must ensure buffer is sized via <see cref="EnsureCapacity"/>).
    /// Returns new offset.
    /// </summary>
    private static int EncodeHeader(byte[] buffer, int offset, string name, byte[] value)
    {
        // Try exact static-table match for ASCII-clean values.
        // Only safe for short ASCII values that match table entries verbatim.
        var valueAscii = TryAsAscii(value);
        if (valueAscii != null)
        {
            var exactIdx = HpackStaticTable.FindExact(name, valueAscii);
            if (exactIdx > 0)
            {
                // Indexed Header Field (1xxxxxxx). Prefix bits = 7, prefix byte high bit = 1.
                EnsureCapacity(buffer, offset, 5);
                offset += HpackInteger.Encode((uint)exactIdx, 7, 0x80, buffer.AsSpan(offset));
                return offset;
            }
        }

        // Literal Header Field without Indexing (0000xxxx). Prefix = 4, prefix byte = 0x00.
        var nameIdx = HpackStaticTable.FindName(name);
        if (nameIdx > 0)
        {
            EnsureCapacity(buffer, offset, 5);
            offset += HpackInteger.Encode((uint)nameIdx, 4, 0x00, buffer.AsSpan(offset));
        }
        else
        {
            // Index 0 means literal name follows.
            EnsureCapacity(buffer, offset, 1);
            buffer[offset++] = 0x00;
            offset = EncodeStringLiteral(buffer, offset, Encoding.UTF8.GetBytes(name));
        }

        // Value as string literal (plain, not Huffman-coded).
        offset = EncodeStringLiteral(buffer, offset, value);
        return offset;
    }

    /// <summary>
    /// Encodes a length-prefixed string. Plain (non-Huffman) form.
    /// </summary>
    private static int EncodeStringLiteral(byte[] buffer, int offset, ReadOnlySpan<byte> data)
    {
        EnsureCapacity(buffer, offset, 5 + data.Length);
        // Length prefix: 1 byte with H=0 (no Huffman) and 7-bit integer
        offset += HpackInteger.Encode((uint)data.Length, 7, 0x00, buffer.AsSpan(offset));
        data.CopyTo(buffer.AsSpan(offset));
        offset += data.Length;
        return offset;
    }

    private static string? TryAsAscii(byte[] value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] >= 0x80) return null;
        }
        return Encoding.ASCII.GetString(value);
    }

    private static void EnsureCapacity(byte[] buffer, int offset, int additional)
    {
        if (offset + additional > buffer.Length)
        {
            throw new InvalidOperationException(
                $"HPACK encode buffer too small: have {buffer.Length}, need {offset + additional}. " +
                "Increase initial estimate in Encode().");
        }
    }
}
