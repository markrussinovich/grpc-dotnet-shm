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

using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Grpc.Net.SharedMemory.Synchronization;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Segment header structure (128 bytes) that identifies and configures a shared memory segment.
/// This layout matches grpc-go-shmem for interoperability.
///
/// Layout (grpc-go-shmem compatible):
/// - Offset 0x00: magic (8 bytes) - "GRPCSHM\0"
/// - Offset 0x08: version (uint32) - protocol version
/// - Offset 0x0C: flags (uint32) - reserved flags
/// - Offset 0x10: totalSize (uint64) - total segment size
/// - Offset 0x18: ringAOff (uint64) - offset to Ring A header
/// - Offset 0x20: ringACap (uint64) - ring A capacity (power of 2)
/// - Offset 0x28: ringBOff (uint64) - offset to Ring B header
/// - Offset 0x30: ringBCap (uint64) - ring B capacity (power of 2)
/// - Offset 0x38: serverPID (uint32) - server process ID
/// - Offset 0x3C: clientPID (uint32) - client process ID
/// - Offset 0x40: serverReady (uint32) - server ready flag (0->1)
/// - Offset 0x44: clientReady (uint32) - client mapped flag (0->1)
/// - Offset 0x48: closed (uint32) - closed flag (0 open, 1 closed)
/// - Offset 0x4C: pad (uint32) - padding
/// - Offset 0x50: maxStreams (uint32) - max concurrent streams
/// - Offset 0x54: openerWakeReady (uint32) - 1 if opener established
///   eventfd waker (matches Go SegmentHeader.openerWakeReady)
/// - Offset 0x58-0x7F: reserved (40 bytes) - padding to 128B
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
public struct SegmentHeader
{
    /// <summary>Magic bytes identifying this as a shared memory segment ("GRPCSHM\0").</summary>
    [FieldOffset(0x00)]
    public ulong MagicValue;

    /// <summary>Protocol version.</summary>
    [FieldOffset(0x08)]
    public uint Version;

    /// <summary>Reserved flags.</summary>
    [FieldOffset(0x0C)]
    public uint Flags;

    /// <summary>Total segment size in bytes.</summary>
    [FieldOffset(0x10)]
    public ulong TotalSize;

    /// <summary>Offset to Ring A (client→server) in the segment.</summary>
    [FieldOffset(0x18)]
    public ulong RingAOffset;

    /// <summary>Data area capacity for Ring A.</summary>
    [FieldOffset(0x20)]
    public ulong RingACapacity;

    /// <summary>Offset to Ring B (server→client) in the segment.</summary>
    [FieldOffset(0x28)]
    public ulong RingBOffset;

    /// <summary>Data area capacity for Ring B.</summary>
    [FieldOffset(0x30)]
    public ulong RingBCapacity;

    /// <summary>Server process ID.</summary>
    [FieldOffset(0x38)]
    public uint ServerPID;

    /// <summary>Client process ID.</summary>
    [FieldOffset(0x3C)]
    public uint ClientPID;

    /// <summary>Server ready flag.</summary>
    [FieldOffset(0x40)]
    public uint ServerReady;

    /// <summary>Client ready flag.</summary>
    [FieldOffset(0x44)]
    public uint ClientReady;

    /// <summary>Closed flag (0 = open, 1 = closed).</summary>
    [FieldOffset(0x48)]
    public uint Closed;

    /// <summary>Padding.</summary>
    [FieldOffset(0x4C)]
    public uint Pad;

    /// <summary>Maximum concurrent streams.</summary>
    [FieldOffset(0x50)]
    public uint MaxStreams;

    /// <summary>
    /// Opener wake-ready flag (Phase 2 cross-process eventfd waker).
    /// Set to 1 by the opener after it successfully obtains a
    /// <c>LinuxDataSegWaker</c> via SCM_RIGHTS / same-process stash;
    /// read by the creator after WaitForClient. When 0 the creator
    /// drops its own waker so both sides converge on the futex /
    /// Windows-events fallback, avoiding asymmetric-wake deadlock.
    /// Matches the layout of Go's SegmentHeader.openerWakeReady.
    /// </summary>
    [FieldOffset(0x54)]
    public uint OpenerWakeReady;

    // Offset 0x58-0x7F: Reserved (40 bytes) - implicitly zeroed
}

/// <summary>
/// Represents a shared memory segment containing two ring buffers for bidirectional communication.
/// Ring A is used for client→server data, Ring B for server→client data.
///
/// This implementation uses zero-copy memory access through <see cref="MappedMemoryManager"/>
/// to operate directly on the memory-mapped region without intermediate buffer copies.
/// </summary>
public sealed partial class Segment : IDisposable
{
    private const int ServerReadyOffset = 0x40;
    private const int ClientReadyOffset = 0x44;
#if LINUX
    private const int OpenerWakeReadyOffset = 0x54;
#endif

    private readonly MemoryMappedFile _mappedFile;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly MappedMemoryManager _memoryManager;
    private readonly Memory<byte> _memory;
    private readonly bool _isServer;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _headerWaitSync = new();
    private int _disposed;
    private int _headerWaitCount;
    private FileStream? _lockFileStream;
#if LINUX
    // Per-data-segment eventfd waker (Phase 2 cross-process). Owned
    // here so both rings share the same kernel fds and lifetime is
    // tied to Segment.Dispose. Null when the eventfd path is
    // disabled, unavailable, or dropped by finalizeDataSegWaker.
    private Synchronization.LinuxDataSegWaker? _eventfdWaker;
    // Cross-process FD-pass server (creator only). Null if the
    // eventfd path is off or the bind / chmod failed.
    private Synchronization.LinuxFdPassServer? _fdPassServer;
#endif

    /// <summary>Gets the segment name.</summary>
    public string Name { get; }

