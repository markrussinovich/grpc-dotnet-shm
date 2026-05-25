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

    // Cached raw HANDLE values for direct-syscall hot path; see ctor.
    private readonly IntPtr _dataEventNative;
    private readonly IntPtr _spaceEventNative;
    private readonly IntPtr _contigEventNative;
    private readonly IntPtr _dataFallbackNative;
    private readonly IntPtr _spaceFallbackNative;
    private readonly IntPtr _contigFallbackNative;

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

        // Cache raw HANDLEs so the no-spin hot path can do direct
        // P/Invoke WaitForSingleObject / SetEvent without allocating
        // a WaitHandle[] per call. We keep AddRef'd SafeWaitHandles via
        // the EventWaitHandle fields, so the raw handles stay valid for
        // the life of this WindowsRingSync.
        _dataEventNative = _dataEvent.DangerousGetHandle();
        _spaceEventNative = _spaceEvent.DangerousGetHandle();
        _contigEventNative = _contigEvent.DangerousGetHandle();
        _dataFallbackNative = _dataWaitHandleFallback?.SafeWaitHandle.DangerousGetHandle() ?? IntPtr.Zero;
        _spaceFallbackNative = _spaceWaitHandleFallback?.SafeWaitHandle.DangerousGetHandle() ?? IntPtr.Zero;
        _contigFallbackNative = _contigWaitHandleFallback?.SafeWaitHandle.DangerousGetHandle() ?? IntPtr.Zero;
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

    /// <summary>
    /// Hard requirement (matches the Linux eventfd path): never spin on
    /// cross-process shared-memory sequence counters before blocking.
    /// <see cref="ShmRing"/> will skip its adaptive outer spin and call
    /// <see cref="WaitForData"/>/<see cref="WaitForSpace"/>/<see cref="WaitForContig"/>
    /// directly, which invoke <see cref="WaitHandle.WaitAny(WaitHandle[], int, bool)"/>
    /// on the named auto-reset event. Each wake therefore costs one
    /// kernel transition (~5-10 µs on Windows) plus the signal write
    /// (~3-5 µs), but the system never busy-polls shared memory.
    ///
    /// Env-var kill-switch <c>SHM_WIN_ALLOW_SPIN=1</c> restores the legacy
    /// ShmRing-outer spin for A/B benchmarking. Default OFF.
    /// </summary>
    public bool SkipSpinWait => !s_allowSpin;

    private static readonly bool s_allowSpin =
        string.Equals(Environment.GetEnvironmentVariable("SHM_WIN_ALLOW_SPIN"),
            "1", StringComparison.Ordinal);

    // Diagnostic counters (env-gated SHM_WIN_DIAG=1) for understanding
    // which syscalls fire during the no-spin hot path.
    private static readonly bool s_diag =
        string.Equals(Environment.GetEnvironmentVariable("SHM_WIN_DIAG"),
            "1", StringComparison.Ordinal);
    private static long s_signalDataCalls;
    private static long s_signalSpaceCalls;
    private static long s_signalContigCalls;
    private static long s_waitDataCalls;
    private static long s_waitSpaceCalls;
    private static long s_waitContigCalls;

    public static (long SigData, long SigSpace, long SigContig, long WaitData, long WaitSpace, long WaitContig) GetWinDiag()
        => (Volatile.Read(ref s_signalDataCalls),
            Volatile.Read(ref s_signalSpaceCalls),
            Volatile.Read(ref s_signalContigCalls),
            Volatile.Read(ref s_waitDataCalls),
            Volatile.Read(ref s_waitSpaceCalls),
            Volatile.Read(ref s_waitContigCalls));

    public bool WaitForData(uint expectedSeq, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (s_diag) Interlocked.Increment(ref s_waitDataCalls);
        if (_useWaitOnAddress)
        {
            return WaitOnAddressLoop(_dataSeqPtr, expectedSeq, timeout, cancellationToken);
        }

        return WaitForEventFast(_dataEventNative, _dataFallbackNative, timeout, cancellationToken);
    }

    public bool WaitForSpace(uint expectedSeq, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (s_diag) Interlocked.Increment(ref s_waitSpaceCalls);
        if (_useWaitOnAddress)
        {
            return WaitOnAddressLoop(_spaceSeqPtr, expectedSeq, timeout, cancellationToken);
        }

        return WaitForEventFast(_spaceEventNative, _spaceFallbackNative, timeout, cancellationToken);
    }

    public bool WaitForContig(uint expectedSeq, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (s_diag) Interlocked.Increment(ref s_waitContigCalls);
        if (_useWaitOnAddress)
        {
            return WaitOnAddressLoop(_contigSeqPtr, expectedSeq, timeout, cancellationToken);
        }

        return WaitForEventFast(_contigEventNative, _contigFallbackNative, timeout, cancellationToken);
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

    /// <summary>
    /// Fast no-spin kernel wait using direct Win32 P/Invoke. Cached
    /// native HANDLE values avoid the per-call <c>WaitHandle[]</c>
    /// allocation in <c>WaitHandle.WaitAny</c> as well as the managed
    /// wait synchronization machinery. Saves ~2-5 µs per kernel wait
    /// on the hot ping-pong path.
    /// </summary>
    private unsafe bool WaitForEventFast(IntPtr nativeHandle, IntPtr nativeFallback, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return false;
        var timeoutMs = timeout.HasValue
            ? (uint)timeout.Value.TotalMilliseconds
            : INFINITE;

        // No fallback + no CT: single-handle WaitForSingleObject.
        if (nativeFallback == IntPtr.Zero && !cancellationToken.CanBeCanceled)
        {
            return WaitForSingleObject(nativeHandle, timeoutMs) == WAIT_OBJECT_0;
        }

        IntPtr* handles = stackalloc IntPtr[3];
        int count = 0;
        handles[count++] = nativeHandle;
        if (nativeFallback != IntPtr.Zero) handles[count++] = nativeFallback;
        if (cancellationToken.CanBeCanceled)
        {
            handles[count++] = cancellationToken.WaitHandle.SafeWaitHandle.DangerousGetHandle();
        }

        var result = WaitForMultipleObjects((uint)count, handles, false, timeoutMs);
        if (result == WAIT_OBJECT_0) return true;
        if (nativeFallback != IntPtr.Zero && result == 1) return true;
        return false;
    }

    public void SignalData()
    {
        if (_useWaitOnAddress)
        {
            WakeByAddressSingle(_dataSeqPtr);
            return;
        }

        if (s_diag) Interlocked.Increment(ref s_signalDataCalls);
        SetEvent(_dataEventNative);
        if (_dataFallbackNative != IntPtr.Zero) SetEvent(_dataFallbackNative);
    }

    public void SignalSpace()
    {
        if (_useWaitOnAddress)
        {
            WakeByAddressSingle(_spaceSeqPtr);
            return;
        }

        if (s_diag) Interlocked.Increment(ref s_signalSpaceCalls);
        SetEvent(_spaceEventNative);
        if (_spaceFallbackNative != IntPtr.Zero) SetEvent(_spaceFallbackNative);
    }

    public void SignalContig()
    {
        if (_useWaitOnAddress)
        {
            WakeByAddressSingle(_contigSeqPtr);
            return;
        }

        if (s_diag) Interlocked.Increment(ref s_signalContigCalls);
        SetEvent(_contigEventNative);
        if (_contigFallbackNative != IntPtr.Zero) SetEvent(_contigFallbackNative);
    }

    /// <summary>
    /// Atomic SignalData+Wait via <c>SignalObjectAndWait</c>. Saves
    /// one kernel transition per RT on the writer-loop's
    /// "signal-then-wait" hot path. <paramref name="localWaitHandleNative"/>
    /// must be a kernel handle (e.g. <see cref="EventWaitHandle"/>'s
    /// <see cref="SafeWaitHandle.DangerousGetHandle"/>) — Slim or
    /// pure-managed primitives won't work.
    /// </summary>
    public bool TrySignalDataAndWaitForLocal(IntPtr localWaitHandleNative, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (_useWaitOnAddress)
        {
            // No combined primitive for WaitOnAddress; fall back.
            WakeByAddressSingle(_dataSeqPtr);
            return false;
        }
        if (localWaitHandleNative == IntPtr.Zero) return false;
        if (cancellationToken.IsCancellationRequested) return false;

        if (s_diag) Interlocked.Increment(ref s_signalDataCalls);

        // Fallback signal must still fire to wake cross-session
        // listeners; SAW only handles the primary event.
        if (_dataFallbackNative != IntPtr.Zero) SetEvent(_dataFallbackNative);

        var ms = timeout.HasValue
            ? (uint)Math.Max(0, (int)timeout.Value.TotalMilliseconds)
            : INFINITE;
        var result = SignalObjectAndWait(_dataEventNative, localWaitHandleNative, ms, false);
        if (s_diag) Interlocked.Increment(ref s_waitDataCalls);
        return result == WAIT_OBJECT_0;
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

    // Fast-path Win32 synchronization P/Invokes. The .NET WaitHandle wrapper
    // allocates a WaitHandle[] per WaitAny call and goes through the managed
    // wait synchronization machinery. For the no-spin SHM hot path that's
    // ~2-5 µs of overhead per kernel wait that we don't need.
    //
    // We bypass it: cache SafeWaitHandle native handles in IntPtr fields and
    // call WaitForSingleObject / WaitForMultipleObjects directly. The handles
    // stay valid for the lifetime of WindowsRingSync (until Dispose).
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial uint WaitForMultipleObjects(uint nCount, IntPtr* lpHandles, [MarshalAs(UnmanagedType.Bool)] bool bWaitAll, uint dwMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEvent(IntPtr hEvent);

    /// <summary>
    /// SignalObjectAndWait: atomically signal one synchronization object
    /// and wait on another in a single syscall. Used by ping-pong hot
    /// paths to collapse the typical "Signal peer; Wait for response"
    /// 2-syscall sequence into 1. Saves ~3-5µs per RT on Windows.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint SignalObjectAndWait(IntPtr hObjectToSignal, IntPtr hObjectToWaitOn, uint dwMilliseconds, [MarshalAs(UnmanagedType.Bool)] bool bAlertable);

    private const uint INFINITE = 0xFFFFFFFF;
    private const uint WAIT_OBJECT_0 = 0;
}

#endif
