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
