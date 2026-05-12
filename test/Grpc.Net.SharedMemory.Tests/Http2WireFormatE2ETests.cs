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
using System.Text;
using Grpc.Core;
using Grpc.Net.SharedMemory.Wire;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

[TestFixture]
[Platform("Win")]
public class Http2WireFormatE2ETests
{
    private static byte[] WrapLpm(ReadOnlySpan<byte> body)
    {
        var buf = new byte[5 + body.Length];
        buf[0] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(1, 4), (uint)body.Length);
        body.CopyTo(buf.AsSpan(5));
        return buf;
    }

    private static byte[] UnwrapLpm(byte[] framed)
    {
        var len = (int)BinaryPrimitives.ReadUInt32BigEndian(framed.AsSpan(1, 4));
        var body = new byte[len];
        framed.AsSpan(5, len).CopyTo(body);
        return body;
    }

    [Test]
    [CancelAfter(10000)]
    public async Task UnaryCall_OverHttp2Wire_Works()
    {
        var segmentName = $"grpc_h2_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var requestData = Encoding.UTF8.GetBytes("ClientPayload");
        var responseData = Encoding.UTF8.GetBytes("Hello over HTTP/2!");

        var serverTask = Task.Run(async () =>
        {
            var stream = await server.AcceptStreamAsync();
            using var s = stream!;
            byte[]? received = null;
            await foreach (var m in s.ReceiveMessagesAsync()) received = UnwrapLpm(m);
            await s.SendResponseHeadersAsync();
            await s.SendMessageAsync(WrapLpm(responseData));
            await s.SendTrailersAsync(StatusCode.OK, "ok");
            return received;
        });

        using var cs = client.CreateStream();
        await cs.SendRequestHeadersAsync("/greet.Greeter/SayHello", "localhost");
        await cs.SendMessageAsync(WrapLpm(requestData));
        await cs.SendHalfCloseAsync();

        await cs.ReceiveResponseHeadersAsync();
        byte[]? resp = null;
        await foreach (var m in cs.ReceiveMessagesAsync()) resp = UnwrapLpm(m);

        var serverReceived = await serverTask;

        Assert.That(serverReceived, Is.EqualTo(requestData));
        Assert.That(resp, Is.EqualTo(responseData));
        Assert.That(cs.Trailers!.GrpcStatusCode, Is.EqualTo(StatusCode.OK));
    }

    [Test]
    [CancelAfter(10000)]
    public async Task ServerStreaming_OverHttp2Wire_Works()
    {
        var segmentName = $"grpc_h2_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 8192, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        const int messageCount = 5;

        var serverTask = Task.Run(async () =>
        {
            var stream = await server.AcceptStreamAsync();
            using var s = stream!;
            await foreach (var _ in s.ReceiveMessagesAsync()) { }
            await s.SendResponseHeadersAsync();
            for (var i = 0; i < messageCount; i++)
            {
                await s.SendMessageAsync(WrapLpm(Encoding.UTF8.GetBytes($"msg-{i}")));
            }
            await s.SendTrailersAsync(StatusCode.OK, "done");
        });

        using var cs = client.CreateStream();
        await cs.SendRequestHeadersAsync("/svc/Stream", "localhost");
        await cs.SendHalfCloseAsync();

        await cs.ReceiveResponseHeadersAsync();
        var received = new List<string>();
        await foreach (var m in cs.ReceiveMessagesAsync())
            received.Add(Encoding.UTF8.GetString(UnwrapLpm(m)));

        await serverTask;
        Assert.That(received.Count, Is.EqualTo(messageCount));
        for (var i = 0; i < messageCount; i++)
            Assert.That(received[i], Is.EqualTo($"msg-{i}"));
    }

    [Test]
    [CancelAfter(15000)]
    public async Task LargePayload_OverHttp2Wire_PreservesBytes()
    {
        var segmentName = $"grpc_h2_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 64 * 1024, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        const int payloadSize = 4 * 1024;
        var rng = new Random(0x1234);
        var requestData = new byte[payloadSize];
        rng.NextBytes(requestData);

        var serverTask = Task.Run(async () =>
        {
            var stream = await server.AcceptStreamAsync();
            using var s = stream!;
            byte[]? received = null;
            await foreach (var m in s.ReceiveMessagesAsync()) received = UnwrapLpm(m);
            await s.SendResponseHeadersAsync();
            await s.SendMessageAsync(WrapLpm(received!));
            await s.SendTrailersAsync(StatusCode.OK, "ok");
            return received;
        });

        using var cs = client.CreateStream();
        await cs.SendRequestHeadersAsync("/svc/Echo", "localhost");
        await cs.SendMessageAsync(WrapLpm(requestData));
        await cs.SendHalfCloseAsync();

        await cs.ReceiveResponseHeadersAsync();
        byte[]? resp = null;
        await foreach (var m in cs.ReceiveMessagesAsync()) resp = UnwrapLpm(m);

        var serverReceived = await serverTask;
        Assert.That(serverReceived, Is.EqualTo(requestData));
        Assert.That(resp, Is.EqualTo(requestData));
    }

    [Test]
    [CancelAfter(60000)]
    public async Task SixteenMB_OverHttp2Wire_PreservesBytes()
    {
        var segmentName = $"grpc_h2_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 64 * 1024 * 1024, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        const int payloadSize = 16 * 1024 * 1024;
        var rng = new Random(0x9876);
        var requestData = new byte[payloadSize];
        rng.NextBytes(requestData);

        var serverTask = Task.Run(async () =>
        {
            var stream = await server.AcceptStreamAsync();
            using var s = stream!;
            byte[]? received = null;
            await foreach (var m in s.ReceiveMessagesAsync()) received = UnwrapLpm(m);
            await s.SendResponseHeadersAsync();
            await s.SendMessageAsync(WrapLpm(received!));
            await s.SendTrailersAsync(StatusCode.OK, "ok");
            return received;
        });

        using var cs = client.CreateStream();
        await cs.SendRequestHeadersAsync("/svc/BigEcho", "localhost");
        await cs.SendMessageAsync(WrapLpm(requestData));
        await cs.SendHalfCloseAsync();

        await cs.ReceiveResponseHeadersAsync();
        byte[]? resp = null;
        await foreach (var m in cs.ReceiveMessagesAsync()) resp = UnwrapLpm(m);

        var serverReceived = await serverTask;
        Assert.That(serverReceived, Is.Not.Null);
        Assert.That(serverReceived!.Length, Is.EqualTo(payloadSize));
        Assert.That(resp, Is.Not.Null);
        Assert.That(resp!.Length, Is.EqualTo(payloadSize));
        Assert.That(resp[0], Is.EqualTo(requestData[0]));
        Assert.That(resp[payloadSize - 1], Is.EqualTo(requestData[payloadSize - 1]));
        Assert.That(resp[payloadSize / 2], Is.EqualTo(requestData[payloadSize / 2]));
    }

    /// <summary>
    /// Boundary cases for HTTP/2's 24-bit frame length cap.
    /// Skips the small-ring (4 KiB) cases because the underlying transport
    /// has a pre-existing limitation with payloads >> ring capacity.
    /// </summary>
    [TestCaseSource(nameof(H2BoundarySizes))]
    [CancelAfter(60000)]
    public async Task H2_PayloadAtFrameBoundary_RoundTrip(int appPayloadSize, ulong ringCapacity)
    {
        var segmentName = $"grpc_h2bnd_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, ringCapacity, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var rng = new Random(0x55AA);
        var requestData = new byte[appPayloadSize];
        rng.NextBytes(requestData);

        var serverTask = Task.Run(async () =>
        {
            var stream = await server.AcceptStreamAsync();
            using var s = stream!;
            byte[]? received = null;
            await foreach (var m in s.ReceiveMessagesAsync()) received = UnwrapLpm(m);
            await s.SendResponseHeadersAsync();
            await s.SendMessageAsync(WrapLpm(received!));
            await s.SendTrailersAsync(StatusCode.OK, "ok");
            return received;
        });

        using var cs = client.CreateStream();
        await cs.SendRequestHeadersAsync("/svc/Boundary", "localhost");
        await cs.SendMessageAsync(WrapLpm(requestData));
        await cs.SendHalfCloseAsync();

        await cs.ReceiveResponseHeadersAsync();
        byte[]? resp = null;
        await foreach (var m in cs.ReceiveMessagesAsync()) resp = UnwrapLpm(m);

        var serverReceived = await serverTask;
        Assert.That(serverReceived, Is.Not.Null);
        Assert.That(serverReceived!.Length, Is.EqualTo(appPayloadSize));
        Assert.That(resp!.Length, Is.EqualTo(appPayloadSize));
        Assert.That(resp.AsSpan().SequenceEqual(requestData), Is.True);
    }

    private static IEnumerable<TestCaseData> H2BoundarySizes()
    {
        const int H2Max = (1 << 24) - 1;

        // Small ring (4 KiB) cases: stress the multi-frame chunking path
        // on a ring that's much smaller than the message. The writer's
        // batch-write deadlock guard (chunk-aware EndBatchWrite) makes
        // these work despite payload >> ring capacity.
        yield return new TestCaseData(2 * 1024, 4096UL).SetName("H2 2KB on 4KB ring (single frame)");
        yield return new TestCaseData(8 * 1024, 4096UL).SetName("H2 8KB on 4KB ring (multi-frame)");
        yield return new TestCaseData(64 * 1024, 4096UL).SetName("H2 64KB on 4KB ring (heavy multi-frame)");

        yield return new TestCaseData(H2Max - 5, 16UL * 1024 * 1024)
            .SetName("H2 (max-5) on 16 MiB ring");
        yield return new TestCaseData(H2Max + 1, 16UL * 1024 * 1024)
            .SetName("H2 (max+1) on 16 MiB ring");
        yield return new TestCaseData(H2Max + 1024, 32UL * 1024 * 1024)
            .SetName("H2 (max+1KB) on 32 MiB ring");
        yield return new TestCaseData(H2Max - 100, 64UL * 1024 * 1024)
            .SetName("H2 (max-100) on 64 MiB ring (cap engages)");
        yield return new TestCaseData(H2Max + 100, 64UL * 1024 * 1024)
            .SetName("H2 (max+100) on 64 MiB ring (forces chunking)");
        yield return new TestCaseData(8 * 1024 * 1024, 64UL * 1024 * 1024)
            .SetName("H2 8 MiB on 64 MiB ring (single frame)");
    }
}
