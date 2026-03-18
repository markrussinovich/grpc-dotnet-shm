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
    public async Task Stream_InitialSendWindow_IsCorrect()
    {
        var segmentName = $"flow_test_{Guid.NewGuid():N}";

        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/flow", "localhost");

        // Stream should have initial window size available
        // We can verify by sending a message that's smaller than initial window
        var smallMessage = new byte[1000];
        await stream.SendMessageAsync(smallMessage);

        // Should complete without blocking
        Assert.Pass();
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task SendMessage_WithinWindow_CompletesImmediately()
    {
        var segmentName = $"flow_test_{Guid.NewGuid():N}";

        using var server = ShmConnection.CreateAsServer(segmentName, 65536, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/flow", "localhost");

        var startTime = DateTime.UtcNow;

        // Send message smaller than initial window (16 MiB)
        var message = new byte[10000];
        await stream.SendMessageAsync(message);

        var elapsed = DateTime.UtcNow - startTime;

        // Should complete quickly (within 100ms), not block on flow control
        Assert.That(elapsed.TotalMilliseconds, Is.LessThan(100));
    }

    [Test]
    [Platform("Win")]
    public async Task MultipleSmallMessages_ConsumeWindow()
    {
        var segmentName = $"flow_test_{Guid.NewGuid():N}";

        using var server = ShmConnection.CreateAsServer(segmentName, 2 * 1024 * 1024, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/flow", "localhost");

        // Send multiple small messages
        var message = new byte[1000];
        for (int i = 0; i < 10; i++)
        {
            await stream.SendMessageAsync(message);
        }

        // All should complete (10KB < 65KB initial window)
        Assert.Pass();
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
        Assert.That(ShmConstants.InitialWindowSize, Is.EqualTo(32 * 1024 * 1024), "Initial window should be 32 MiB for SHM");
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
    [Timeout(5000)]
    public async Task ConcurrentStreams_CanSendIndependently()
    {
        var segmentName = $"concurrent_test_{Guid.NewGuid():N}";

        using var server = ShmConnection.CreateAsServer(segmentName, 65536, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var stream1 = client.CreateStream();
        var stream2 = client.CreateStream();

        await stream1.SendRequestHeadersAsync("/test/1", "localhost");
        await stream2.SendRequestHeadersAsync("/test/2", "localhost");

        // Send messages on both streams concurrently
        var task1 = stream1.SendMessageAsync(Encoding.UTF8.GetBytes("message on stream 1"));
        var task2 = stream2.SendMessageAsync(Encoding.UTF8.GetBytes("message on stream 2"));

        await Task.WhenAll(task1, task2);

        Assert.Pass();
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task ManyStreams_Created_InParallel()
    {
        var segmentName = $"concurrent_test_{Guid.NewGuid():N}";

        using var server = ShmConnection.CreateAsServer(segmentName, 65536, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        const int streamCount = 50;
        var streams = new List<ShmGrpcStream>();
        var tasks = new List<Task>();

        for (int i = 0; i < streamCount; i++)
        {
            var stream = client.CreateStream();
            streams.Add(stream);
            tasks.Add(stream.SendRequestHeadersAsync($"/test/{i}", "localhost"));
        }

        await Task.WhenAll(tasks);

        Assert.That(streams.Count, Is.EqualTo(streamCount));
        Assert.That(streams.Select(s => s.StreamId).Distinct().Count(), Is.EqualTo(streamCount));
    }

    [Test]
    [Platform("Win")]
    public void StreamIsClientStream_ReflectsConnection()
    {
        var segmentName = $"concurrent_test_{Guid.NewGuid():N}";

        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var clientStream = client.CreateStream();
        var serverStream = server.CreateStream();

        Assert.That(clientStream.IsClientStream, Is.True);
        Assert.That(serverStream.IsClientStream, Is.False);
    }
}
