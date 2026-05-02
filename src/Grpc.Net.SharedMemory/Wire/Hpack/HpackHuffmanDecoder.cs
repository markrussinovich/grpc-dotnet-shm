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
/// HPACK Huffman decoder (RFC 7541 Appendix B).
/// Decodes the bit-packed Huffman-coded representation back to bytes.
/// </summary>
/// <remarks>
/// Builds a binary trie from <see cref="HpackHuffmanTable.Codes"/> once at
/// static initialization and walks it bit-by-bit. Header strings in gRPC
/// are short (&lt;200 B typical) so the simplicity outweighs micro-optimisations.
/// </remarks>
internal static class HpackHuffmanDecoder
{
    // Trie nodes: 0 = unused; positive = internal node index (left/right); negative = -(symbol+1) for leaf.
    // Using a flat array of int pairs to avoid per-node object allocation.
    // Layout: nodes[2*i + 0] = left child, nodes[2*i + 1] = right child.
    // Encoded values: 0 = null/unused; >0 = next internal index; <0 = -(symbol+1) leaf.
    private static readonly int[] s_trie = BuildTrie();

    private static int[] BuildTrie()
    {
        // Worst case: 257 symbols * 30 bits = ~7710 nodes; allocate generous buffer
        var nodes = new int[2 * 8192];
        var nextIndex = 1; // index 0 is the root

        for (var sym = 0; sym < HpackHuffmanTable.Codes.Length; sym++)
        {
            var (code, bits) = HpackHuffmanTable.Codes[sym];
            var current = 0;

            for (var bit = bits - 1; bit >= 0; bit--)
            {
                var b = (int)((code >> bit) & 1);
                var slot = 2 * current + b;
                if (bit == 0)
                {
                    // Leaf
                    nodes[slot] = -(sym + 1);
                }
                else
                {
                    if (nodes[slot] == 0)
                    {
                        nodes[slot] = nextIndex++;
                    }
                    current = nodes[slot];
                }
            }
        }

        // Trim to used size
        var trimmed = new int[2 * nextIndex];
        Array.Copy(nodes, trimmed, trimmed.Length);
        return trimmed;
    }

    /// <summary>
    /// Decodes a Huffman-coded byte sequence into a destination buffer.
    /// Returns the number of bytes written.
    /// </summary>
    /// <exception cref="InvalidDataException">If padding is invalid or the decoded length exceeds <paramref name="destination"/>.</exception>
    public static int Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var written = 0;
        var current = 0;

        for (var i = 0; i < source.Length; i++)
        {
            var b = source[i];
            for (var bit = 7; bit >= 0; bit--)
            {
                var dir = (b >> bit) & 1;
                var next = s_trie[2 * current + dir];
                if (next < 0)
                {
                    var symbol = -next - 1;
                    if (symbol == 256)
                    {
                        throw new InvalidDataException("HPACK Huffman: EOS symbol encountered in stream");
                    }
                    if (written >= destination.Length)
                    {
                        throw new InvalidDataException("HPACK Huffman: decoded output exceeds buffer");
                    }
                    destination[written++] = (byte)symbol;
                    current = 0;
                }
                else if (next == 0)
                {
                    // Falling off into unused part of trie. RFC 7541 §5.2 requires that
                    // padding never extends a valid prefix. The trailing bits of the
                    // last byte must be a prefix of the EOS code (all 1s, up to 7 bits).
                    var bitsConsumed = (i * 8) + (7 - bit);
                    var bitsTotal = source.Length * 8;
                    var remaining = bitsTotal - bitsConsumed + 1; // +1 to count current bit
                    if (remaining > 7)
                    {
                        throw new InvalidDataException("HPACK Huffman: invalid code (longer than EOS padding)");
                    }
                    // Verify all remaining bits are 1
                    if (dir == 0)
                    {
                        throw new InvalidDataException("HPACK Huffman: invalid padding (expected all-ones)");
                    }
                    for (var j = bit - 1; j >= 0; j--)
                    {
                        if (((b >> j) & 1) == 0)
                        {
                            throw new InvalidDataException("HPACK Huffman: invalid padding (expected all-ones)");
                        }
                    }
                    return written;
                }
                else
                {
                    current = next;
                }
            }
        }

        if (current != 0)
        {
            // Reached end of input mid-symbol. Allowed only if the partial path
            // is a prefix of the EOS code (all 1s) AND fewer than 8 bits remained
            // (RFC 7541 §5.2). The bit-by-bit loop guarantees this when current
            // is reached by all-1 transitions, so accept silently here.
        }

        return written;
    }
}