    /// <summary>Gets the path to the backing file.</summary>
    public string FilePath { get; }

    /// <summary>Gets the Ring A (client→server) ring buffer.</summary>
    public ShmRing RingA { get; }

    /// <summary>Gets the Ring B (server→client) ring buffer.</summary>
    public ShmRing RingB { get; }

    /// <summary>Gets the segment header.</summary>
    public SegmentHeader Header => GetHeader();

    /// <summary>Gets the total segment size in bytes.</summary>
    public long Size { get; }

    /// <summary>Gets the MappedMemoryManager for direct memory access (advanced usage).</summary>
    public MappedMemoryManager MemoryManager => _memoryManager;

    /// <summary>Gets the raw memory span for the entire segment.</summary>
    public Memory<byte> Memory => _memory;

    /// <summary>Gets whether this is the server side of the segment.</summary>
    public bool IsServer => _isServer;

    private Segment(
        string name,
        string filePath,
        MemoryMappedFile mappedFile,
        MemoryMappedViewAccessor accessor,
        MappedMemoryManager memoryManager,
        bool isServer,
        ulong ringAOffset,
        ulong ringACapacity,
        ulong ringBOffset,
        ulong ringBCapacity,
#if LINUX
        Synchronization.LinuxDataSegWaker? eventfdWaker = null,
        Synchronization.LinuxFdPassServer? fdPassServer = null)
#else
        object? eventfdWaker = null,
        object? fdPassServer = null)
