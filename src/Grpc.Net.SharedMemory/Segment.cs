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
/// - Offset 0x54-0x7F: reserved (44 bytes) - padding to 128B
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

    // Offset 0x54-0x7F: Reserved (44 bytes) - implicitly zeroed
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

    private readonly MemoryMappedFile _mappedFile;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly MappedMemoryManager _memoryManager;
    private readonly Memory<byte> _memory;
    private readonly bool _isServer;
    private int _disposed;

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
        ulong ringBCapacity)
    {
        Name = name;
        FilePath = filePath;
        _mappedFile = mappedFile;
        _accessor = accessor;
        _memoryManager = memoryManager;
        _memory = memoryManager.Memory;
        _isServer = isServer;
        Size = memoryManager.Length;

        // Create ring sync primitives
        IRingSync? syncA = null;
        IRingSync? syncB = null;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                syncA = RingSyncFactory.Create(name, "A", isServer, memoryManager, (int)ringAOffset);
                syncB = RingSyncFactory.Create(name, "B", isServer, memoryManager, (int)ringBOffset);
            }
            else if (OperatingSystem.IsLinux())
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

        return new Segment(name, filePath, mappedFile, accessor, memoryManager, true,
            ringAOffset, ringCapacity, ringBOffset, ringCapacity);
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

        // Open the backing file
        var backingFile = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        // Validate minimum size
        if (backingFile.Length < ShmConstants.SegmentHeaderSize)
        {
            backingFile.Dispose();
            throw new InvalidDataException($"Segment file too small: {backingFile.Length} bytes");
        }

        // Create memory-mapped file from the backing file (temporarily for header read)
        MemoryMappedFile mappedFile;
        try
        {
            mappedFile = MemoryMappedFile.CreateFromFile(
                backingFile,
                mapName: null,
                backingFile.Length,
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

        return new Segment(name, filePath, mappedFile, accessor, memoryManager, false,
            header.RingAOffset, header.RingACapacity, header.RingBOffset, header.RingBCapacity);
    }

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
            MaxStreams = BinaryPrimitives.ReadUInt32LittleEndian(span[0x50..0x54])
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
        }
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

    private Task WaitForHeaderFlagAsync(int offset, CancellationToken cancellationToken)
    {
        if (IsHeaderFlagSet(offset))
        {
            return Task.CompletedTask;
        }

        if (OperatingSystem.IsWindows())
        {
            return Task.Run(() => WaitHeaderFlagWindows(offset, cancellationToken), cancellationToken);
        }

        if (OperatingSystem.IsLinux())
        {
            return Task.Run(() => WaitHeaderFlagLinux(offset, cancellationToken), cancellationToken);
        }

        return WaitHeaderFlagPollingAsync(offset, cancellationToken);
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
        
        // Clean up any stale segment from previous run
        TryRemoveSegment(ctlName);
        
        return Create(ctlName, ShmConstants.MinRingCapacity, 0);
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

        RingA.Dispose();
        RingB.Dispose();
        _memoryManager.Dispose();
        _accessor.Dispose();
        _mappedFile.Dispose();
        // Intentionally do NOT delete the backing file
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        RingA.Dispose();
        RingB.Dispose();
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
    }
}
