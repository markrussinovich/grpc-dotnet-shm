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

using System.Runtime.InteropServices;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Ring buffer header structure (64 bytes) that resides in shared memory.
/// This layout matches grpc-go-shmem for interoperability.
/// 
/// Layout (grpc-go-shmem compatible):
/// - Offset 0x00:  capacity (ulong) - ring data area size (power of 2)
/// - Offset 0x08:  widx (ulong) - monotonic write index (producer)
/// - Offset 0x10:  ridx (ulong) - monotonic read index (consumer)
/// - Offset 0x18:  dataSeq (uint) - data sequence for futex (producer increments)
/// - Offset 0x1C:  spaceSeq (uint) - space sequence for futex (consumer increments)
/// - Offset 0x20:  closed (uint) - closed flag (producer sets to 1)
/// - Offset 0x24:  pad (uint) - padding
/// - Offset 0x28:  contigSeq (uint) - contiguity sequence (consumer increments on every read commit)
/// - Offset 0x2C:  spaceWaiters (uint) - number of writers waiting on space
/// - Offset 0x30:  contigWaiters (uint) - number of writers waiting on contiguity
/// - Offset 0x34:  dataWaiters (uint) - number of readers waiting for data
/// - Offset 0x38-0x3F: reserved (8 bytes) - padding to 64B
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct RingHeader
{
    /// <summary>Ring data area capacity in bytes (must be power of 2).</summary>
    [FieldOffset(0x00)]
    public ulong Capacity;

    /// <summary>Monotonic write index (producer advances this).</summary>
    [FieldOffset(0x08)]
    public ulong WriteIdx;

    /// <summary>Monotonic read index (consumer advances this).</summary>
    [FieldOffset(0x10)]
    public ulong ReadIdx;

    /// <summary>Data availability sequence number for futex/event signaling.</summary>
    [FieldOffset(0x18)]
    public uint DataSeq;

    /// <summary>Space availability sequence number for futex/event signaling.</summary>
    [FieldOffset(0x1C)]
    public uint SpaceSeq;

    /// <summary>Ring closed flag (0 = open, 1 = closed).</summary>
    [FieldOffset(0x20)]
    public uint Closed;

    /// <summary>Padding.</summary>
    [FieldOffset(0x24)]
    public uint Pad;

    /// <summary>Contiguity sequence number for signaling.</summary>
    [FieldOffset(0x28)]
    public uint ContigSeq;

    /// <summary>Number of writers waiting for space.</summary>
    [FieldOffset(0x2C)]
    public uint SpaceWaiters;

    /// <summary>Number of writers waiting for contiguity.</summary>
    [FieldOffset(0x30)]
    public uint ContigWaiters;

    /// <summary>Number of readers waiting for data.</summary>
    [FieldOffset(0x34)]
    public uint DataWaiters;

    // Offset 0x38-0x3F: Reserved (8 bytes) - implicitly zeroed
}

/// <summary>
/// Snapshot of ring buffer state for debugging and diagnostics.
/// </summary>
public readonly struct RingState
{
    /// <summary>Total ring capacity.</summary>
    public ulong Capacity { get; init; }

    /// <summary>Current write index (monotonic).</summary>
    public ulong WriteIdx { get; init; }

    /// <summary>Current read index (monotonic).</summary>
    public ulong ReadIdx { get; init; }

    /// <summary>Bytes currently in ring (WriteIdx - ReadIdx).</summary>
    public ulong Used => WriteIdx - ReadIdx;

    /// <summary>Bytes available for writing.</summary>
    public ulong Available => Capacity - Used;

    /// <summary>Data availability sequence number.</summary>
    public uint DataSeq { get; init; }

    /// <summary>Space availability sequence number.</summary>
    public uint SpaceSeq { get; init; }

    /// <summary>Contiguity sequence number.</summary>
    public uint ContigSeq { get; init; }

    /// <summary>Ring closed flag.</summary>
    public bool Closed { get; init; }

    /// <summary>Number of readers waiting for data.</summary>
    public uint DataWaiters { get; init; }

    /// <summary>Number of writers waiting for space.</summary>
    public uint SpaceWaiters { get; init; }

    /// <summary>Number of writers waiting for contiguity.</summary>
    public uint ContigWaiters { get; init; }

    /// <summary>
    /// Returns a string representation of this ring state for debugging.
    /// </summary>
    public override string ToString()
    {
        return $"RingState(Used={Used}/{Capacity}, WIdx={WriteIdx}, RIdx={ReadIdx}, Closed={Closed}, DataWaiters={DataWaiters}, SpaceWaiters={SpaceWaiters}, ContigWaiters={ContigWaiters})";
    }
}