#endif
    {
        Name = name;
        FilePath = filePath;
        _mappedFile = mappedFile;
        _accessor = accessor;
        _memoryManager = memoryManager;
        _memory = memoryManager.Memory;
        _isServer = isServer;
        Size = memoryManager.Length;
#if LINUX
        _eventfdWaker = eventfdWaker;
        _fdPassServer = fdPassServer;
#endif

        // Create ring sync primitives
        IRingSync? syncA = null;
        IRingSync? syncB = null;

        try
        {
#if LINUX
            if (eventfdWaker != null)
            {
                // Both rings on this side share the same waker; a
                // wake from the peer just means "check your ring
                // state". finalizeDataSegWaker may later replace these
                // with futex if the peer doesn't have eventfd.
                syncA = new Synchronization.LinuxEventfdRingSync(eventfdWaker);
                syncB = new Synchronization.LinuxEventfdRingSync(eventfdWaker);
            }
            else
#endif
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                syncA = RingSyncFactory.Create(name, "A", isServer, memoryManager, (int)ringAOffset);
                syncB = RingSyncFactory.Create(name, "B", isServer, memoryManager, (int)ringBOffset);
            }
        }
        catch
        {
            // Sync primitives are optional — fall back to polling.
            // Dispose any partially created sync to avoid OS handle leak.
            syncA?.Dispose();
            syncA = null;
            syncB?.Dispose();
            syncB = null;
        }

        // Create ring buffers operating directly on mapped memory (zero-copy)
        // isOwner=isServer so only the server sets Closed flag in shared memory
        RingA = new ShmRing(_memory, (int)ringAOffset, ringACapacity, syncA, isOwner: isServer);
        RingB = new ShmRing(_memory, (int)ringBOffset, ringBCapacity, syncB, isOwner: isServer);
    }

    /// <summary>
    /// Creates a new shared memory segment (server-side).
    /// Uses file-backed shared memory at /dev/shm/grpc_shm_{name} on Linux (preferred)
    /// or %TEMP%\grpc_shm_{name} as fallback, for grpc-go-shmem compatibility.
    /// </summary>
    /// <param name="name">The segment name for identification.</param>
    /// <param name="ringCapacity">The capacity for each ring buffer (must be power of 2).</param>
    /// <param name="maxStreams">Maximum concurrent streams.</param>
    /// <returns>The created segment.</returns>
    public static Segment Create(string name, ulong ringCapacity = 64 * 1024 * 1024, uint maxStreams = 100)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Segment name cannot be null or empty", nameof(name));
        }

        if (ringCapacity == 0 || (ringCapacity & (ringCapacity - 1)) != 0)
        {
            throw new ArgumentException("Ring capacity must be a power of 2", nameof(ringCapacity));
        }

        // Calculate total segment size
        // Layout: [SegmentHeader (128)] [RingA Header (64)] [RingA Data] [RingB Header (64)] [RingB Data]
        var ringAOffset = (ulong)ShmConstants.SegmentHeaderSize;
        var ringBOffset = ringAOffset + (ulong)ShmConstants.RingHeaderSize + ringCapacity;
        var totalSize = ringBOffset + (ulong)ShmConstants.RingHeaderSize + ringCapacity;

        // Use file-backed shared memory like grpc-go-shmem: %TEMP%\grpc_shm_{name}
        var filePath = GenerateSegmentPath(name);

        // Create the backing file if it doesn't exist, or fail if it does (like Go's O_EXCL)
        if (File.Exists(filePath))
        {
            throw new IOException($"Segment '{name}' already exists at {filePath}");
        }

        // Create the backing file
        using (var fs = new FileStream(filePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            fs.SetLength((long)totalSize);
        }

        // Create memory-mapped file from the backing file
        var backingFile = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        MemoryMappedFile mappedFile;
        MemoryMappedViewAccessor accessor;
        MappedMemoryManager memoryManager;
        try
        {
            mappedFile = MemoryMappedFile.CreateFromFile(
                backingFile,
                mapName: null,
                (long)totalSize,
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None,
                leaveOpen: false);
        }
        catch
        {
            backingFile.Dispose();
            throw;
        }

        try
        {
            accessor = mappedFile.CreateViewAccessor(0, (long)totalSize, MemoryMappedFileAccess.ReadWrite);
        }
        catch
        {
            mappedFile.Dispose();
            throw;
        }

        try
        {
            memoryManager = new MappedMemoryManager(accessor);
        }
        catch
        {
            accessor.Dispose();
            mappedFile.Dispose();
            throw;
        }
        var memory = memoryManager.Memory;

        // Initialize segment header with grpc-go-shmem compatible magic
        var header = new SegmentHeader
        {
            MagicValue = BitConverter.ToUInt64(ShmConstants.SegmentMagicBytes),
            Version = ShmConstants.ProtocolVersion,
            Flags = 0,
            TotalSize = totalSize,
            RingAOffset = ringAOffset,
            RingACapacity = ringCapacity,
            RingBOffset = ringBOffset,
            RingBCapacity = ringCapacity,
            ServerPID = (uint)Environment.ProcessId,
            ClientPID = 0,
            ServerReady = 1,  // Server is ready when creating
            ClientReady = 0,
            Closed = 0,
            Pad = 0,
            MaxStreams = maxStreams
        };

        // Write header directly to mapped memory (zero-copy)
        WriteSegmentHeader(memory.Span, header);

        // Initialize ring headers directly in mapped memory
        InitializeRingHeader(memory.Span, (int)ringAOffset, ringCapacity);
        InitializeRingHeader(memory.Span, (int)ringBOffset, ringCapacity);

        // Flush to ensure visibility to other processes
        accessor.Flush();

#if LINUX
        // Phase 2 eventfd wake: when the env var is set, allocate a
        // per-segment pair of eventfds. The creator keeps one waker;
        // the same-process opener will claim the other via
        // EventfdRegistry.TryClaimOpener, and cross-process openers
        // will receive duplicates over SCM_RIGHTS via the per-segment
        // Unix-domain socket served by LinuxFdPassServer.
        Synchronization.LinuxDataSegWaker? creatorWaker = null;
        Synchronization.LinuxFdPassServer? fdPassServer = null;
        if (EventfdWakeEnabled)
        {
            try
            {
                var (waker, stash) = Synchronization.EventfdRegistry.AllocateAndStash(name);
                creatorWaker = waker;
                fdPassServer = Synchronization.LinuxFdPassServer.Start(filePath, stash);
                if (fdPassServer == null)
                {
                    // Cross-process clients can't reach us; keep the
                    // waker for same-process consumers via the stash.
                    // (Acceptable: same-process tests still get the
                    // perf win; cross-process opens fall back to
                    // futex via OpenerWakeReady=false.)
                }
            }
            catch
            {
                // Allocation failure (kernel ENFILE etc.): drop back
                // to futex by leaving the stash empty.
                if (creatorWaker != null)
                {
                    Synchronization.EventfdRegistry.Drop(name);
                    creatorWaker.Dispose();
                    creatorWaker = null;
                }
            }
        }

        return new Segment(name, filePath, mappedFile, accessor, memoryManager, true,
            ringAOffset, ringCapacity, ringBOffset, ringCapacity,
            eventfdWaker: creatorWaker, fdPassServer: fdPassServer);
#else
        return new Segment(name, filePath, mappedFile, accessor, memoryManager, true,
            ringAOffset, ringCapacity, ringBOffset, ringCapacity);
#endif
    }

    /// <summary>
    /// Opens an existing shared memory segment (client-side).
    /// Opens file-backed shared memory at /dev/shm/grpc_shm_{name} on Linux (preferred)
    /// or %TEMP%\grpc_shm_{name} as fallback, for grpc-go-shmem compatibility.
    /// </summary>
    /// <param name="name">The segment name.</param>
    /// <returns>The opened segment.</returns>
    public static Segment Open(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Segment name cannot be null or empty", nameof(name));
        }

        // Try to find the segment in either location (like grpc-go-shmem does)
        var filePath = FindExistingSegmentPath(name)
            ?? throw new FileNotFoundException($"Segment '{name}' not found at /dev/shm/grpc_shm_{name} or /tmp/grpc_shm_{name}");

        // Open the backing file. Treat "vanished between probe and open" as
        // FileNotFoundException so callers polling for server readiness can
        // retry uniformly (they already retry that exception).
        FileStream backingFile;
        try
        {
            backingFile = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new FileNotFoundException($"Segment '{name}' not found at '{filePath}' (directory missing)", filePath, ex);
        }

        long backingFileLength;
        try
        {
            // Reading Length post-construction has been observed to throw
            // ObjectDisposedException when the file is unlinked or replaced
            // by a concurrent server-side dispose between FindExistingSegmentPath
            // and here. Convert to FileNotFoundException so the test/client
            // retry loop sees a familiar transient error.
            try
            {
                backingFileLength = backingFile.Length;
            }
            catch (ObjectDisposedException ex)
            {
                throw new FileNotFoundException(
                    $"Segment '{name}' became unavailable during open (likely concurrent server tear-down)",
                    filePath, ex);
            }

            // Validate minimum size
            if (backingFileLength < ShmConstants.SegmentHeaderSize)
            {
                throw new InvalidDataException($"Segment file too small: {backingFileLength} bytes");
            }
        }
        catch
        {
            backingFile.Dispose();
            throw;
        }

        // Create memory-mapped file from the backing file (temporarily for header read)
        MemoryMappedFile mappedFile;
        try
        {
            mappedFile = MemoryMappedFile.CreateFromFile(
                backingFile,
                mapName: null,
                backingFileLength,
                MemoryMappedFileAccess.ReadWrite,
                HandleInheritability.None,
                leaveOpen: false);
        }
        catch
        {
            backingFile.Dispose();
            throw;
        }

        // Read and validate header
        SegmentHeader header;
        try
        {
            using var headerAccessor = mappedFile.CreateViewAccessor(0, ShmConstants.SegmentHeaderSize, MemoryMappedFileAccess.Read);
            var headerBuffer = new byte[ShmConstants.SegmentHeaderSize];
            headerAccessor.ReadArray(0, headerBuffer, 0, headerBuffer.Length);
            header = ReadSegmentHeader(headerBuffer);
        }
        catch
        {
            mappedFile.Dispose();
            throw;
        }

        // Validate magic - check for grpc-go-shmem compatible "GRPCSHM\0"
        var expectedMagic = BitConverter.ToUInt64(ShmConstants.SegmentMagicBytes);
        if (header.MagicValue != expectedMagic)
        {
            mappedFile.Dispose();
            // Magic == 0 is the "file truncated to size but header not yet
            // written" state inside <see cref="Create"/>: SetLength happens
            // before WriteSegmentHeader, so a probe loop racing a starting
            // server can observe an all-zero header. Surface this as
            // FileNotFoundException so readiness probes (which already retry
            // that exception) keep polling instead of failing the test.
            if (header.MagicValue == 0)
            {
                throw new FileNotFoundException(
                    $"Segment '{name}' exists but has not been initialised yet (likely starting server)",
                    filePath);
            }
            throw new InvalidDataException($"Invalid segment magic: expected 'GRPCSHM\\0', got 0x{header.MagicValue:X16}");
        }

        if (header.Version != ShmConstants.ProtocolVersion)
        {
            mappedFile.Dispose();
            throw new InvalidDataException($"Unsupported protocol version: expected {ShmConstants.ProtocolVersion}, got {header.Version}");
        }

        // Use TotalSize from header if available, otherwise calculate
        var totalSize = header.TotalSize > 0
            ? header.TotalSize
            : header.RingBOffset + (ulong)ShmConstants.RingHeaderSize + header.RingBCapacity;

        MemoryMappedViewAccessor accessor;
        MappedMemoryManager memoryManager;
        try
        {
            accessor = mappedFile.CreateViewAccessor(0, (long)totalSize, MemoryMappedFileAccess.ReadWrite);
        }
        catch
        {
            mappedFile.Dispose();
            throw;
        }

        try
        {
            memoryManager = new MappedMemoryManager(accessor);
        }
        catch
        {
            accessor.Dispose();
            mappedFile.Dispose();
            throw;
        }

        return CreateOpenedSegment(name, filePath, mappedFile, accessor, memoryManager, header);
    }

