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
/// Segment header structure (64 bytes) that identifies and configures a shared memory segment.
/// This layout matches grpc-go-shmem for interoperability.
/// 
/// Layout:
/// - Offset 0-3:   magic (uint32) - "SHM1" = 0x53484D31
/// - Offset 4-7:   version (uint32) - protocol version
/// - Offset 8-11:  maxStreams (uint32) - maximum concurrent streams
/// - Offset 12-15: flags (uint32) - reserved flags
/// - Offset 16-23: ringAOffset (uint64) - offset to Ring A in segment
/// - Offset 24-31: ringACapacity (uint64) - data area capacity for Ring A
/// - Offset 32-39: ringBOffset (uint64) - offset to Ring B in segment
/// - Offset 40-47: ringBCapacity (uint64) - data area capacity for Ring B
/// - Offset 48-63: reserved (16 bytes) - future use
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct SegmentHeader
{
    /// <summary>Magic number identifying this as a shared memory segment ("SHM1").</summary>
    [FieldOffset(0)]
    public uint Magic;

    /// <summary>Protocol version.</summary>
    [FieldOffset(4)]
    public uint Version;

    /// <summary>Maximum concurrent streams.</summary>
    [FieldOffset(8)]
    public uint MaxStreams;

    /// <summary>Reserved flags.</summary>
    [FieldOffset(12)]
    public uint Flags;

    /// <summary>Offset to Ring A (client→server) in the segment.</summary>
    [FieldOffset(16)]
    public ulong RingAOffset;

    /// <summary>Data area capacity for Ring A.</summary>
    [FieldOffset(24)]
    public ulong RingACapacity;

    /// <summary>Offset to Ring B (server→client) in the segment.</summary>
    [FieldOffset(32)]
    public ulong RingBOffset;

    /// <summary>Data area capacity for Ring B.</summary>
    [FieldOffset(40)]
    public ulong RingBCapacity;

    // Offset 48-63: Reserved (16 bytes) - implicitly zeroed
}

/// <summary>
/// Represents a shared memory segment containing two ring buffers for bidirectional communication.
/// Ring A is used for client→server data, Ring B for server→client data.
/// </summary>
public sealed class Segment : IDisposable
{
    private readonly MemoryMappedFile _mappedFile;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly Memory<byte> _memory;
    private readonly byte[] _memoryBuffer;
    private readonly bool _isServer;
    private bool _disposed;

    /// <summary>Gets the segment name.</summary>
    public string Name { get; }

    /// <summary>Gets the Ring A (client→server) ring buffer.</summary>
    public ShmRing RingA { get; }

    /// <summary>Gets the Ring B (server→client) ring buffer.</summary>
    public ShmRing RingB { get; }

    /// <summary>Gets the segment header.</summary>
    public SegmentHeader Header => GetHeader();

    /// <summary>Gets the total segment size in bytes.</summary>
    public long Size { get; }

