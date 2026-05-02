#region Copyright notice and license

// Copyright 2026 The gRPC Authors
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

using System.Text;
using Grpc.Net.SharedMemory.Wire;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests.Wire;

[TestFixture]
public class Http2CodecTests
{
    private const int RingCapacity = 64 * 1024;

    private static ShmRing CreateRing()
    {
        var memory = new byte[ShmConstants.RingHeaderSize + RingCapacity];
        return new ShmRing(memory, 0, RingCapacity) { Wire = WireFormat.Http2 };
    }

    [Test]
    public void Message_SmallPayload_RoundTripViaH2DataFrame()
    {
        using var ring = CreateRing();
        var streamId = 1u;
        var body = Encoding.UTF8.GetBytes("hello, http2 over shm!");
        var payload = new byte[5 + body.Length];
        payload[0] = 0;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1, 4), (uint)body.Length);
        body.CopyTo(payload.AsSpan(5));

        var hdr = new FrameHeader(FrameType.Message, streamId, (uint)payload.Length, MessageFlags.EndStream);
        FrameProtocol.WriteFrame(ring, hdr, payload);

        var (rh, rp) = FrameProtocol.ReadFramePayload(ring);
        try
        {
            Assert.That(rh.Type, Is.EqualTo(FrameType.Message));
            Assert.That(rh.StreamId, Is.EqualTo(streamId));
            Assert.That(rh.Length, Is.EqualTo((uint)payload.Length));
            Assert.That(rh.Flags & MessageFlags.EndStream, Is.EqualTo(MessageFlags.EndStream));
            Assert.That(rp.Memory.ToArray(), Is.EquivalentTo(payload));
        }
        finally { rp.Release(); }
    }

    [Test]
    public void Headers_ClientInitial_RoundTripViaHpack()
    {
        using var ring = CreateRing();
        var v1 = new HeadersV1
        {
            HeaderType = 0,
            Method = "/greet.Greeter/SayHello",
            Authority = "localhost",
            Metadata = new[] { new MetadataKV("custom-h", "value-1") },
        };
        var (encoded, encodedLen) = v1.Encode();
        try
        {
            var hdr = new FrameHeader(FrameType.Headers, 1u, (uint)encodedLen, HeadersFlags.Initial);
            FrameProtocol.WriteFrame(ring, hdr, encoded.AsSpan(0, encodedLen));
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(encoded); }

        var (rh, rp) = FrameProtocol.ReadFramePayload(ring);
        try
        {
            Assert.That(rh.Type, Is.EqualTo(FrameType.Headers));
            Assert.That(rh.StreamId, Is.EqualTo(1u));
            var rt = HeadersV1.Decode(rp.Memory.Span);
            Assert.That(rt.HeaderType, Is.EqualTo((byte)0));
            Assert.That(rt.Method, Is.EqualTo(v1.Method));
            Assert.That(rt.Authority, Is.EqualTo(v1.Authority));
            Assert.That(rt.Metadata.Count, Is.EqualTo(1));
        }
        finally { rp.Release(); }
    }

    [Test]
    public void Trailers_AfterHeaders_RoundTripViaHpack()
    {
        using var ring = CreateRing();
        var streamId = 3u;

        var initial = new HeadersV1 { HeaderType = 1 };
        var (initialBuf, initialLen) = initial.Encode();
        try
        {
            FrameProtocol.WriteFrame(ring,
                new FrameHeader(FrameType.Headers, streamId, (uint)initialLen, HeadersFlags.Initial),
                initialBuf.AsSpan(0, initialLen));
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(initialBuf); }
        var (hdr1, pld1) = FrameProtocol.ReadFramePayload(ring);
        pld1.Release();
        Assert.That(hdr1.Type, Is.EqualTo(FrameType.Headers));

        var trailers = new TrailersV1
        {
            GrpcStatusCode = global::Grpc.Core.StatusCode.OK,
            GrpcStatusMessage = "ok",
            Metadata = new[] { new MetadataKV("trailer-x", "y") },
        };
        var (tbuf, tlen) = trailers.Encode();
        try
        {
            FrameProtocol.WriteFrame(ring,
                new FrameHeader(FrameType.Trailers, streamId, (uint)tlen, TrailersFlags.EndStream),
                tbuf.AsSpan(0, tlen));
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(tbuf); }
        var (hdr2, pld2) = FrameProtocol.ReadFramePayload(ring);
        try
        {
            Assert.That(hdr2.Type, Is.EqualTo(FrameType.Trailers));
            Assert.That(hdr2.StreamId, Is.EqualTo(streamId));
            var rt = TrailersV1.Decode(pld2.Memory.Span);
            Assert.That(rt.GrpcStatusCode, Is.EqualTo(global::Grpc.Core.StatusCode.OK));
            Assert.That(rt.GrpcStatusMessage, Is.EqualTo("ok"));
            Assert.That(rt.Metadata.Count, Is.EqualTo(1));
        }
        finally { pld2.Release(); }
    }

    [Test]
    public void Cancel_RoundTripViaRstStream()
    {
        using var ring = CreateRing();
        FrameProtocol.WriteCancel(ring, streamId: 42);
        var (h, p) = FrameProtocol.ReadFramePayload(ring);
        try
        {
            Assert.That(h.Type, Is.EqualTo(FrameType.Cancel));
            Assert.That(h.StreamId, Is.EqualTo(42u));
        }
        finally { p.Release(); }
    }

    [Test]
    public void Ping_RoundTripWithPayload()
    {
        using var ring = CreateRing();
        var pingData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        FrameProtocol.WritePing(ring, flags: 0, pingData);
        var (h, p) = FrameProtocol.ReadFramePayload(ring);
        try
        {
            Assert.That(h.Type, Is.EqualTo(FrameType.Ping));
            Assert.That(p.Memory.ToArray(), Is.EquivalentTo(pingData));
        }
        finally { p.Release(); }
    }

    [Test]
    public void WindowUpdate_RoundTrip_PreservesIncrement()
    {
        using var ring = CreateRing();
        FrameProtocol.WriteWindowUpdate(ring, streamId: 5, windowSizeIncrement: 65535);
        var (h, p) = FrameProtocol.ReadFramePayload(ring);
        try
        {
            Assert.That(h.Type, Is.EqualTo(FrameType.WindowUpdate));
            Assert.That(h.StreamId, Is.EqualTo(5u));
            Assert.That(p.Memory.Length, Is.EqualTo(4));
            var increment = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.Memory.Span);
            Assert.That(increment, Is.EqualTo(65535u));
        }
        finally { p.Release(); }
    }

    [Test]
    public void GoAway_RoundTrip_PreservesDebugMessage()
    {
        using var ring = CreateRing();
        FrameProtocol.WriteGoAway(ring, flags: 0, debugMessage: "shutting down");
        var (h, p) = FrameProtocol.ReadFramePayload(ring);
        try
        {
            Assert.That(h.Type, Is.EqualTo(FrameType.GoAway));
            var msg = System.Text.Encoding.UTF8.GetString(p.Memory.Span);
            Assert.That(msg, Is.EqualTo("shutting down"));
        }
        finally { p.Release(); }
    }
}
