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

#if WINDOWS

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Grpc.Net.SharedMemory.Synchronization;

/// <summary>
/// Windows implementation of ring synchronization using named events.
/// Uses auto-reset events for cross-process signaling.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsRingSync : IRingSync
{
    private const int DataSeqOffset = 0x18;
    private const int SpaceSeqOffset = 0x1C;
    private const int ContigSeqOffset = 0x28;

    private readonly SafeWaitHandle _dataEvent;
    private readonly SafeWaitHandle _spaceEvent;
    private readonly SafeWaitHandle _contigEvent;
    private readonly EventWaitHandle _dataWaitHandle;
    private readonly EventWaitHandle _spaceWaitHandle;
    private readonly EventWaitHandle _contigWaitHandle;
    private readonly unsafe uint* _dataSeqPtr;
    private readonly unsafe uint* _spaceSeqPtr;
    private readonly unsafe uint* _contigSeqPtr;
    private readonly bool _useWaitOnAddress;
    private bool _disposed;

    public WindowsRingSync(string segmentName, string ringId, bool isServer)
    {
        _useWaitOnAddress = false;
        _dataSeqPtr = null;
        _spaceSeqPtr = null;
        _contigSeqPtr = null;

        var dataEventName = $"Global\\grpc_shm_{segmentName}_{ringId}_data";
        var spaceEventName = $"Global\\grpc_shm_{segmentName}_{ringId}_space";
        var contigEventName = $"Global\\grpc_shm_{segmentName}_{ringId}_contig";

        if (isServer)
        {
            // Server creates the events
            _dataWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, dataEventName);
            _spaceWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, spaceEventName);
            _contigWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, contigEventName);
        }
        else
        {
            // Client opens existing events
            _dataWaitHandle = EventWaitHandle.OpenExisting(dataEventName);
            _spaceWaitHandle = EventWaitHandle.OpenExisting(spaceEventName);
            _contigWaitHandle = EventWaitHandle.OpenExisting(contigEventName);
        }

        _dataEvent = _dataWaitHandle.SafeWaitHandle;
        _spaceEvent = _spaceWaitHandle.SafeWaitHandle;
        _contigEvent = _contigWaitHandle.SafeWaitHandle;
    }

    public unsafe WindowsRingSync(string segmentName, string ringId, bool isServer, MappedMemoryManager memoryManager, int ringHeaderOffset)
        : this(segmentName, ringId, isServer)
    {
        ArgumentNullException.ThrowIfNull(memoryManager);

        _useWaitOnAddress = true;
        _dataSeqPtr = memoryManager.GetUInt32Pointer(ringHeaderOffset + DataSeqOffset);
        _spaceSeqPtr = memoryManager.GetUInt32Pointer(ringHeaderOffset + SpaceSeqOffset);
        _contigSeqPtr = memoryManager.GetUInt32Pointer(ringHeaderOffset + ContigSeqOffset);
    }

    public bool WaitForData(uint expectedSeq, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (_useWaitOnAddress)
        {
            return WaitOnAddressLoop(_dataSeqPtr, expectedSeq, timeout, cancellationToken);
        }

        return WaitForEvent(_dataWaitHandle, timeout, cancellationToken);
    }

    public bool WaitForSpace(uint expectedSeq, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (_useWaitOnAddress)
        {
            return WaitOnAddressLoop(_spaceSeqPtr, expectedSeq, timeout, cancellationToken);
        }

        return WaitForEvent(_spaceWaitHandle, timeout, cancellationToken);
    }

    public bool WaitForContig(uint expectedSeq, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (_useWaitOnAddress)
        {
            return WaitOnAddressLoop(_contigSeqPtr, expectedSeq, timeout, cancellationToken);
        }

        return WaitForEvent(_contigWaitHandle, timeout, cancellationToken);
    }

    private static bool WaitForEvent(EventWaitHandle handle, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var timeoutMs = timeout.HasValue ? (int)timeout.Value.TotalMilliseconds : Timeout.Infinite;

        if (cancellationToken.CanBeCanceled)
        {
            // Wait with cancellation support
            var waitHandles = new WaitHandle[] { handle, cancellationToken.WaitHandle };
            var result = WaitHandle.WaitAny(waitHandles, timeoutMs);

            // result == 0 means the event was signaled
            // result == 1 means cancellation was requested
            // result == WaitHandle.WaitTimeout means timeout
            return result == 0;
        }
        else
        {
            return handle.WaitOne(timeoutMs);
        }
    }

    public void SignalData()
    {
        if (_useWaitOnAddress)
        {
            unsafe
            {
                WakeByAddressSingle(_dataSeqPtr);
            }
            return;
        }

        _dataWaitHandle.Set();
    }

    public void SignalSpace()
    {
        if (_useWaitOnAddress)
        {
            unsafe
            {
                WakeByAddressSingle(_spaceSeqPtr);
            }
            return;
        }

        _spaceWaitHandle.Set();
    }

    public void SignalContig()
    {
        if (_useWaitOnAddress)
        {
            unsafe
            {
                WakeByAddressSingle(_contigSeqPtr);
            }
            return;
        }

        _contigWaitHandle.Set();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _dataWaitHandle?.Dispose();
        _spaceWaitHandle?.Dispose();
        _contigWaitHandle?.Dispose();
    }

    private static unsafe bool WaitOnAddressLoop(uint* address, uint expectedValue, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var remaining = timeout;
        while (true)
        {
            if (Volatile.Read(ref *address) != expectedValue)
            {
                return true;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            var waitMs = GetWaitTimeoutMilliseconds(remaining);
            if (waitMs == 0)
            {
                return false;
            }

            var compare = expectedValue;
            WaitOnAddress(address, &compare, (IntPtr)sizeof(uint), waitMs == Timeout.Infinite ? uint.MaxValue : (uint)waitMs);

            if (remaining.HasValue && waitMs != Timeout.Infinite)
            {
                remaining = remaining.Value - TimeSpan.FromMilliseconds(waitMs);
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }
            }
        }
    }

    private static int GetWaitTimeoutMilliseconds(TimeSpan? remaining)
    {
        if (!remaining.HasValue)
        {
            return Timeout.Infinite;
        }

        if (remaining.Value <= TimeSpan.Zero)
        {
            return 0;
        }

        return (int)Math.Min(remaining.Value.TotalMilliseconds, 100);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial bool WaitOnAddress(void* address, void* compareAddress, IntPtr addressSize, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial void WakeByAddressSingle(void* address);
    }
}

#endif
