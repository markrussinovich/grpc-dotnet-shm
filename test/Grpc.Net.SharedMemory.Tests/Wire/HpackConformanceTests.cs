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
using Grpc.Net.SharedMemory.Wire.Hpack;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests.Wire;

/// <summary>
/// HPACK conformance tests against RFC 7541 Appendix C canonical test vectors.
/// </summary>
[TestFixture]
public class HpackConformanceTests
{
    [Test]
    public void Decode_C21_LiteralWithIndexing()
    {
        var encoded = HexToBytes(
            "400a 6375 7374 6f6d 2d6b 6579 0d63 7573" +
            "746f 6d2d 6865 6164 6572");
        var headers = HpackDecoder.Decode(encoded);
        Assert.That(headers, Has.Count.EqualTo(1));
        Assert.That(headers[0].Name, Is.EqualTo("custom-key"));
        Assert.That(Encoding.ASCII.GetString(headers[0].Value), Is.EqualTo("custom-header"));
    }

    [Test]
    public void Decode_C22_LiteralWithoutIndexing_IndexedName()
    {
        var encoded = HexToBytes("040c 2f73 616d 706c 652f 7061 7468");
        var headers = HpackDecoder.Decode(encoded);
        Assert.That(headers, Has.Count.EqualTo(1));
        Assert.That(headers[0].Name, Is.EqualTo(":path"));
        Assert.That(Encoding.ASCII.GetString(headers[0].Value), Is.EqualTo("/sample/path"));
    }

    [Test]
    public void Decode_C23_LiteralNeverIndexed()
    {
        var encoded = HexToBytes("1008 7061 7373 776f 7264 0673 6563 7265 74");
        var headers = HpackDecoder.Decode(encoded);
        Assert.That(headers, Has.Count.EqualTo(1));
        Assert.That(headers[0].Name, Is.EqualTo("password"));
        Assert.That(Encoding.ASCII.GetString(headers[0].Value), Is.EqualTo("secret"));
    }

    [Test]
    public void Decode_C24_IndexedHeaderField()
    {
        var encoded = HexToBytes("82");
        var headers = HpackDecoder.Decode(encoded);
        Assert.That(headers, Has.Count.EqualTo(1));
        Assert.That(headers[0].Name, Is.EqualTo(":method"));
        Assert.That(Encoding.ASCII.GetString(headers[0].Value), Is.EqualTo("GET"));
    }

    [Test]
    public void Decode_C41_HuffmanRequest_FullStream()
    {
        var encoded = HexToBytes("8286 8441 8cf1 e3c2 e5f2 3a6b a0ab 90f4 ff");
        var headers = HpackDecoder.Decode(encoded);
        Assert.That(headers, Has.Count.EqualTo(4));
        Assert.That(headers[0].Name, Is.EqualTo(":method"));
        Assert.That(Encoding.ASCII.GetString(headers[0].Value), Is.EqualTo("GET"));
        Assert.That(headers[3].Name, Is.EqualTo(":authority"));
        Assert.That(Encoding.ASCII.GetString(headers[3].Value), Is.EqualTo("www.example.com"));
    }

    [Test]
    public void Decode_C61_HuffmanResponse_FullStream()
    {
        var encoded = HexToBytes(
            "4882 6402 5885 aec3 771a 4b61 96d0 7abe" +
            "9410 54d4 44a8 2005 9504 0b81 66e0 82a6" +
            "2d1b ff6e 919d 29ad 1718 63c7 8f0b 97c8" +
            "e9ae 82ae 43d3");
        var headers = HpackDecoder.Decode(encoded);
        Assert.That(headers, Has.Count.EqualTo(4));
        Assert.That(headers[0].Name, Is.EqualTo(":status"));
        Assert.That(Encoding.ASCII.GetString(headers[0].Value), Is.EqualTo("302"));
        Assert.That(headers[2].Name, Is.EqualTo("date"));
        Assert.That(Encoding.ASCII.GetString(headers[2].Value), Is.EqualTo("Mon, 21 Oct 2013 20:13:21 GMT"));
    }

    [Test]
    public void Encode_OutputIs_StructurallyValid_HpackBlock()
    {
        var input = new List<(string Name, byte[] Value)>
        {
            (":method", Encoding.ASCII.GetBytes("POST")),
            (":scheme", Encoding.ASCII.GetBytes("http")),
            (":path", Encoding.ASCII.GetBytes("/svc/method")),
            ("custom-h", Encoding.UTF8.GetBytes("v")),
        };

        var (buf, len) = HpackEncoder.Encode(input);
        try
        {
            var decoded = HpackDecoder.Decode(buf.AsSpan(0, len));
            Assert.That(decoded, Has.Count.EqualTo(input.Count));
            for (var i = 0; i < input.Count; i++)
            {
                Assert.That(decoded[i].Name, Is.EqualTo(input[i].Name));
                Assert.That(decoded[i].Value, Is.EquivalentTo(input[i].Value));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static byte[] HexToBytes(string hex)
    {
        var clean = hex.Replace(" ", "", StringComparison.Ordinal);
        var result = new byte[clean.Length / 2];
        for (var i = 0; i < result.Length; i++)
            result[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
        return result;
    }
}
