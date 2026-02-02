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
public class FrameProtocolTests
{
    [Test]
    public void FrameHeader_EncodeDecode_RoundTrip()
    {
        // Arrange
        var header = new FrameHeader(FrameType.Message, 123, 456, MessageFlags.More);

        // Act
        var buffer = new byte[ShmConstants.FrameHeaderSize];
        header.EncodeTo(buffer);
        var decoded = FrameHeader.DecodeFrom(buffer);

        // Assert
        Assert.That(decoded.Type, Is.EqualTo(FrameType.Message));
        Assert.That(decoded.StreamId, Is.EqualTo(123U));
        Assert.That(decoded.Length, Is.EqualTo(456U));
        Assert.That(decoded.Flags, Is.EqualTo(MessageFlags.More));
        Assert.That(decoded.Reserved, Is.EqualTo(0));
        Assert.That(decoded.Reserved2, Is.EqualTo(0U));
    }

    [Test]
    public void FrameHeader_EncodeDecode_Headers()
    {
        // Arrange
        var header = new FrameHeader(FrameType.Headers, 1, 100, HeadersFlags.Initial);

        // Act
        var buffer = new byte[ShmConstants.FrameHeaderSize];
        header.EncodeTo(buffer);
        var decoded = FrameHeader.DecodeFrom(buffer);

        // Assert
        Assert.That(decoded.Type, Is.EqualTo(FrameType.Headers));
        Assert.That(decoded.StreamId, Is.EqualTo(1U));
        Assert.That(decoded.Length, Is.EqualTo(100U));
        Assert.That(decoded.Flags, Is.EqualTo(HeadersFlags.Initial));
    }

    [Test]
    public void FrameHeader_EncodeDecode_Trailers()
    {
        // Arrange
        var header = new FrameHeader(FrameType.Trailers, 5, 50, TrailersFlags.EndStream);

        // Act
        var buffer = new byte[ShmConstants.FrameHeaderSize];
        header.EncodeTo(buffer);
        var decoded = FrameHeader.DecodeFrom(buffer);

        // Assert
        Assert.That(decoded.Type, Is.EqualTo(FrameType.Trailers));
        Assert.That(decoded.StreamId, Is.EqualTo(5U));
        Assert.That(decoded.Length, Is.EqualTo(50U));
        Assert.That(decoded.Flags, Is.EqualTo(TrailersFlags.EndStream));
    }

    [Test]
    public void FrameHeader_EncodeDecode_GoAway()
    {
        // Arrange
        var header = new FrameHeader(FrameType.GoAway, 0, 0, GoAwayFlags.Draining);

        // Act
        var buffer = new byte[ShmConstants.FrameHeaderSize];
        header.EncodeTo(buffer);
        var decoded = FrameHeader.DecodeFrom(buffer);

        // Assert
        Assert.That(decoded.Type, Is.EqualTo(FrameType.GoAway));
        Assert.That(decoded.StreamId, Is.EqualTo(0U));
        Assert.That(decoded.Flags, Is.EqualTo(GoAwayFlags.Draining));
    }

    [Test]
    public void FrameHeader_EncodeDecode_WindowUpdate()
    {
        // Arrange
        var header = new FrameHeader(FrameType.WindowUpdate, 7, 4, 0);

        // Act
        var buffer = new byte[ShmConstants.FrameHeaderSize];
        header.EncodeTo(buffer);
        var decoded = FrameHeader.DecodeFrom(buffer);

        // Assert
        Assert.That(decoded.Type, Is.EqualTo(FrameType.WindowUpdate));
        Assert.That(decoded.StreamId, Is.EqualTo(7U));
        Assert.That(decoded.Length, Is.EqualTo(4U));
    }

    [Test]
    public void FrameHeader_EncodeDecode_Ping()
    {
        // Arrange
        var header = new FrameHeader(FrameType.Ping, 0, 8, PingFlags.Bdp);

        // Act
        var buffer = new byte[ShmConstants.FrameHeaderSize];
        header.EncodeTo(buffer);
        var decoded = FrameHeader.DecodeFrom(buffer);

        // Assert
        Assert.That(decoded.Type, Is.EqualTo(FrameType.Ping));
        Assert.That(decoded.Length, Is.EqualTo(8U));
        Assert.That(decoded.Flags, Is.EqualTo(PingFlags.Bdp));
    }

    [Test]
    public void FrameHeader_Encode_BufferTooSmall_ThrowsArgumentException()
    {
        // Arrange
        var header = new FrameHeader(FrameType.Message, 1, 100, 0);
        var buffer = new byte[10]; // Too small

        // Act & Assert
        Assert.Throws<ArgumentException>(() => header.EncodeTo(buffer));
    }

