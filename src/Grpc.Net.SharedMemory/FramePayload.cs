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
    public static readonly FramePayload Empty = new(ReadOnlyMemory<byte>.Empty, null, null, 0, 0);

    private readonly byte[]? _pooledBuffer;

    // Deferred CommitRead fields (FromRingMemory).
    private readonly ShmRing? _ring;
    private readonly ulong _commitReadIdx;
    private readonly int _commitReadBytes;

    // Speculative: ring ref for in-flight counter decrement (FromRingMemoryPreCommitted).
    // Distinct from _ring (which is for deferred CommitRead).
    private readonly ShmRing? _speculativeRing;

    public ReadOnlyMemory<byte> Memory { get; }

    public int Length => Memory.Length;

    private FramePayload(ReadOnlyMemory<byte> memory, byte[]? pooledBuffer,
        ShmRing? ring, ulong commitReadIdx, int commitReadBytes,
        ShmRing? speculativeRing = null)
    {
        Memory = memory;
        _pooledBuffer = pooledBuffer;
        _ring = ring;
        _commitReadIdx = commitReadIdx;
        _commitReadBytes = commitReadBytes;
        _speculativeRing = speculativeRing;
    }

    public static FramePayload FromPooled(byte[] buffer, int length)
    {
        return new FramePayload(buffer.AsMemory(0, length), buffer, null, 0, 0);
    }

    /// <summary>
    /// Creates a zero-copy payload backed by ring buffer memory.
    /// CommitRead is deferred until <see cref="Release"/>.
    /// </summary>
    internal static FramePayload FromRingMemory(
        ReadOnlyMemory<byte> memory, ShmRing ring, ulong commitReadIdx, int commitReadBytes)
    {
        return new FramePayload(memory, null, ring, commitReadIdx, commitReadBytes);
    }

    /// <summary>
    /// Creates a speculative zero-copy payload. CommitRead has already been
    /// called. Release decrements the in-flight counter so FrameReader can
    /// issue more speculative reads.
    /// </summary>
    internal static FramePayload FromRingMemoryPreCommitted(ReadOnlyMemory<byte> memory, ShmRing ring)
    {
        return new FramePayload(memory, null, null, 0, 0, speculativeRing: ring);
    }

    public void Release()
    {
        if (_pooledBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(_pooledBuffer);
        }
        else if (_ring != null)
        {
            _ring.CommitReadRaw(_commitReadIdx, _commitReadBytes);
        }

        // Decrement speculative in-flight counter.
        if (_speculativeRing != null)
        {
            Interlocked.Decrement(ref _speculativeRing.SpeculativeInFlight);
        }
    }
}
