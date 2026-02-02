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
/// </summary>
internal sealed partial class LinuxRingSync : IRingSync
{
    // Futex operation constants
    private const int FUTEX_WAIT = 0;
    private const int FUTEX_WAKE = 1;
    private const int FUTEX_PRIVATE_FLAG = 128;

    // We don't use FUTEX_PRIVATE since we need cross-process synchronization

    private unsafe uint* _dataSeqPtr;
    private unsafe uint* _spaceSeqPtr;
    private unsafe uint* _contigSeqPtr;
    private bool _disposed;

    public LinuxRingSync()
    {
        // Pointers will be set when the ring is attached
    }

    /// <summary>
    /// Sets the memory pointers for futex operations.
    /// Must be called after the shared memory is mapped.
    /// </summary>
    public unsafe void SetPointers(uint* dataSeqPtr, uint* spaceSeqPtr, uint* contigSeqPtr)
    {
        _dataSeqPtr = dataSeqPtr;
        _spaceSeqPtr = spaceSeqPtr;
        _contigSeqPtr = contigSeqPtr;
    }

    public bool WaitForData(uint expectedSeq, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        unsafe
        {
            return FutexWait(_dataSeqPtr, expectedSeq, timeout, cancellationToken);
        }
    }

    public bool WaitForSpace(uint expectedSeq, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        unsafe
        {
            return FutexWait(_spaceSeqPtr, expectedSeq, timeout, cancellationToken);
        }
    }

    public bool WaitForContig(uint expectedSeq, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        unsafe
        {
            return FutexWait(_contigSeqPtr, expectedSeq, timeout, cancellationToken);
        }
    }

    private static unsafe bool FutexWait(uint* addr, uint expectedVal, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (addr == null)
        {
            throw new InvalidOperationException("Futex address not set. Call SetPointers first.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        // Check if value changed before waiting
        if (Volatile.Read(ref *addr) != expectedVal)
        {
            return true; // Already changed, no need to wait
        }

        if (timeout.HasValue)
        {
            var ts = new Timespec
            {
                tv_sec = (long)timeout.Value.TotalSeconds,
                tv_nsec = (long)((timeout.Value.TotalSeconds - (long)timeout.Value.TotalSeconds) * 1_000_000_000)
            };

            var result = futex(addr, FUTEX_WAIT, expectedVal, &ts, null, 0);
            if (result == -1)
            {
                var errno = Marshal.GetLastWin32Error();
                // EAGAIN (11) means the value changed - this is success
                // ETIMEDOUT (110) means timeout
                // EINTR (4) means interrupted - retry or return based on cancellation
                if (errno == 11) return true;  // EAGAIN
                if (errno == 110) return false; // ETIMEDOUT
                if (errno == 4) return !cancellationToken.IsCancellationRequested; // EINTR
            }
            return result == 0;
        }
        else
        {
            var result = futex(addr, FUTEX_WAIT, expectedVal, null, null, 0);
            if (result == -1)
            {
                var errno = Marshal.GetLastWin32Error();
                if (errno == 11) return true;  // EAGAIN - value changed
                if (errno == 4) return !cancellationToken.IsCancellationRequested; // EINTR
            }
            return result == 0;
        }
    }

    public void SignalData()
    {
        unsafe
        {
            if (_dataSeqPtr != null)
            {
                futex(_dataSeqPtr, FUTEX_WAKE, 1, null, null, 0);
            }
        }
    }

    public void SignalSpace()
    {
        unsafe
        {
            if (_spaceSeqPtr != null)
            {
                futex(_spaceSeqPtr, FUTEX_WAKE, 1, null, null, 0);
            }
        }
    }

    public void SignalContig()
    {
        unsafe
        {
            if (_contigSeqPtr != null)
            {
                futex(_contigSeqPtr, FUTEX_WAKE, 1, null, null, 0);
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        // Futex doesn't need explicit cleanup
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long tv_sec;
        public long tv_nsec;
    }

    // P/Invoke for futex syscall
    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int futex(
        uint* uaddr,
        int futex_op,
        uint val,
        Timespec* timeout,
        uint* uaddr2,
        uint val3);
}

#endif
