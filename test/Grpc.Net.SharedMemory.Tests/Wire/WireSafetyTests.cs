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

using System.Buffers.Binary;
using Grpc.Net.SharedMemory.Wire;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests.Wire;

/// <summary>
/// Defensive tests for the wire-protocol parsers: malformed inputs from a
/// peer must be rejected with a clear protocol error rather than silently
/// downgraded, truncated, or used to drive resource allocation.
/// </summary>
[TestFixture]
public class WireSafetyTests
{
    // ---- ControlWire CONNECT request validation ----

    [Test]
    public void DecodeConnectRequest_TruncatedAdvertisement_Throws()
    {
        // Version + ringA(8) + ringB(8) + flags(1) + count(1=2) but only 1 format byte present.
        var bad = new byte[]
        {
            ShmConstants.ControlWireVersion,
            0,0,0,0,0,0,0,0,           // ringA
            0,0,0,0,0,0,0,0,           // ringB
            0,                          // flags
            2,                          // declares 2 formats
            1,                          // only one format byte
        };
        var ex = Assert.Throws<InvalidDataException>(() => ControlWire.DecodeConnectRequest(bad));
        Assert.That(ex!.Message, Does.Contain("truncated"));
    }

    [Test]
    public void DecodeConnectRequest_NoH2Advertisement_Throws()
    {
        // Peer advertises only Custom16 (legacy, value 0). Server must reject.
        var bad = new byte[]
        {
            ShmConstants.ControlWireVersion,
            0,0,0,0,0,0,0,0,
            0,0,0,0,0,0,0,0,
            0,
            1,
            0,                          // Custom16 only
        };
        var ex = Assert.Throws<InvalidDataException>(() => ControlWire.DecodeConnectRequest(bad));
        Assert.That(ex!.Message, Does.Contain("HTTP/2"));
    }

    [Test]
    public void DecodeConnectRequest_MissingExtension_Throws()
    {
        // Legacy v1 payload (just version + ring + flags), no wire-format byte.
        var bad = new byte[]
        {
            ShmConstants.ControlWireVersion,
            0,0,0,0,0,0,0,0,
            0,0,0,0,0,0,0,0,
            0,
        };
        var ex = Assert.Throws<InvalidDataException>(() => ControlWire.DecodeConnectRequest(bad));
        Assert.That(ex!.Message, Does.Contain("HTTP/2"));
    }

    [Test]
    public void DecodeConnectRequest_ValidH2Advertisement_Roundtrips()
    {
        var encoded = ControlWire.EncodeConnectRequest(
            ringA: 4096, ringB: 4096, singleStreamMode: true);
        var (a, b, ss) = ControlWire.DecodeConnectRequest(encoded);
        Assert.That(a, Is.EqualTo(4096UL));
        Assert.That(b, Is.EqualTo(4096UL));
        Assert.That(ss, Is.True);
    }

    // ---- ControlWire ACCEPT response validation ----

    [Test]
    public void DecodeConnectResponse_NonHttp2WireFormat_Throws()
    {
        // version + nameLen(4=1) + 'X' + 0xFE
        var bad = new byte[]
        {
            ShmConstants.ControlWireVersion,
            1,0,0,0,
            (byte)'X',
            0xFE,
        };
        var ex = Assert.Throws<InvalidDataException>(() => ControlWire.DecodeConnectResponse(bad));
        Assert.That(ex!.Message, Does.Contain("HTTP/2"));
    }

    [Test]
    public void DecodeConnectResponse_NoExtension_Throws()
    {
        // No trailing format byte. Legacy responses are no longer accepted —
        // a server that does not announce H2 is a protocol error.
        var bad = new byte[]
        {
            ShmConstants.ControlWireVersion,
            3,0,0,0,
            (byte)'s', (byte)'e', (byte)'g',
        };
        var ex = Assert.Throws<InvalidDataException>(() => ControlWire.DecodeConnectResponse(bad));
        Assert.That(ex!.Message, Does.Contain("missing wire-format byte"));
    }

    [Test]
    public void DecodeConnectResponse_Http2_Roundtrips()
    {
        var encoded = ControlWire.EncodeConnectResponse("seg");
        var name = ControlWire.DecodeConnectResponse(encoded);
        Assert.That(name, Is.EqualTo("seg"));
    }

    // ---- LPM body length DoS guard ----

    [Test]
    public void Read_LpmHeader_DeclaringHugeBody_Throws_NoLargeAllocation()
    {
        const int RingCap = 64 * 1024;
        var memory = new byte[ShmConstants.RingHeaderSize + RingCap];
        using var ring = new ShmRing(memory, 0, RingCap);

        // Craft a DATA frame containing exactly 5 bytes (the LPM header) declaring
        // a 2 GiB body. The accumulator path will see a partial LPM and try to
        // allocate `5 + bodyLen`. The DoS guard must reject this.
        // Wire layout: 9-byte H2 DATA header + 5-byte LPM header.
        const int payloadLen = 5;
        const uint giantBodyLen = (uint)2_000_000_000;

        Span<byte> hdr = stackalloc byte[Http2FrameHeader.Size];
        Http2FrameHeader.Encode(hdr, Http2FrameType.Data, flags: 0, streamId: 1, payloadLen);
        var reservation = ring.ReserveWrite(Http2FrameHeader.Size + payloadLen, default);
        // Header
        hdr.CopyTo(reservation.First.Span);
        // LPM header inside DATA payload: compressed(1) + length(4 BE)
        var lpmStart = Http2FrameHeader.Size;
        reservation.First.Span[lpmStart] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(
            reservation.First.Span.Slice(lpmStart + 1, 4), giantBodyLen);
        ring.CommitWrite(reservation, Http2FrameHeader.Size + payloadLen);

        // Now send a follow-up empty DATA so the reader has something to wait on
        // after rejecting; but the rejection itself should happen on the first
        // ReadFramePayload call.
        Assert.Throws<InvalidDataException>(() => FrameProtocol.ReadFramePayload(ring));
    }
}