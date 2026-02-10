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

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Grpc.Net.SharedMemory.Synchronization;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Exception thrown when the ring buffer is closed.
/// </summary>
public class RingClosedException : Exception
{
    public RingClosedException() : base("Ring buffer is closed") { }
    public RingClosedException(string message) : base(message) { }
}

/// <summary>
/// Represents a write reservation for zero-copy writes to the ring buffer.
/// </summary>
public readonly struct WriteReservation
{
    /// <summary>First contiguous slice (from write position to end of buffer or requested size).</summary>
    public Memory<byte> First { get; init; }

    /// <summary>Second contiguous slice (from start of buffer) - may be empty if First has enough space.</summary>
    public Memory<byte> Second { get; init; }

    /// <summary>Total reserved bytes.</summary>
    public int Length => First.Length + Second.Length;

    internal ShmRing? Ring { get; init; }
    internal ulong WriteIdx { get; init; }
    internal int MaxBytes { get; init; }
}

/// <summary>
/// Represents a read reservation for zero-copy reads from the ring buffer.
/// </summary>
public readonly struct ReadReservation
{
    /// <summary>First contiguous slice.</summary>
    public ReadOnlyMemory<byte> First { get; init; }

    /// <summary>Second contiguous slice (handles wrap-around).</summary>
    public ReadOnlyMemory<byte> Second { get; init; }

    /// <summary>Total bytes available to read.</summary>
    public int Length => First.Length + Second.Length;

    internal ShmRing? Ring { get; init; }
    internal ulong CommitReadIdx { get; init; }
    internal int MaxBytes { get; init; }
}

/// <summary>
/// Single-Producer Single-Consumer (SPSC) ring buffer operating over shared memory
/// with event-driven blocking. This implementation provides high-performance
/// cross-process communication with zero-copy operations and minimal kernel calls
/// through futex-based (Linux) or named event (Windows) synchronization.
///
/// This implementation matches the grpc-go-shmem ring buffer for interoperability.
/// </summary>
public sealed class ShmRing : IDisposable
{
    private readonly Memory<byte> _memory;
    private readonly ulong _capacity;
    private readonly ulong _capMask;
    private readonly int _headerOffset;
    private readonly int _dataOffset;
    private readonly IRingSync? _sync;
    private readonly bool _isOwner;

    private volatile bool _localClosed;
    private ulong _pendingReadIdx;

    // Adaptive spin state
    private int _dataSpinCutoff = ShmConstants.SpinIterationsDefault;
    private int _spaceSpinCutoff = ShmConstants.SpinIterationsDefault;

    /// <summary>
    /// Creates a new ShmRing from a memory region.
    /// </summary>
    /// <param name="memory">The memory region containing the ring (header + data).</param>
    /// <param name="headerOffset">Offset to the ring header within the memory.</param>
    /// <param name="capacity">The data area capacity (must be power of 2).</param>
    /// <param name="sync">Optional synchronization primitive for cross-process signaling.</param>
    /// <param name="isOwner">If true, this instance owns the ring and will set the Closed flag in shared memory on dispose.</param>
    public ShmRing(Memory<byte> memory, int headerOffset, ulong capacity, IRingSync? sync = null, bool isOwner = true)
    {
        if (capacity == 0 || !IsPowerOfTwo(capacity))
        {
            throw new ArgumentException("Capacity must be a power of two", nameof(capacity));
        }

        var requiredSize = headerOffset + ShmConstants.RingHeaderSize + (int)capacity;
        if (memory.Length < requiredSize)
        {
            throw new ArgumentException($"Memory region too small. Required: {requiredSize}, Got: {memory.Length}", nameof(memory));
        }

        _memory = memory;
        _headerOffset = headerOffset;
        _dataOffset = headerOffset + ShmConstants.RingHeaderSize;
        _capacity = capacity;
        _capMask = capacity - 1;
        _sync = sync;
        _isOwner = isOwner;

        // Initialize pending read index from current shared read index
        ref var header = ref GetHeader();
        _pendingReadIdx = Volatile.Read(ref header.ReadIdx);
    }

    /// <summary>
    /// Gets the ring buffer capacity.
    /// </summary>
    public ulong Capacity => _capacity;

    /// <summary>
    /// Gets whether the ring is closed.
    /// </summary>
    public bool IsClosed => _localClosed || GetHeader().Closed != 0;

