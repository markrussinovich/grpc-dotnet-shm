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
}
