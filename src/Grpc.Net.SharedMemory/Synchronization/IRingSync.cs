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

namespace Grpc.Net.SharedMemory.Synchronization;

/// <summary>
/// Abstraction for cross-process synchronization primitives.
/// On Windows, this uses named events.
/// On Linux, this uses futex.
/// </summary>
public interface IRingSync : IDisposable
{
    /// <summary>
    /// True when the underlying wake primitive is fast enough that the
    /// ring should NOT spin before blocking. The futex-backed Linux and
    /// WaitOnAddress-backed Windows implementations return false — they
    /// benefit from a few thousand spin iterations to absorb the
    /// 80us syscall round-trip on quick wakes. The eventfd-backed
    /// Linux implementation returns true — its blocking read is fast
    /// enough that spinning is pure CPU burn.
    /// </summary>
    bool SkipSpinWait => false;

    /// <summary>
    /// Waits for data to become available.
    /// </summary>
    /// <param name="expectedSeq">The expected sequence number to wait on.</param>
    /// <param name="timeout">Timeout for the wait (null for infinite).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if signaled, false if timeout.</returns>
    bool WaitForData(uint expectedSeq, TimeSpan? timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Waits for space to become available.
    /// </summary>
    /// <param name="expectedSeq">The expected sequence number to wait on.</param>
    /// <param name="timeout">Timeout for the wait (null for infinite).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if signaled, false if timeout.</returns>
    bool WaitForSpace(uint expectedSeq, TimeSpan? timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Waits for contiguity improvement.
    /// </summary>
    /// <param name="expectedSeq">The expected sequence number to wait on.</param>
    /// <param name="timeout">Timeout for the wait (null for infinite).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if signaled, false if timeout.</returns>
    bool WaitForContig(uint expectedSeq, TimeSpan? timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Signals that new data is available.
    /// </summary>
    void SignalData();

    /// <summary>
    /// Signals that space is available.
    /// </summary>
    void SignalSpace();

    /// <summary>
    /// Signals that contiguity improved.
    /// </summary>
    void SignalContig();

    /// <summary>
    /// Fan-out cascade primitive: writes a wake to OUR OWN side of the
    /// wake fd so any other same-side parker observes its edge. Called
    /// by <see cref="ShmRing"/>'s wait paths when a wake arrived but
    /// THIS waiter's condition turned out to be unmet (the kernel woke
    /// the wrong parker among 2+ parked on a shared eventfd — the Go
    /// side's Layer-3 fix in shm_dataseg_wake_linux.go). The default
    /// implementation is a no-op because the futex / Windows-events
    /// paths address per-condition seq values and don't share a single
    /// wake fd across conditions.
    /// </summary>
    void RewakeLocal() { }

    /// <summary>
    /// Atomically signal "new data is available" on the peer's wake
    /// primitive AND wait on a local kernel event for "more work" on
    /// this side. Used by the writer-loop hot path to collapse the
    /// "SignalData → kernel return → WaitForLocalWork → kernel block"
    /// 2-syscall sequence into a single kernel transition (Windows:
    /// <c>SignalObjectAndWait</c>; saves ~10 µs per RT in tight
    /// ping-pong workloads).
    ///
    /// Implementations that cannot offer the atomic combined primitive
    /// (e.g. Linux eventfd has no equivalent syscall) should fall back
    /// to the default implementation, which performs <see cref="SignalData"/>
    /// followed by an OS wait on <paramref name="localWaitHandleNative"/>.
    /// </summary>
    /// <param name="localWaitHandleNative">Native OS handle to wait on
    /// for in-process "more work" signaling. Must be a kernel event /
    /// kernel semaphore on Windows; on Linux this is the raw fd of an
    /// eventfd or pipe.</param>
    /// <param name="timeout">Wait timeout (null for infinite).</param>
    /// <param name="cancellationToken">Cancellation token. Implementations
    /// MAY check this only after the syscall returns.</param>
    /// <returns>True if the local handle was signaled; false if the
    /// wait timed out or was cancelled. Callers must double-check the
    /// shared-memory condition after a true return.</returns>
    bool TrySignalDataAndWaitForLocal(IntPtr localWaitHandleNative, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        // Default: emulate via separate signal then wait.
        SignalData();
        return false; // signal an "unsupported" return so the caller
                      // falls back to its own wait sequence. The caller
                      // will then do its own .Wait() on the local
                      // primitive. Avoids forcing every IRingSync to
                      // P/Invoke kernel-handle waits.
    }
}

/// <summary>
/// Factory for creating ring synchronization primitives.
/// </summary>
public static class RingSyncFactory
{
    /// <summary>
    /// Creates a ring sync primitive for the specified segment name.
    /// On Windows, creates named events.
    /// On Linux, returns a futex-based implementation.
    /// </summary>
    /// <param name="segmentName">The segment name for unique identification.</param>
    /// <param name="ringId">The ring identifier (e.g., "A" or "B").</param>
    /// <param name="isServer">True if this is the server (creates events), false for client (opens events).</param>
    public static IRingSync Create(string segmentName, string ringId, bool isServer)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateWindowsSync(segmentName, ringId, isServer);
        }
        else if (OperatingSystem.IsLinux())
        {
            return CreateLinuxSync();
        }
        else
        {
            throw new PlatformNotSupportedException("Shared memory transport requires Windows or Linux.");
        }
    }

    /// <summary>
    /// Creates a ring sync primitive with direct memory access for futex operations.
    /// </summary>
    /// <param name="segmentName">The segment name for unique identification.</param>
    /// <param name="ringId">The ring identifier (e.g., "A" or "B").</param>
    /// <param name="isServer">True if this is the server (creates events), false for client (opens events).</param>
    /// <param name="memoryManager">The memory manager providing direct access to the mapped region.</param>
    /// <param name="ringHeaderOffset">The offset to the ring header within the mapped region.</param>
    public static IRingSync Create(string segmentName, string ringId, bool isServer, MappedMemoryManager memoryManager, int ringHeaderOffset)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateWindowsSync(segmentName, ringId, isServer, memoryManager, ringHeaderOffset);
        }
        else if (OperatingSystem.IsLinux())
        {
            // Note: the eventfd wake primitive is constructed directly
            // by Segment.Create / Open (it requires a shared per-side
            // LinuxDataSegWaker that the factory cannot easily reach).
            // The factory only produces the futex-backed sync as a
            // fallback / default path.
            return CreateLinuxSyncWithPointers(memoryManager, ringHeaderOffset);
        }
        else
        {
            throw new PlatformNotSupportedException("Shared memory transport requires Windows or Linux.");
        }
    }

