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

    [Test]
    public void TrailersOnly_HeadersWithEndStream_SplitsIntoHeadersAndTrailers()
    {
        // gRFC G3 trailers-only response: server returns a non-OK status (or
        // any status without a body) as a SINGLE H2 HEADERS frame with
        // END_STREAM set, carrying both the response pseudo-headers
        // (`:status`, `content-type`) and the gRPC trailing fields
        // (`grpc-status`, `grpc-message`, custom trailer metadata).
        //
        // The receiving codec MUST surface this as TWO logical frames —
        // initial Headers (without EndStream) followed by Trailers (with
        // EndStream) — so the upper layer's response state machine
        // observes a Trailers frame and completes the call. Without the
        // split, the call hangs forever waiting for trailers that never
        // arrive on the wire.
        using var ring = CreateRing();
        var streamId = 7u;

        // Build the HPACK header block by hand to mirror what a real
        // grpc-go / grpc-java server would emit for a NotFound response.
        var fields = new List<(string Name, byte[] Value)>
        {
            (":status", System.Text.Encoding.ASCII.GetBytes("200")),
            ("content-type", System.Text.Encoding.ASCII.GetBytes("application/grpc")),
            ("grpc-status", System.Text.Encoding.ASCII.GetBytes("5")),       // NotFound
            ("grpc-message", System.Text.Encoding.ASCII.GetBytes("not found")),
            ("custom-trailer", System.Text.Encoding.ASCII.GetBytes("xyz")),
        };
        var (hpackBuf, hpackLen) = Grpc.Net.SharedMemory.Wire.Hpack.HpackEncoder.Encode(fields);
        try
        {
            // Hand-craft an H2 HEADERS frame: 9-byte header + HPACK block.
            // Flags = END_HEADERS | END_STREAM (canonical trailers-only form).
            byte flags = (byte)(Http2Flags.EndHeaders | Http2Flags.EndStream);
            var totalLen = Http2FrameHeader.Size + hpackLen;
            var frameBytes = new byte[totalLen];
            Http2FrameHeader.Encode(
                frameBytes.AsSpan(0, Http2FrameHeader.Size),
                Http2FrameType.Headers, flags, streamId, hpackLen);
            hpackBuf.AsSpan(0, hpackLen).CopyTo(frameBytes.AsSpan(Http2FrameHeader.Size));
            ring.Write(frameBytes);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(hpackBuf);
        }

        // First read: surfaces the response Headers half WITHOUT EndStream.
        // Upper-layer response handler treats this as the initial response
        // headers and continues waiting for trailers (which arrive on the
        // very next read thanks to the codec's pending-frame stash).
        var (h1, p1) = FrameProtocol.ReadFramePayload(ring);
        try
        {
            Assert.That(h1.Type, Is.EqualTo(FrameType.Headers),
                "First frame from a trailers-only HEADERS must be initial Headers.");
            Assert.That(h1.StreamId, Is.EqualTo(streamId));
            // Internal Headers frame uses HeadersFlags.Initial (0x01) which
            // shares its byte value with TrailersFlags.EndStream — flag-byte
            // semantics are interpreted relative to FrameType, not by raw bit
            // overlap. Asserting `Type == Headers` IS the EndStream-absence
            // check at the internal-frame level.
            Assert.That(h1.Flags, Is.EqualTo((byte)HeadersFlags.Initial));

            var hv1 = HeadersV1.Decode(p1.Memory.Span);
            Assert.That(hv1.HeaderType, Is.EqualTo((byte)1),
                "Trailers-only's Headers half maps to server-initial style.");
        }
        finally { p1.Release(); }

        // Second read: surfaces the Trailers half. CRITICAL: this read must
        // NOT block on the ring (the wire is empty after the single H2
        // HEADERS) — the codec must return the stashed synthetic Trailers
        // frame from its decoder state.
        var (h2, p2) = FrameProtocol.ReadFramePayload(ring);
        try
        {
            Assert.That(h2.Type, Is.EqualTo(FrameType.Trailers),
                "Second frame from a trailers-only HEADERS must be Trailers.");
            Assert.That(h2.StreamId, Is.EqualTo(streamId));
            Assert.That(h2.Flags & TrailersFlags.EndStream, Is.EqualTo(TrailersFlags.EndStream),
                "Trailers half must carry EndStream (signals call completion).");

            var tv1 = TrailersV1.Decode(p2.Memory.Span);
            Assert.That(tv1.GrpcStatusCode, Is.EqualTo(global::Grpc.Core.StatusCode.NotFound),
                "Trailers half must carry the parsed grpc-status (gRFC G3).");
            Assert.That(tv1.GrpcStatusMessage, Is.EqualTo("not found"),
                "Trailers half must carry the parsed grpc-message.");
            // Custom trailer metadata belongs in the trailers half by gRFC
            // convention (the only half that semantically owns trailing fields).
            Assert.That(tv1.Metadata, Has.Count.EqualTo(1));
            Assert.That(tv1.Metadata[0].Key, Is.EqualTo("custom-trailer"));
        }
        finally { p2.Release(); }
    }

    [Test]
    public void TrailersOnly_FollowedByNextStream_DoesNotContaminateState()
    {
        // After draining a trailers-only stream's two synthetic frames, the
        // codec's per-ring decoder state must be clean: a subsequent stream
        // on the same ring must be parsed without leakage from the prior
        // trailers-only state (e.g., the StreamsWithInitialHeaders entry
        // for stream 7 must not bleed into stream 9).
        using var ring = CreateRing();

        // First: drive a trailers-only HEADERS on stream 7, drain both
        // synthetic frames.
        var fields = new List<(string Name, byte[] Value)>
        {
            (":status", System.Text.Encoding.ASCII.GetBytes("200")),
            ("grpc-status", System.Text.Encoding.ASCII.GetBytes("5")),
        };
        var (hpackBuf, hpackLen) = Grpc.Net.SharedMemory.Wire.Hpack.HpackEncoder.Encode(fields);
        try
        {
            byte flags = (byte)(Http2Flags.EndHeaders | Http2Flags.EndStream);
            var frameBytes = new byte[Http2FrameHeader.Size + hpackLen];
            Http2FrameHeader.Encode(
                frameBytes.AsSpan(0, Http2FrameHeader.Size),
                Http2FrameType.Headers, flags, 7u, hpackLen);
            hpackBuf.AsSpan(0, hpackLen).CopyTo(frameBytes.AsSpan(Http2FrameHeader.Size));
            ring.Write(frameBytes);
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(hpackBuf); }

        var (h1, p1) = FrameProtocol.ReadFramePayload(ring);
        Assert.That(h1.Type, Is.EqualTo(FrameType.Headers));
        p1.Release();

        var (h2, p2) = FrameProtocol.ReadFramePayload(ring);
        Assert.That(h2.Type, Is.EqualTo(FrameType.Trailers));
        p2.Release();

        // Now: a fresh non-trailers-only HEADERS on stream 9 must surface
        // as a single Headers frame (no synthetic Trailers).
        var initial = new HeadersV1 { HeaderType = 1 };
        var (initBuf, initLen) = initial.Encode();
        try
        {
            FrameProtocol.WriteFrame(ring,
                new FrameHeader(FrameType.Headers, 9u, (uint)initLen, HeadersFlags.Initial),
                initBuf.AsSpan(0, initLen));
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(initBuf); }

        var (h3, p3) = FrameProtocol.ReadFramePayload(ring);
        try
        {
            Assert.That(h3.Type, Is.EqualTo(FrameType.Headers));
            Assert.That(h3.StreamId, Is.EqualTo(9u));
            // Same flag-overlap caveat as above: a fresh non-trailers-only
            // Headers carries only HeadersFlags.Initial; the test that the
            // synthetic Trailers stash didn't bleed into the next stream is
            // simply that the very next read produces an internal Headers
            // frame (not Trailers).
            Assert.That(h3.Flags, Is.EqualTo((byte)HeadersFlags.Initial));
        }
        finally { p3.Release(); }
    }
}