#if LINUX
    private static Segment CreateOpenedSegment(
        string name,
        string filePath,
        MemoryMappedFile mappedFile,
        MemoryMappedViewAccessor accessor,
        MappedMemoryManager memoryManager,
        SegmentHeader header)
    {
        // Phase 2 eventfd wake: try the same-process stash first
        // (zero-syscall fast path); on miss, attempt cross-process
        // SCM_RIGHTS handoff. On any failure leave the waker null so
        // the rings fall back to futex.
        Synchronization.LinuxDataSegWaker? openerWaker = null;
        if (EventfdWakeEnabled)
        {
            openerWaker = Synchronization.EventfdRegistry.TryClaimOpener(name);
            if (openerWaker == null)
            {
                var fds = Synchronization.LinuxFdPassClient.TryReceive(filePath);
                if (fds != null && fds.Length == 2)
                {
                    // fds[0] = creator's recv (our peer); fds[1] = our recv.
                    openerWaker = new Synchronization.LinuxDataSegWaker(
                        myReadFd: fds[1], peerReadFd: fds[0], ownsFds: true);
                }
            }
        }

        // Publish the opener's wake status BEFORE the rings go live.
        // The creator reads this in FinalizeDataSegWaker after
        // WaitForClient and drops its own waker if we couldn't
        // establish one — ensures both sides converge on the same
        // wake primitive (avoids asymmetric-wake deadlock).
        WriteOpenerWakeReady(memoryManager, openerWaker != null);

        return new Segment(name, filePath, mappedFile, accessor, memoryManager, false,
            header.RingAOffset, header.RingACapacity, header.RingBOffset, header.RingBCapacity,
            eventfdWaker: openerWaker, fdPassServer: null);
    }
#else
    private static Segment CreateOpenedSegment(
        string name,
        string filePath,
        MemoryMappedFile mappedFile,
        MemoryMappedViewAccessor accessor,
        MappedMemoryManager memoryManager,
        SegmentHeader header)
    {
        return new Segment(name, filePath, mappedFile, accessor, memoryManager, false,
            header.RingAOffset, header.RingACapacity, header.RingBOffset, header.RingBCapacity);
    }
