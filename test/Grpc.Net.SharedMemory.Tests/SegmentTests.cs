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

using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

[TestFixture]
public class SegmentTests
{
    [Test]
    [Platform("Win")]
    public void Segment_Create_CreatesNewSegment()
    {
        // Arrange
        var name = $"grpc_test_{Guid.NewGuid():N}";

        // Act
        using var segment = Segment.Create(name, ringCapacity: 4096, maxStreams: 100);

        // Assert
        Assert.That(segment, Is.Not.Null);
        Assert.That(segment.RingA, Is.Not.Null);
        Assert.That(segment.RingB, Is.Not.Null);
    }

    [Test]
    [Platform("Win")]
    public void Segment_RingBuffers_HaveCorrectCapacity()
    {
        // Arrange
        var name = $"grpc_test_{Guid.NewGuid():N}";

        // Act
        using var segment = Segment.Create(name, ringCapacity: 8192, maxStreams: 100);

        // Assert
        Assert.That(segment.RingA.Capacity, Is.GreaterThan(0));
        Assert.That(segment.RingB.Capacity, Is.GreaterThan(0));
    }

    [Test]
    [Platform("Win")]
    public void Segment_WriteFrameHeader_Works()
    {
        // Arrange
        var name = $"grpc_test_{Guid.NewGuid():N}";
        using var segment = Segment.Create(name, ringCapacity: 4096, maxStreams: 100);

        // Write a frame header
        var header = new FrameHeader
        {
            Length = 100,
            StreamId = 42,
            Type = FrameType.Message,
            Flags = 0
        };

        // Encode header
        Span<byte> headerBytes = stackalloc byte[ShmConstants.FrameHeaderSize];
        header.EncodeTo(headerBytes);
        segment.RingA.Write(headerBytes);

        // Read back header
        var readBuffer = new byte[ShmConstants.FrameHeaderSize];
        var bytesRead = segment.RingA.Read(readBuffer.AsSpan());

        Assert.That(bytesRead, Is.EqualTo(ShmConstants.FrameHeaderSize));
        var decoded = FrameHeader.DecodeFrom(readBuffer);
        Assert.That(decoded.StreamId, Is.EqualTo(42));
        Assert.That(decoded.Type, Is.EqualTo(FrameType.Message));
    }

    [Test]
    [Platform("Win")]
    public void Segment_Header_HasCorrectMagicAndVersion()
    {
        // Arrange
        var name = $"grpc_test_{Guid.NewGuid():N}";

        using var segment = Segment.Create(name, ringCapacity: 4096, maxStreams: 100);

        // Assert
        var header = segment.Header;
        
        // Verify grpc-go-shmem compatible magic "GRPCSHM\0"
        var expectedMagic = BitConverter.ToUInt64(ShmConstants.SegmentMagicBytes);
        Assert.That(header.MagicValue, Is.EqualTo(expectedMagic));
        Assert.That(header.Version, Is.EqualTo(ShmConstants.ProtocolVersion));
        Assert.That(header.MaxStreams, Is.EqualTo(100));
        Assert.That(header.ServerReady, Is.EqualTo(1));
    }

    [Test]
    [Platform("Win")]
    public void SegmentHeader_Size_Is128Bytes()
    {
        // Verify 128-byte header size for grpc-go-shmem compatibility
        Assert.That(ShmConstants.SegmentHeaderSize, Is.EqualTo(128));
        Assert.That(System.Runtime.InteropServices.Marshal.SizeOf<SegmentHeader>(), Is.EqualTo(128));
    }

    [Test]
    [Platform("Win")]
    public void RingHeader_Size_Is64Bytes()
    {
        // Verify 64-byte ring header size
        Assert.That(ShmConstants.RingHeaderSize, Is.EqualTo(64));
        Assert.That(System.Runtime.InteropServices.Marshal.SizeOf<RingHeader>(), Is.EqualTo(64));
    }

    [Test]
    [Platform("Win")]
    public void RingHeader_Layout_MatchesGoShmem()
    {
        // Verify field offsets match grpc-go-shmem shm_segment.go RingHeader
        // Go layout:
        // 0x00: capacity, 0x08: widx, 0x10: ridx, 0x18: dataSeq, 0x1C: spaceSeq
        // 0x20: closed, 0x24: pad, 0x28: contigSeq, 0x2C: spaceWaiters
        // 0x30: contigWaiters, 0x34: dataWaiters, 0x38-0x3F: reserved
        var header = new RingHeader
        {
            Capacity = 0x1234567890ABCDEF,
            WriteIdx = 0x1111111111111111,
            ReadIdx = 0x2222222222222222,
            DataSeq = 0x33333333,
            SpaceSeq = 0x44444444,
            Closed = 0x55555555,
            Pad = 0x66666666,
            ContigSeq = 0x77777777,
            SpaceWaiters = 0x88888888,
            ContigWaiters = 0x99999999,
            DataWaiters = 0xAAAAAAAA
        };

        // Marshal to bytes and verify layout
        var bytes = new byte[64];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            System.Runtime.InteropServices.Marshal.StructureToPtr(header, handle.AddrOfPinnedObject(), false);
        }
        finally
        {
            handle.Free();
        }

        // Verify layout matches Go
        Assert.That(BitConverter.ToUInt64(bytes, 0x00), Is.EqualTo(0x1234567890ABCDEF), "capacity at 0x00");
        Assert.That(BitConverter.ToUInt64(bytes, 0x08), Is.EqualTo(0x1111111111111111), "widx at 0x08");
        Assert.That(BitConverter.ToUInt64(bytes, 0x10), Is.EqualTo(0x2222222222222222), "ridx at 0x10");
        Assert.That(BitConverter.ToUInt32(bytes, 0x18), Is.EqualTo(0x33333333), "dataSeq at 0x18");
        Assert.That(BitConverter.ToUInt32(bytes, 0x1C), Is.EqualTo(0x44444444), "spaceSeq at 0x1C");
        Assert.That(BitConverter.ToUInt32(bytes, 0x20), Is.EqualTo(0x55555555), "closed at 0x20");
        Assert.That(BitConverter.ToUInt32(bytes, 0x24), Is.EqualTo(0x66666666), "pad at 0x24");
        Assert.That(BitConverter.ToUInt32(bytes, 0x28), Is.EqualTo(0x77777777), "contigSeq at 0x28");
        Assert.That(BitConverter.ToUInt32(bytes, 0x2C), Is.EqualTo(0x88888888), "spaceWaiters at 0x2C");
        Assert.That(BitConverter.ToUInt32(bytes, 0x30), Is.EqualTo(0x99999999), "contigWaiters at 0x30");
        Assert.That(BitConverter.ToUInt32(bytes, 0x34), Is.EqualTo(0xAAAAAAAA), "dataWaiters at 0x34");
    }

    [Test]
    [Platform("Win")]
    public void Segment_Create_InvalidName_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Segment.Create("", ringCapacity: 4096, maxStreams: 100));
        Assert.Throws<ArgumentException>(() => Segment.Create(null!, ringCapacity: 4096, maxStreams: 100));
    }

    [Test]
    [Platform("Win")]
    public void Segment_Create_NonPowerOfTwo_ThrowsArgumentException()
    {
        var name = $"grpc_test_{Guid.NewGuid():N}";
        Assert.Throws<ArgumentException>(() => Segment.Create(name, ringCapacity: 1000, maxStreams: 100));
    }

    // Note: Cross-segment communication tests require proper shared memory implementation
    // which needs unsafe/pointer-based memory access or native interop.
    // Skipping for now - to be implemented in Phase 2.
}
