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
    [Platform("Win")]
    public void FrameProtocol_WriteAndRead_HeadersFrame()
    {
        // Arrange
        var name = $"grpc_test_{Guid.NewGuid():N}";
        using var seg = Segment.Create(name, ringCapacity: 4096, maxStreams: 100);

        var headersPayload = new HeadersV1
        {
            Version = 1,
            HeaderType = 0,
            Method = "/test/Method",
            Authority = "localhost",
            Metadata = Array.Empty<MetadataKV>()
        }.Encode();

        var header = new FrameHeader(FrameType.Headers, streamId: 1, length: (uint)headersPayload.Length, flags: HeadersFlags.Initial);

        // Act
        FrameProtocol.WriteFrame(seg.RingA, header, headersPayload.AsSpan());

        // Read the frame
        var (readHeader, payload) = FrameProtocol.ReadFrame(seg.RingA);

        // Assert
        Assert.That(readHeader.Type, Is.EqualTo(FrameType.Headers));
        Assert.That(readHeader.StreamId, Is.EqualTo(1));
        Assert.That(payload.Length, Is.EqualTo(headersPayload.Length));

        // Verify headers decode
        var decoded = HeadersV1.Decode(payload);
        Assert.That(decoded.Method, Is.EqualTo("/test/Method"));
    }

    [Test]
    [Platform("Win")]
    public void FrameProtocol_WriteAndRead_MessageFrame()
    {
        // Arrange
        var name = $"grpc_test_{Guid.NewGuid():N}";
        using var seg = Segment.Create(name, ringCapacity: 8192, maxStreams: 100);

        var messageData = new byte[256];
        for (int i = 0; i < messageData.Length; i++)
        {
            messageData[i] = (byte)(i % 256);
        }

        // Act
        FrameProtocol.WriteMessage(seg.RingA, streamId: 42, messageData.AsSpan(), isLast: false);

        // Read the frame
        var (header, payload) = FrameProtocol.ReadFrame(seg.RingA);

        // Assert
        Assert.That(header.Type, Is.EqualTo(FrameType.Message));
        Assert.That(header.StreamId, Is.EqualTo(42));
        Assert.That(payload, Is.EqualTo(messageData));
    }

    [Test]
    [Platform("Win")]
    public void FrameProtocol_WriteAndRead_TrailersFrame()
    {
        // Arrange
        var name = $"grpc_test_{Guid.NewGuid():N}";
        using var seg = Segment.Create(name, ringCapacity: 4096, maxStreams: 100);

        var trailersPayload = new TrailersV1
        {
            GrpcStatusCode = Grpc.Core.StatusCode.OK,
            GrpcStatusMessage = "",
            Metadata = Array.Empty<MetadataKV>()
        }.Encode();

        var header = new FrameHeader(FrameType.Trailers, streamId: 1, length: (uint)trailersPayload.Length, flags: TrailersFlags.EndStream);

        // Act
        FrameProtocol.WriteFrame(seg.RingA, header, trailersPayload.AsSpan());

        var (readHeader, payload) = FrameProtocol.ReadFrame(seg.RingA);

        // Assert
        Assert.That(readHeader.Type, Is.EqualTo(FrameType.Trailers));
        Assert.That(readHeader.Flags, Is.EqualTo(TrailersFlags.EndStream));

        var decoded = TrailersV1.Decode(payload);
        Assert.That(decoded.GrpcStatusCode, Is.EqualTo(Grpc.Core.StatusCode.OK));
    }

    [Test]
    [Platform("Win")]
    public void FrameProtocol_WriteAndRead_PingFrame()
    {
        // Arrange
        var name = $"grpc_test_{Guid.NewGuid():N}";
        using var seg = Segment.Create(name, ringCapacity: 4096, maxStreams: 100);

        var pingData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        // Act - Write a PING frame
        FrameProtocol.WritePing(seg.RingA, flags: 0, pingData.AsSpan());

        var (header, payload) = FrameProtocol.ReadFrame(seg.RingA);

        // Assert
        Assert.That(header.Type, Is.EqualTo(FrameType.Ping));
        Assert.That(header.Length, Is.EqualTo(8));
        Assert.That(payload, Is.EqualTo(pingData));
    }

    [Test]
    [Platform("Win")]
    public void FrameProtocol_MultipleFrames_ReadInOrder()
    {
        // Arrange
        var name = $"grpc_test_{Guid.NewGuid():N}";
        using var seg = Segment.Create(name, ringCapacity: 8192, maxStreams: 100);

        // Write multiple frames
        FrameProtocol.WritePing(seg.RingA, 0, new byte[8]);
        FrameProtocol.WriteMessage(seg.RingA, 1, new byte[] { 1, 2, 3 }, isLast: false);
        FrameProtocol.WriteHalfClose(seg.RingA, 1);

        // Read and verify order
        var (h1, _) = FrameProtocol.ReadFrame(seg.RingA);
        Assert.That(h1.Type, Is.EqualTo(FrameType.Ping));

        var (h2, p2) = FrameProtocol.ReadFrame(seg.RingA);
        Assert.That(h2.Type, Is.EqualTo(FrameType.Message));
        Assert.That(p2, Is.EqualTo(new byte[] { 1, 2, 3 }));

        var (h3, _) = FrameProtocol.ReadFrame(seg.RingA);
        Assert.That(h3.Type, Is.EqualTo(FrameType.HalfClose));
    }

    [Test]
    [Platform("Win")]
    public void FrameProtocol_WriteCancel_Works()
    {
        // Arrange
        var name = $"grpc_test_{Guid.NewGuid():N}";
        using var seg = Segment.Create(name, ringCapacity: 4096, maxStreams: 100);

        // Act
        FrameProtocol.WriteCancel(seg.RingA, streamId: 99);

        var (header, payload) = FrameProtocol.ReadFrame(seg.RingA);

        // Assert
        Assert.That(header.Type, Is.EqualTo(FrameType.Cancel));
        Assert.That(header.StreamId, Is.EqualTo(99));
        Assert.That(header.Length, Is.EqualTo(0));
    }

    [Test]
    [Platform("Win")]
    public void FrameProtocol_WriteGoAway_Works()
    {
        // Arrange
        var name = $"grpc_test_{Guid.NewGuid():N}";
        using var seg = Segment.Create(name, ringCapacity: 4096, maxStreams: 100);

        // Act
        FrameProtocol.WriteGoAway(seg.RingA, GoAwayFlags.Draining, "server shutdown");

        var (header, payload) = FrameProtocol.ReadFrame(seg.RingA);

        // Assert
        Assert.That(header.Type, Is.EqualTo(FrameType.GoAway));
        Assert.That(header.Flags, Is.EqualTo(GoAwayFlags.Draining));
        Assert.That(System.Text.Encoding.UTF8.GetString(payload), Is.EqualTo("server shutdown"));
    }

    [Test]
    [Platform("Win")]
    public void FrameProtocol_WriteWindowUpdate_Works()
    {
        // Arrange
        var name = $"grpc_test_{Guid.NewGuid():N}";
        using var seg = Segment.Create(name, ringCapacity: 4096, maxStreams: 100);

        // Act
        FrameProtocol.WriteWindowUpdate(seg.RingA, streamId: 5, windowSizeIncrement: 65535);

        var (header, payload) = FrameProtocol.ReadFrame(seg.RingA);

        // Assert
        Assert.That(header.Type, Is.EqualTo(FrameType.WindowUpdate));
        Assert.That(header.StreamId, Is.EqualTo(5));
        Assert.That(header.Length, Is.EqualTo(4));

        // Verify increment value
        var increment = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload);
        Assert.That(increment, Is.EqualTo(65535));
    }
}
