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
        Assert.That(header.Magic, Is.EqualTo(ShmConstants.SegmentMagic));
        Assert.That(header.Version, Is.EqualTo(ShmConstants.ProtocolVersion));
        Assert.That(header.MaxStreams, Is.EqualTo(100));
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
