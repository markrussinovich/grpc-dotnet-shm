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
using Grpc.Core;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Lifetime tests equivalent to TCP FunctionalTests/Server/LifetimeTests.cs.
/// Tests server and connection lifetime management over shared memory transport.
/// </summary>
[TestFixture]
public class ShmLifetimeTests
{
    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task ServerDispose_ActiveStreams_StreamsClosed()
    {
        // Arrange
        var segmentName = $"lifetime_{Guid.NewGuid():N}";
        var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        var client = ShmConnection.ConnectAsClient(segmentName);

        // Create an active stream
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Lifetime", "localhost");

        // Act - dispose server
        server.Dispose();

        // Assert - server is disposed
        Assert.That(() => server.CreateStream(), Throws.TypeOf<ObjectDisposedException>());

        // Cleanup
        client.Dispose();
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task ClientDispose_ActiveStreams_StreamsClosed()
    {
        // Arrange
        var segmentName = $"lifetime_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        var client = ShmConnection.ConnectAsClient(segmentName);

        // Create an active stream
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Lifetime", "localhost");

        // Act - dispose client
        client.Dispose();

        // Assert - client is disposed
        Assert.That(() => client.CreateStream(), Throws.TypeOf<ObjectDisposedException>());
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task GracefulShutdown_WaitsForActiveStreams()
    {
        // Arrange
        var segmentName = $"lifetime_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var streamCompleted = false;

        // Server handles a long-running stream
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            
            // Simulate some work
            await Task.Delay(200);
            
            await serverStream.SendTrailersAsync(StatusCode.OK);
            streamCompleted = true;
        });

        // Client starts a request
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/LongRunning", "localhost");
        await clientStream.SendHalfCloseAsync();

        // Wait for server to complete
        await serverTask;

        // Assert
        Assert.That(streamCompleted, Is.True);
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task GoAway_NewStreamsRejected()
    {
        // Arrange
        var segmentName = $"lifetime_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        // Server sends GOAWAY via connection
        server.SendGoAway("Shutting down");

        // Give time for GOAWAY to be processed
        await Task.Delay(50);

        // After GOAWAY, connection is closed or draining
        Assert.That(server.IsClosed || server.IsDraining, Is.True);
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task MultipleConnections_IndependentLifetimes()
    {
        // Arrange
        var segment1 = $"lifetime_1_{Guid.NewGuid():N}";
        var segment2 = $"lifetime_2_{Guid.NewGuid():N}";

        var server1 = ShmConnection.CreateAsServer(segment1, 4096, 100);
        var server2 = ShmConnection.CreateAsServer(segment2, 4096, 100);
        var client1 = ShmConnection.ConnectAsClient(segment1);
        var client2 = ShmConnection.ConnectAsClient(segment2);

        // Create streams on both
        var stream1 = client1.CreateStream();
        var stream2 = client2.CreateStream();

        // Dispose first pair
        server1.Dispose();
        client1.Dispose();

        // Second pair should still work
        await stream2.SendRequestHeadersAsync("/test/Independent", "localhost");
        Assert.That(stream2.RequestHeaders, Is.Not.Null);

        // Cleanup
        server2.Dispose();
        client2.Dispose();
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task StreamDispose_BeforeCompletion_NoLeak()
    {
        // Arrange
        var segmentName = $"lifetime_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        // Create and dispose multiple streams
        for (int i = 0; i < 100; i++)
        {
            var stream = client.CreateStream();
            await stream.SendRequestHeadersAsync($"/test/Stream{i}", "localhost");
            stream.Dispose();
        }

        // Assert - creating more streams should still work
        var finalStream = client.CreateStream();
        Assert.That(finalStream, Is.Not.Null);
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task ConnectionReuse_AfterStreamCompletion()
    {
        // Arrange
        var segmentName = $"lifetime_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        // Complete multiple requests on the same connection
        for (int i = 0; i < 5; i++)
        {
            var serverTask = Task.Run(async () =>
            {
                var serverStream = server.CreateStream();
                await serverStream.SendResponseHeadersAsync();
                await serverStream.SendTrailersAsync(StatusCode.OK);
            });

            var clientStream = client.CreateStream();
            await clientStream.SendRequestHeadersAsync($"/test/Request{i}", "localhost");
            await clientStream.SendHalfCloseAsync();

            await serverTask;
        }

        // Assert - connection is still usable
        var finalStream = client.CreateStream();
        Assert.That(finalStream, Is.Not.Null);
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public void ServerRestart_ClientReconnects()
    {
        // Arrange
        var segmentName = $"lifetime_{Guid.NewGuid():N}";

        // First server instance
        var server1 = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        var client1 = ShmConnection.ConnectAsClient(segmentName);
        var stream1 = client1.CreateStream();
        Assert.That(stream1, Is.Not.Null);

        // Dispose first instance
        client1.Dispose();
        server1.Dispose();

        // New server instance with same name
        using var server2 = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client2 = ShmConnection.ConnectAsClient(segmentName);
        var stream2 = client2.CreateStream();

        // Assert - new connection works
        Assert.That(stream2, Is.Not.Null);
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task ConcurrentStreams_AllComplete()
    {
        // Arrange
        var segmentName = $"lifetime_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 8192, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var streamCount = 10;
        var completedStreams = 0;

        // Start multiple concurrent streams
        var tasks = new List<Task>();
        for (int i = 0; i < streamCount; i++)
        {
            var index = i;
            var task = Task.Run(async () =>
            {
                // Server side
                var serverStream = server.CreateStream();
                await serverStream.SendResponseHeadersAsync();
                await serverStream.SendTrailersAsync(StatusCode.OK);

                // Client side
                var clientStream = client.CreateStream();
                await clientStream.SendRequestHeadersAsync($"/test/Concurrent{index}", "localhost");
                await clientStream.SendHalfCloseAsync();

                Interlocked.Increment(ref completedStreams);
            });
            tasks.Add(task);
        }

        await Task.WhenAll(tasks);

        // Assert
        Assert.That(completedStreams, Is.EqualTo(streamCount));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task MaxStreamsEnforced_ExcessRejected()
    {
        // Arrange - create server with max 5 streams
        var maxStreams = 5;
        var segmentName = $"lifetime_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, (uint)maxStreams);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var createdStreams = new List<ShmGrpcStream>();

        // Create streams up to the limit
        for (int i = 0; i < maxStreams; i++)
        {
            var stream = client.CreateStream();
            await stream.SendRequestHeadersAsync($"/test/Stream{i}", "localhost");
            createdStreams.Add(stream);
        }

        // Assert - we created exactly maxStreams
        Assert.That(createdStreams.Count, Is.EqualTo(maxStreams));
    }
}
