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
/// Minimal HPACK decoder (RFC 7541) for the SHM transport.
/// </summary>
/// <remarks>
/// <para>
/// Supports all four representations defined by RFC 7541 §6:
/// indexed, literal with incremental indexing, literal without indexing,
/// and literal never indexed. Incremental-indexing semantics are ignored
/// (we do not maintain a dynamic table; index references beyond the static
/// table are treated as protocol errors).
/// </para>
/// <para>
/// Both plain and Huffman-coded string literals are supported; Huffman
/// decoding delegates to <see cref="HpackHuffmanDecoder"/>.
/// </para>
/// </remarks>
internal static class HpackDecoder
{
    /// <summary>
    /// Decodes an entire HPACK header block into a list of name/value pairs.
    /// </summary>
    public static List<(string Name, byte[] Value)> Decode(ReadOnlySpan<byte> source)
    {
        var headers = new List<(string, byte[])>();
        var pos = 0;

        while (pos < source.Length)
        {
            var b = source[pos];

            if ((b & 0x80) != 0)
            {
                // 1xxxxxxx Indexed Header Field (§6.1)
                var idx = HpackInteger.Decode(b, 7, source[(pos + 1)..], out var read);
                pos += 1 + read;
                if (idx == 0 || idx > HpackStaticTable.Count)
                {
                    throw new InvalidDataException(
                        $"HPACK indexed header references index {idx} outside static table (no dynamic table is maintained)");
                }
                var entry = HpackStaticTable.Entries[idx];
                headers.Add((entry.Name, Encoding.ASCII.GetBytes(entry.Value)));
                continue;
            }

            if ((b & 0xC0) == 0x40)
            {
                // 01xxxxxx Literal w/ Incremental Indexing (§6.2.1) — 6-bit prefix
                pos = DecodeLiteral(source, pos, 6, headers);
                continue;
            }

            if ((b & 0xE0) == 0x20)
            {
                // 001xxxxx Dynamic Table Size Update (§6.3) — 5-bit prefix
                var newSize = HpackInteger.Decode(b, 5, source[(pos + 1)..], out var read);
                pos += 1 + read;
                if (newSize > 0)
                {
                    throw new InvalidDataException(
                        $"HPACK dynamic table size update to {newSize} rejected (we advertise SETTINGS_HEADER_TABLE_SIZE = 0)");
                }
                continue;
            }

            // 0000xxxx Literal w/o Indexing or 0001xxxx Literal Never Indexed — both 4-bit prefix
            pos = DecodeLiteral(source, pos, 4, headers);
        }

        return headers;
    }

    private static int DecodeLiteral(ReadOnlySpan<byte> source, int pos, int prefixBits,
        List<(string, byte[])> headers)
    {
        var b = source[pos];
        var nameIdx = HpackInteger.Decode(b, prefixBits, source[(pos + 1)..], out var read);
        pos += 1 + read;

        string name;
        if (nameIdx == 0)
        {
            name = DecodeString(source, ref pos);
        }
        else if (nameIdx <= HpackStaticTable.Count)
        {
            name = HpackStaticTable.Entries[(int)nameIdx].Name;
        }
        else
        {
            throw new InvalidDataException(
                $"HPACK literal header references name index {nameIdx} outside static table");
        }

        var value = DecodeStringBytes(source, ref pos);
        headers.Add((name, value));
        return pos;
    }

    private static string DecodeString(ReadOnlySpan<byte> source, ref int pos)
    {
        var bytes = DecodeStringBytes(source, ref pos);
        return Encoding.UTF8.GetString(bytes);
    }

    private static byte[] DecodeStringBytes(ReadOnlySpan<byte> source, ref int pos)
    {
        var first = source[pos];
        var huffman = (first & 0x80) != 0;
        var length = (int)HpackInteger.Decode(first, 7, source[(pos + 1)..], out var read);
        pos += 1 + read;
        if (length < 0 || pos + length > source.Length)
        {
            throw new InvalidDataException("HPACK string literal truncated");
        }

        var raw = source.Slice(pos, length);
        pos += length;

        if (!huffman)
        {
            return raw.ToArray();
        }

        // Decode Huffman. Worst case expansion: ~2x is more than enough since
        // the shortest code is 5 bits (output ≤ 8/5 ≈ 1.6× of input).
        var dst = new byte[length * 2 + 8];
        var written = HpackHuffmanDecoder.Decode(raw, dst);
        var result = new byte[written];
        Array.Copy(dst, result, written);
        return result;
    }
}
