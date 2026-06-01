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

using System.Buffers;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Frame payload wrapper that owns a pooled buffer.
/// Call <see cref="Release"/> to return the buffer to the pool.
/// </summary>
public readonly struct FramePayload
{
    public static readonly FramePayload Empty = new(ReadOnlyMemory<byte>.Empty, null);

    private readonly byte[]? _pooledBuffer;

    // Speculative: ring ref + reserved bytes for safety margin release.
    private readonly ShmRing? _speculativeRing;
    private readonly int _speculativeBytes;

    public ReadOnlyMemory<byte> Memory { get; }

    public int Length => Memory.Length;

    /// <summary>
    /// True if this payload is a speculative zero-copy view of ring memory
    /// (versus a pool-backed copy). Used by upper-layer multi-frame
    /// assemblers to decide whether to chain segments (preserving ZC) or
    /// copy into a contiguous buffer (compressed-message path needs that).
    /// </summary>
    public bool IsSpeculativeZeroCopy => _speculativeRing != null;

    /// <summary>
    /// Round-7 PR-B object passthrough: when this payload carries a HEADERS
    /// or TRAILERS frame whose HPACK block was decoded directly to a
    /// <see cref="HeadersV1"/> / <see cref="TrailersV1"/> by the H2 codec,
    /// the decoded object is stashed here so upper-layer consumers can
    /// skip the redundant <c>HeadersV1.Encode</c> &#x2192; bytes &#x2192;
    /// <c>HeadersV1.Decode</c> round-trip. <see langword="null"/> for all
    /// other frame types (DATA, control frames) and for HEADERS frames
    /// that took the byte-fallback path.
    /// </summary>
    public object? DecodedHeader { get; }

    private FramePayload(ReadOnlyMemory<byte> memory, byte[]? pooledBuffer,
        ShmRing? speculativeRing = null, int speculativeBytes = 0,
        object? decodedHeader = null)
    {
        Memory = memory;
        _pooledBuffer = pooledBuffer;
        _speculativeRing = speculativeRing;
        _speculativeBytes = speculativeBytes;
        DecodedHeader = decodedHeader;
    }

    public static FramePayload FromPooled(byte[] buffer, int length)
    {
        return new FramePayload(buffer.AsMemory(0, length), buffer);
    }

    /// <summary>
    /// Round-7 PR-B: creates a payload that carries an already-decoded
    /// <see cref="HeadersV1"/> or <see cref="TrailersV1"/> instead of
    /// serialized bytes. Used by the H2 codec read path to hand the
    /// decoded object directly to the upper layer, eliminating the
    /// <c>Encode</c>-then-<c>Decode</c> round-trip the byte path requires.
    /// The returned payload has empty <see cref="Memory"/>; consumers MUST
    /// check <see cref="DecodedHeader"/> first for HEADERS / TRAILERS
    /// frame types before reading bytes.
    /// </summary>
    internal static FramePayload FromDecodedHeader(object decodedHeader)
    {
        return new FramePayload(ReadOnlyMemory<byte>.Empty, null, decodedHeader: decodedHeader);
    }

    /// <summary>
    /// Creates a speculative zero-copy payload. CommitRead has already been
    /// called and SpeculativeReservedBytes incremented. Release decrements
    /// SpeculativeReservedBytes to restore writer capacity.
    /// </summary>
    internal static FramePayload FromRingMemorySpeculative(ReadOnlyMemory<byte> memory, ShmRing ring, int reservedBytes)
    {
        return new FramePayload(memory, null, speculativeRing: ring, speculativeBytes: reservedBytes);
    }

    public void Release()
    {
        if (_pooledBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(_pooledBuffer);
        }

        // Restore writer capacity by releasing the speculative reservation.
        if (_speculativeRing != null)
        {
            // Decrement the per-ring speculative byte count first. This is
            // an atomic operation; the value returned is the post-decrement
            // count.
            //
            // EndZcReservation must fire EXACTLY ONCE per chain anchor —
            // once <c>SpeculativeReservedBytes</c> hits 0 AND the codec
            // has already closed the chain (<c>!IsChainOpen</c>). Two
            // possibilities at the moment we fire:
            //
            //   - Single-frame ZC (no chain opened): <c>IsChainOpen</c> is
            //     false at all times; we always EndZc when the only frame's
            //     bytes are released.
            //   - Multi-frame chain ZC: codec opened the chain on the first
            //     frame (<c>IsChainOpen=true</c>), closes it on the final
            //     frame's emit. Consumer Releases fire in arbitrary order.
            //     Without the <c>!IsChainOpen</c> gate, the FIRST Release
            //     would tear down the anchor (clearing <c>_zcActive</c>),
            //     stranding the still-in-flight chain frames whose data
            //     would be overwritten by the cross-process writer.
            //
            // The <c>(remaining == 0 &amp;&amp; !IsChainOpen)</c> condition
            // is checked atomically per Release; only one Release will see
            // both true (the last one chronologically that is also after
            // codec's CloseZcChain).
            var remaining = Interlocked.Add(
                ref _speculativeRing.SpeculativeReservedBytes, -_speculativeBytes);
            if (remaining == 0 && !_speculativeRing.IsChainOpen)
            {
                _speculativeRing.EndZcReservation();
            }
        }
    }
}
