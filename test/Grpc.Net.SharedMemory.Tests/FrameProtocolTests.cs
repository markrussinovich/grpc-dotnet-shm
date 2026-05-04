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
        }.EncodeToArray();

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
        }.EncodeToArray();

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

    [Test]
    public void ReadFramePayload_LargePayload_PreservesData()
    {
        // Verifies that large payload reads preserve data correctly.
        const int PayloadSize1 = 260_000;
        const int PayloadSize2 = 270_000;
        var name = $"grpc_test_{Guid.NewGuid():N}";
        using var seg = Segment.Create(name, ringCapacity: 2 * 1024 * 1024, maxStreams: 100);
        var ring = seg.RingA;

        var msg1 = new byte[PayloadSize1];
        Array.Fill(msg1, (byte)0xAA);
        var msg2 = new byte[PayloadSize2];
        Array.Fill(msg2, (byte)0xBB);

        FrameProtocol.WriteMessage(ring, streamId: 1, msg1, isLast: false);
        FrameProtocol.WriteMessage(ring, streamId: 1, msg2, isLast: true);

        // Read first frame
        var (h1, p1) = FrameProtocol.ReadFramePayload(ring);
        Assert.That(p1.Length, Is.EqualTo(PayloadSize1));
        Assert.That(p1.Memory.Span[0], Is.EqualTo(0xAA));

        // Second read
        var (h2, p2) = FrameProtocol.ReadFramePayload(ring);
        Assert.That(p2.Length, Is.EqualTo(PayloadSize2));
        Assert.That(p2.Memory.Span[0], Is.EqualTo(0xBB));

        // Release in order
        p1.Release();
        p2.Release();
    }

    [Test]
    public void ReadFramePayload_LargePayload_MixedRelease_Safe()
    {
        // Verifies that releasing payloads in any order leaves
        // the ring in a consistent state.
        const int PayloadSize = 260_000;
        var name = $"grpc_test_{Guid.NewGuid():N}";
        using var seg = Segment.Create(name, ringCapacity: 2 * 1024 * 1024, maxStreams: 100);
        var ring = seg.RingA;

        var msg1 = new byte[PayloadSize];
        Array.Fill(msg1, (byte)0x11);
        var msg2 = new byte[PayloadSize];
        Array.Fill(msg2, (byte)0x22);

        FrameProtocol.WriteMessage(ring, streamId: 1, msg1, isLast: false);
        FrameProtocol.WriteMessage(ring, streamId: 1, msg2, isLast: true);

        var (_, p1) = FrameProtocol.ReadFramePayload(ring);
        var (_, p2) = FrameProtocol.ReadFramePayload(ring);

        // Release out of order — p2 first
        p2.Release();
        p1.Release();

        // Write and read a new frame to verify ring is functional
        var msg3 = new byte[PayloadSize];
        Array.Fill(msg3, (byte)0x33);
        FrameProtocol.WriteMessage(ring, streamId: 1, msg3, isLast: true);

        var (h3, p3) = FrameProtocol.ReadFramePayload(ring);
        Assert.That(p3.Memory.Span[0], Is.EqualTo(0x33));
        p3.Release();
    }

    [Test]
    public void ReadFramePayload_LargePayload_ProducerWriteAfterRelease()
    {
        // Verifies that the producer can write after a large payload
        // is released and the ring space is reclaimed.
        // With maxFramePayload = ringCapacity/4, use a payload that fits in one frame.
        const int RingCapacity = 512 * 1024;
        const int PayloadSize = RingCapacity / 4 - 20; // fits in one frame
        var name = $"grpc_test_{Guid.NewGuid():N}";
        using var seg = Segment.Create(name, ringCapacity: RingCapacity, maxStreams: 100);
        var ring = seg.RingA;

        var largeMsg = new byte[PayloadSize];
        Array.Fill(largeMsg, (byte)0xAA);
        FrameProtocol.WriteMessage(ring, streamId: 1, largeMsg, isLast: true);

        // Read payload and verify
        var (_, payload) = FrameProtocol.ReadFramePayload(ring);
        Assert.That(payload.Memory.Span[0], Is.EqualTo(0xAA));
        Assert.That(payload.Length, Is.EqualTo(PayloadSize));

        // Release the payload
        payload.Release();

        // Write again — should succeed after borrow released
        var msg2 = new byte[PayloadSize];
        Array.Fill(msg2, (byte)0xBB);
        FrameProtocol.WriteMessage(ring, streamId: 1, msg2, isLast: true);

        var (_, p2) = FrameProtocol.ReadFramePayload(ring);
        Assert.That(p2.Memory.Span[0], Is.EqualTo(0xBB));
        p2.Release();
    }
}