#endif

    /// <summary>
    /// Generates the path to the shared memory backing file.
    /// Uses /dev/shm/grpc_shm_{name} on Linux (preferred) or %TEMP%\grpc_shm_{name} as fallback.
    /// This matches the grpc-go-shmem convention for cross-language interoperability.
    /// </summary>
    private static string GenerateSegmentPath(string name)
    {
        // On Linux, prefer /dev/shm for true shared memory (matches grpc-go-shmem)
        if (OperatingSystem.IsLinux())
        {
            const string devShm = "/dev/shm";
            if (Directory.Exists(devShm))
            {
                return Path.Combine(devShm, $"grpc_shm_{name}");
            }
        }
        
        // Fallback to temp directory (Windows or Linux without /dev/shm)
        return Path.Combine(Path.GetTempPath(), $"grpc_shm_{name}");
    }

    /// <summary>
    /// Finds an existing segment by checking both /dev/shm and /tmp.
    /// This matches grpc-go-shmem's behavior of trying both locations.
    /// </summary>
    private static string? FindExistingSegmentPath(string name)
    {
        if (OperatingSystem.IsLinux())
        {
            // Try /dev/shm first (preferred)
            var devShmPath = Path.Combine("/dev/shm", $"grpc_shm_{name}");
            if (File.Exists(devShmPath))
            {
                return devShmPath;
            }
        }

        // Try temp directory
        var tempPath = Path.Combine(Path.GetTempPath(), $"grpc_shm_{name}");
        if (File.Exists(tempPath))
        {
            return tempPath;
        }

        return null;
    }

    /// <summary>
    /// Flushes the memory-mapped file to ensure visibility to other processes.
    /// With zero-copy access, this just triggers the OS flush mechanism.
    /// </summary>
    public void Flush()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _accessor.Flush();
    }

    /// <summary>
    /// Refreshes is no longer needed with zero-copy access.
    /// The memory is always directly accessing the mapped region.
    /// This method is kept for API compatibility but does nothing.
    /// </summary>
    [Obsolete("Refresh is not needed with zero-copy memory access. Memory operations work directly on the mapped region.")]
    public void Refresh()
    {
        // No-op: With zero-copy access, we're always reading from the mapped region
    }

    private SegmentHeader GetHeader()
    {
        return ReadSegmentHeader(_memory.Span);
    }

    // ===== Phase 2 eventfd wake (Linux only) =====

    /// <summary>
    /// Process-wide gate for the eventfd wake path. Honoured at segment
    /// creation / open time: when set, the creator allocates a pair of
    /// eventfds and serves them to cross-process openers via
    /// <c>SCM_RIGHTS</c> over a per-segment Unix domain socket;
    /// same-process openers claim the peer fd from
    /// <see cref="Synchronization.EventfdRegistry"/>.
    /// </summary>
    internal static bool EventfdWakeEnabled =>
        OperatingSystem.IsLinux()
        && string.Equals(Environment.GetEnvironmentVariable("SHM_EVENTFD_WAKE"), "1", StringComparison.Ordinal);

#if LINUX
    /// <summary>
    /// Persists the opener-side wake-readiness flag in the segment
    /// header at offset <c>0x54</c>. Invoked by the opener BEFORE
    /// signaling <see cref="SetClientReady"/> so the creator observes
    /// a stable value when <c>WaitForClient</c> returns.
    /// </summary>
    /// <remarks>
    /// Memory ordering: the kernel <c>SetEvent</c> / <c>Write</c>(eventfd)
    /// call inside <see cref="SetClientReady"/> provides a full release
    /// barrier on the writer side, and the matching <c>WaitForEvent</c>
    /// / <c>Read</c>(eventfd) on the reader side provides an acquire
    /// barrier. <see cref="Thread.MemoryBarrier"/> is added defensively
    /// so the contract is explicit even if a future change drops the
    /// kernel hop (e.g. an in-process fast path).
    /// </remarks>
    private static void WriteOpenerWakeReady(MappedMemoryManager mem, bool ready)
    {
        var v = ready ? 1u : 0u;
        BinaryPrimitives.WriteUInt32LittleEndian(
            mem.Memory.Span[OpenerWakeReadyOffset..(OpenerWakeReadyOffset + sizeof(uint))], v);
        Thread.MemoryBarrier();
    }

    /// <summary>
    /// Reads the opener-side wake-readiness flag from the segment
    /// header. Used by the creator's <see cref="FinalizeDataSegWaker"/>
    /// after WaitForClient.
    /// </summary>
    /// <remarks>
    /// See <see cref="WriteOpenerWakeReady"/> for the
    /// release-on-write / acquire-on-read pairing.
    /// </remarks>
    private uint ReadOpenerWakeReady()
    {
        Thread.MemoryBarrier();
        return BinaryPrimitives.ReadUInt32LittleEndian(
            _memory.Span[OpenerWakeReadyOffset..(OpenerWakeReadyOffset + sizeof(uint))]);
    }

    /// <summary>
    /// Resolves the eventfd-waker peer state. Caller (dialer after
    /// connect-response, listener after Accept) invokes this AFTER the
    /// opposite side's ready signal so the value is stable. When the
    /// creator holds a waker but the opener didn't establish one
    /// (<c>OpenerWakeReady == 0</c>) the creator drops its waker so
    /// both sides converge on the futex wake path — prevents the
    /// asymmetric-wake deadlock where one side parks on read(efd)
    /// while the other only signals via futex.
    /// </summary>
    public void FinalizeDataSegWaker()
    {
        if (!_isServer) return;            // opener already published before signalling
        if (_eventfdWaker == null) return;   // we never had a waker; nothing to drop

        if (ReadOpenerWakeReady() != 0)
        {
            return; // opener has eventfd too — keep ours
        }

        DropCreatorWaker();
    }

    private void DropCreatorWaker()
    {
        // Pull the stash entry first so a stragger same-process Open
        // cannot claim a waker we are about to invalidate.
        Synchronization.EventfdRegistry.Drop(Name);

        var server = _fdPassServer;
        _fdPassServer = null;
        server?.Dispose();

        var waker = _eventfdWaker;
        _eventfdWaker = null;
        waker?.Dispose();

        // Reinstall a futex-backed sync so the rings keep working.
        RingA.ReplaceSync(RingSyncFactory.Create(Name, "A", _isServer, _memoryManager, (int)RingA.HeaderOffset));
        RingB.ReplaceSync(RingSyncFactory.Create(Name, "B", _isServer, _memoryManager, (int)RingB.HeaderOffset));
    }
#else
    /// <summary>No-op on platforms without the eventfd waker.</summary>
    public void FinalizeDataSegWaker() { }