    /// <summary>
    /// Gets a snapshot of the current ring state for debugging.
    /// </summary>
    public RingState GetState()
    {
        if (_localClosed)
        {
            return new RingState { Capacity = _capacity, Closed = true };
        }

        ref var header = ref GetHeader();
        return new RingState
        {
            Capacity = _capacity,
            WriteIdx = Volatile.Read(ref header.WriteIdx),
            ReadIdx = Volatile.Read(ref header.ReadIdx),
            DataSeq = Volatile.Read(ref header.DataSeq),
            SpaceSeq = Volatile.Read(ref header.SpaceSeq),
            ContigSeq = Volatile.Read(ref header.ContigSeq),
            Closed = header.Closed != 0,
            DataWaiters = Volatile.Read(ref header.DataWaiters),
            SpaceWaiters = Volatile.Read(ref header.SpaceWaiters)
        };
    }

    /// <summary>
    /// Writes data to the ring buffer, blocking until space is available.
    /// </summary>
    /// <param name="data">The data to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="RingClosedException">Thrown if the ring is closed.</exception>
    /// <exception cref="OperationCanceledException">Thrown if cancelled.</exception>
    public void Write(ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty)
        {
            return;
        }

        if ((ulong)data.Length > _capacity)
        {
            throw new ArgumentException($"Data ({data.Length} bytes) exceeds ring capacity ({_capacity} bytes)", nameof(data));
        }

