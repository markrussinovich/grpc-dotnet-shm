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

#if LINUX

using System.Runtime.InteropServices;

namespace Grpc.Net.SharedMemory.Synchronization;

/// <summary>
/// Linux implementation of ring synchronization using futex syscalls.
/// Futex allows efficient cross-process waiting on shared memory locations.
///
/// This implementation matches the grpc-go-shmem futex usage:
/// - Uses FUTEX_WAIT (0) and FUTEX_WAKE (1) operations (not PRIVATE for cross-process)
/// - Operates directly on sequence counters in the ring header
/// - Handles EAGAIN, ETIMEDOUT, EINTR errors appropriately
/// </summary>
internal sealed partial class LinuxRingSync : IRingSync
{
    // Futex operation constants - matching grpc-go-shmem shm_futex_linux.go
    private const int FUTEX_WAIT = 0;  // FUTEX_WAIT (shared, for cross-process)
    private const int FUTEX_WAKE = 1;  // FUTEX_WAKE (shared, for cross-process)

    // Error codes
    private const int EAGAIN = 11;      // Value mismatch - not an error
    private const int EINTR = 4;        // Interrupted by signal - retry
    private const int ETIMEDOUT = 110;  // Timeout elapsed

    // Ring header field offsets (matching RingHeader layout)
    private const int DataSeqOffset = 0x18;    // 24 bytes
    private const int SpaceSeqOffset = 0x1C;   // 28 bytes
    private const int ContigSeqOffset = 0x28;  // 40 bytes

    private readonly unsafe byte* _ringHeaderPtr;
    private bool _disposed;

    /// <summary>
    /// Creates a LinuxRingSync with pointers not yet set.
    /// </summary>
    public LinuxRingSync()
    {
        unsafe
        {
            _ringHeaderPtr = null;
        }
    }

    /// <summary>
    /// Creates a LinuxRingSync with direct access to the memory-mapped ring header.
    /// </summary>
    /// <param name="memoryManager">The memory manager providing direct access to mapped memory.</param>
    /// <param name="ringHeaderOffset">The offset to the ring header within the mapped region.</param>
    public LinuxRingSync(MappedMemoryManager memoryManager, int ringHeaderOffset)
    {
        ArgumentNullException.ThrowIfNull(memoryManager);

        unsafe
        {
            _ringHeaderPtr = memoryManager.Pointer + ringHeaderOffset;
        }
    }

    /// <summary>
    /// Sets the memory pointers for futex operations.
    /// Must be called after the shared memory is mapped if using the default constructor.
    /// </summary>
    public unsafe void SetPointers(uint* dataSeqPtr, uint* spaceSeqPtr, uint* contigSeqPtr)
    {
        // Legacy method - kept for compatibility but not used with MappedMemoryManager
        // The new constructor directly calculates pointers from the ring header offset
    }

    public bool WaitForData(uint expectedSeq, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        unsafe
        {
            if (_ringHeaderPtr == null)
            {
                // Fall back to spin wait if pointers not set
                Thread.SpinWait(1);
                return true;
            }

            var addr = (uint*)(_ringHeaderPtr + DataSeqOffset);
            return FutexWait(addr, expectedSeq, timeout, cancellationToken);
        }
    }

    public bool WaitForSpace(uint expectedSeq, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        unsafe
        {
            if (_ringHeaderPtr == null)
            {
                Thread.SpinWait(1);
                return true;
            }

            var addr = (uint*)(_ringHeaderPtr + SpaceSeqOffset);
            return FutexWait(addr, expectedSeq, timeout, cancellationToken);
        }
    }

    public bool WaitForContig(uint expectedSeq, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        unsafe
        {
            if (_ringHeaderPtr == null)
            {
                Thread.SpinWait(1);
                return true;
            }

            var addr = (uint*)(_ringHeaderPtr + ContigSeqOffset);
            return FutexWait(addr, expectedSeq, timeout, cancellationToken);
        }
    }

    private static unsafe bool FutexWait(uint* addr, uint expectedVal, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        // Critical: Re-check the value atomically before entering the syscall
        // This prevents the lost-wake race where another thread increments
        // the sequence and wakes us between our snapshot and futex entry
        // (matches grpc-go-shmem shm_futex_linux.go pattern)
        if (Volatile.Read(ref *addr) != expectedVal)
        {
            return true; // Value already changed, no need to wait
        }

        if (timeout.HasValue)
        {
            var ts = new Timespec
            {
                tv_sec = (long)timeout.Value.TotalSeconds,
                tv_nsec = (long)((timeout.Value.TotalMilliseconds % 1000) * 1_000_000)
            };

            var result = futex(addr, FUTEX_WAIT, expectedVal, &ts, null, 0);
            if (result == -1)
            {
                var errno = Marshal.GetLastWin32Error();
                // EAGAIN (11) means the value changed - this is success
                if (errno == EAGAIN) return true;
                // ETIMEDOUT (110) means timeout
                if (errno == ETIMEDOUT) return false;
                // EINTR (4) means interrupted - retry or return based on cancellation
                if (errno == EINTR) return !cancellationToken.IsCancellationRequested;
            }
            return result == 0;
        }
        else
        {
            var result = futex(addr, FUTEX_WAIT, expectedVal, null, null, 0);
            if (result == -1)
            {
                var errno = Marshal.GetLastWin32Error();
                if (errno == EAGAIN) return true;  // EAGAIN - value changed
                if (errno == EINTR) return !cancellationToken.IsCancellationRequested; // EINTR
            }
            return result == 0;
        }
    }

    public void SignalData()
    {
        unsafe
        {
            if (_ringHeaderPtr != null)
            {
                var addr = (uint*)(_ringHeaderPtr + DataSeqOffset);
                futex(addr, FUTEX_WAKE, 1, null, null, 0);
            }
        }
    }

    public void SignalSpace()
    {
        unsafe
        {
            if (_ringHeaderPtr != null)
            {
                var addr = (uint*)(_ringHeaderPtr + SpaceSeqOffset);
                futex(addr, FUTEX_WAKE, 1, null, null, 0);
            }
        }
    }

    public void SignalContig()
    {
        unsafe
        {
            if (_ringHeaderPtr != null)
            {
                var addr = (uint*)(_ringHeaderPtr + ContigSeqOffset);
                futex(addr, FUTEX_WAKE, 1, null, null, 0);
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        // Futex doesn't need explicit cleanup - the memory is managed by the Segment
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long tv_sec;
        public long tv_nsec;
    }

    // SYS_futex syscall number for x86-64 Linux
    private const int SYS_futex = 202;

    // P/Invoke for syscall wrapper to invoke futex
    // futex is not directly exported by glibc, so we use syscall()
    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial long syscall(long number, uint* uaddr, int futex_op, uint val, Timespec* timeout, uint* uaddr2, uint val3);

    private static unsafe int futex(uint* uaddr, int futex_op, uint val, Timespec* timeout, uint* uaddr2, uint val3)
    {
        var result = syscall(SYS_futex, uaddr, futex_op, val, timeout, uaddr2, val3);
        return (int)result;
    }
}

#endif
