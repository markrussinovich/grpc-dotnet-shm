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
using Grpc.Net.SharedMemory.Wire.Hpack;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests.Wire;

[TestFixture]
public class HpackTests
{
    [Test]
    public void IndexedAndLiteral_RoundTrip_ReproducesHeaders()
    {
        var input = new List<(string, byte[])>
        {
            (":method", Encoding.ASCII.GetBytes("POST")),
            (":scheme", Encoding.ASCII.GetBytes("http")),
            (":path", Encoding.ASCII.GetBytes("/greet.Greeter/SayHello")),
            (":authority", Encoding.ASCII.GetBytes("localhost")),
            ("content-type", Encoding.ASCII.GetBytes("application/grpc")),
            ("user-agent", Encoding.ASCII.GetBytes("grpc-dotnet-shm/test")),
            ("custom-header", Encoding.UTF8.GetBytes("hello world")),
        };

        var (buf, len) = HpackEncoder.Encode(input);
        try
        {
            var decoded = HpackDecoder.Decode(buf.AsSpan(0, len));
            Assert.That(decoded, Has.Count.EqualTo(input.Count));
            for (var i = 0; i < input.Count; i++)
            {
                Assert.That(decoded[i].Name, Is.EqualTo(input[i].Item1));
                Assert.That(decoded[i].Value, Is.EquivalentTo(input[i].Item2));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    [Test]
    public void DecoderRejectsDynamicTableSizeUpdateNonZero()
    {
        var blob = new byte[] { 0b0010_0001 };
        Assert.Throws<InvalidDataException>(() => HpackDecoder.Decode(blob));
    }

    [Test]
    public void DecoderAcceptsDynamicTableSizeUpdateZero_FollowedByHeader()
    {
        // RFC 7541 §6.3 dynamic table size update. We advertise
        // SETTINGS_HEADER_TABLE_SIZE=0 so the only legitimate update from
        // a peer is size=0 (acknowledging our advertisement, or explicitly
        // disabling its own dynamic table after a previous non-zero
        // setting). Encoding for size=0 with a 5-bit prefix is 0b001_00000
        // = 0x20 (no continuation bytes since 0 < 31).
        //
        // After the size update, a real-world peer typically continues
        // with normal indexed/literal headers. We verify the decoder
        // accepts the size=0 update as a no-op AND continues parsing.
        var blob = new byte[]
        {
            0x20,        // size update, value=0
            0x82,        // indexed header field, idx=2 → :method GET
        };
        var headers = HpackDecoder.Decode(blob);
        Assert.That(headers, Has.Count.EqualTo(1));
        Assert.That(headers[0].Name, Is.EqualTo(":method"));
        Assert.That(System.Text.Encoding.ASCII.GetString(headers[0].Value), Is.EqualTo("GET"));
    }

    [Test]
    public void HuffmanRoundTrip_ViaKnownVector_DecodesCorrectly()
    {
        var encoded = new byte[]
        {
            0xf1, 0xe3, 0xc2, 0xe5, 0xf2, 0x3a, 0x6b, 0xa0, 0xab, 0x90, 0xf4, 0xff,
        };
        var dst = new byte[64];
        var written = HpackHuffmanDecoder.Decode(encoded, dst);
        var s = Encoding.ASCII.GetString(dst, 0, written);
        Assert.That(s, Is.EqualTo("www.example.com"));
    }

    [Test]
    public void IntegerEncoder_RoundTrip_VariousValues()
    {
        Span<byte> buf = stackalloc byte[8];
        for (var prefixBits = 4; prefixBits <= 7; prefixBits++)
        {
            uint[] cases = { 0, 1, 5, 10, 30, 100, 1000, 100000 };
            foreach (var v in cases)
            {
                var written = HpackInteger.Encode(v, prefixBits, 0, buf);
                var first = buf[0];
                var decoded = HpackInteger.Decode(first, prefixBits, buf.Slice(1, written - 1), out var read);
                Assert.That(decoded, Is.EqualTo(v));
                Assert.That(read, Is.EqualTo(written - 1));
            }
        }
    }

    [Test]
    public void StaticTable_FindExact_KnownEntries()
    {
        Assert.That(HpackStaticTable.FindExact(":method", "POST"), Is.EqualTo(3));
        Assert.That(HpackStaticTable.FindExact(":scheme", "http"), Is.EqualTo(6));
        Assert.That(HpackStaticTable.FindExact(":scheme", "https"), Is.EqualTo(7));
        Assert.That(HpackStaticTable.FindExact(":status", "200"), Is.EqualTo(8));
        Assert.That(HpackStaticTable.FindExact(":authority", ""), Is.EqualTo(1));
        Assert.That(HpackStaticTable.FindExact("user-agent", "anything"), Is.EqualTo(0));
        Assert.That(HpackStaticTable.FindName("user-agent"), Is.EqualTo(58));
    }

    [Test]
    public void IntegerDecoder_RejectsOverflow_AllContinuationBytesMaxed()
    {
        // RFC 7541 §5.1: encoded integers must fit in uint32.
        //
        // Construct a sequence whose final byte clears the continuation
        // bit but causes the running uint accumulator to wrap. The
        // pre-fix decoder used native uint arithmetic without overflow
        // detection on the FINAL accumulation step (only the next-byte
        // shift counter could throw), so it returned a silently-wrapped
        // small value instead of erroring.
        //
        // prefix = 7-bit, max = 127 (signals continuation).
        // bytes 0..3 (each 0xFF):
        //   value += 0x7F << 0 / 7 / 14 / 21
        //   accumulated = 127 + 0x7F + 0x3F80 + 0x1FC000 + 0xFE000000
        //               = 0xFE20407F (just under 4 GiB)
        // byte 4 = 0x10:
        //   high bit clear → loop terminates here (no shift bump → old
        //   code's `shift >= 32` check never fires)
        //   contribution = 0x10 << 28 = 0x1_00000000 → uint addition
        //   silently wraps; value finally returned = 0xFE20407F (wrong).
        //   Correct behaviour: throw InvalidDataException.
        var source = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x10 };
        Assert.Throws<InvalidDataException>(() =>
        {
            HpackInteger.Decode(firstByte: 0x7F, prefixBits: 7, source, out _);
        });
    }

    [Test]
    public void IntegerDecoder_RejectsOverflow_LongContinuation()
    {
        // Pathological: 8 continuation bytes — far beyond the 32-bit
        // uint range. Decoder must throw before completing the loop.
        var source = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };
        Assert.Throws<InvalidDataException>(() =>
        {
            HpackInteger.Decode(firstByte: 0x7F, prefixBits: 7, source, out _);
        });
    }

    [Test]
    public void HuffmanDecoder_RejectsEndOfInputMidSymbolWithNonOnesPrefix()
    {
        // Construct a valid Huffman prefix of an internal trie node whose
        // path is NOT all-1s, then truncate the stream there. RFC 7541 §5.2
        // requires trailing bits at end-of-input to be a strict prefix of
        // the EOS code (all 1s). A non-all-ones partial path means the
        // peer truncated mid-symbol on a non-padding boundary.
        //
        // Symbol 'a' = 0b00011 (5 bits). Encode just one byte with the
        // top 5 bits = 00011 and the trailing 3 bits = 0 (padding zeros).
        // Decoder should successfully read 'a' from the first 5 bits, then
        // see end-of-input with 3 remaining 0-bits. Since 0-bit padding
        // does not match the EOS-prefix all-ones requirement, the
        // remaining bits route to the `next == 0` branch first which
        // catches the invalid padding. To test the END-OF-BUFFER branch
        // specifically, encode a partial code that ends ON a 0-bit
        // transition INSIDE the trie:
        //   Symbol 'b' = 0b100011 (6 bits). Encode just 4 bits (1000),
        //   with trailing 4 bits also 0. 1000-prefix walks the trie to
        //   an internal node along a 0-bit, so end-of-byte leaves
        //   `current != 0` and `partialAllOnes == false`.
        var encoded = new byte[] { 0b1000_0000 };
        var dst = new byte[16];
        Assert.Throws<InvalidDataException>(() =>
            HpackHuffmanDecoder.Decode(encoded, dst));
    }

    [Test]
    public void HuffmanDecoder_AcceptsValidEosPaddingAtEnd()
    {
        // Sanity: encoding "www.example.com" (Appendix C.4 of RFC 7541)
        // ends with 6 padding bits = 111111 (prefix of EOS code = 30 ones).
        // This case is covered by HuffmanRoundTrip_ViaKnownVector_DecodesCorrectly
        // already; replicate here as a regression guard against the
        // tightened end-of-input check accidentally rejecting valid
        // EOS-prefix padding.
        var encoded = new byte[]
        {
            0xf1, 0xe3, 0xc2, 0xe5, 0xf2, 0x3a, 0x6b, 0xa0, 0xab, 0x90, 0xf4, 0xff,
        };
        var dst = new byte[64];
        var written = HpackHuffmanDecoder.Decode(encoded, dst);
        Assert.That(System.Text.Encoding.ASCII.GetString(dst, 0, written),
            Is.EqualTo("www.example.com"));
    }
}

[TestFixture]
public class Http2FrameHeaderTests
{
    [Test]
    public void Encode_Decode_RoundTrip()
    {
        Span<byte> buf = stackalloc byte[Http2FrameHeader.Size];
        Http2FrameHeader.Encode(buf, Http2FrameType.Data, 0x05, streamId: 0x1234567, payloadLength: 1024);
        var (type, flags, len, sid) = Http2FrameHeader.Decode(buf);
        Assert.That(type, Is.EqualTo(Http2FrameType.Data));
        Assert.That(flags, Is.EqualTo((byte)0x05));
        Assert.That(len, Is.EqualTo(1024));
        Assert.That(sid, Is.EqualTo(0x1234567u));
    }