    private Segment(
        string name,
        MemoryMappedFile mappedFile,
        MemoryMappedViewAccessor accessor,
        byte[] memoryBuffer,
        bool isServer,
        ulong ringAOffset,
        ulong ringACapacity,
        ulong ringBOffset,
        ulong ringBCapacity)
    {
        Name = name;
        _mappedFile = mappedFile;
        _accessor = accessor;
        _memoryBuffer = memoryBuffer;
        _memory = memoryBuffer;
        _isServer = isServer;
        Size = memoryBuffer.Length;

        // Create ring sync primitives
        IRingSync? syncA = null;
        IRingSync? syncB = null;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                syncA = RingSyncFactory.Create(name, "A", isServer);
                syncB = RingSyncFactory.Create(name, "B", isServer);
            }
        }
        catch
        {
            // Sync primitives are optional - fall back to polling if not available
        }

        // Create ring buffers
        RingA = new ShmRing(_memory, (int)ringAOffset, ringACapacity, syncA);
        RingB = new ShmRing(_memory, (int)ringBOffset, ringBCapacity, syncB);
    }

    /// <summary>
    /// Creates a new shared memory segment (server-side).
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
        // Layout: [SegmentHeader (64)] [RingA Header (64)] [RingA Data] [RingB Header (64)] [RingB Data]
        var ringAOffset = (ulong)ShmConstants.SegmentHeaderSize;
        var ringBOffset = ringAOffset + (ulong)ShmConstants.RingHeaderSize + ringCapacity;
        var totalSize = ringBOffset + (ulong)ShmConstants.RingHeaderSize + ringCapacity;

        // Create memory-mapped file
        var mappedFile = MemoryMappedFile.CreateNew(
            $"grpc_shm_{name}",
            (long)totalSize,
            MemoryMappedFileAccess.ReadWrite);

        var accessor = mappedFile.CreateViewAccessor(0, (long)totalSize, MemoryMappedFileAccess.ReadWrite);

        // Read the mapped memory into a managed buffer
        var memoryBuffer = new byte[totalSize];
        accessor.ReadArray(0, memoryBuffer, 0, memoryBuffer.Length);

        // Initialize segment header
        var header = new SegmentHeader
        {
            Magic = ShmConstants.SegmentMagic,
            Version = ShmConstants.ProtocolVersion,
            MaxStreams = maxStreams,
            Flags = 0,
            RingAOffset = ringAOffset,
            RingACapacity = ringCapacity,
            RingBOffset = ringBOffset,
            RingBCapacity = ringCapacity
        };

        // Write header to buffer
        WriteSegmentHeader(memoryBuffer, header);

        // Initialize ring headers
        InitializeRingHeader(memoryBuffer, (int)ringAOffset, ringCapacity);
        InitializeRingHeader(memoryBuffer, (int)ringBOffset, ringCapacity);

        // Write back to mapped file
        accessor.WriteArray(0, memoryBuffer, 0, memoryBuffer.Length);

        return new Segment(name, mappedFile, accessor, memoryBuffer, true,
            ringAOffset, ringCapacity, ringBOffset, ringCapacity);
    }

    /// <summary>
    /// Opens an existing shared memory segment (client-side).
    /// </summary>
    /// <param name="name">The segment name.</param>
    /// <returns>The opened segment.</returns>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static Segment Open(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Segment name cannot be null or empty", nameof(name));
        }

        // Open existing memory-mapped file
        var mappedFile = MemoryMappedFile.OpenExisting($"grpc_shm_{name}", MemoryMappedFileRights.ReadWrite);

        // We need to read the header first to determine the size
        using var headerAccessor = mappedFile.CreateViewAccessor(0, ShmConstants.SegmentHeaderSize, MemoryMappedFileAccess.Read);
        var headerBuffer = new byte[ShmConstants.SegmentHeaderSize];
        headerAccessor.ReadArray(0, headerBuffer, 0, headerBuffer.Length);

        var header = ReadSegmentHeader(headerBuffer);

        if (header.Magic != ShmConstants.SegmentMagic)
        {
            mappedFile.Dispose();
            throw new InvalidDataException($"Invalid segment magic: expected 0x{ShmConstants.SegmentMagic:X8}, got 0x{header.Magic:X8}");
        }

        if (header.Version != ShmConstants.ProtocolVersion)
        {
            mappedFile.Dispose();
            throw new InvalidDataException($"Unsupported protocol version: expected {ShmConstants.ProtocolVersion}, got {header.Version}");
        }

        // Calculate total size and open full view
        var totalSize = header.RingBOffset + (ulong)ShmConstants.RingHeaderSize + header.RingBCapacity;
        var accessor = mappedFile.CreateViewAccessor(0, (long)totalSize, MemoryMappedFileAccess.ReadWrite);

        var memoryBuffer = new byte[totalSize];
        accessor.ReadArray(0, memoryBuffer, 0, memoryBuffer.Length);

        return new Segment(name, mappedFile, accessor, memoryBuffer, false,
            header.RingAOffset, header.RingACapacity, header.RingBOffset, header.RingBCapacity);
    }

    /// <summary>
    /// Synchronizes the in-memory buffer with the memory-mapped file.
    /// Call this periodically or after writes to ensure data is visible to other processes.
    /// </summary>
    public void Flush()
    {
        if (_disposed) return;
        _accessor.WriteArray(0, _memoryBuffer, 0, _memoryBuffer.Length);
        _accessor.Flush();
    }

    /// <summary>
    /// Reads the latest data from the memory-mapped file into the in-memory buffer.
    /// </summary>
    public void Refresh()
    {
        if (_disposed) return;
        _accessor.ReadArray(0, _memoryBuffer, 0, _memoryBuffer.Length);
    }

    private SegmentHeader GetHeader()
    {
        return ReadSegmentHeader(_memoryBuffer);
    }

    private static void WriteSegmentHeader(byte[] buffer, SegmentHeader header)
    {
        var span = buffer.AsSpan(0, ShmConstants.SegmentHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0..4], header.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..8], header.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..12], header.MaxStreams);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..16], header.Flags);
        BinaryPrimitives.WriteUInt64LittleEndian(span[16..24], header.RingAOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(span[24..32], header.RingACapacity);
        BinaryPrimitives.WriteUInt64LittleEndian(span[32..40], header.RingBOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(span[40..48], header.RingBCapacity);
    }

    private static SegmentHeader ReadSegmentHeader(byte[] buffer)
    {
        var span = buffer.AsSpan(0, ShmConstants.SegmentHeaderSize);
        return new SegmentHeader
        {
            Magic = BinaryPrimitives.ReadUInt32LittleEndian(span[0..4]),
            Version = BinaryPrimitives.ReadUInt32LittleEndian(span[4..8]),
            MaxStreams = BinaryPrimitives.ReadUInt32LittleEndian(span[8..12]),
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(span[12..16]),
            RingAOffset = BinaryPrimitives.ReadUInt64LittleEndian(span[16..24]),
            RingACapacity = BinaryPrimitives.ReadUInt64LittleEndian(span[24..32]),
            RingBOffset = BinaryPrimitives.ReadUInt64LittleEndian(span[32..40]),
            RingBCapacity = BinaryPrimitives.ReadUInt64LittleEndian(span[40..48])
        };
    }

    private static void InitializeRingHeader(byte[] buffer, int offset, ulong capacity)
    {
        var span = buffer.AsSpan(offset, ShmConstants.RingHeaderSize);
        span.Clear(); // Zero all fields
        // Write capacity at offset 40
        BinaryPrimitives.WriteUInt64LittleEndian(span[40..48], capacity);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        RingA.Dispose();
        RingB.Dispose();
        _accessor.Dispose();
        _mappedFile.Dispose();
    }
}
