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

    private FramePayload(ReadOnlyMemory<byte> memory, byte[]? pooledBuffer,
        ShmRing? speculativeRing = null, int speculativeBytes = 0)
    {
        Memory = memory;
        _pooledBuffer = pooledBuffer;
        _speculativeRing = speculativeRing;
        _speculativeBytes = speculativeBytes;
    }

    public static FramePayload FromPooled(byte[] buffer, int length)
    {
        return new FramePayload(buffer.AsMemory(0, length), buffer);
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
            Interlocked.Add(ref _speculativeRing.SpeculativeReservedBytes, -_speculativeBytes);
        }
    }
}