/// <summary>
/// Tests for <see cref="ShmGrpcRequestStream"/> partial-write reassembly.
/// Uses a capturing ShmGrpcStream mock to verify that arbitrarily split
/// gRPC frames are correctly reassembled into complete messages.
/// </summary>
[TestFixture]
public class ShmGrpcRequestStreamTests
{
    /// <summary>
    /// Builds a gRPC frame: [compressed:1][length:4 big-endian][payload].
    /// </summary>
    private static byte[] BuildGrpcFrame(byte[] payload)
    {
        var frame = new byte[5 + payload.Length];
        frame[0] = 0; // not compressed
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(5));
        return frame;
    }

    /// <summary>
    /// Helper: Creates a real connection + stream + ShmGrpcRequestStream,
    /// writes data through it, then reads back from the ring to verify.
    /// </summary>
    private static async Task<List<byte[]>> WriteAndCapture(Func<ShmGrpcRequestStream, Task> writeAction)
    {
        var name = $"grpc_test_{Guid.NewGuid():N}";
        using var seg = Segment.Create(name, ringCapacity: 64 * 1024, maxStreams: 100);
        var clientConn = ShmConnection.FromClientSegment(name, seg);
        var clientStream = clientConn.CreateStream();

        var requestStream = new ShmGrpcRequestStream(clientStream);
        await clientStream.SendRequestHeadersAsync("/test/RequestStream", "localhost");
        await writeAction(requestStream);

        // Half-close so the frame writer flushes all pending entries
        await clientStream.SendHalfCloseAsync();

        // Allow the frame writer thread to process enqueued entries
        await Task.Delay(200);

        // Read messages from the TX ring using a timeout to avoid hanging
        var txRing = clientConn.TxRing;
        var messages = new List<byte[]>();
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        while (true)
        {
            var (header, payload) = FrameProtocol.ReadFramePayload(txRing, readCts.Token);
            if (header.Type == FrameType.Headers)
            {
                payload.Release();
                continue;
            }
            if (header.Type == FrameType.HalfClose)
            {
                payload.Release();
                break;
            }
            if (header.Type == FrameType.Message)
            {
                // Skip 5-byte gRPC LPM header (compression flag + length)
                messages.Add(payload.Memory.Slice(5).ToArray());
                payload.Release();
            }
            else
            {
                payload.Release();
            }
        }

        clientStream.Dispose();
        await clientConn.DisposeAsync();
        return messages;
    }

    [Test]
    public async Task WriteAsync_SplitHeader_ReassemblesCorrectly()
    {
        var payload = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        var frame = BuildGrpcFrame(payload);

        var messages = await WriteAndCapture(async rs =>
        {
            // Split: first 3 bytes of header, then remaining 2 bytes + body
            await rs.WriteAsync(frame.AsMemory(0, 3));
            await rs.WriteAsync(frame.AsMemory(3));
        });

        Assert.That(messages.Count, Is.EqualTo(1));
        Assert.That(messages[0], Is.EqualTo(payload));
    }

    [Test]
    public async Task WriteAsync_SplitBody_ReassemblesCorrectly()
    {
        var payload = new byte[100];
        Array.Fill(payload, (byte)0x42);
        var frame = BuildGrpcFrame(payload);

        var messages = await WriteAndCapture(async rs =>
        {
            // Split: header + first 10 bytes of body, then remaining 90
            await rs.WriteAsync(frame.AsMemory(0, 15));
            await rs.WriteAsync(frame.AsMemory(15));
        });

        Assert.That(messages.Count, Is.EqualTo(1));
        Assert.That(messages[0].Length, Is.EqualTo(100));
        Assert.That(messages[0][0], Is.EqualTo(0x42));
    }

    [Test]
    public async Task WriteAsync_ExactHeaderBoundary_ReassemblesCorrectly()
    {
        // Exact 5-byte header in one call, body in next — the edge case
        // where _bodyExpected is set but _bodyBufLen = 0.
        var payload = new byte[50];
        Array.Fill(payload, (byte)0x99);
        var frame = BuildGrpcFrame(payload);

        var messages = await WriteAndCapture(async rs =>
        {
            await rs.WriteAsync(frame.AsMemory(0, 5));
            await rs.WriteAsync(frame.AsMemory(5));
        });

        Assert.That(messages.Count, Is.EqualTo(1));
        Assert.That(messages[0].Length, Is.EqualTo(50));
        Assert.That(messages[0][0], Is.EqualTo(0x99));
    }

    [Test]
    public async Task WriteAsync_MultipleFramesInSingleCall_ParsedCorrectly()
    {
        var payload1 = new byte[] { 0x11, 0x22 };
        var payload2 = new byte[] { 0x33, 0x44, 0x55 };

        var messages = await WriteAndCapture(async rs =>
        {
            var frame1 = BuildGrpcFrame(payload1);
            var frame2 = BuildGrpcFrame(payload2);
            var combined = new byte[frame1.Length + frame2.Length];
            frame1.CopyTo(combined, 0);
            frame2.CopyTo(combined, frame1.Length);
            await rs.WriteAsync(combined);
        });

        Assert.That(messages.Count, Is.EqualTo(2));
        Assert.That(messages[0], Is.EqualTo(payload1));
        Assert.That(messages[1], Is.EqualTo(payload2));
    }

    #region Zero-Copy Read Tests

    [Test]
    public void ZeroCopyRead_Speculative_CommitsReadIdx()
    {
        // Speculative path: CommitRead immediately, return ring memory.
        var name = $"grpc_zc_{Guid.NewGuid():N}";
        using var seg = Segment.Create(name, ringCapacity: 2 * 1024 * 1024, maxStreams: 10);
        var ring = seg.RingA;

        var payload = new byte[128 * 1024]; // Must be >= 64KB for ZC threshold
        new Random(42).NextBytes(payload);
        FrameProtocol.WriteMessage(ring, streamId: 1, payload, isLast: true);

        var (header, fp) = FrameProtocol.ReadFramePayload(ring, zeroCopy: true);

        Assert.That(header.Type, Is.EqualTo(FrameType.Message));
        Assert.That(fp.Length, Is.EqualTo(128 * 1024));
        Assert.That(fp.Memory.Span.SequenceEqual(payload), Is.True);

        // Speculative: reserved bytes should be non-zero while held
        Assert.That(ring.SpeculativeReservedBytes, Is.GreaterThan(0));

        fp.Release();
        Assert.That(ring.SpeculativeReservedBytes, Is.EqualTo(0));
    }

    [Test]
    public void ZeroCopyRead_Deferred_HoldsReadIdx()
    {
        // When too many speculative bytes are reserved, use deferred.
        var name = $"grpc_zc_{Guid.NewGuid():N}";
        using var seg = Segment.Create(name, ringCapacity: 2 * 1024 * 1024, maxStreams: 10);
        var ring = seg.RingA;

        var payload1 = new byte[128 * 1024]; // Must be >= 64KB for ZC threshold
        var payload2 = new byte[128 * 1024];
        var payload3 = new byte[128 * 1024];
        new Random(1).NextBytes(payload1);
        new Random(2).NextBytes(payload2);
        new Random(3).NextBytes(payload3);

        FrameProtocol.WriteMessage(ring, 1, payload1, true);
        FrameProtocol.WriteMessage(ring, 1, payload2, true);
        FrameProtocol.WriteMessage(ring, 1, payload3, true);

        // Frame 1: speculative (SpeculativeReservedBytes == 0 at read time)
        var (_, fp1) = FrameProtocol.ReadFramePayload(ring, zeroCopy: true);
        Assert.That(ring.SpeculativeReservedBytes, Is.GreaterThan(0));

        // Frame 2: NOT speculative — SpeculativeReservedBytes > 0 from fp1,
        // so it falls through to copy mode (FIFO safety: at most one ZC
        // buffer in flight at a time).
        var reserved1 = ring.SpeculativeReservedBytes;
        var (_, fp2) = FrameProtocol.ReadFramePayload(ring, zeroCopy: true);
        // Reserved bytes unchanged (fp2 went to copy, not speculative)
        Assert.That(ring.SpeculativeReservedBytes, Is.EqualTo(reserved1));
        Assert.That(fp2.Memory.Span.SequenceEqual(payload2), Is.True);

        // Frame 3: also copy mode (fp1 still held)
        var (_, fp3) = FrameProtocol.ReadFramePayload(ring, zeroCopy: true);
        Assert.That(ring.SpeculativeReservedBytes, Is.EqualTo(reserved1));
        Assert.That(fp3.Memory.Span.SequenceEqual(payload3), Is.True);

        fp3.Release();
        fp1.Release();
        fp2.Release();
    }

    [Test]
    public void ZeroCopyRead_DeferredBeforeSpeculative_NoSkip()
    {
        // Core safety test: if a deferred frame exists, subsequent frames
        // must NOT do speculative commit (would skip deferred bytes).
        var name = $"grpc_zc_{Guid.NewGuid():N}";
        using var seg = Segment.Create(name, ringCapacity: 2 * 1024 * 1024, maxStreams: 10);
        var ring = seg.RingA;

        var p1 = new byte[128 * 1024]; // Must be >= 64KB for ZC threshold
        var p2 = new byte[128 * 1024];
        var p3 = new byte[128 * 1024];
        new Random(10).NextBytes(p1);
        new Random(20).NextBytes(p2);
        new Random(30).NextBytes(p3);

        FrameProtocol.WriteMessage(ring, 1, p1, true);
        FrameProtocol.WriteMessage(ring, 1, p2, true);
        FrameProtocol.WriteMessage(ring, 1, p3, true);

        // Frames 1-2: speculative (max=2)
        var (_, fp1) = FrameProtocol.ReadFramePayload(ring, zeroCopy: true);
        var (_, fp2) = FrameProtocol.ReadFramePayload(ring, zeroCopy: true);
        var reserved = ring.SpeculativeReservedBytes;
        Assert.That(reserved, Is.GreaterThan(0));

        // Frame 3: deferred (too many speculative bytes)
        var (_, fp3) = FrameProtocol.ReadFramePayload(ring, zeroCopy: true);

        // Release fp1-fp2 → reserved bytes drop
        fp1.Release();
        fp2.Release();
        Assert.That(ring.SpeculativeReservedBytes, Is.LessThan(reserved));

        // Verify fp3 data is still valid (not overwritten)
        Assert.That(fp3.Memory.Span.SequenceEqual(p3), Is.True);

        fp3.Release();
    }

    [Test]
    public void ZeroCopyRead_WrapAround_FallsBackToCopy()
    {
        // When payload wraps around the ring, must copy to pooled buffer.
        var name = $"grpc_zc_{Guid.NewGuid():N}";
        // Small ring to force wrap-around
        using var seg = Segment.Create(name, ringCapacity: 4096, maxStreams: 10);
        var ring = seg.RingA;

        // Write and read until we're near the end of the ring
        for (int i = 0; i < 3; i++)
        {
            var filler = new byte[1000];
            FrameProtocol.WriteMessage(ring, 1, filler, true);
            var (_, fp) = FrameProtocol.ReadFramePayload(ring, zeroCopy: true);
            fp.Release();
        }

        // Now write a message that should wrap around
        var payload = new byte[1000];
        new Random(99).NextBytes(payload);
        FrameProtocol.WriteMessage(ring, 1, payload, true);

        // Read with zeroCopy — should fall back to copy (wrap-around)
        var (h, p) = FrameProtocol.ReadFramePayload(ring, zeroCopy: true);
        Assert.That(h.Type, Is.EqualTo(FrameType.Message));
        Assert.That(p.Memory.Span.Slice(0, payload.Length).SequenceEqual(payload), Is.True);
        p.Release();
    }

    [Test]
    public void ZeroCopyRead_ChainZc_ReleasesAnchor_WhenContinuationWraps()
    {
        // Repro: chain-ZC anchor must close even when the chain's final
        // frame falls to the copy path because its payload reservation
        // wraps the ring boundary.
        //
        // Multi-frame chain ZC opens on the first frame when total LPM
        // size ≤ ChainZcBudget (cap/2) and the first payload reservation
        // is contiguous. The codec previously assumed every chain frame
        // would also be contiguous and only called CloseZcChain on the
        // tryZc=true branch. But a continuation frame can be non-
        // contiguous if the writer's payload spans the ring's wrap
        // boundary; in that case the codec falls to the copy path and
        // (pre-fix) did NOT call CloseZcChain. The chain anchor stays
        // open, so FramePayload.Release on the still-held ZC frame
        // cannot fire EndZcReservation — header.ReadIdx never advances
        // and the writer eventually deadlocks.
        //
        // Layout (cap = 1 MiB, maxFramePayload = cap/3 ≈ 349 525):
        //   * One filler frame (640 KiB total wire) advances readIdx so
        //     that the chain message starts ~640 KiB into the ring.
        //   * Chain message 400 KiB: frame 1 ≈ 349 525 B (contiguous,
        //     ZC-eligible, opens chain), frame 2 ≈ 60 075 B whose
        //     payload reservation wraps because (640 KiB + frame 1 wire)
        //     + frame 2 payload > cap.
        const int Cap = 1024 * 1024;
        var name = $"grpc_chainwrap_{Guid.NewGuid():N}";
        using var seg = Segment.Create(name, ringCapacity: Cap, maxStreams: 10);
        var ring = seg.RingA;

        // Pre-position readIdx to ~640 KiB into the ring. Use TWO filler
        // frames each below the cap/3 chunking threshold (so each is a
        // single non-chunked frame); two reads then advance ReadIdx by
        // exactly 2 × (16 + 327 664) = 655 360 bytes ≈ 640 KiB.
        const int FillerPayload = 327_664;
        var filler = new byte[FillerPayload];
        for (var i = 0; i < 2; i++)
        {
            FrameProtocol.WriteMessage(ring, streamId: 1, filler, isLast: true);
            var (_, fpFiller) = FrameProtocol.ReadFramePayload(ring, zeroCopy: false);
            fpFiller.Release();
        }

        var positionAfterFiller = ring.GetState().ReadIdx & ((ulong)Cap - 1UL);
        Assert.That(positionAfterFiller, Is.GreaterThan(638UL * 1024UL),
            "Pre-positioning sanity: readIdx must clear the wrap-trigger threshold.");
        Assert.That(positionAfterFiller, Is.LessThanOrEqualTo(699UL * 1024UL),
            "Pre-positioning sanity: readIdx must leave room for frame 1 to be contiguous.");

        // Build a 400 KiB chain payload prefixed with a valid 5-byte gRPC
        // LPM header so the codec's chain-ZC sniff reads a credible body
        // length (otherwise random bytes would declare an oversized body
        // and cause the codec to take the copy-mode branch instead of
        // opening the chain).
        var chainPayload = new byte[400 * 1024];
        new Random(99).NextBytes(chainPayload);
        chainPayload[0] = 0; // no compression
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            chainPayload.AsSpan(1, 4), (uint)(chainPayload.Length - 5));

        FrameProtocol.WriteMessage(ring, streamId: 1, chainPayload, isLast: true);

        // Read the chain. Each ReadFramePayload returns one frame.
        var frames = new System.Collections.Generic.List<FramePayload>();
        var assembled = new byte[chainPayload.Length];
        var off = 0;
        var sawWrap = false;
        while (true)
        {
            var (h, fp) = FrameProtocol.ReadFramePayload(ring, zeroCopy: true);
            frames.Add(fp);
            // A speculative-ZC frame's Memory points at ring storage; a
            // copied frame's Memory points at a pooled buffer. Track that
            // at least one of the chain frames took the copy path so the
            // test fails clearly if the writer's chunking changes and
            // stops triggering the wrap.
            if (!fp.IsSpeculativeZeroCopy)
            {
                sawWrap = true;
            }
            fp.Memory.Span.CopyTo(assembled.AsSpan(off));
            off += fp.Length;
            if ((h.Flags & MessageFlags.More) == 0)
            {
                break;
            }
        }

        Assert.That(off, Is.EqualTo(chainPayload.Length));
        Assert.That(assembled.SequenceEqual(chainPayload), Is.True,
            "Reassembled chain must equal the original payload.");
        Assert.That(sawWrap, Is.True,
            "Test sanity: at least one chain frame must hit the wrap copy path " +
            "(otherwise the deadlock scenario isn't being exercised).");

        // Release frames; once both ZC anchor and copy bookkeeping are
        // resolved, EndZcReservation must fire so SpeculativeReservedBytes
        // returns to 0 AND the chain anchor is closed.
        foreach (var fp in frames)
        {
            fp.Release();
        }

        Assert.That(ring.IsChainOpen, Is.False,
            "Chain anchor must close after the final frame's release " +
            "(pre-fix: stuck true forever when last frame wrapped).");
        Assert.That(ring.IsZcChainActive, Is.False,
            "ZC anchor must release after the chain's last release.");
        Assert.That(ring.SpeculativeReservedBytes, Is.EqualTo(0L));

        // Sanity: the writer should now see free space again. If the chain
        // anchor leaked, the deferred ReadIdx would still be at the chain
        // start, hiding the released bytes from the writer.
        Assert.That(ring.GetState().Used, Is.LessThan((ulong)(Cap / 4)),
            "Writer's free-space view must reflect the released chain.");
    }

    #endregion
}
