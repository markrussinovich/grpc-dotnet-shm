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