    [Test]
    public void FrameHeader_Decode_BufferTooSmall_ThrowsArgumentException()
    {
        // Arrange
        var buffer = new byte[10]; // Too small

        // Act & Assert
        Assert.Throws<ArgumentException>(() => FrameHeader.DecodeFrom(buffer));
    }

    [Test]
    public void FrameHeader_ByteLayout_MatchesGoImplementation()
    {
        // Arrange - create header with known values
        var header = new FrameHeader(FrameType.Message, 0x12345678, 0xAABBCCDD, 0x55);

        // Act
        var buffer = new byte[ShmConstants.FrameHeaderSize];
        header.EncodeTo(buffer);

        // Assert - verify byte layout matches grpc-go-shmem (little-endian)
        // Length (4 bytes): 0xAABBCCDD
        Assert.That(buffer[0], Is.EqualTo(0xDD));
        Assert.That(buffer[1], Is.EqualTo(0xCC));
        Assert.That(buffer[2], Is.EqualTo(0xBB));
        Assert.That(buffer[3], Is.EqualTo(0xAA));

        // StreamId (4 bytes): 0x12345678
        Assert.That(buffer[4], Is.EqualTo(0x78));
        Assert.That(buffer[5], Is.EqualTo(0x56));
        Assert.That(buffer[6], Is.EqualTo(0x34));
        Assert.That(buffer[7], Is.EqualTo(0x12));

        // Type (1 byte): 0x02 (Message)
        Assert.That(buffer[8], Is.EqualTo(0x02));

        // Flags (1 byte): 0x55
        Assert.That(buffer[9], Is.EqualTo(0x55));

        // Reserved (2 bytes): 0x0000
        Assert.That(buffer[10], Is.EqualTo(0x00));
        Assert.That(buffer[11], Is.EqualTo(0x00));

        // Reserved2 (4 bytes): 0x00000000
        Assert.That(buffer[12], Is.EqualTo(0x00));
        Assert.That(buffer[13], Is.EqualTo(0x00));
        Assert.That(buffer[14], Is.EqualTo(0x00));
        Assert.That(buffer[15], Is.EqualTo(0x00));
    }

    [Test]
    public void FrameHeader_ToString_ReturnsReadableFormat()
    {
        // Arrange
        var header = new FrameHeader(FrameType.Message, 5, 100, MessageFlags.More);

        // Act
        var str = header.ToString();

        // Assert
        Assert.That(str, Does.Contain("Message"));
        Assert.That(str, Does.Contain("5"));
        Assert.That(str, Does.Contain("100"));
    }

    [Test]
    public void FrameHeader_AllFrameTypes_EncodeCorrectly()
    {
        var frameTypes = new[]
        {
            (FrameType.Pad, (byte)0x00),
            (FrameType.Headers, (byte)0x01),
            (FrameType.Message, (byte)0x02),
            (FrameType.Trailers, (byte)0x03),
            (FrameType.Cancel, (byte)0x04),
            (FrameType.GoAway, (byte)0x05),
            (FrameType.Ping, (byte)0x06),
            (FrameType.Pong, (byte)0x07),
            (FrameType.HalfClose, (byte)0x08),
            (FrameType.WindowUpdate, (byte)0x09)
        };

        foreach (var (frameType, expectedByte) in frameTypes)
        {
            var header = new FrameHeader(frameType, 0, 0, 0);
            var buffer = new byte[ShmConstants.FrameHeaderSize];
            header.EncodeTo(buffer);

            Assert.That(buffer[8], Is.EqualTo(expectedByte), $"FrameType {frameType} should encode to {expectedByte}");
        }
    }

    [Test]
    public void FrameHeader_LargeValues_EncodeDecodeCorrectly()
    {
        // Arrange - use maximum values
        var header = new FrameHeader(FrameType.Message, uint.MaxValue, uint.MaxValue - 1, 0xFF);

        // Act
        var buffer = new byte[ShmConstants.FrameHeaderSize];
        header.EncodeTo(buffer);
        var decoded = FrameHeader.DecodeFrom(buffer);

        // Assert
        Assert.That(decoded.StreamId, Is.EqualTo(uint.MaxValue));
        Assert.That(decoded.Length, Is.EqualTo(uint.MaxValue - 1));
        Assert.That(decoded.Flags, Is.EqualTo(0xFF));
    }

    [Test]
    public void ShmConstants_FrameHeaderSize_Is16()
    {
        Assert.That(ShmConstants.FrameHeaderSize, Is.EqualTo(16));
    }

    [Test]
    public void ShmConstants_RingHeaderSize_Is64()
    {
        Assert.That(ShmConstants.RingHeaderSize, Is.EqualTo(64));
    }

    [Test]
    public void ShmConstants_SegmentMagic_MatchesGoImplementation()
    {
        // "SHM1" in ASCII = 0x53484D31
        Assert.That(ShmConstants.SegmentMagic, Is.EqualTo(0x53484D31U));
    }
}