        ref var header = ref GetHeader();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_localClosed || header.Closed != 0)
            {
                throw new RingClosedException();
            }

            var writeIdx = Volatile.Read(ref header.WriteIdx);
            var readIdx = Volatile.Read(ref header.ReadIdx);
            var used = writeIdx - readIdx;
            var available = _capacity - used;

            if ((ulong)data.Length <= available)
            {
                // Space available - perform the write
                var writePos = writeIdx & _capMask;
                var dataSpan = GetDataSpan();

                if (writePos + (ulong)data.Length <= _capacity)
                {
                    // Simple case: no wrap
                    data.CopyTo(dataSpan.Slice((int)writePos, data.Length));
                }
                else
                {
                    // Wrap case: split the write
                    var firstChunk = (int)(_capacity - writePos);
                    data[..firstChunk].CopyTo(dataSpan.Slice((int)writePos, firstChunk));
                    data[firstChunk..].CopyTo(dataSpan[..(data.Length - firstChunk)]);
                }

                // Publish new write index (release semantics)
                Volatile.Write(ref header.WriteIdx, writeIdx + (ulong)data.Length);

                // Signal waiters
                if (data.Length > 0)
                {
                    Interlocked.Increment(ref header.DataSeq);
                    if (Volatile.Read(ref header.DataWaiters) > 0)
                    {
                        _sync?.SignalData();
                    }
                }

                return;
            }

            // Not enough space - wait for it
            WaitForSpace(ref header, (ulong)data.Length, cancellationToken);
        }
    }

    /// <summary>
    /// Reads data from the ring buffer, blocking until data is available.
    /// </summary>
    /// <param name="buffer">The buffer to read into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of bytes read.</returns>
    /// <exception cref="RingClosedException">Thrown if the ring is closed and empty.</exception>
    /// <exception cref="OperationCanceledException">Thrown if cancelled.</exception>
    public int Read(Span<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        ref var header = ref GetHeader();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var writeIdx = Volatile.Read(ref header.WriteIdx);
            var readIdx = Volatile.Read(ref header.ReadIdx);
            var used = writeIdx - readIdx;

            if (used > 0)
            {
                // Data available - perform the read
                var toRead = Math.Min((ulong)buffer.Length, used);
                var readPos = readIdx & _capMask;
                var dataSpan = GetDataSpan();
                var bytesRead = 0;

                if (readPos + toRead <= _capacity)
                {
                    // Simple case: no wrap
                    dataSpan.Slice((int)readPos, (int)toRead).CopyTo(buffer);
                    bytesRead = (int)toRead;
                }
                else
                {
                    // Wrap case: split the read
                    var firstChunk = (int)(_capacity - readPos);
                    dataSpan.Slice((int)readPos, firstChunk).CopyTo(buffer);
                    var secondChunk = (int)toRead - firstChunk;
                    dataSpan[..secondChunk].CopyTo(buffer[firstChunk..]);
                    bytesRead = (int)toRead;
                }

                // Publish new read index (release semantics)
                Volatile.Write(ref header.ReadIdx, readIdx + (ulong)bytesRead);

                // Signal space availability
                if (bytesRead > 0)
                {
                    Interlocked.Increment(ref header.ContigSeq);
                    if (Volatile.Read(ref header.SpaceWaiters) > 0)
                    {
                        Interlocked.Increment(ref header.SpaceSeq);
                        _sync?.SignalSpace();
                    }
                }

                return bytesRead;
            }

            // Check if closed with no data
            if (_localClosed || header.Closed != 0)
            {
                throw new RingClosedException();
            }

            // No data - wait for it
            WaitForData(ref header, cancellationToken);
        }
    }

    /// <summary>
    /// Reserves space for writing, returning slices for zero-copy writes.
    /// The reservation must be committed via CommitWrite.
    /// </summary>
    /// <param name="size">The number of bytes to reserve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A write reservation containing memory slices.</returns>
    public WriteReservation ReserveWrite(int size, CancellationToken cancellationToken = default)
    {
        if (size <= 0)
        {
            throw new ArgumentException("Size must be positive", nameof(size));
        }

        if ((ulong)size > _capacity)
        {
            throw new ArgumentException($"Size ({size} bytes) exceeds ring capacity ({_capacity} bytes)", nameof(size));
        }

        ref var header = ref GetHeader();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_localClosed || header.Closed != 0)
            {
                throw new RingClosedException();
            }

            var writeIdx = Volatile.Read(ref header.WriteIdx);
            var readIdx = Volatile.Read(ref header.ReadIdx);
            var used = writeIdx - readIdx;
            var available = _capacity - used;

            if ((ulong)size <= available)
            {
                var writePos = writeIdx & _capMask;

                Memory<byte> first, second;

                if (writePos + (ulong)size <= _capacity)
                {
                    // No wrap needed
                    first = _memory.Slice(_dataOffset + (int)writePos, size);
                    second = Memory<byte>.Empty;
                }
                else
                {
                    // Wrap case
                    var firstLen = (int)(_capacity - writePos);
                    first = _memory.Slice(_dataOffset + (int)writePos, firstLen);
                    second = _memory.Slice(_dataOffset, size - firstLen);
                }

                return new WriteReservation
                {
                    First = first,
                    Second = second,
                    Ring = this,
                    WriteIdx = writeIdx,
                    MaxBytes = size
                };
            }

            WaitForSpace(ref header, (ulong)size, cancellationToken);
        }
    }

    /// <summary>
    /// Commits a write reservation, publishing the written bytes.
    /// </summary>
    /// <param name="reservation">The reservation to commit.</param>
    /// <param name="bytesWritten">The number of bytes actually written (must not exceed reservation size).</param>
    public void CommitWrite(WriteReservation reservation, int bytesWritten)
    {
        if (reservation.Ring != this)
        {
            throw new ArgumentException("Reservation is not for this ring", nameof(reservation));
        }

        if (bytesWritten < 0 || bytesWritten > reservation.MaxBytes)
        {
            throw new ArgumentException($"Invalid bytes written: {bytesWritten}. Must be 0-{reservation.MaxBytes}", nameof(bytesWritten));
        }

        if (_localClosed)
        {
            return;
        }

        ref var header = ref GetHeader();

        // Publish new write index
        Volatile.Write(ref header.WriteIdx, reservation.WriteIdx + (ulong)bytesWritten);

        // Signal waiters
        if (bytesWritten > 0)
        {
            Interlocked.Increment(ref header.DataSeq);
            if (Volatile.Read(ref header.DataWaiters) > 0)
            {
                _sync?.SignalData();
            }
        }
    }

    /// <summary>
    /// Reserves bytes for reading, returning slices for zero-copy reads.
    /// The reservation must be committed via CommitRead.
    /// </summary>
    /// <param name="size">The number of bytes to reserve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A read reservation containing memory slices.</returns>
    public ReadReservation ReserveRead(int size, CancellationToken cancellationToken = default)
    {
        if (size <= 0)
        {
            throw new ArgumentException("Size must be positive", nameof(size));
        }

        ref var header = ref GetHeader();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var writeIdx = Volatile.Read(ref header.WriteIdx);
            var pendingIdx = Volatile.Read(ref _pendingReadIdx);
            var available = writeIdx - pendingIdx;

            // If closed, allow draining remaining data
            if ((_localClosed || header.Closed != 0) && available == 0)
            {
                throw new RingClosedException();
            }

            if (available >= (ulong)size)
            {
                var readPos = pendingIdx & _capMask;
                ReadOnlyMemory<byte> first, second;

                if (readPos + (ulong)size <= _capacity)
                {
                    // No wrap needed
                    first = _memory.Slice(_dataOffset + (int)readPos, size);
                    second = ReadOnlyMemory<byte>.Empty;
                }
                else
                {
                    // Wrap case
                    var firstLen = (int)(_capacity - readPos);
                    first = _memory.Slice(_dataOffset + (int)readPos, firstLen);
                    second = _memory.Slice(_dataOffset, size - firstLen);
                }

                // Advance pending read index
                Volatile.Write(ref _pendingReadIdx, pendingIdx + (ulong)size);

                return new ReadReservation
                {
                    First = first,
                    Second = second,
                    Ring = this,
                    CommitReadIdx = Volatile.Read(ref header.ReadIdx),
                    MaxBytes = size
                };
            }

            // If closed with insufficient data
            if (_localClosed || header.Closed != 0)
            {
                throw new RingClosedException();
            }

            WaitForData(ref header, cancellationToken);
        }
    }

    /// <summary>
    /// Commits a read reservation, freeing space for the writer.
    /// </summary>
    /// <param name="reservation">The reservation to commit.</param>
    /// <param name="bytesConsumed">The number of bytes consumed (must not exceed reservation size).</param>
    public void CommitRead(ReadReservation reservation, int bytesConsumed)
    {
        if (reservation.Ring != this)
        {
            throw new ArgumentException("Reservation is not for this ring", nameof(reservation));
        }

        if (bytesConsumed < 0 || bytesConsumed > reservation.MaxBytes)
        {
            throw new ArgumentException($"Invalid bytes consumed: {bytesConsumed}. Must be 0-{reservation.MaxBytes}", nameof(bytesConsumed));
        }

        if (_localClosed)
        {
            return;
        }

        ref var header = ref GetHeader();

        // Advance shared read index
        Volatile.Write(ref header.ReadIdx, reservation.CommitReadIdx + (ulong)bytesConsumed);

        // Signal space availability
        if (bytesConsumed > 0)
        {
            Interlocked.Increment(ref header.ContigSeq);
            if (Volatile.Read(ref header.SpaceWaiters) > 0)
            {
                Interlocked.Increment(ref header.SpaceSeq);
                _sync?.SignalSpace();
            }
        }
    }

    /// <summary>
    /// Closes the ring buffer. Readers can still drain remaining data.
    /// Only the owner (server) sets the Closed flag in shared memory.
    /// </summary>
    public void Close()
    {
        if (_localClosed)
        {
            return;
        }

        _localClosed = true;

        // Only the owner (server) should set the Closed flag in shared memory
        // Clients just close locally to stop their own read/write operations
        if (_isOwner)
        {
            ref var header = ref GetHeader();
            Volatile.Write(ref header.Closed, 1);

            // Wake all waiters
            Interlocked.Increment(ref header.DataSeq);
            Interlocked.Increment(ref header.SpaceSeq);
            Interlocked.Increment(ref header.ContigSeq);

            _sync?.SignalData();
            _sync?.SignalSpace();
            _sync?.SignalContig();
        }
    }

    public void Dispose()
    {
        Close();
        _sync?.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref RingHeader GetHeader()
    {
        return ref MemoryMarshal.AsRef<RingHeader>(_memory.Span.Slice(_headerOffset, ShmConstants.RingHeaderSize));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> GetDataSpan()
    {
        return _memory.Span.Slice(_dataOffset, (int)_capacity);
    }

    private void WaitForSpace(ref RingHeader header, ulong needed, CancellationToken cancellationToken)
    {
        // Adaptive spin before blocking
        var spinLimit = Volatile.Read(ref _spaceSpinCutoff);
        for (var i = 0; i < spinLimit; i++)
        {
            var writeIdx = Volatile.Read(ref header.WriteIdx);
            var readIdx = Volatile.Read(ref header.ReadIdx);
            if (_capacity - (writeIdx - readIdx) >= needed)
            {
                // Success - adapt spin limit upward
                if (i > 0)
                {
                    var newCutoff = Math.Min(ShmConstants.SpinIterationsMax, (7 * spinLimit + i * 2) / 8);
                    Volatile.Write(ref _spaceSpinCutoff, Math.Max(ShmConstants.SpinIterationsMin, newCutoff));
                }
                return;
            }

            if (header.Closed != 0 || _localClosed)
            {
                throw new RingClosedException();
            }

            Thread.SpinWait(1);
        }

        // Spin failed - adapt downward and fall back to blocking
        var reducedCutoff = (7 * spinLimit + ShmConstants.SpinIterationsMin) / 8;
        Volatile.Write(ref _spaceSpinCutoff, Math.Max(ShmConstants.SpinIterationsMin, reducedCutoff));

        // Block on sync primitive
        Interlocked.Increment(ref header.SpaceWaiters);
        try
        {
            var seq = Volatile.Read(ref header.SpaceSeq);

            // Re-check before blocking
            var writeIdx = Volatile.Read(ref header.WriteIdx);
            var readIdx = Volatile.Read(ref header.ReadIdx);
            if (_capacity - (writeIdx - readIdx) >= needed)
            {
                return;
            }

            _sync?.WaitForSpace(seq, TimeSpan.FromMilliseconds(100), cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref header.SpaceWaiters);
        }
    }

    private void WaitForData(ref RingHeader header, CancellationToken cancellationToken)
    {
        // Adaptive spin before blocking
        var spinLimit = Volatile.Read(ref _dataSpinCutoff);
        for (var i = 0; i < spinLimit; i++)
        {
            var writeIdx = Volatile.Read(ref header.WriteIdx);
            var readIdx = Volatile.Read(ref header.ReadIdx);
            if (writeIdx > readIdx)
            {
                // Success - adapt spin limit upward
                if (i > 0)
                {
                    var newCutoff = Math.Min(ShmConstants.SpinIterationsMax, (7 * spinLimit + i * 2) / 8);
                    Volatile.Write(ref _dataSpinCutoff, Math.Max(ShmConstants.SpinIterationsMin, newCutoff));
                }
                return;
            }

            if (header.Closed != 0 || _localClosed)
            {
                throw new RingClosedException();
            }

            Thread.SpinWait(1);
        }

        // Spin failed - adapt downward and fall back to blocking
        var reducedCutoff = (7 * _dataSpinCutoff + ShmConstants.SpinIterationsMin) / 8;
        Volatile.Write(ref _dataSpinCutoff, Math.Max(ShmConstants.SpinIterationsMin, reducedCutoff));

        // Block on sync primitive
        Interlocked.Increment(ref header.DataWaiters);
        try
        {
            var seq = Volatile.Read(ref header.DataSeq);

            // Re-check before blocking
            var writeIdx = Volatile.Read(ref header.WriteIdx);
            var readIdx = Volatile.Read(ref header.ReadIdx);
            if (writeIdx > readIdx)
            {
                return;
            }

            _sync?.WaitForData(seq, TimeSpan.FromMilliseconds(100), cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref header.DataWaiters);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPowerOfTwo(ulong value)
    {
        return value > 0 && (value & (value - 1)) == 0;
    }

    /// <summary>
    /// Gets the number of bytes available to read.
    /// </summary>
    public ulong ReadableBytes
    {
        get
        {
            ref var header = ref GetHeader();
            var writeIdx = Volatile.Read(ref header.WriteIdx);
            var readIdx = Volatile.Read(ref header.ReadIdx);
            return writeIdx - readIdx;
        }
    }

    /// <summary>
    /// Gets the number of bytes available to write.
    /// </summary>
    public ulong WritableBytes
    {
        get
        {
            ref var header = ref GetHeader();
            var writeIdx = Volatile.Read(ref header.WriteIdx);
            var readIdx = Volatile.Read(ref header.ReadIdx);
            var used = writeIdx - readIdx;
            return _capacity - used;
        }
    }

    /// <summary>
    /// Checks if at least the specified number of bytes can be read.
    /// </summary>
    public bool TryPeek(int minBytes, out int available)
    {
        if (_localClosed)
        {
            available = 0;
            return false;
        }

        ref var header = ref GetHeader();
        var writeIdx = Volatile.Read(ref header.WriteIdx);
        var readIdx = Volatile.Read(ref header.ReadIdx);
        var used = (int)(writeIdx - readIdx);
        available = used;
        return used >= minBytes;
    }

    /// <summary>
    /// Checks if at least the specified number of bytes can be written.
    /// </summary>
    public bool CanWrite(int size)
    {
        if (_localClosed)
        {
            return false;
        }

        ref var header = ref GetHeader();
        var writeIdx = Volatile.Read(ref header.WriteIdx);
        var readIdx = Volatile.Read(ref header.ReadIdx);
        var used = writeIdx - readIdx;
        var available = _capacity - used;
        return (ulong)size <= available;
    }

    /// <summary>
    /// Tries to read data without blocking.
    /// </summary>
    /// <param name="buffer">The buffer to read into.</param>
    /// <returns>True if data was read, false if no data available.</returns>
    public bool TryRead(Span<byte> buffer)
    {
        if (buffer.IsEmpty || _localClosed)
        {
            return false;
        }

        ref var header = ref GetHeader();
        var writeIdx = Volatile.Read(ref header.WriteIdx);
        var readIdx = Volatile.Read(ref header.ReadIdx);
        var used = writeIdx - readIdx;

        if (used < (ulong)buffer.Length)
        {
            return false; // Not enough data
        }

        // Data available - perform the read
        var readPos = readIdx & _capMask;
        var dataSpan = GetDataSpan();

        if (readPos + (ulong)buffer.Length <= _capacity)
        {
            // Simple case: no wrap
            dataSpan.Slice((int)readPos, buffer.Length).CopyTo(buffer);
        }
        else
        {
            // Wrap case: split the read
            var firstChunk = (int)(_capacity - readPos);
            dataSpan.Slice((int)readPos, firstChunk).CopyTo(buffer);
            var secondChunk = buffer.Length - firstChunk;
            dataSpan[..secondChunk].CopyTo(buffer[firstChunk..]);
        }

        // Publish new read index (release semantics)
        Volatile.Write(ref header.ReadIdx, readIdx + (ulong)buffer.Length);

        // Signal space availability
        if (buffer.Length > 0)
        {
            Interlocked.Increment(ref header.ContigSeq);
            if (Volatile.Read(ref header.SpaceWaiters) > 0)
            {
                Interlocked.Increment(ref header.SpaceSeq);
                _sync?.SignalSpace();
            }
        }

        return true;
    }

    /// <summary>
    /// Tries to write data without blocking.
    /// </summary>
    /// <param name="data">The data to write.</param>
    /// <returns>True if data was written, false if not enough space.</returns>
    public bool TryWrite(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return true;
        }

        if (_localClosed || (ulong)data.Length > _capacity)
        {
            return false;
        }

        ref var header = ref GetHeader();

        if (header.Closed != 0)
        {
            return false;
        }

        var writeIdx = Volatile.Read(ref header.WriteIdx);
        var readIdx = Volatile.Read(ref header.ReadIdx);
        var used = writeIdx - readIdx;
        var available = _capacity - used;

        if ((ulong)data.Length > available)
        {
            return false; // Not enough space
        }

        // Space available - perform the write
        var writePos = writeIdx & _capMask;
        var dataSpan = GetDataSpan();

        if (writePos + (ulong)data.Length <= _capacity)
        {
            // Simple case: no wrap
            data.CopyTo(dataSpan.Slice((int)writePos, data.Length));
        }
        else
        {
            // Wrap case: split the write
            var firstChunk = (int)(_capacity - writePos);
            data[..firstChunk].CopyTo(dataSpan.Slice((int)writePos, firstChunk));
            data[firstChunk..].CopyTo(dataSpan[..(data.Length - firstChunk)]);
        }

        // Publish new write index (release semantics)
        Volatile.Write(ref header.WriteIdx, writeIdx + (ulong)data.Length);

        // Signal waiters
        if (data.Length > 0)
        {
            Interlocked.Increment(ref header.DataSeq);
            if (Volatile.Read(ref header.DataWaiters) > 0)
            {
                _sync?.SignalData();
            }
        }

        return true;
    }
}