#endif

    private static void WriteSegmentHeader(Span<byte> buffer, SegmentHeader header)
    {
        var span = buffer.Slice(0, ShmConstants.SegmentHeaderSize);
        span.Clear(); // Zero all bytes first

        // Write grpc-go-shmem compatible header (128 bytes)
        BinaryPrimitives.WriteUInt64LittleEndian(span[0x00..0x08], header.MagicValue);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x08..0x0C], header.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x0C..0x10], header.Flags);
        BinaryPrimitives.WriteUInt64LittleEndian(span[0x10..0x18], header.TotalSize);
        BinaryPrimitives.WriteUInt64LittleEndian(span[0x18..0x20], header.RingAOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(span[0x20..0x28], header.RingACapacity);
        BinaryPrimitives.WriteUInt64LittleEndian(span[0x28..0x30], header.RingBOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(span[0x30..0x38], header.RingBCapacity);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x38..0x3C], header.ServerPID);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x3C..0x40], header.ClientPID);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x40..0x44], header.ServerReady);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x44..0x48], header.ClientReady);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x48..0x4C], header.Closed);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x4C..0x50], header.Pad);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x50..0x54], header.MaxStreams);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x54..0x58], header.OpenerWakeReady);
    }

    private static SegmentHeader ReadSegmentHeader(Span<byte> buffer)
    {
        var span = buffer.Slice(0, ShmConstants.SegmentHeaderSize);
        return new SegmentHeader
        {
            MagicValue = BinaryPrimitives.ReadUInt64LittleEndian(span[0x00..0x08]),
            Version = BinaryPrimitives.ReadUInt32LittleEndian(span[0x08..0x0C]),
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(span[0x0C..0x10]),
            TotalSize = BinaryPrimitives.ReadUInt64LittleEndian(span[0x10..0x18]),
            RingAOffset = BinaryPrimitives.ReadUInt64LittleEndian(span[0x18..0x20]),
            RingACapacity = BinaryPrimitives.ReadUInt64LittleEndian(span[0x20..0x28]),
            RingBOffset = BinaryPrimitives.ReadUInt64LittleEndian(span[0x28..0x30]),
            RingBCapacity = BinaryPrimitives.ReadUInt64LittleEndian(span[0x30..0x38]),
            ServerPID = BinaryPrimitives.ReadUInt32LittleEndian(span[0x38..0x3C]),
            ClientPID = BinaryPrimitives.ReadUInt32LittleEndian(span[0x3C..0x40]),
            ServerReady = BinaryPrimitives.ReadUInt32LittleEndian(span[0x40..0x44]),
            ClientReady = BinaryPrimitives.ReadUInt32LittleEndian(span[0x44..0x48]),
            Closed = BinaryPrimitives.ReadUInt32LittleEndian(span[0x48..0x4C]),
            Pad = BinaryPrimitives.ReadUInt32LittleEndian(span[0x4C..0x50]),
            MaxStreams = BinaryPrimitives.ReadUInt32LittleEndian(span[0x50..0x54]),
            OpenerWakeReady = BinaryPrimitives.ReadUInt32LittleEndian(span[0x54..0x58])
        };
    }

    private static void InitializeRingHeader(Span<byte> buffer, int offset, ulong capacity)
    {
        var span = buffer.Slice(offset, ShmConstants.RingHeaderSize);
        span.Clear(); // Zero all fields
        // Write capacity at offset 0 (grpc-go-shmem layout: capacity is first field)
        BinaryPrimitives.WriteUInt64LittleEndian(span[0..8], capacity);
    }

    /// <summary>
    /// Sets the ServerReady flag and signals waiting clients.
    /// </summary>
    public void SetServerReady(bool ready)
    {
        var span = _memory.Span;
        var value = ready ? 1u : 0u;
        BinaryPrimitives.WriteUInt32LittleEndian(span[ServerReadyOffset..(ServerReadyOffset + sizeof(uint))], value);
        Flush();
        if (ready)
        {
            SignalHeaderFlagWaiters(ServerReadyOffset);
        }
    }

    /// <summary>
    /// Sets the ClientReady flag and signals waiting servers.
    /// </summary>
    public void SetClientReady(bool ready)
    {
        var span = _memory.Span;
        var value = ready ? 1u : 0u;
        BinaryPrimitives.WriteUInt32LittleEndian(span[ClientReadyOffset..(ClientReadyOffset + sizeof(uint))], value);
        Flush();
        if (ready)
        {
            SignalHeaderFlagWaiters(ClientReadyOffset);
            // Also signal the named event used by grpc-go-shmem's WaitForClient.
            // Go waits on WaitForSingleObject(named event), which is NOT woken by
            // WakeByAddressSingle. Both mechanisms are needed for cross-language compat.
            SignalClientReadyNamedEvent(Name);
        }
    }

    /// <summary>
    /// Best-effort signal of the Windows named event that Go's WaitForClient
    /// blocks on. The real readiness flag is already persisted in shared
    /// memory; this is only an optimization to wake the Go side faster.
    /// All failures are silently swallowed — matching WindowsRingSync's
    /// treatment of event access problems as non-fatal.
    /// Event name format: Local\grpc_shm_{segmentName}_clientReady
    /// </summary>
    private static void SignalClientReadyNamedEvent(string segmentName)
    {
#if WINDOWS
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var eventName = $"Local\\grpc_shm_{segmentName}_clientReady";
            using var evt = EventWaitHandle.OpenExisting(eventName);
            evt.Set();
        }
        catch
        {
            // Best-effort: named event may not exist (Go server not started,
            // or .NET-to-.NET where this event isn't used), or may be
            // inaccessible (session/ACL mismatch). The shared memory
            // clientReady flag is the authoritative signal; this wake is
            // purely an optimization.
        }
