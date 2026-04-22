#region Copyright notice and license

// Copyright 2025 The gRPC Authors
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
using NUnit.Framework;
using Grpc.Core;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// End-to-end tests demonstrating full gRPC-style request/response over shared memory.
/// Each test uses AcceptStreamAsync + ReceiveMessagesAsync for real cross-stream data exchange.
/// </summary>
[TestFixture]
public class EndToEndTests
{
    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task UnaryCall_SimpleRequestResponse_Works()
    {
        var segmentName = $"grpc_e2e_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 4096, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        var requestData = Encoding.UTF8.GetBytes("GreeterClient");
        var responseData = Encoding.UTF8.GetBytes("Hello, World!");

        var serverTask = Task.Run(async () =>
        {
            var stream = await serverConnection.AcceptStreamAsync();
            using var s = stream!;

            // Read request from client
            byte[]? received = null;
            await foreach (var m in s.ReceiveMessagesAsync())
                received = m;

            // Send response
            await s.SendResponseHeadersAsync();
            await s.SendMessageAsync(responseData);
            await s.SendTrailersAsync(StatusCode.OK, "Success");

            return received;
        });

        using var cs = clientConnection.CreateStream();
        var metadata = new Metadata { { "client-id", "test-client" } };
        await cs.SendRequestHeadersAsync("/greet.Greeter/SayHello", "localhost", metadata);
        await cs.SendMessageAsync(requestData);
        await cs.SendHalfCloseAsync();

        // Read response
        await cs.ReceiveResponseHeadersAsync();
        byte[]? resp = null;
        await foreach (var m in cs.ReceiveMessagesAsync())
            resp = m;

        var serverReceived = await serverTask;

        Assert.That(serverReceived, Is.EqualTo(requestData));
        Assert.That(resp, Is.EqualTo(responseData));
        Assert.That(cs.Trailers!.GrpcStatusCode, Is.EqualTo(StatusCode.OK));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task ServerStreaming_MultipleMessages_Works()
    {
        var segmentName = $"grpc_e2e_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 8192, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        var messageCount = 5;

        var serverTask = Task.Run(async () =>
        {
            var stream = await serverConnection.AcceptStreamAsync();
            using var s = stream!;

            // Drain client half-close
            await foreach (var _ in s.ReceiveMessagesAsync()) { }

            await s.SendResponseHeadersAsync();
            for (int i = 0; i < messageCount; i++)
            {
                var message = Encoding.UTF8.GetBytes($"Message {i}");
                await s.SendMessageAsync(message);
            }
            await s.SendTrailersAsync(StatusCode.OK);
        });

        using var cs = clientConnection.CreateStream();
        await cs.SendRequestHeadersAsync("/test/ServerStream", "localhost");
        await cs.SendHalfCloseAsync();

        await cs.ReceiveResponseHeadersAsync();
        var received = new List<byte[]>();
        await foreach (var m in cs.ReceiveMessagesAsync())
            received.Add(m);

        await serverTask;

        Assert.That(received.Count, Is.EqualTo(messageCount));
        for (int i = 0; i < messageCount; i++)
        {
            Assert.That(Encoding.UTF8.GetString(received[i]), Is.EqualTo($"Message {i}"));
        }
        Assert.That(cs.Trailers!.GrpcStatusCode, Is.EqualTo(StatusCode.OK));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task ClientStreaming_MultipleMessages_Works()
    {
        var segmentName = $"grpc_e2e_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 8192, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        var messageCount = 5;

        var serverTask = Task.Run(async () =>
        {
            var stream = await serverConnection.AcceptStreamAsync();
            using var s = stream!;

            // Read all client messages
            var received = new List<byte[]>();
            await foreach (var m in s.ReceiveMessagesAsync())
                received.Add(m);

            await s.SendResponseHeadersAsync();
            var summary = Encoding.UTF8.GetBytes($"Received {received.Count}");
            await s.SendMessageAsync(summary);
            await s.SendTrailersAsync(StatusCode.OK);

            return received;
        });

        using var cs = clientConnection.CreateStream();
        await cs.SendRequestHeadersAsync("/test/ClientStream", "localhost");

        for (int i = 0; i < messageCount; i++)
        {
            var message = Encoding.UTF8.GetBytes($"Client message {i}");
            await cs.SendMessageAsync(message);
        }
        await cs.SendHalfCloseAsync();

        await cs.ReceiveResponseHeadersAsync();
        byte[]? resp = null;
        await foreach (var m in cs.ReceiveMessagesAsync())
            resp = m;

        var serverReceived = await serverTask;

        Assert.That(serverReceived.Count, Is.EqualTo(messageCount));
        for (int i = 0; i < messageCount; i++)
        {
            Assert.That(Encoding.UTF8.GetString(serverReceived[i]), Is.EqualTo($"Client message {i}"));
        }
        Assert.That(resp, Is.Not.Null);
        Assert.That(Encoding.UTF8.GetString(resp!), Is.EqualTo($"Received {messageCount}"));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task BidirectionalStreaming_Works()
    {
        var segmentName = $"grpc_e2e_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 8192, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        var serverTask = Task.Run(async () =>
        {
            var stream = await serverConnection.AcceptStreamAsync();
            using var s = stream!;

            // Read all client messages
            var received = new List<byte[]>();
            await foreach (var m in s.ReceiveMessagesAsync())
                received.Add(m);

            // Echo them back
            await s.SendResponseHeadersAsync();
            foreach (var msg in received)
                await s.SendMessageAsync(msg);
            await s.SendTrailersAsync(StatusCode.OK);

            return received;
        });

        using var cs = clientConnection.CreateStream();
        await cs.SendRequestHeadersAsync("/test/BiDi", "localhost");

        for (int i = 0; i < 3; i++)
        {
            var message = Encoding.UTF8.GetBytes($"Request {i}");
            await cs.SendMessageAsync(message);
        }
        await cs.SendHalfCloseAsync();

        await cs.ReceiveResponseHeadersAsync();
        var clientReceived = new List<byte[]>();
        await foreach (var m in cs.ReceiveMessagesAsync())
            clientReceived.Add(m);

        var serverReceived = await serverTask;

        Assert.That(serverReceived.Count, Is.EqualTo(3));
        Assert.That(clientReceived.Count, Is.EqualTo(3));
        for (int i = 0; i < 3; i++)
        {
            Assert.That(Encoding.UTF8.GetString(serverReceived[i]), Is.EqualTo($"Request {i}"));
            Assert.That(Encoding.UTF8.GetString(clientReceived[i]), Is.EqualTo($"Request {i}"));
        }
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task ErrorResponse_ReturnsStatusCode()
    {
        var segmentName = $"grpc_e2e_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 4096, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        var serverTask = Task.Run(async () =>
        {
            var stream = await serverConnection.AcceptStreamAsync();
            using var s = stream!;

            // Drain client messages
            await foreach (var _ in s.ReceiveMessagesAsync()) { }

            // Return error
            await s.SendResponseHeadersAsync();
            await s.SendTrailersAsync(StatusCode.InvalidArgument, "Missing required field");
        });

        using var cs = clientConnection.CreateStream();
        await cs.SendRequestHeadersAsync("/test/Error", "localhost");
        await cs.SendHalfCloseAsync();

        await cs.ReceiveResponseHeadersAsync();
        await foreach (var _ in cs.ReceiveMessagesAsync()) { }

        await serverTask;

        Assert.That(cs.Trailers, Is.Not.Null);
        Assert.That(cs.Trailers!.GrpcStatusCode, Is.EqualTo(StatusCode.InvalidArgument));
        Assert.That(cs.Trailers!.GrpcStatusMessage, Is.EqualTo("Missing required field"));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task Cancellation_CancelsStream()
    {
        var segmentName = $"grpc_e2e_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 4096, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        var clientStream = clientConnection.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Cancel", "localhost");

        // Server accepts stream
        var serverStream = await serverConnection.AcceptStreamAsync();
        Assert.That(serverStream, Is.Not.Null);

        // Client cancels — sends Cancel frame to server
        await clientStream.CancelAsync();
        Assert.That(clientStream.IsCancelled, Is.True);

        // Server observes cancellation: ReceiveMessagesAsync yields no
        // messages because the inbound channel is completed by the
        // Cancel frame. This proves cancel propagated through the ring.
        int serverMsgCount = 0;
        await foreach (var _ in serverStream!.ReceiveMessagesAsync())
            serverMsgCount++;
        Assert.That(serverMsgCount, Is.EqualTo(0));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task Deadline_PropagatesInHeaders()
    {
        var segmentName = $"grpc_e2e_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 4096, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        var deadline = DateTime.UtcNow.AddSeconds(30);

        var serverTask = Task.Run(async () =>
        {
            var stream = await serverConnection.AcceptStreamAsync();
            using var s = stream!;

            // Server reads request headers (available after AcceptStreamAsync)
            var headers = s.RequestHeaders;

            // Drain messages + respond
            await foreach (var _ in s.ReceiveMessagesAsync()) { }
            await s.SendResponseHeadersAsync();
            await s.SendTrailersAsync(StatusCode.OK);

            return headers;
        });

        using var cs = clientConnection.CreateStream();
        await cs.SendRequestHeadersAsync("/test/Deadline", "localhost", deadline: deadline);
        await cs.SendHalfCloseAsync();

        await cs.ReceiveResponseHeadersAsync();
        await foreach (var _ in cs.ReceiveMessagesAsync()) { }

        var serverHeaders = await serverTask;

        Assert.That(serverHeaders, Is.Not.Null);
        Assert.That(serverHeaders!.DeadlineUnixNano, Is.GreaterThan(0UL));
        Assert.That(serverHeaders.Method, Is.EqualTo("/test/Deadline"));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task Metadata_RoundTrips()
    {
        var segmentName = $"grpc_e2e_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 4096, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        var metadata = new Metadata
        {
            { "x-custom-header", "custom-value" },
            { "x-another-header", "another-value" }
        };

        var serverTask = Task.Run(async () =>
        {
            var stream = await serverConnection.AcceptStreamAsync();
            using var s = stream!;

            var headers = s.RequestHeaders;

            await foreach (var _ in s.ReceiveMessagesAsync()) { }
            await s.SendResponseHeadersAsync();
            await s.SendTrailersAsync(StatusCode.OK);

            return headers;
        });

        using var cs = clientConnection.CreateStream();
        await cs.SendRequestHeadersAsync("/test/Metadata", "localhost", metadata);
        await cs.SendHalfCloseAsync();

        await cs.ReceiveResponseHeadersAsync();
        await foreach (var _ in cs.ReceiveMessagesAsync()) { }

        var serverHeaders = await serverTask;

        Assert.That(serverHeaders, Is.Not.Null);
        Assert.That(serverHeaders!.Metadata.Count, Is.EqualTo(2));

        var customHeader = serverHeaders.Metadata.FirstOrDefault(m => m.Key == "x-custom-header");
        Assert.That(customHeader.Key, Is.Not.Null);
        Assert.That(Encoding.UTF8.GetString(customHeader.Values[0]), Is.EqualTo("custom-value"));

        var anotherHeader = serverHeaders.Metadata.FirstOrDefault(m => m.Key == "x-another-header");
        Assert.That(anotherHeader.Key, Is.Not.Null);
        Assert.That(Encoding.UTF8.GetString(anotherHeader.Values[0]), Is.EqualTo("another-value"));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task LargeMessage_TransfersCorrectly()
    {
        var segmentName = $"grpc_e2e_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 2 * 1024 * 1024, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        var largeMessage = new byte[32 * 1024]; // 32KB
        new Random(42).NextBytes(largeMessage);

        var serverTask = Task.Run(async () =>
        {
            var stream = await serverConnection.AcceptStreamAsync();
            using var s = stream!;

            // Read the large message
            byte[]? received = null;
            await foreach (var m in s.ReceiveMessagesAsync())
                received = m;

            // Echo it back
            await s.SendResponseHeadersAsync();
            await s.SendMessageAsync(received!);
            await s.SendTrailersAsync(StatusCode.OK);

            return received;
        });

        using var cs = clientConnection.CreateStream();
        await cs.SendRequestHeadersAsync("/test/Large", "localhost");
        await cs.SendMessageAsync(largeMessage);
        await cs.SendHalfCloseAsync();

        await cs.ReceiveResponseHeadersAsync();
        byte[]? resp = null;
        await foreach (var m in cs.ReceiveMessagesAsync())
            resp = m;

        var serverReceived = await serverTask;

        Assert.That(serverReceived, Is.Not.Null);
        Assert.That(serverReceived!.Length, Is.EqualTo(largeMessage.Length));
        Assert.That(serverReceived, Is.EqualTo(largeMessage));
        Assert.That(resp, Is.Not.Null);
        Assert.That(resp!.Length, Is.EqualTo(largeMessage.Length));
        Assert.That(resp, Is.EqualTo(largeMessage));
    }

    [Test]
    [Platform("Win")]
    public void MultipleConnections_WorkIndependently()
    {
        var segment1 = $"grpc_e2e_1_{Guid.NewGuid():N}";
        var segment2 = $"grpc_e2e_2_{Guid.NewGuid():N}";

        using var server1 = ShmConnection.CreateAsServer(segment1, ringCapacity: 4096, maxStreams: 100);
        using var server2 = ShmConnection.CreateAsServer(segment2, ringCapacity: 4096, maxStreams: 100);

        using var client1 = ShmConnection.ConnectAsClient(segment1);
        using var client2 = ShmConnection.ConnectAsClient(segment2);

        var stream1 = client1.CreateStream();
        var stream2 = client2.CreateStream();

        Assert.That(stream1.StreamId, Is.EqualTo(1));
        Assert.That(stream2.StreamId, Is.EqualTo(1));
        Assert.That(client1.Name, Is.Not.EqualTo(client2.Name));
    }
}
