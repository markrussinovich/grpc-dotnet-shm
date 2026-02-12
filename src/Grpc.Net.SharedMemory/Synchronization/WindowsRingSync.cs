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
internal sealed unsafe partial class WindowsRingSync : IRingSync
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
    private readonly EventWaitHandle? _dataWaitHandleFallback;
    private readonly EventWaitHandle? _spaceWaitHandleFallback;
    private readonly EventWaitHandle? _contigWaitHandleFallback;
    private readonly uint* _dataSeqPtr;
    private readonly uint* _spaceSeqPtr;
    private readonly uint* _contigSeqPtr;
    private readonly bool _useWaitOnAddress;
    private bool _disposed;

    public WindowsRingSync(string segmentName, string ringId, bool isServer)
    {
        _useWaitOnAddress = false;
        _dataSeqPtr = null;
        _spaceSeqPtr = null;
        _contigSeqPtr = null;

        var localDataEventName = $"Local\\grpc_shm_{segmentName}_{ringId}_data";
        var localSpaceEventName = $"Local\\grpc_shm_{segmentName}_{ringId}_space";
        var localContigEventName = $"Local\\grpc_shm_{segmentName}_{ringId}_contig";
        var globalDataEventName = $"Global\\grpc_shm_{segmentName}_{ringId}_data";
        var globalSpaceEventName = $"Global\\grpc_shm_{segmentName}_{ringId}_space";
        var globalContigEventName = $"Global\\grpc_shm_{segmentName}_{ringId}_contig";

        if (isServer)
        {
            // Server creates Local events and, when allowed, also creates Global events for cross-session clients.
            _dataWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, localDataEventName);
            _spaceWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, localSpaceEventName);
            _contigWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, localContigEventName);

            _dataWaitHandleFallback = TryCreateEvent(globalDataEventName);
            _spaceWaitHandleFallback = TryCreateEvent(globalSpaceEventName);
            _contigWaitHandleFallback = TryCreateEvent(globalContigEventName);
        }
        else
        {
            // Client opens Local first, then Global if Local is not available.
            _dataWaitHandle = OpenExistingWithFallback(localDataEventName, globalDataEventName);
            _spaceWaitHandle = OpenExistingWithFallback(localSpaceEventName, globalSpaceEventName);
            _contigWaitHandle = OpenExistingWithFallback(localContigEventName, globalContigEventName);
            _dataWaitHandleFallback = null;
            _spaceWaitHandleFallback = null;
            _contigWaitHandleFallback = null;
        }

        _dataEvent = _dataWaitHandle.SafeWaitHandle;
        _spaceEvent = _spaceWaitHandle.SafeWaitHandle;
        _contigEvent = _contigWaitHandle.SafeWaitHandle;
    }

    public unsafe WindowsRingSync(string segmentName, string ringId, bool isServer, MappedMemoryManager memoryManager, int ringHeaderOffset)
        : this(segmentName, ringId, isServer)
    {
        ArgumentNullException.ThrowIfNull(memoryManager);

        // WaitOnAddress/WakeByAddressSingle match on virtual addresses.
        // When client and server use separate memory mappings of the same
        // shared memory segment (different MemoryMappedViewAccessor instances),
        // their virtual addresses differ, so wakes never reach the waiters.
        // This applies to both in-process and cross-process scenarios.
        // Named events (used when _useWaitOnAddress is false) work correctly
        // across any process/mapping boundary.
        _useWaitOnAddress = false;
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

        return WaitForEvent(_dataWaitHandle, _dataWaitHandleFallback, timeout, cancellationToken);
    }

    public bool WaitForSpace(uint expectedSeq, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (_useWaitOnAddress)
        {
            return WaitOnAddressLoop(_spaceSeqPtr, expectedSeq, timeout, cancellationToken);
        }

        return WaitForEvent(_spaceWaitHandle, _spaceWaitHandleFallback, timeout, cancellationToken);
    }

    public bool WaitForContig(uint expectedSeq, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (_useWaitOnAddress)
        {
            return WaitOnAddressLoop(_contigSeqPtr, expectedSeq, timeout, cancellationToken);
        }

        return WaitForEvent(_contigWaitHandle, _contigWaitHandleFallback, timeout, cancellationToken);
    }

    private static bool WaitForEvent(EventWaitHandle handle, EventWaitHandle? fallbackHandle, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var timeoutMs = timeout.HasValue ? (int)timeout.Value.TotalMilliseconds : Timeout.Infinite;

        if (cancellationToken.CanBeCanceled)
        {
            // Wait with cancellation support
            var waitHandles = fallbackHandle == null
                ? new WaitHandle[] { handle, cancellationToken.WaitHandle }
                : new WaitHandle[] { handle, fallbackHandle, cancellationToken.WaitHandle };
            var result = WaitHandle.WaitAny(waitHandles, timeoutMs);

            // result == 0 means the event was signaled
            // result == 1 means cancellation was requested
            // result == WaitHandle.WaitTimeout means timeout
            if (result == 0)
            {
                return true;
            }

            if (fallbackHandle != null && result == 1)
            {
                return true;
            }

            return false;
        }
        else
        {
            if (fallbackHandle == null)
            {
                return handle.WaitOne(timeoutMs);
            }

            return WaitHandle.WaitAny(new WaitHandle[] { handle, fallbackHandle }, timeoutMs) != WaitHandle.WaitTimeout;
        }
    }

    public void SignalData()
    {
        if (_useWaitOnAddress)
        {
            WakeByAddressSingle(_dataSeqPtr);
            return;
        }

        _dataWaitHandle.Set();
        _dataWaitHandleFallback?.Set();
    }

    public void SignalSpace()
    {
        if (_useWaitOnAddress)
        {
            WakeByAddressSingle(_spaceSeqPtr);
            return;
        }

        _spaceWaitHandle.Set();
        _spaceWaitHandleFallback?.Set();
    }

    public void SignalContig()
    {
        if (_useWaitOnAddress)
        {
            WakeByAddressSingle(_contigSeqPtr);
            return;
        }

        _contigWaitHandle.Set();
        _contigWaitHandleFallback?.Set();
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
        _dataWaitHandleFallback?.Dispose();
        _spaceWaitHandleFallback?.Dispose();
        _contigWaitHandleFallback?.Dispose();
    }

    private static EventWaitHandle OpenExistingWithFallback(string localName, string globalName)
    {
        try
        {
            return EventWaitHandle.OpenExisting(localName);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return EventWaitHandle.OpenExisting(globalName);
        }
        catch (UnauthorizedAccessException)
        {
            return EventWaitHandle.OpenExisting(globalName);
        }
    }

    private static EventWaitHandle? TryCreateEvent(string name)
    {
        try
        {
            return new EventWaitHandle(false, EventResetMode.AutoReset, name);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
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

    [LibraryImport("api-ms-win-core-synch-l1-2-0.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool WaitOnAddress(void* address, void* compareAddress, IntPtr addressSize, uint milliseconds);

    [LibraryImport("api-ms-win-core-synch-l1-2-0.dll")]
    private static unsafe partial void WakeByAddressSingle(void* address);
}

#endif
