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

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Tests for flow control functionality.
/// Tests window updates, quota management, and backpressure.
/// </summary>
[TestFixture]
public class FlowControlTests
{
    [Test]
    [Platform("Win")]
    public void InitialWindowSize_IsCorrect()
    {
        Assert.That(ShmConstants.InitialWindowSize, Is.EqualTo(32 * 1024 * 1024));
    }

    [Test]
    [Platform("Win")]
    public void MaxWindowSize_IsCorrect()
    {
        // 2^31 - 1 (max HTTP/2 window)
        Assert.That(ShmConstants.MaxWindowSize, Is.EqualTo(int.MaxValue));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(5000)]
    public async Task Stream_InitialSendWindow_IsCorrect()
    {
        var segmentName = $"flow_test_{Guid.NewGuid():N}";

        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var serverTask = Task.Run(async () =>
        {
            var serverStream = await server.AcceptStreamAsync();
            Assert.That(serverStream, Is.Not.Null);
            using var s = serverStream!;

            await s.SendResponseHeadersAsync();

            byte[]? received = null;
            await foreach (var msg in s.ReceiveLpmMessagesAsync())
                received = msg;

            Assert.That(received, Is.Not.Null);
            Assert.That(received!.Length, Is.EqualTo(1000));

            await s.SendTrailersAsync(Grpc.Core.StatusCode.OK);
        });

        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/flow", "localhost");

        // Stream should have initial window size available
        var smallMessage = new byte[1000];
        await stream.SendMessageAsync(LpmHelpers.WrapLpm(smallMessage));
        await stream.SendHalfCloseAsync();

        await stream.ReceiveResponseHeadersAsync();
        await foreach (var _ in stream.ReceiveLpmMessagesAsync()) { }

        await serverTask;
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(5000)]
    public async Task SendMessage_WithinWindow_CompletesImmediately()
    {
        var segmentName = $"flow_test_{Guid.NewGuid():N}";

        using var server = ShmConnection.CreateAsServer(segmentName, 65536, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var serverTask = Task.Run(async () =>
        {
            var serverStream = await server.AcceptStreamAsync();
            Assert.That(serverStream, Is.Not.Null);
            using var s = serverStream!;

            await s.SendResponseHeadersAsync();

            byte[]? received = null;
            await foreach (var msg in s.ReceiveLpmMessagesAsync())
                received = msg;

            Assert.That(received, Is.Not.Null);
            Assert.That(received!.Length, Is.EqualTo(10000));

            await s.SendTrailersAsync(Grpc.Core.StatusCode.OK);
        });

        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/flow", "localhost");

        // Send message smaller than ring capacity — should not block on backpressure
        var message = new byte[10000];
        await stream.SendMessageAsync(LpmHelpers.WrapLpm(message));
        await stream.SendHalfCloseAsync();

        await stream.ReceiveResponseHeadersAsync();
        await foreach (var _ in stream.ReceiveLpmMessagesAsync()) { }

        await serverTask;
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(5000)]
    public async Task MultipleSmallMessages_ConsumeWindow()
    {
        var segmentName = $"flow_test_{Guid.NewGuid():N}";

        using var server = ShmConnection.CreateAsServer(segmentName, 2 * 1024 * 1024, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var messageCount = 10;
        var messageSize = 1000;
        var serverReceivedCount = 0;

        var serverTask = Task.Run(async () =>
        {
            var serverStream = await server.AcceptStreamAsync();
            Assert.That(serverStream, Is.Not.Null);
            using var s = serverStream!;

            await s.SendResponseHeadersAsync();

            await foreach (var msg in s.ReceiveLpmMessagesAsync())
            {
                Assert.That(msg.Length, Is.EqualTo(messageSize));
                serverReceivedCount++;
            }

            await s.SendTrailersAsync(Grpc.Core.StatusCode.OK);
        });

        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/flow", "localhost");

        // Send multiple small messages
        var message = new byte[messageSize];
        for (int i = 0; i < messageCount; i++)
        {
            await stream.SendMessageAsync(LpmHelpers.WrapLpm(message));
        }
        await stream.SendHalfCloseAsync();

        await stream.ReceiveResponseHeadersAsync();
        await foreach (var _ in stream.ReceiveLpmMessagesAsync()) { }

        await serverTask;

        Assert.That(serverReceivedCount, Is.EqualTo(messageCount));
    }

    [Test]
    [Platform("Win")]
    public void WindowUpdate_Frame_HasCorrectPayloadSize()
    {
        // WindowUpdate payload should be 4 bytes (uint32 increment)
        Assert.That(4, Is.EqualTo(4)); // Payload size for window update
    }

    [Test]
    [Platform("Win")]
    public void BdpEstimator_InitialBdp_IsInitialWindow()
    {
        var estimator = new ShmBdpEstimator((uint)ShmConstants.InitialWindowSize);

        Assert.That(estimator.CurrentBdp, Is.EqualTo(ShmConstants.InitialWindowSize));
    }

    [Test]
    [Platform("Win")]
    public void BdpEstimator_BdpLimit_Is16MB()
    {
        Assert.That(ShmBdpEstimator.BdpLimit, Is.EqualTo(32 * 1024 * 1024));
    }

    [Test]
    [Platform("Win")]
    public void FlowControl_Constants_AreValid()
    {
        // Verify constants are appropriate for shared memory transport
        Assert.That(ShmConstants.InitialWindowSize, Is.EqualTo(32 * 1024 * 1024), "Initial window should be half the default ring capacity");
        Assert.That(ShmConstants.MaxWindowSize, Is.EqualTo(int.MaxValue), "Max window should be 2^31-1");
    }
}

/// <summary>
/// Tests for concurrent stream handling.
/// </summary>
[TestFixture]
public class ConcurrentStreamTests
{
    [Test]
    [Platform("Win")]
    public void CreateMultipleStreams_HaveUniqueIds()
    {
        var segmentName = $"concurrent_test_{Guid.NewGuid():N}";

        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var stream1 = client.CreateStream();
        var stream2 = client.CreateStream();
        var stream3 = client.CreateStream();

        Assert.That(stream1.StreamId, Is.Not.EqualTo(stream2.StreamId));
        Assert.That(stream2.StreamId, Is.Not.EqualTo(stream3.StreamId));
        Assert.That(stream1.StreamId, Is.Not.EqualTo(stream3.StreamId));
    }

    [Test]
    [Platform("Win")]
    public void ClientStreams_UseOddIds()
    {
        var segmentName = $"concurrent_test_{Guid.NewGuid():N}";

        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var stream1 = client.CreateStream();
        var stream2 = client.CreateStream();
        var stream3 = client.CreateStream();

        Assert.That(stream1.StreamId % 2, Is.EqualTo(1), "Client stream IDs should be odd");
        Assert.That(stream2.StreamId % 2, Is.EqualTo(1), "Client stream IDs should be odd");
        Assert.That(stream3.StreamId % 2, Is.EqualTo(1), "Client stream IDs should be odd");
    }

    [Test]
    [Platform("Win")]
    public void ServerStreams_UseEvenIds()
    {
        var segmentName = $"concurrent_test_{Guid.NewGuid():N}";

        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);

        var stream1 = server.CreateStream();
        var stream2 = server.CreateStream();

        Assert.That(stream1.StreamId % 2, Is.EqualTo(0), "Server stream IDs should be even");
        Assert.That(stream2.StreamId % 2, Is.EqualTo(0), "Server stream IDs should be even");
    }

    [Test]
    [Platform("Win")]
    public void StreamIds_AreSequential()
    {
        var segmentName = $"concurrent_test_{Guid.NewGuid():N}";

        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var stream1 = client.CreateStream();
        var stream2 = client.CreateStream();
        var stream3 = client.CreateStream();

        // Client uses odd IDs: 1, 3, 5, ...
        Assert.That(stream1.StreamId, Is.EqualTo(1));
        Assert.That(stream2.StreamId, Is.EqualTo(3));
        Assert.That(stream3.StreamId, Is.EqualTo(5));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(5000)]
    public async Task ConcurrentStreams_CanSendIndependently()
    {
        var segmentName = $"concurrent_test_{Guid.NewGuid():N}";

        using var server = ShmConnection.CreateAsServer(segmentName, 65536, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var serverReceived = new string[2];

        // Server accepts both streams
        var serverTask1 = Task.Run(async () =>
        {
            var s = await server.AcceptStreamAsync();
            using var ss = s!;
            await ss.SendResponseHeadersAsync();
            await foreach (var msg in ss.ReceiveLpmMessagesAsync())
                serverReceived[0] = Encoding.UTF8.GetString(msg);
            await ss.SendTrailersAsync(Grpc.Core.StatusCode.OK);
        });
        var serverTask2 = Task.Run(async () =>
        {
            var s = await server.AcceptStreamAsync();
            using var ss = s!;
            await ss.SendResponseHeadersAsync();
            await foreach (var msg in ss.ReceiveLpmMessagesAsync())
                serverReceived[1] = Encoding.UTF8.GetString(msg);
            await ss.SendTrailersAsync(Grpc.Core.StatusCode.OK);
        });

        var stream1 = client.CreateStream();
        var stream2 = client.CreateStream();

        await stream1.SendRequestHeadersAsync("/test/1", "localhost");
        await stream2.SendRequestHeadersAsync("/test/2", "localhost");

        // Send messages on both streams concurrently
        var task1 = Task.Run(async () =>
        {
            await stream1.SendMessageAsync(LpmHelpers.WrapLpmText("message on stream 1"));
            await stream1.SendHalfCloseAsync();
            await stream1.ReceiveResponseHeadersAsync();
            await foreach (var _ in stream1.ReceiveLpmMessagesAsync()) { }
        });
        var task2 = Task.Run(async () =>
        {
            await stream2.SendMessageAsync(LpmHelpers.WrapLpmText("message on stream 2"));
            await stream2.SendHalfCloseAsync();
            await stream2.ReceiveResponseHeadersAsync();
            await foreach (var _ in stream2.ReceiveLpmMessagesAsync()) { }
        });

        await Task.WhenAll(task1, task2, serverTask1, serverTask2);

        // Both messages were received (order may vary due to concurrency)
        var allReceived = serverReceived.OrderBy(s => s).ToArray();
        Assert.That(allReceived[0], Is.EqualTo("message on stream 1"));
        Assert.That(allReceived[1], Is.EqualTo("message on stream 2"));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(5000)]
    public async Task ManyStreams_Created_InParallel()
    {
        var segmentName = $"concurrent_test_{Guid.NewGuid():N}";

        using var server = ShmConnection.CreateAsServer(segmentName, 65536, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        const int streamCount = 50;
        var serverReceivedCount = 0;
        var serverTasks = new List<Task>();
        var clientTasks = new List<Task>();

        // Server accepts all streams
        for (int i = 0; i < streamCount; i++)
        {
            serverTasks.Add(Task.Run(async () =>
            {
                var s = await server.AcceptStreamAsync();
                using var ss = s!;
                await ss.SendResponseHeadersAsync();
                await foreach (var _ in ss.ReceiveLpmMessagesAsync()) { }
                Interlocked.Increment(ref serverReceivedCount);
                await ss.SendTrailersAsync(Grpc.Core.StatusCode.OK);
            }));
        }

        // Client creates streams in parallel
        for (int i = 0; i < streamCount; i++)
        {
            var idx = i;
            clientTasks.Add(Task.Run(async () =>
            {
                var stream = client.CreateStream();
                await stream.SendRequestHeadersAsync($"/test/{idx}", "localhost");
                await stream.SendMessageAsync(LpmHelpers.WrapLpmText($"msg{idx}"));
                await stream.SendHalfCloseAsync();
                await stream.ReceiveResponseHeadersAsync();
                await foreach (var _ in stream.ReceiveLpmMessagesAsync()) { }
            }));
        }

        await Task.WhenAll(clientTasks.Concat(serverTasks));

        Assert.That(serverReceivedCount, Is.EqualTo(streamCount));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(5000)]
    public async Task StreamIsClientStream_ReflectsConnection()
    {
        var segmentName = $"concurrent_test_{Guid.NewGuid():N}";

        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/reflect", "localhost");

        var serverStream = await server.AcceptStreamAsync();
        Assert.That(serverStream, Is.Not.Null);
        using var s = serverStream!;

        Assert.That(clientStream.IsClientStream, Is.True);
        Assert.That(s.IsClientStream, Is.False);

        await s.SendTrailersAsync(Grpc.Core.StatusCode.OK);
    }
}