#endif
    }

    /// <summary>
    /// Checks if the server is ready.
    /// </summary>
    public bool IsServerReady()
    {
        var span = _memory.Span;
        return BinaryPrimitives.ReadUInt32LittleEndian(span[ServerReadyOffset..(ServerReadyOffset + sizeof(uint))]) != 0;
    }

    /// <summary>
    /// Checks if the client is ready.
    /// </summary>
    public bool IsClientReady()
    {
        var span = _memory.Span;
        return BinaryPrimitives.ReadUInt32LittleEndian(span[ClientReadyOffset..(ClientReadyOffset + sizeof(uint))]) != 0;
    }

    /// <summary>
    /// Waits for the server to be ready.
    /// </summary>
    public async Task WaitForServerAsync(CancellationToken cancellationToken = default)
    {
        await WaitForHeaderFlagAsync(ServerReadyOffset, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for the client to be ready.
    /// </summary>
    public async Task WaitForClientAsync(CancellationToken cancellationToken = default)
    {
        await WaitForHeaderFlagAsync(ClientReadyOffset, cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitForHeaderFlagAsync(int offset, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (IsHeaderFlagSet(offset))
        {
            return;
        }

        lock (_headerWaitSync)
        {
            ThrowIfDisposed();
            _headerWaitCount++;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                await Task.Run(() => WaitHeaderFlagWindows(offset, linkedCts.Token), linkedCts.Token).ConfigureAwait(false);
                return;
            }

            if (OperatingSystem.IsLinux())
            {
                await Task.Run(() => WaitHeaderFlagLinux(offset, linkedCts.Token), linkedCts.Token).ConfigureAwait(false);
                return;
            }

            await WaitHeaderFlagPollingAsync(offset, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            ThrowIfDisposed();
            throw;
        }
        finally
        {
            lock (_headerWaitSync)
            {
                _headerWaitCount--;
                if (_headerWaitCount == 0)
                {
                    Monitor.PulseAll(_headerWaitSync);
                }
            }
        }
    }

    private bool IsHeaderFlagSet(int offset)
    {
        var span = _memory.Span;
        return BinaryPrimitives.ReadUInt32LittleEndian(span[offset..(offset + sizeof(uint))]) != 0;
    }

    private async Task WaitHeaderFlagPollingAsync(int offset, CancellationToken cancellationToken)
    {
        while (!IsHeaderFlagSet(offset))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }

#pragma warning disable CA1822 // Instance member accessed inside platform-conditional #if block
    private unsafe void WaitHeaderFlagWindows(int offset, CancellationToken cancellationToken)
    {
#if WINDOWS
        var flagPtr = _memoryManager.GetUInt32Pointer(offset);
        while (Volatile.Read(ref *flagPtr) == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compare = 0u;
            WaitOnAddress(flagPtr, &compare, (IntPtr)sizeof(uint), 100);
        }
#else
        throw new PlatformNotSupportedException("Windows readiness wait is not available on this platform.");
#endif
    }
#pragma warning restore CA1822

#pragma warning disable CA1822
    private unsafe void WaitHeaderFlagLinux(int offset, CancellationToken cancellationToken)
    {
#if LINUX
        var flagPtr = _memoryManager.GetUInt32Pointer(offset);
        while (Volatile.Read(ref *flagPtr) == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FutexWait(flagPtr, 0, 100);
        }
#else
        throw new PlatformNotSupportedException("Linux readiness wait is not available on this platform.");
#endif
    }
#pragma warning restore CA1822

    private unsafe void SignalHeaderFlagWaiters(int offset)
    {
        var flagPtr = _memoryManager.GetUInt32Pointer(offset);
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            WakeByAddressSingle(flagPtr);
            return;
        }
#endif

#if LINUX
        if (OperatingSystem.IsLinux())
        {
            FutexWake(flagPtr);
        }
#endif
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private void CancelAndWaitForHeaderWaiters()
    {
        _disposeCts.Cancel();

        lock (_headerWaitSync)
        {
            while (_headerWaitCount != 0)
            {
                Monitor.Wait(_headerWaitSync);
            }
        }
    }

#if WINDOWS
    [LibraryImport("api-ms-win-core-synch-l1-2-0.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool WaitOnAddress(void* address, void* compareAddress, IntPtr addressSize, uint milliseconds);

    [LibraryImport("api-ms-win-core-synch-l1-2-0.dll")]
    private static unsafe partial void WakeByAddressSingle(void* address);
#endif

#if LINUX
    private const int FutexWaitOp = 0;
    private const int FutexWakeOp = 1;
    private const int SysFutex = 202;

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long tv_sec;
        public long tv_nsec;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial long syscall(long number, uint* uaddr, int futex_op, uint val, Timespec* timeout, uint* uaddr2, uint val3);

    private static unsafe void FutexWait(uint* address, uint expected, int timeoutMs)
    {
        Timespec ts;
        if (timeoutMs <= 0)
        {
            ts = default;
        }
        else
        {
            ts = new Timespec
            {
                tv_sec = timeoutMs / 1000,
                tv_nsec = (timeoutMs % 1000) * 1_000_000
            };
        }

        syscall(SysFutex, address, FutexWaitOp, expected, timeoutMs <= 0 ? null : &ts, null, 0);
    }

    private static unsafe void FutexWake(uint* address)
    {
        syscall(SysFutex, address, FutexWakeOp, 1, null, null, 0);
    }
#endif

    /// <summary>
    /// Tries to delete a segment file if it exists.
    /// </summary>
    public static bool TryRemoveSegment(string name)
    {
        var filePath = GenerateSegmentPath(name);
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                return true;
            }
        }
        catch
        {
            // Ignore errors
        }
        return false;
    }

    /// <summary>
    /// Removes all segment files whose names start with the given prefix.
    /// Useful for cleaning up stale segments left by crashed processes.
    /// </summary>
    public static int TryRemoveSegmentsByPrefix(string namePrefix)
    {
        var filePrefix = $"grpc_shm_{namePrefix}";
        var count = 0;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                const string devShm = "/dev/shm";
                if (Directory.Exists(devShm))
                {
                    foreach (var file in Directory.EnumerateFiles(devShm, filePrefix + "*"))
                    {
                        try { File.Delete(file); count++; } catch { }
                    }
                }
            }
            var tempDir = Path.GetTempPath();
            foreach (var file in Directory.EnumerateFiles(tempDir, filePrefix + "*"))
            {
                try { File.Delete(file); count++; } catch { }
            }
        }
        catch { }
        return count;
    }

    /// <summary>
    /// Checks if a segment exists (checks both /dev/shm and /tmp on Linux).
    /// </summary>
    public static bool Exists(string name)
    {
        return FindExistingSegmentPath(name) != null;
    }

    /// <summary>
    /// Creates a control segment with minimal ring sizes for connection establishment.
    /// </summary>
    public static Segment CreateControlSegment(string baseName)
    {
        var ctlName = baseName + ShmConstants.ControlSegmentSuffix;

        // Remove stale control segment from a previous crashed instance.
        //
        // On Windows, File.Delete fails if the file is memory-mapped by a live
        // process, so TryRemoveSegment is safe — only orphaned files are removed.
        //
        // On Linux, unlink succeeds even on mapped files. To prevent endpoint
        // steal / split-brain we use an advisory lock file ({ctlName}.lock).
        // The protocol is lock-first:
        //   1. Atomically acquire exclusive ownership via the .lock file.
        //      If another server holds it → throw "endpoint in use".
        //   2. Only after the lock is held, remove any stale ctl file.
        //   3. Create the new ctl segment.
        // This eliminates the TOCTOU window: no server can see a ctl file
        // without a lock because step 1 always precedes step 2-3.
        FileStream? lockStream = null;

        if (OperatingSystem.IsWindows())
        {
            TryRemoveSegment(ctlName);
        }
        else
        {
            // Step 1: Acquire exclusive lock. FileShare.None makes the open
            // fail with IOException if any other process holds the file.
            // FileMode.OpenOrCreate ensures the lock file is created atomically
            // if it doesn't exist yet.
            var lockPath = GenerateSegmentPath(ctlName) + ".lock";
            try
            {
                lockStream = new FileStream(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                // Lock held by another process → live server.
                throw new IOException(
                    $"SHM endpoint '{baseName}' is held by a live server. " +
                    $"Cannot create a new control segment while another instance is active.");
            }

            // Step 2: We hold the lock. Any existing ctl file is stale (the
            // previous holder either crashed or exited without cleanup).
            TryRemoveSegment(ctlName);
        }

        // Step 3: Create the new control segment.
        Segment segment;
        try
        {
            segment = Create(ctlName, ShmConstants.MinRingCapacity, 0);
        }
        catch
        {
            // Delete before close to avoid lock file race (see Dispose).
            if (lockStream != null)
            {
                try { File.Delete(lockStream.Name); } catch { }
                lockStream.Dispose();
            }
            throw;
        }

        // Transfer lock ownership to the segment so it's held until Dispose().
        segment._lockFileStream = lockStream;
        return segment;
    }

    /// <summary>
    /// Removes stale connection data segment files matching {baseName}_conn_*.
    /// Only safe to call when the corresponding control segment is known to be
    /// inactive (no live server holds it).
    /// </summary>
    internal static void TryRemoveStaleConnectionSegments(string baseName)
    {
        TryRemoveSegmentsByPrefix(baseName + "_conn_");
    }

    /// <summary>
    /// Opens a control segment for client-side connection.
    /// </summary>
    public static Segment OpenControlSegment(string baseName)
    {
        var ctlName = baseName + ShmConstants.ControlSegmentSuffix;
        return Open(ctlName);
    }

    /// <summary>
    /// Unmaps the shared memory region without closing / cleaning up the backing file.
    /// Used by clients that need to release the mapping while the server retains ownership.
    /// </summary>
    public void UnmapWithoutClose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        CancelAndWaitForHeaderWaiters();

        RingA.Dispose();
        RingB.Dispose();
        _memoryManager.Dispose();
        _accessor.Dispose();
        _mappedFile.Dispose();
        _disposeCts.Dispose();
        // Intentionally do NOT delete the backing file
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        CancelAndWaitForHeaderWaiters();

        RingA.Dispose();
        RingB.Dispose();

#if LINUX
        // Phase 2 eventfd-waker teardown (Linux only). Order matters:
        //   1. Stop the FD-pass server FIRST so no cross-process opener
        //      can dial in and receive about-to-be-EBADF descriptors.
        //   2. Close the waker (Dispose writes a wake to MyReadFd so any
        //      same-process opener parked on Read returns 0, then closes
        //      the eventfds when ownsFds=true).
        //   3. Drop the stash so a late same-process Open returns null.
        var fdPassServer = _fdPassServer;
        _fdPassServer = null;
        fdPassServer?.Dispose();

        var waker = _eventfdWaker;
        _eventfdWaker = null;
        waker?.Dispose();

        if (_isServer)
        {
            Synchronization.EventfdRegistry.Drop(Name);
        }
#endif

        _memoryManager.Dispose();
        _accessor.Dispose();
        _mappedFile.Dispose();

        // Server cleans up the backing file
        if (_isServer && !string.IsNullOrEmpty(FilePath))
        {
            try
            {
                File.Delete(FilePath);
            }
            catch
            {
                // Best effort cleanup - file may still be in use by clients
            }
        }

        // Release the advisory lock file (Linux endpoint-steal protection).
        // Delete BEFORE closing: on Linux, unlinking an open file is safe
        // (the fd keeps the lock alive). This prevents the race where
        // process B opens the lock after A closes it, then A deletes B's
        // lock, allowing C to create a fresh lock — split-brain.
        if (_lockFileStream != null)
        {
            var lockPath = _lockFileStream.Name;
            try { File.Delete(lockPath); } catch { }
            try { _lockFileStream.Dispose(); } catch { }
            _lockFileStream = null;
        }

        _disposeCts.Dispose();
    }
}