#if WINDOWS
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IRingSync CreateWindowsSync(string segmentName, string ringId, bool isServer)
    {
        return new WindowsRingSync(segmentName, ringId, isServer);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IRingSync CreateWindowsSync(string segmentName, string ringId, bool isServer, MappedMemoryManager memoryManager, int ringHeaderOffset)
    {
        return new WindowsRingSync(segmentName, ringId, isServer, memoryManager, ringHeaderOffset);
    }
#else
    private static IRingSync CreateWindowsSync(string segmentName, string ringId, bool isServer)
    {
        throw new PlatformNotSupportedException("Windows sync not available on this platform.");
    }

    private static IRingSync CreateWindowsSync(string segmentName, string ringId, bool isServer, MappedMemoryManager memoryManager, int ringHeaderOffset)
    {
        throw new PlatformNotSupportedException("Windows sync not available on this platform.");
    }
#endif

#if LINUX
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static IRingSync CreateLinuxSync()
    {
        return new LinuxRingSync();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static IRingSync CreateLinuxSyncWithPointers(MappedMemoryManager memoryManager, int ringHeaderOffset)
    {
        return new LinuxRingSync(memoryManager, ringHeaderOffset);
    }
#else
    private static IRingSync CreateLinuxSync()
    {
        throw new PlatformNotSupportedException("Linux sync not available on this platform.");
    }

    private static IRingSync CreateLinuxSyncWithPointers(MappedMemoryManager memoryManager, int ringHeaderOffset)
    {
        throw new PlatformNotSupportedException("Linux sync not available on this platform.");
    }
#endif
}
