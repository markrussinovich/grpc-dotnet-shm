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
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// A <see cref="MemoryManager{T}"/> implementation that provides zero-copy access
/// to memory-mapped file regions. This enables direct operations on shared memory
/// without copying data to intermediate buffers.
///
/// This class uses unsafe pointer operations to acquire direct access to the
/// memory-mapped region, implementing true zero-copy shared memory access
/// as required by the grpc-go-shmem compatible implementation.
/// </summary>
/// <remarks>
/// Usage pattern:
/// 1. Create from a MemoryMappedViewAccessor
/// 2. Use GetMemory() or GetSpan() to access the mapped region
/// 3. Dispose when done to release the pointer
///
/// The memory is pinned for the lifetime of this object, which is required
/// for shared memory operations where the underlying address must remain stable.
/// </remarks>
public sealed unsafe class MappedMemoryManager : MemoryManager<byte>
{
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly long _offset;
    private readonly int _length;
    private byte* _pointer;
    private bool _disposed;

    /// <summary>
    /// Creates a new MappedMemoryManager over the entire accessor.
    /// </summary>
    /// <param name="accessor">The memory-mapped view accessor to wrap.</param>
    public MappedMemoryManager(MemoryMappedViewAccessor accessor)
        : this(accessor, 0, checked((int)accessor.Capacity))
    {
    }

    /// <summary>
    /// Creates a new MappedMemoryManager over a region of the accessor.
    /// </summary>
    /// <param name="accessor">The memory-mapped view accessor to wrap.</param>
    /// <param name="offset">The offset within the accessor.</param>
    /// <param name="length">The length of the region to expose.</param>
    public MappedMemoryManager(MemoryMappedViewAccessor accessor, long offset, int length)
    {
        ArgumentNullException.ThrowIfNull(accessor);

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative");
        }

        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Length cannot be negative");
        }

        if (offset + length > accessor.Capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(length),
                $"Offset + length ({offset + length}) exceeds accessor capacity ({accessor.Capacity})");
        }

        _accessor = accessor;
        _offset = offset;
        _length = length;

        // Acquire the pointer to the memory-mapped region
        // This pins the memory and returns a raw pointer
        _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);

        // Adjust pointer by the offset in the view (accounting for page alignment)
        // The accessor may have a PointerOffset due to page alignment
        _pointer += _accessor.PointerOffset + _offset;
    }

    /// <summary>
    /// Gets the raw pointer to the mapped memory region.
    /// </summary>
    /// <remarks>
    /// This pointer is valid for the lifetime of this object.
    /// Use with caution - direct pointer manipulation bypasses memory safety.
    /// </remarks>
    public byte* Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _pointer;
        }
    }

    /// <summary>
    /// Gets the length of the mapped region.
    /// </summary>
    public int Length => _length;

    /// <inheritdoc/>
    public override Span<byte> GetSpan()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new Span<byte>(_pointer, _length);
    }

    /// <inheritdoc/>
    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (elementIndex < 0 || elementIndex >= _length)
        {
            throw new ArgumentOutOfRangeException(nameof(elementIndex));
        }

        // The memory is already pinned via AcquirePointer, just return a handle
        return new MemoryHandle(_pointer + elementIndex, pinnable: this);
    }

    /// <inheritdoc/>
    public override void Unpin()
    {
        // The memory stays pinned until disposal - this is intentional for shared memory
        // where we need stable addresses across operations
    }

    /// <summary>
    /// Disposes the memory manager and releases the pointer.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;

            // Release the pointer we acquired
            if (_pointer != null)
            {
                // Adjust pointer back to original before releasing
                _pointer -= _accessor.PointerOffset + _offset;
                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
    }

    /// <summary>
    /// Creates a MappedMemoryManager for a sub-region of this manager.
    /// The parent manager must remain alive for the lifetime of the child.
    /// </summary>
    /// <param name="offset">Offset within this region.</param>
    /// <param name="length">Length of the sub-region.</param>
    /// <returns>A new MappedMemoryManager for the sub-region.</returns>
    public MappedMemoryManager Slice(int offset, int length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        if (length < 0 || offset + length > _length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        return new MappedMemoryManager(_accessor, _offset + offset, length);
    }

    /// <summary>
    /// Performs a volatile read of a uint value at the specified offset.
    /// </summary>
    public uint VolatileReadUInt32(int offset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (offset < 0 || offset + sizeof(uint) > _length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        return Volatile.Read(ref *(uint*)(_pointer + offset));
    }

    /// <summary>
    /// Performs a volatile read of a ulong value at the specified offset.
    /// </summary>
    public ulong VolatileReadUInt64(int offset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (offset < 0 || offset + sizeof(ulong) > _length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        return Volatile.Read(ref *(ulong*)(_pointer + offset));
    }

    /// <summary>
    /// Performs a volatile write of a uint value at the specified offset.
    /// </summary>
    public void VolatileWriteUInt32(int offset, uint value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (offset < 0 || offset + sizeof(uint) > _length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        Volatile.Write(ref *(uint*)(_pointer + offset), value);
    }

    /// <summary>
    /// Performs a volatile write of a ulong value at the specified offset.
    /// </summary>
    public void VolatileWriteUInt64(int offset, ulong value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (offset < 0 || offset + sizeof(ulong) > _length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        Volatile.Write(ref *(ulong*)(_pointer + offset), value);
    }

    /// <summary>
    /// Atomically compares and exchanges a value at the specified offset.
    /// </summary>
    /// <returns>The original value.</returns>
    public uint InterlockedCompareExchange(int offset, uint value, uint comparand)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (offset < 0 || offset + sizeof(uint) > _length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        return Interlocked.CompareExchange(ref *(uint*)(_pointer + offset), value, comparand);
    }

    /// <summary>
    /// Atomically increments a value at the specified offset.
    /// </summary>
    /// <returns>The incremented value.</returns>
    public uint InterlockedIncrement(int offset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (offset < 0 || offset + sizeof(uint) > _length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        return Interlocked.Increment(ref *(uint*)(_pointer + offset));
    }

    /// <summary>
    /// Gets a pointer to a specific offset for use with futex or other low-level operations.
    /// </summary>
    public uint* GetUInt32Pointer(int offset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (offset < 0 || offset + sizeof(uint) > _length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        return (uint*)(_pointer + offset);
    }
}

/// <summary>
/// Extension methods for creating MappedMemoryManager instances.
/// </summary>
public static class MappedMemoryManagerExtensions
{
    /// <summary>
    /// Creates a zero-copy Memory&lt;byte&gt; view over the memory-mapped accessor.
    /// </summary>
    /// <param name="accessor">The memory-mapped view accessor.</param>
    /// <returns>A MappedMemoryManager that provides zero-copy access.</returns>
    public static MappedMemoryManager CreateZeroCopyMemory(this MemoryMappedViewAccessor accessor)
    {
        return new MappedMemoryManager(accessor);
    }

    /// <summary>
    /// Creates a zero-copy Memory&lt;byte&gt; view over a region of the memory-mapped accessor.
    /// </summary>
    /// <param name="accessor">The memory-mapped view accessor.</param>
    /// <param name="offset">The offset within the accessor.</param>
    /// <param name="length">The length of the region.</param>
    /// <returns>A MappedMemoryManager that provides zero-copy access.</returns>
    public static MappedMemoryManager CreateZeroCopyMemory(
        this MemoryMappedViewAccessor accessor,
        long offset,
        int length)
    {
        return new MappedMemoryManager(accessor, offset, length);
    }
}
