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
using Grpc.Core;
using Grpc.Net.SharedMemory.Wire;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

[TestFixture]
[Platform("Win")]
public class Http2NegotiatedE2ETests
{
    private static byte[] WrapLpm(ReadOnlySpan<byte> body)
    {
        var buf = new byte[5 + body.Length];
        buf[0] = 0;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(1, 4), (uint)body.Length);
        body.CopyTo(buf.AsSpan(5));
        return buf;
    }

    private static byte[] UnwrapLpm(byte[] framed)
    {
        var len = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(framed.AsSpan(1, 4));
        var body = new byte[len];
        framed.AsSpan(5, len).CopyTo(body);
        return body;
    }

    [Test]
    [CancelAfter(15000)]
    public async Task NegotiatedHttp2_UnaryRoundTrip_Works()
    {
        var baseName = $"grpc_h2neg_{Guid.NewGuid():N}";
        using var listener = new ShmControlListener(baseName, ringCapacity: 8192, maxStreams: 100);

        var serverTask = Task.Run(async () =>
        {
            var conn = await listener.AcceptAsync();
            try
            {
                var stream = await conn.AcceptStreamAsync();
                using var s = stream!;
                byte[]? received = null;
                await foreach (var m in s.ReceiveMessagesAsync()) received = UnwrapLpm(m);
                await s.SendResponseHeadersAsync();
                await s.SendMessageAsync(WrapLpm(received!));
                await s.SendTrailersAsync(StatusCode.OK, "ok");
                return received;
            }
            finally
            {
                conn.Dispose();
            }
        });

        using var handler = new ShmControlHandler(baseName,
            new ShmClientTransportOptions { PreferHttp2 = true });
        var conn = await handler.ConnectForTest(default);
        try
        {
            using var cs = conn.CreateStream();
            var payload = Encoding.UTF8.GetBytes("hello via H2 over SHM");
            await cs.SendRequestHeadersAsync("/svc/UnaryCall", "localhost");
            await cs.SendMessageAsync(WrapLpm(payload));
            await cs.SendHalfCloseAsync();

            await cs.ReceiveResponseHeadersAsync();
            byte[]? resp = null;
            await foreach (var m in cs.ReceiveMessagesAsync()) resp = UnwrapLpm(m);

            var received = await serverTask;
            Assert.That(received, Is.EqualTo(payload));
            Assert.That(resp, Is.EqualTo(payload));
            Assert.That(conn.GetTxRingWireFormatForTest(), Is.EqualTo(WireFormat.Http2));
        }
        finally
        {
            conn.Dispose();
        }
    }
}
