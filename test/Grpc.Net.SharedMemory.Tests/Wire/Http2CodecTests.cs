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

using System.Buffers;
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
        return new ShmRing(memory, 0, RingCapacity);
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
            // Round-7 PR-B: codec attaches the decoded HeadersV1 object
            // directly to FramePayload.DecodedHeader, eliminating the
            // bytes → HeadersV1 round-trip. Fall back to byte decode if
            // the object isn't attached (defensive).
            var rt = rp.DecodedHeader as HeadersV1 ?? HeadersV1.Decode(rp.Memory.Span);
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
            // Round-7 PR-B: prefer codec-attached object over byte decode.
            var rt = pld2.DecodedHeader as TrailersV1 ?? TrailersV1.Decode(pld2.Memory.Span);
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

            // Round-7 PR-B: prefer codec-attached object over byte decode.
            var hv1 = p1.DecodedHeader as HeadersV1 ?? HeadersV1.Decode(p1.Memory.Span);
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

            // Round-7 PR-B: prefer codec-attached object over byte decode.
            var tv1 = p2.DecodedHeader as TrailersV1 ?? TrailersV1.Decode(p2.Memory.Span);
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

    [Test]
    public void Settings_AckWithPayload_ThrowsAndConsumesFrame()
    {
        // RFC 7540 §6.5.3: SETTINGS frame with the ACK flag set MUST have
        // payload length 0; otherwise the connection is malformed and the
        // peer must treat it as FRAME_SIZE_ERROR.
        //
        // Critical correctness property: the codec must CONSUME the bogus
        // payload bytes before throwing. If it only consumed the 9-byte
        // frame header, the next ReadFramePayload call would interpret the
        // leftover payload bytes as the start of a new H2 frame header and
        // the read pointer would desync (manifests as cryptic "Unknown H2
        // frame type" errors or a hard hang on the connection).
        using var ring = CreateRing();

        // Hand-craft a SETTINGS+ACK frame with a (forbidden) 6-byte payload.
        const byte ackFlag = 0x01;
        var bogusPayload = new byte[] { 0x00, 0x03, 0x00, 0x00, 0x10, 0x00 };
        var totalLen = Http2FrameHeader.Size + bogusPayload.Length;
        var frame1 = new byte[totalLen];
        Http2FrameHeader.Encode(
            frame1.AsSpan(0, Http2FrameHeader.Size),
            Http2FrameType.Settings, ackFlag, streamId: 0, payloadLength: bogusPayload.Length);
        bogusPayload.CopyTo(frame1.AsSpan(Http2FrameHeader.Size));

        // Then write a normal Message frame so we can verify the read
        // pointer advanced past the malformed SETTINGS by checking the
        // next read returns a coherent frame (and not garbage interpreted
        // from the bogus payload bytes).
        var goodBody = System.Text.Encoding.UTF8.GetBytes("ok");
        var goodLpm = new byte[5 + goodBody.Length];
        goodLpm[0] = 0;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            goodLpm.AsSpan(1, 4), (uint)goodBody.Length);
        goodBody.CopyTo(goodLpm.AsSpan(5));

        ring.Write(frame1);
        var goodHdr = new FrameHeader(FrameType.Message, 1u, (uint)goodLpm.Length, MessageFlags.EndStream);
        FrameProtocol.WriteFrame(ring, goodHdr, goodLpm);

        Assert.Throws<InvalidDataException>(() => FrameProtocol.ReadFramePayload(ring),
            "SETTINGS ACK with non-zero payload must throw.");

        // Read pointer must have advanced past the malformed SETTINGS frame
        // (header + bogus payload). The next read must return the good
        // Message frame intact — proves no desync.
        var (h, p) = FrameProtocol.ReadFramePayload(ring);
        try
        {
            Assert.That(h.Type, Is.EqualTo(FrameType.Message),
                "If SETTINGS ACK desync occurred, this read would mis-interpret " +
                "the bogus payload bytes as a frame header and produce garbage.");
            Assert.That(h.StreamId, Is.EqualTo(1u));
            Assert.That(p.Memory.ToArray(), Is.EquivalentTo(goodLpm));
        }
        finally { p.Release(); }
    }

    [Test]
    public void Data_MultipleLpmInOneFrame_SurfaceAsSeparateMessages()
    {
        // RFC 7540 §6.1: H2 DATA carries an opaque byte stream — gRPC LPM
        // message boundaries are NOT aligned with H2 frame boundaries.
        // A peer MAY pack two or more complete LPM messages into a single
        // DATA frame (writer-side coalescing for small messages).
        //
        // Pre-fix the codec rejected this as "multiple gRPC LPM messages
        // not supported", which broke interop with any peer that batches
        // small messages (commonly grpc-go's internal write buffer
        // flush). The fix consumes all complete LPMs in the DATA's body,
        // returns the first as the call's response, and stashes the rest
        // for the next ReadFramePayload call (FIFO order preserved).
        using var ring = CreateRing();
        var streamId = 11u;

        // Build three LPM messages, concatenated.
        var bodies = new[]
        {
            System.Text.Encoding.UTF8.GetBytes("first"),
            System.Text.Encoding.UTF8.GetBytes("second-message"),
            System.Text.Encoding.UTF8.GetBytes("third"),
        };
        var combined = new List<byte>();
        foreach (var body in bodies)
        {
            combined.Add(0); // no compression
            var lenBytes = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(lenBytes, (uint)body.Length);
            combined.AddRange(lenBytes);
            combined.AddRange(body);
        }
        var combinedArr = combined.ToArray();

        // Hand-craft an H2 DATA frame with END_STREAM carrying ALL THREE
        // LPM messages. END_STREAM applies to whichever Message is the
        // last one this DATA produces (gRPC mapping of H2 stream end).
        var totalLen = Http2FrameHeader.Size + combinedArr.Length;
        var frame = new byte[totalLen];
        Http2FrameHeader.Encode(
            frame.AsSpan(0, Http2FrameHeader.Size),
            Http2FrameType.Data, Http2Flags.EndStream, streamId, combinedArr.Length);
        combinedArr.CopyTo(frame.AsSpan(Http2FrameHeader.Size));
        ring.Write(frame);

        // Read each surfaced Message in order.
        var read = new List<(FrameHeader Header, byte[] Body)>();
        for (var i = 0; i < bodies.Length; i++)
        {
            var (h, p) = FrameProtocol.ReadFramePayload(ring);
            try
            {
                Assert.That(h.Type, Is.EqualTo(FrameType.Message));
                Assert.That(h.StreamId, Is.EqualTo(streamId));
                read.Add((h, p.Memory.ToArray()));
            }
            finally { p.Release(); }
        }

        // Each surfaced Message must contain the full LPM (5-byte header
        // + body) for one of the input messages, in order.
        for (var i = 0; i < bodies.Length; i++)
        {
            var expected = new byte[5 + bodies[i].Length];
            expected[0] = 0;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                expected.AsSpan(1, 4), (uint)bodies[i].Length);
            bodies[i].CopyTo(expected.AsSpan(5));
            Assert.That(read[i].Body, Is.EquivalentTo(expected),
                $"Coalesced LPM #{i} must surface intact and in wire order.");
        }

        // Only the LAST Message must carry EndStream — the H2 DATA
        // END_STREAM applies to the call's terminal LPM, not to every
        // packed message.
        Assert.That(read[0].Header.Flags & MessageFlags.EndStream, Is.EqualTo(0),
            "Non-last coalesced messages must not carry EndStream.");
        Assert.That(read[1].Header.Flags & MessageFlags.EndStream, Is.EqualTo(0),
            "Non-last coalesced messages must not carry EndStream.");
        Assert.That(read[2].Header.Flags & MessageFlags.EndStream, Is.EqualTo(MessageFlags.EndStream),
            "The last surfaced message must carry EndStream.");
    }

    [Test]
    public void Data_TooManyCoalescedLpm_ThrowsAndClearsPendingFrames()
    {
        using var ring = CreateRing();
        var streamId = 13u;
        var messageCount = Http2Codec.MaxPendingSyntheticFrames + 2;
        var combined = new byte[messageCount * 5];
        for (var i = 0; i < messageCount; i++)
        {
            combined[i * 5] = 0;
        }

        var oversizedFrame = new byte[Http2FrameHeader.Size + combined.Length];
        Http2FrameHeader.Encode(
            oversizedFrame.AsSpan(0, Http2FrameHeader.Size),
            Http2FrameType.Data, 0, streamId, combined.Length);
        combined.CopyTo(oversizedFrame.AsSpan(Http2FrameHeader.Size));
        ring.Write(oversizedFrame);

        Assert.Throws<InvalidDataException>(() => FrameProtocol.ReadFramePayload(ring));

        var goodBody = System.Text.Encoding.UTF8.GetBytes("after-limit");
        var goodLpm = new byte[5 + goodBody.Length];
        goodLpm[0] = 0;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(goodLpm.AsSpan(1, 4), (uint)goodBody.Length);
        goodBody.CopyTo(goodLpm.AsSpan(5));
        FrameProtocol.WriteFrame(ring,
            new FrameHeader(FrameType.Message, streamId, (uint)goodLpm.Length, MessageFlags.EndStream),
            goodLpm);

        var (header, payload) = FrameProtocol.ReadFramePayload(ring);
        try
        {
            Assert.That(header.Type, Is.EqualTo(FrameType.Message));
            Assert.That(payload.Memory.ToArray(), Is.EquivalentTo(goodLpm));
        }
        finally { payload.Release(); }
    }

    [Test]
    public void ReserveRead_DuringSpeculativeZcHold_EntersKernelBlockNotBusyWait()
    {
        // Regression: while a speculative-ZC anchor is held, the shared
        // header.ReadIdx is intentionally frozen to keep the cross-process
        // writer from wrapping onto still-held bytes. Pre-fix
        // ReserveRead's wait path delegated to WaitForData which compared
        // header.ReadIdx to header.WriteIdx — and the frozen ReadIdx
        // continued to satisfy `WriteIdx > ReadIdx` indefinitely. The
        // spin-loop's success branch returned WITHOUT ever entering the
        // kernel-block path's `Interlocked.Increment(DataWaiters)`. The
        // reader thread therefore tight-looped at 100% CPU, and any
        // back-pressure signal the writer might want to send (gated on
        // <c>DataWaiters > 0</c>) would never fire because no waiter
        // ever registered.
        //
        // Fix: ReserveRead now calls WaitForDataAfter(_pendingReadIdx),
        // which compares against the local pending index and so does NOT
        // see the still-held ZC bytes as "available". After the spin
        // window expires it enters the kernel-block path, increments
        // DataWaiters, and parks in <c>_sync.WaitForData</c>.
        //
        // Verification: launch a parallel ReserveRead worker while the
        // ZC payload is still held, give it long enough to descend into
        // the kernel-block path, then assert DataWaiters == 1. Pre-fix
        // it would stay at 0 because the spin loop returned successfully
        // every iteration. Post-fix it sits at 1 for the duration.
        //
        // We use <see cref="Segment.Create"/> rather than the bare
        // ShmRing constructor so that <c>_sync</c> is a real
        // synchronisation primitive (named events on Windows, futex on
        // Linux) — without a real sync, both fixed and buggy versions
        // would skip the kernel-block step (the pre-check after
        // DataWaiters++ would still bail) and the test would lose its
        // discriminator.
        var name = $"grpc_zcwait_{Guid.NewGuid():N}";
        using var seg = Segment.Create(name, ringCapacity: 2 * 1024 * 1024, maxStreams: 10);
        var ring = seg.RingA;

        // Write one ZC-eligible payload (>= 64 KiB threshold).
        var lpmBody = new byte[128 * 1024];
        new Random(1).NextBytes(lpmBody);
        var lpm = new byte[5 + lpmBody.Length];
        lpm[0] = 0;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            lpm.AsSpan(1, 4), (uint)lpmBody.Length);
        lpmBody.CopyTo(lpm.AsSpan(5));
        FrameProtocol.WriteMessage(ring, streamId: 1, lpm, isLast: true);

        var (_, fp) = FrameProtocol.ReadFramePayload(ring, zeroCopy: true);
        try
        {
            Assert.That(fp.IsSpeculativeZeroCopy, Is.True,
                "Test sanity: payload must take the ZC path so the anchor is held.");
            Assert.That(ring.SpeculativeReservedBytes, Is.GreaterThan(0L),
                "Test sanity: ZC anchor must be held while the FramePayload is in scope.");

            using var cts = new CancellationTokenSource();
            var worker = Task.Run(() =>
            {
                try { ring.ReserveRead(9, cts.Token); }
                catch (OperationCanceledException) { /* expected */ }
            });

            // Wait long enough for the worker to traverse the spin window
            // (ShmConstants.SpinIterationsDefault is in the few-thousand
            // range and Thread.SpinWait(1) is sub-microsecond) and enter
            // the kernel-block path. 200 ms is overkill but keeps the
            // test robust on slow CI.
            var entered = SpinWait.SpinUntil(
                () => ring.GetState().DataWaiters > 0,
                TimeSpan.FromMilliseconds(500));

            Assert.That(entered, Is.True,
                "ReserveRead must enter the kernel-block path (DataWaiters=1) " +
                "while the ZC anchor is held. Pre-fix WaitForData returned " +
                "from the spin loop on every iteration because header.ReadIdx " +
                "was frozen, never reaching the DataWaiters increment — the " +
                "reader thread saturated a core and writer-side signalling " +
                "never observed a waiter.");
            Assert.That(ring.GetState().DataWaiters, Is.EqualTo(1u));

            cts.Cancel();
            Assert.That(worker.Wait(TimeSpan.FromSeconds(5)), Is.True,
                "ReserveRead must observe cancellation promptly.");
        }
        finally { fp.Release(); }
    }

    // ===== H2 frame validation tests =====
    //
    // RFC 7540 §6.x defines strict size / streamId rules for control
    // frames. Pre-fix our codec was lax, silently mapping any-length
    // RST_STREAM into an internal Cancel and accepting non-spec-compliant
    // SETTINGS / PING / GOAWAY / WINDOW_UPDATE. Each test below crafts a
    // malformed frame, asserts the codec throws InvalidDataException,
    // then writes a normal Message frame after and verifies it reads
    // back intact — proves the read pointer stayed in sync (the codec
    // must consume the malformed frame's full payload before throwing).

    private static void WriteHandcraftedH2Frame(
        ShmRing ring, Http2FrameType type, byte flags, uint streamId, byte[] payload)
    {
        var totalLen = Http2FrameHeader.Size + payload.Length;
        var frame = new byte[totalLen];
        Http2FrameHeader.Encode(
            frame.AsSpan(0, Http2FrameHeader.Size),
            type, flags, streamId, payload.Length);
        payload.CopyTo(frame.AsSpan(Http2FrameHeader.Size));
        ring.Write(frame);
    }

    private static void WriteGoodMessageFrame(ShmRing ring, uint streamId)
    {
        var body = System.Text.Encoding.UTF8.GetBytes("ok");
        var lpm = new byte[5 + body.Length];
        lpm[0] = 0;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(lpm.AsSpan(1, 4), (uint)body.Length);
        body.CopyTo(lpm.AsSpan(5));
        FrameProtocol.WriteFrame(ring,
            new FrameHeader(FrameType.Message, streamId, (uint)lpm.Length, MessageFlags.EndStream),
            lpm);
    }

    private static void AssertNextReadIsGoodMessage(ShmRing ring, uint streamId)
    {
        var (h, p) = FrameProtocol.ReadFramePayload(ring);
        try
        {
            Assert.That(h.Type, Is.EqualTo(FrameType.Message),
                "If the malformed frame caused desync, this read would mis-interpret " +
                "leftover payload bytes as a frame header and produce garbage.");
            Assert.That(h.StreamId, Is.EqualTo(streamId));
        }
        finally { p.Release(); }
    }

    [Test]
    public void RstStream_WrongPayloadLength_ThrowsAndConsumesFrame()
    {
        // RFC 7540 §6.4: RST_STREAM payload MUST be exactly 4 bytes.
        using var ring = CreateRing();
        WriteHandcraftedH2Frame(ring, Http2FrameType.RstStream, 0, streamId: 5,
            payload: new byte[] { 0x00, 0x00, 0x00, 0x08, 0xAA, 0xBB }); // 6 bytes
        Assert.Throws<InvalidDataException>(() => FrameProtocol.ReadFramePayload(ring));

        WriteGoodMessageFrame(ring, streamId: 7);
        AssertNextReadIsGoodMessage(ring, streamId: 7);
    }

    [Test]
    public void RstStream_StreamIdZero_ThrowsAndConsumesFrame()
    {
        // RFC 7540 §6.4: RST_STREAM streamId MUST be non-zero (idle stream
        // identifier 0 is reserved for connection-level frames).
        using var ring = CreateRing();
        WriteHandcraftedH2Frame(ring, Http2FrameType.RstStream, 0, streamId: 0,
            payload: new byte[] { 0x00, 0x00, 0x00, 0x08 });
        Assert.Throws<InvalidDataException>(() => FrameProtocol.ReadFramePayload(ring));

        WriteGoodMessageFrame(ring, streamId: 11);
        AssertNextReadIsGoodMessage(ring, streamId: 11);
    }

    [Test]
    public void Settings_NonAck_PayloadNotMultipleOfSix_Throws()
    {
        // RFC 7540 §6.5: non-ACK SETTINGS payload MUST be a multiple of 6
        // (each setting is 2-byte id + 4-byte value).
        using var ring = CreateRing();
        WriteHandcraftedH2Frame(ring, Http2FrameType.Settings, 0, streamId: 0,
            payload: new byte[] { 0x00, 0x03, 0x00, 0x00, 0x10 }); // 5 bytes
        Assert.Throws<InvalidDataException>(() => FrameProtocol.ReadFramePayload(ring));

        WriteGoodMessageFrame(ring, streamId: 13);
        AssertNextReadIsGoodMessage(ring, streamId: 13);
    }

    [Test]
    public void Settings_NonZeroStreamId_Throws()
    {
        // RFC 7540 §6.5: SETTINGS streamId MUST be 0.
        using var ring = CreateRing();
        // Empty payload (would be valid for ACK) but on non-zero streamId
        // and with no ACK flag.
        WriteHandcraftedH2Frame(ring, Http2FrameType.Settings, 0, streamId: 1,
            payload: Array.Empty<byte>());
        Assert.Throws<InvalidDataException>(() => FrameProtocol.ReadFramePayload(ring));

        WriteGoodMessageFrame(ring, streamId: 15);
        AssertNextReadIsGoodMessage(ring, streamId: 15);
    }

    [Test]
    public void Ping_NonZeroStreamId_Throws()
    {
        // RFC 7540 §6.7: PING streamId MUST be 0.
        using var ring = CreateRing();
        WriteHandcraftedH2Frame(ring, Http2FrameType.Ping, 0, streamId: 3,
            payload: new byte[8]); // valid 8-byte payload, but bad streamId
        Assert.Throws<InvalidDataException>(() => FrameProtocol.ReadFramePayload(ring));

        WriteGoodMessageFrame(ring, streamId: 17);
        AssertNextReadIsGoodMessage(ring, streamId: 17);
    }

    [Test]
    public void GoAway_NonZeroStreamId_Throws()
    {
        // RFC 7540 §6.8: GOAWAY streamId MUST be 0.
        using var ring = CreateRing();
        WriteHandcraftedH2Frame(ring, Http2FrameType.GoAway, 0, streamId: 9,
            payload: new byte[8]); // valid 8-byte length but bad streamId
        Assert.Throws<InvalidDataException>(() => FrameProtocol.ReadFramePayload(ring));

        WriteGoodMessageFrame(ring, streamId: 19);
        AssertNextReadIsGoodMessage(ring, streamId: 19);
    }

    [Test]
    public void WindowUpdate_ZeroIncrement_Throws()
    {
        // RFC 7540 §6.9.1: WINDOW_UPDATE increment MUST be non-zero.
        using var ring = CreateRing();
        WriteHandcraftedH2Frame(ring, Http2FrameType.WindowUpdate, 0, streamId: 0,
            payload: new byte[4]); // 4 bytes of zeros = increment of 0
        Assert.Throws<InvalidDataException>(() => FrameProtocol.ReadFramePayload(ring));

        WriteGoodMessageFrame(ring, streamId: 21);
        AssertNextReadIsGoodMessage(ring, streamId: 21);
    }

    [Test]
    public void Headers_WithContinuation_TwoFragments_RoundTrip()
    {
        // RFC 7540 §6.10: a HEADERS payload may span HEADERS + N
        // CONTINUATION frames, each except the last with END_HEADERS=0
        // and the last with END_HEADERS=1. The whole sequence MUST stay
        // on the same streamId and MUST NOT be interleaved with other
        // frame types.
        //
        // Real H2 peers (grpc-go, grpc-java) emit CONTINUATION when a
        // single header block exceeds SETTINGS_MAX_FRAME_SIZE — most
        // commonly with large `grpc-status-details-bin` trailers in
        // error responses. Pre-fix our codec rejected any HEADERS
        // without END_HEADERS as "not supported", breaking interop.
        using var ring = CreateRing();
        var streamId = 23u;

        // Build an HPACK header block large enough to be worth splitting
        // (we'll split a small block — the codec doesn't care about the
        // split point, only that streamId / END_HEADERS rules hold).
        var fields = new List<(string Name, byte[] Value)>
        {
            (":method", System.Text.Encoding.ASCII.GetBytes("POST")),
            (":scheme", System.Text.Encoding.ASCII.GetBytes("http")),
            (":path", System.Text.Encoding.ASCII.GetBytes("/svc/M")),
            (":authority", System.Text.Encoding.ASCII.GetBytes("h")),
            ("content-type", System.Text.Encoding.ASCII.GetBytes("application/grpc")),
            ("custom-h", System.Text.Encoding.ASCII.GetBytes("v1")),
        };
        var (hpackBuf, hpackLen) = Grpc.Net.SharedMemory.Wire.Hpack.HpackEncoder.Encode(fields);
        try
        {
            // Split the HPACK block into two roughly equal halves.
            var splitPoint = hpackLen / 2;
            var firstHalf = hpackBuf.AsSpan(0, splitPoint).ToArray();
            var secondHalf = hpackBuf.AsSpan(splitPoint, hpackLen - splitPoint).ToArray();

            // HEADERS without END_HEADERS, then CONTINUATION with END_HEADERS.
            // First HEADERS: NO END_HEADERS flag. (END_STREAM is fine here
            // because gRPC client-initial often has END_STREAM=0, but for
            // round-trip-checking we leave it 0.)
            WriteHandcraftedH2Frame(ring,
                Http2FrameType.Headers, flags: 0, streamId, firstHalf);

            // CONTINUATION with END_HEADERS=1.
            WriteHandcraftedH2Frame(ring,
                Http2FrameType.Continuation, flags: Http2Flags.EndHeaders, streamId, secondHalf);

            // Read back. The codec must reassemble the two fragments,
            // decode the HPACK block, and emit a single Headers frame.
            var (h, p) = FrameProtocol.ReadFramePayload(ring);
            try
            {
                Assert.That(h.Type, Is.EqualTo(FrameType.Headers));
                Assert.That(h.StreamId, Is.EqualTo(streamId));

                // Round-7 PR-B: prefer codec-attached object over byte decode.
                var hv1 = p.DecodedHeader as HeadersV1 ?? HeadersV1.Decode(p.Memory.Span);
                Assert.That(hv1.HeaderType, Is.EqualTo((byte)0),
                    "Reassembled HEADERS must be parsed as client-initial.");
                Assert.That(hv1.Method, Is.EqualTo("/svc/M"));
                Assert.That(hv1.Authority, Is.EqualTo("h"));
            }
            finally { p.Release(); }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(hpackBuf);
        }
    }

    [Test]
    public void Headers_WithContinuation_StreamIdMismatch_Throws()
    {
        // RFC 7540 §6.10: CONTINUATION streamId MUST match the originating
        // HEADERS streamId. A peer that interleaves CONTINUATION on a
        // different stream is malformed (PROTOCOL_ERROR).
        using var ring = CreateRing();
        var headersStream = 25u;
        var fields = new List<(string Name, byte[] Value)>
        {
            (":status", System.Text.Encoding.ASCII.GetBytes("200")),
        };
        var (hpackBuf, hpackLen) = Grpc.Net.SharedMemory.Wire.Hpack.HpackEncoder.Encode(fields);
        try
        {
            var firstHalf = hpackBuf.AsSpan(0, 1).ToArray();
            var secondHalf = hpackBuf.AsSpan(1, hpackLen - 1).ToArray();

            WriteHandcraftedH2Frame(ring,
                Http2FrameType.Headers, flags: 0, headersStream, firstHalf);
            // CONTINUATION on a DIFFERENT stream — protocol error.
            WriteHandcraftedH2Frame(ring,
                Http2FrameType.Continuation, flags: Http2Flags.EndHeaders,
                streamId: headersStream + 2, secondHalf);

            Assert.Throws<InvalidDataException>(() => FrameProtocol.ReadFramePayload(ring));
        }
        finally { ArrayPool<byte>.Shared.Return(hpackBuf); }
    }

    [Test]
    public void Headers_WithContinuation_NonContinuationFrameInterleaved_Throws()
    {
        // RFC 7540 §6.10: between a HEADERS without END_HEADERS and the
        // terminal CONTINUATION, NO other frame type is allowed (not
        // even on a different stream). Peer that interleaves DATA /
        // SETTINGS / etc. is malformed.
        using var ring = CreateRing();
        var streamId = 27u;
        var fields = new List<(string Name, byte[] Value)>
        {
            (":status", System.Text.Encoding.ASCII.GetBytes("200")),
        };
        var (hpackBuf, hpackLen) = Grpc.Net.SharedMemory.Wire.Hpack.HpackEncoder.Encode(fields);
        try
        {
            var firstHalf = hpackBuf.AsSpan(0, 1).ToArray();

            WriteHandcraftedH2Frame(ring,
                Http2FrameType.Headers, flags: 0, streamId, firstHalf);
            // Interleave a DATA frame instead of a CONTINUATION.
            var bogusData = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x05, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE };
            WriteHandcraftedH2Frame(ring,
                Http2FrameType.Data, flags: 0, streamId, bogusData);

            Assert.Throws<InvalidDataException>(() => FrameProtocol.ReadFramePayload(ring));
        }
        finally { ArrayPool<byte>.Shared.Return(hpackBuf); }
    }

    [Test]
    public void Continuation_WithoutPrecedingHeaders_Throws()
    {
        // Stray CONTINUATION (no HEADERS before it) — the dispatcher
        // case `Http2FrameType.Continuation` should reject it as
        // out-of-sequence per RFC 7540 §6.10.
        using var ring = CreateRing();
        WriteHandcraftedH2Frame(ring,
            Http2FrameType.Continuation, flags: Http2Flags.EndHeaders, streamId: 1,
            payload: new byte[] { 0x82 }); // single indexed-header byte
        Assert.Throws<InvalidDataException>(() => FrameProtocol.ReadFramePayload(ring));

        WriteGoodMessageFrame(ring, streamId: 29);
        AssertNextReadIsGoodMessage(ring, streamId: 29);
    }

    // ===== Chain-ZC tests (2026-06-01 PR) =====
    //
    // Verify the multi-frame chain-ZC receive path:
    //   1. Each H2 DATA frame body surfaces as a per-chunk synthetic
    //      Message with MessageFlags.More (last has More=0 + optional
    //      EndStream).
    //   2. The first chunk includes the 5-byte gRPC LPM header so
    //      LazyChainRos can decode it via firstFrameBodyOffset:5.
    //   3. SpeculativeReservedBytes is non-zero during the chain (anchor
    //      held) and returns to 0 after all chunks Release.
    //   4. CloseZcChain fires EndZcReservation so header.ReadIdx is
    //      published — ring's UsedBytesApprox returns to 0.

    [Test]
    public void ChainZc_MultiFrame_SurfacesPerChunkAndReleasesAnchor()
    {
        var name = $"grpc_chainzc_{Guid.NewGuid():N}";
        using var seg = Segment.Create(name, ringCapacity: 4 * 1024 * 1024, maxStreams: 10);
        var ring = seg.RingA;
        // Chain-ZC currently requires SingleStreamMode (see
        // IsChainZcStartEligible). Set it so the codec engages the chain
        // path; without this the test would silently fall back to copy.
        ring.SingleStreamMode = true;

        // 1 MiB LPM body — meets ChainZcMinLpmBytes (1 MiB) and
        // ChainZcMinChunkBytes (256 KiB) gates. Split into 3 DATA
        // frames whose body sizes are: [400 KiB, 400 KiB, 224 KiB].
        // (First frame's 400 KiB body carries 5-byte LPM header + 395 KiB
        // of LPM body; subsequent frames are pure LPM body bytes.)
        const int totalBodyLen = 1024 * 1024;
        var lpmBody = new byte[totalBodyLen];
        new Random(42).NextBytes(lpmBody);

        // Frame 1: 5-byte LPM header + first 399 KiB of body.
        const int chunk1Body = 400 * 1024;
        var frame1 = new byte[5 + chunk1Body - 5];
        frame1[0] = 0;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            frame1.AsSpan(1, 4), (uint)totalBodyLen);
        lpmBody.AsSpan(0, chunk1Body - 5).CopyTo(frame1.AsSpan(5));
        FrameProtocol.WriteFrame(ring,
            new FrameHeader(FrameType.Message, streamId: 1, (uint)frame1.Length, MessageFlags.More),
            frame1);

        // Frames 2..N: pure body continuations.
        const int chunk2Body = 400 * 1024;
        var frame2 = lpmBody.AsSpan(chunk1Body - 5, chunk2Body).ToArray();
        FrameProtocol.WriteFrame(ring,
            new FrameHeader(FrameType.Message, streamId: 1, (uint)frame2.Length, MessageFlags.More),
            frame2);

        var frame3Len = totalBodyLen - (chunk1Body - 5) - chunk2Body;
        var frame3 = lpmBody.AsSpan(chunk1Body - 5 + chunk2Body, frame3Len).ToArray();
        FrameProtocol.WriteFrame(ring,
            new FrameHeader(FrameType.Message, streamId: 1, (uint)frame3.Length, MessageFlags.EndStream),
            frame3);

        // Read all 3 chunks. Each must surface as Message with the
        // appropriate More / EndStream flags, payload bytes intact,
        // and ZC anchor held until the LAST Release.
        var (h1, p1) = FrameProtocol.ReadFramePayload(ring, zeroCopy: true);
        Assert.That(h1.Type, Is.EqualTo(FrameType.Message));
        Assert.That(((byte)h1.Flags & MessageFlags.More), Is.EqualTo(MessageFlags.More),
            "First chain chunk must have More flag set.");
        Assert.That(p1.IsSpeculativeZeroCopy, Is.True,
            "First chain chunk must be a speculative-ZC ring view (no copy).");
        Assert.That(ring.SpeculativeReservedBytes, Is.GreaterThan(0L),
            "Anchor must be held after first chunk surface.");
        Assert.That(ring.IsChainOpen, Is.True,
            "ChainOpen marker must be set during chain.");

        var (h2, p2) = FrameProtocol.ReadFramePayload(ring, zeroCopy: true);
        Assert.That(h2.Type, Is.EqualTo(FrameType.Message));
        Assert.That(((byte)h2.Flags & MessageFlags.More), Is.EqualTo(MessageFlags.More),
            "Middle chain chunk must have More flag set.");
        Assert.That(p2.IsSpeculativeZeroCopy, Is.True,
            "Middle chain chunk must be ZC.");

        var (h3, p3) = FrameProtocol.ReadFramePayload(ring, zeroCopy: true);
        Assert.That(h3.Type, Is.EqualTo(FrameType.Message));
        Assert.That(((byte)h3.Flags & MessageFlags.More), Is.EqualTo(0),
            "Final chain chunk must NOT have More flag.");
        Assert.That(((byte)h3.Flags & MessageFlags.EndStream), Is.EqualTo(MessageFlags.EndStream),
            "Final chain chunk inherits END_STREAM from H2 DATA frame.");
        Assert.That(ring.IsChainOpen, Is.False,
            "ChainOpen cleared after final chunk's CloseZcChain.");

        // Verify body content: first 5 bytes of p1 are LPM header,
        // then body[0..chunk1Body-5] in p1[5..], body[chunk1Body-5..chunk1Body-5+chunk2Body] in p2,
        // body[...] in p3.
        Assert.That(p1.Memory.Span[0], Is.EqualTo((byte)0), "compFlag");
        var declared = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(p1.Memory.Span.Slice(1, 4));
        Assert.That(declared, Is.EqualTo((uint)totalBodyLen));
        Assert.That(p1.Memory.Slice(5).ToArray(), Is.EquivalentTo(lpmBody.AsSpan(0, chunk1Body - 5).ToArray()));
        Assert.That(p2.Memory.ToArray(), Is.EquivalentTo(lpmBody.AsSpan(chunk1Body - 5, chunk2Body).ToArray()));
        Assert.That(p3.Memory.ToArray(), Is.EquivalentTo(lpmBody.AsSpan(chunk1Body - 5 + chunk2Body, frame3Len).ToArray()));

        // Release in arbitrary order — order independence is critical
        // because LazyChainRos releases prev-frame during next-segment's
        // GetSpan (i.e., during MergeFrom).
        p2.Release();
        Assert.That(ring.IsZcChainActive, Is.True, "Anchor still held while frames remain.");
        p1.Release();
        Assert.That(ring.IsZcChainActive, Is.True, "Anchor still held while final frame in flight.");
        p3.Release();
        Assert.That(ring.IsZcChainActive, Is.False,
            "Anchor must release after the last chain frame's Release publishes header.ReadIdx.");
        Assert.That(ring.SpeculativeReservedBytes, Is.EqualTo(0L),
            "No reserved bytes after all chunks released.");
        Assert.That(ring.UsedBytesApprox(), Is.EqualTo(0UL),
            "Ring fully drained — header.ReadIdx caught up to WriteIdx.");
    }

    /// <summary>
    /// Chain-ZC must NOT engage when SingleStreamMode is off, because
    /// the chain-frozen header.ReadIdx would starve any concurrent
    /// stream's reservations on the same ring. Verifies the explicit
    /// SingleStreamMode gate in IsChainZcStartEligible by feeding ALL
    /// chunks of a multi-frame LPM, then asserting (a) no chain anchor
    /// ever opened and (b) the message surfaced via the accumulator
    /// copy path (single FromPooled, no ZC).
    /// </summary>
    [Test]
    public void ChainZc_FallsBackToCopy_WhenSingleStreamModeOff()
    {
        var name = $"grpc_chainzc_ssoff_{Guid.NewGuid():N}";
        using var seg = Segment.Create(name, ringCapacity: 4 * 1024 * 1024, maxStreams: 10);
        var ring = seg.RingA;
        // Deliberately leave SingleStreamMode at its default (false).

        WriteChainFrames(ring, totalBodyLen: 1024 * 1024, compFlag: 0);

        // The codec will accumulate all 3 frames into LpmAccumulator.Buffer
        // and surface ONE Message at the end. We must drain all 3 codec-
        // level reads (the first 2 may return null = no completion).
        var (h, p) = DrainChainFromCodec(ring);
        try
        {
            Assert.That(h.Type, Is.EqualTo(FrameType.Message));
            Assert.That(p.IsSpeculativeZeroCopy, Is.False,
                "With SingleStreamMode=false the chain-ZC start gate rejects, " +
                "so the codec must accumulate-and-copy.");
            Assert.That(ring.IsChainOpen, Is.False,
                "_chainOpen must remain false: chain-ZC never started.");
            Assert.That(ring.SpeculativeReservedBytes, Is.EqualTo(0L),
                "No anchor held: no SpeculativeReservedBytes increment.");
        }
        finally { p.Release(); }
    }

    /// <summary>
    /// Chain-ZC must NOT engage when the gRPC LPM compression flag is set,
    /// because LazyChainRos-fed downstream consumers cannot feed segmented
    /// ROS to a decompressor.
    /// </summary>
    [Test]
    public void ChainZc_FallsBackToCopy_WhenCompressed()
    {
        var name = $"grpc_chainzc_cz_{Guid.NewGuid():N}";
        using var seg = Segment.Create(name, ringCapacity: 4 * 1024 * 1024, maxStreams: 10);
        var ring = seg.RingA;
        ring.SingleStreamMode = true;

        WriteChainFrames(ring, totalBodyLen: 1024 * 1024, compFlag: 1);

        var (h, p) = DrainChainFromCodec(ring);
        try
        {
            Assert.That(h.Type, Is.EqualTo(FrameType.Message));
            Assert.That(p.IsSpeculativeZeroCopy, Is.False,
                "compFlag != 0 must fall back to accumulator copy " +
                "(decompressor needs contiguous input).");
            Assert.That(ring.IsChainOpen, Is.False,
                "_chainOpen must remain false: compFlag gate rejected the chain.");
            Assert.That(ring.SpeculativeReservedBytes, Is.EqualTo(0L),
                "No anchor held: compFlag gate fired before BeginZcReservation.");
        }
        finally { p.Release(); }
    }

    /// <summary>
    /// Malformed-peer guard (2026-06-01 round-2): an H2 DATA frame with
    /// END_STREAM AND an incomplete LPM body (declared > body) must throw
    /// InvalidDataException BEFORE opening a chain anchor — otherwise the
    /// downstream LazyChainRos would block forever on the next Pull and
    /// the chain anchor would leak.
    /// </summary>
    [Test]
    public void ChainZc_MalformedEndStreamOnFirstFrame_ThrowsAndDoesNotOpenAnchor()
    {
        var name = $"grpc_chainzc_eos1_{Guid.NewGuid():N}";
        using var seg = Segment.Create(name, ringCapacity: 4 * 1024 * 1024, maxStreams: 10);
        var ring = seg.RingA;
        ring.SingleStreamMode = true;

        // Declare a 1 MiB LPM but only send a 400 KiB DATA frame with EOS.
        const int declaredBody = 1024 * 1024;
        const int chunk1Body = 400 * 1024;
        var frame1 = new byte[chunk1Body];
        frame1[0] = 0;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            frame1.AsSpan(1, 4), (uint)declaredBody);
        new Random(99).NextBytes(frame1.AsSpan(5));
        FrameProtocol.WriteFrame(ring,
            new FrameHeader(FrameType.Message, streamId: 1,
                (uint)frame1.Length, MessageFlags.EndStream),
            frame1);

        Assert.Throws<InvalidDataException>(() =>
            FrameProtocol.ReadFramePayload(ring, zeroCopy: true),
            "Incomplete-LPM END_STREAM on the first frame must throw " +
            "InvalidDataException before chain-ZC anchor opens.");

        Assert.That(ring.IsChainOpen, Is.False,
            "_chainOpen must NEVER have been set: malformed-EOS gate fires " +
            "before OpenZcChain.");
        Assert.That(ring.IsZcChainActive, Is.False,
            "_zcActive must NEVER have been set: malformed-EOS gate fires " +
            "before BeginZcReservation.");
        Assert.That(ring.SpeculativeReservedBytes, Is.EqualTo(0L),
            "No anchor held: malformed-EOS gate fires before " +
            "Interlocked.Add(SpeculativeReservedBytes).");
    }

    // Helper: writes a 3-frame multi-frame LPM (400 KiB + 400 KiB + tail)
    // with the given compression flag in the first chunk's LPM header.
    private static void WriteChainFrames(ShmRing ring, int totalBodyLen, byte compFlag)
    {
        var lpmBody = new byte[totalBodyLen];
        new Random(13).NextBytes(lpmBody);

        const int chunk1Body = 400 * 1024;
        var frame1 = new byte[chunk1Body];
        frame1[0] = compFlag;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            frame1.AsSpan(1, 4), (uint)totalBodyLen);
        lpmBody.AsSpan(0, chunk1Body - 5).CopyTo(frame1.AsSpan(5));
        FrameProtocol.WriteFrame(ring,
            new FrameHeader(FrameType.Message, streamId: 1, (uint)frame1.Length, MessageFlags.More),
            frame1);

        const int chunk2Body = 400 * 1024;
        var frame2 = lpmBody.AsSpan(chunk1Body - 5, chunk2Body).ToArray();
        FrameProtocol.WriteFrame(ring,
            new FrameHeader(FrameType.Message, streamId: 1, (uint)frame2.Length, MessageFlags.More),
            frame2);

        var frame3Len = totalBodyLen - (chunk1Body - 5) - chunk2Body;
        var frame3 = lpmBody.AsSpan(chunk1Body - 5 + chunk2Body, frame3Len).ToArray();
        FrameProtocol.WriteFrame(ring,
            new FrameHeader(FrameType.Message, streamId: 1, (uint)frame3.Length, MessageFlags.EndStream),
            frame3);
    }

    // Helper: drains a chain (or accumulator-assembled) message from the
    // codec. Returns the FINAL completed Message (the accumulator copy
    // path may return null on the intermediate frames; we drain until a
    // Message surfaces).
    private static (FrameHeader, FramePayload) DrainChainFromCodec(ShmRing ring)
    {
        // For the copy path the codec consumes intermediate frames into
        // LpmAccumulator and returns ONE Message on the final frame. For
        // the chain path it returns one Message per frame. Either way
        // ReadFramePayload only returns when SOME logical frame is
        // available, so a single call returns the FIRST surfaced
        // Message. For the copy path that is the assembled LPM (one
        // call); for the chain path we'd see the first chunk (three
        // calls total). Both cases this helper returns the first
        // surfaced frame, which is sufficient for the fall-back assertions.
        return FrameProtocol.ReadFramePayload(ring, zeroCopy: true);
    }
}