    [Test]
    public void Encode_RejectsOutOfRangeLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            Span<byte> b = stackalloc byte[Http2FrameHeader.Size];
            Http2FrameHeader.Encode(b, Http2FrameType.Data, 0, 1, payloadLength: (1 << 24));
        });
    }

    [Test]
    public void Encode_RejectsHighBitInStreamId()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            Span<byte> b = stackalloc byte[Http2FrameHeader.Size];
            Http2FrameHeader.Encode(b, Http2FrameType.Data, 0, 0x80000000u, 0);
        });
    }

    [Test]
    public void Decode_MasksReservedBitOfStreamId()
    {
        Span<byte> buf = stackalloc byte[Http2FrameHeader.Size];
        buf[0] = 0; buf[1] = 0; buf[2] = 0;
        buf[3] = (byte)Http2FrameType.Data;
        buf[4] = 0;
        buf[5] = 0xFF; buf[6] = 0xFF; buf[7] = 0xFF; buf[8] = 0xFF;
        var (_, _, _, sid) = Http2FrameHeader.Decode(buf);
        Assert.That(sid, Is.EqualTo(0x7FFFFFFFu));
    }
}

[TestFixture]
public class HpackHeadersAdapterTests
{
    [Test]
    public void EncodeDecodeClientInitial_RoundTrip()
    {
        var v1 = new HeadersV1
        {
            HeaderType = 0,
            Method = "/greet.Greeter/SayHello",
            Authority = "localhost",
            DeadlineUnixNano = 0,
            Metadata = new[]
            {
                new MetadataKV("custom-1", "value-1"),
                new MetadataKV("custom-2", "value-2"),
            },
        };
        var (buf, len) = HpackHeadersAdapter.EncodeHeaders(v1);
        try
        {
            var roundtrip = HpackHeadersAdapter.DecodeHeaders(buf.AsSpan(0, len));
            Assert.That(roundtrip.HeaderType, Is.EqualTo((byte)0));
            Assert.That(roundtrip.Method, Is.EqualTo(v1.Method));
            Assert.That(roundtrip.Authority, Is.EqualTo(v1.Authority));
            Assert.That(roundtrip.Metadata.Count, Is.EqualTo(2));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    [Test]
    public void EncodeDecodeServerInitial_RoundTrip()
    {
        var v1 = new HeadersV1 { HeaderType = 1 };
        var (buf, len) = HpackHeadersAdapter.EncodeHeaders(v1);
        try
        {
            var roundtrip = HpackHeadersAdapter.DecodeHeaders(buf.AsSpan(0, len));
            Assert.That(roundtrip.HeaderType, Is.EqualTo((byte)1));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    [Test]
    public void EncodeDecodeTrailers_RoundTrip()
    {
        var v1 = new TrailersV1
        {
            GrpcStatusCode = global::Grpc.Core.StatusCode.OK,
            GrpcStatusMessage = "ok",
            Metadata = new[] { new MetadataKV("trailer-1", "x") },
        };
        var (buf, len) = HpackHeadersAdapter.EncodeTrailers(v1);
        try
        {
            var rt = HpackHeadersAdapter.DecodeTrailers(buf.AsSpan(0, len));
            Assert.That(rt.GrpcStatusCode, Is.EqualTo(global::Grpc.Core.StatusCode.OK));
            Assert.That(rt.GrpcStatusMessage, Is.EqualTo("ok"));
            Assert.That(rt.Metadata.Count, Is.EqualTo(1));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    [Test]
    public void EncodeHeaders_BinaryMetadata_EmitsBase64OnTheWire()
    {
        // gRFC G2 / gRPC over HTTP/2 §"Binary headers": values for
        // metadata keys ending in "-bin" MUST be base64-encoded on the
        // HTTP/2 wire (HPACK layer). Our internal HeadersV1 stores raw
        // bytes; the HpackHeadersAdapter is the boundary that does the
        // conversion. Without this, real H2 peers (grpc-go, grpc-java,
        // grpc-c++) would mis-interpret our binary metadata or reject
        // values that contain bytes the HTTP header grammar disallows.
        var rawBinary = new byte[] { 0x00, 0x01, 0xFF, 0x80, 0x10, 0x20 }; // contains non-printable bytes
        var v1 = new HeadersV1
        {
            HeaderType = 0,
            Method = "/svc/M",
            Authority = "h",
            Metadata = new[] { new MetadataKV("custom-trailer-bin", rawBinary) },
        };
        var (buf, len) = HpackHeadersAdapter.EncodeHeaders(v1);
        try
        {
            // Decode the raw HPACK output (NOT via the adapter) to inspect
            // exactly what bytes went on the wire.
            var rawHeaders = HpackDecoder.Decode(buf.AsSpan(0, len));
            var found = rawHeaders.FirstOrDefault(h => h.Name == "custom-trailer-bin");
            Assert.That(found.Name, Is.EqualTo("custom-trailer-bin"));

            // The wire value must be the base64 ASCII text of rawBinary.
            var expectedBase64 = Convert.ToBase64String(rawBinary);
            var actualWireText = Encoding.ASCII.GetString(found.Value);
            Assert.That(actualWireText, Is.EqualTo(expectedBase64),
                "Binary metadata MUST be base64-encoded on the H2/HPACK wire (gRFC G2).");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    [Test]
    public void DecodeHeaders_BinaryMetadata_RoundTripsRawBytes()
    {
        // End-to-end through our adapter: encode raw bytes via the adapter,
        // decode via the adapter, expect raw bytes back. The adapter's
        // base64 encode/decode pair must cancel exactly.
        var rawBinary = new byte[] { 0x00, 0x01, 0xFF, 0x80, 0x10, 0x20, 0x42 };
        var v1 = new HeadersV1
        {
            HeaderType = 0,
            Method = "/svc/M",
            Authority = "h",
            Metadata = new[] { new MetadataKV("trace-id-bin", rawBinary) },
        };
        var (buf, len) = HpackHeadersAdapter.EncodeHeaders(v1);
        try
        {
            var roundTripped = HpackHeadersAdapter.DecodeHeaders(buf.AsSpan(0, len));
            Assert.That(roundTripped.Metadata, Has.Count.EqualTo(1));
            Assert.That(roundTripped.Metadata[0].Key, Is.EqualTo("trace-id-bin"));
            Assert.That(roundTripped.Metadata[0].Values, Has.Count.EqualTo(1));
            Assert.That(roundTripped.Metadata[0].Values[0], Is.EquivalentTo(rawBinary),
                "Adapter must round-trip raw -bin metadata bytes via base64 encode/decode.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    [Test]
    public void DecodeTrailers_BinaryMetadata_RoundTripsRawBytes()
    {
        // Same round-trip property for trailers (status-details-bin etc.).
        var rawDetails = new byte[] { 0x12, 0x05, 0x68, 0x65, 0x6C, 0x6C, 0x6F }; // a tiny google.rpc.Status proto
        var v1 = new TrailersV1
        {
            GrpcStatusCode = global::Grpc.Core.StatusCode.NotFound,
            Metadata = new[] { new MetadataKV("grpc-status-details-bin", rawDetails) },
        };
        var (buf, len) = HpackHeadersAdapter.EncodeTrailers(v1);
        try
        {
            var rt = HpackHeadersAdapter.DecodeTrailers(buf.AsSpan(0, len));
            Assert.That(rt.Metadata, Has.Count.EqualTo(1));
            Assert.That(rt.Metadata[0].Key, Is.EqualTo("grpc-status-details-bin"));
            Assert.That(rt.Metadata[0].Values[0], Is.EquivalentTo(rawDetails));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    [Test]
    public void DecodeHeaders_NonBinaryMetadata_NoBase64Treatment()
    {
        // Plain-text metadata (key NOT ending in "-bin") must pass through
        // verbatim with no base64 transformation. Specifically: if a
        // user-supplied non-binary value happens to be valid base64 by
        // accident, we must NOT decode it.
        var v1 = new HeadersV1
        {
            HeaderType = 0,
            Method = "/svc/M",
            Authority = "h",
            Metadata = new[] { new MetadataKV("user-id", "QUJD") }, // looks like base64 of "ABC"
        };
        var (buf, len) = HpackHeadersAdapter.EncodeHeaders(v1);
        try
        {
            var rt = HpackHeadersAdapter.DecodeHeaders(buf.AsSpan(0, len));
            var roundTrippedText = Encoding.UTF8.GetString(rt.Metadata[0].Values[0]);
            Assert.That(roundTrippedText, Is.EqualTo("QUJD"),
                "Non-binary metadata must NOT be base64-decoded even if it looks like base64.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }
}
