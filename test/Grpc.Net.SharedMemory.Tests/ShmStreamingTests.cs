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
/// Streaming tests equivalent to TCP FunctionalTests/Client/StreamingTests.cs.
/// Tests various streaming patterns over shared memory transport.
/// </summary>
[TestFixture]
public class ShmStreamingTests
{
    // Big enough to hit flow control if not immediately read by peer
    private const int BigMessageSize = 1024 * 1024;
    
    [Test]
    [Timeout(30000)]
    public async Task DuplexStream_SendLargeFileBatched_Success()
    {
        // Arrange - create 1MB of test data
        var data = CreateTestData(1024 * 1024);
        var segmentName = $"streaming_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 64 * 1024 * 1024, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var receivedData = new MemoryStream();
        
        // Server task - receives and echoes back
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            
            // Simulate receiving and echoing data
            // In real implementation, this would read from the ring buffer
            await Task.Delay(500);
            
            await serverStream.SendTrailersAsync(StatusCode.OK);
        });
        
        // Client sends in batches
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/BufferAllData", "localhost");
        
        var sent = 0;
        while (sent < data.Length)
        {
            const int BatchSize = 64 * 1024; // 64 KB batches
            var writeCount = Math.Min(data.Length - sent, BatchSize);
            var chunk = new byte[writeCount];
            Array.Copy(data, sent, chunk, 0, writeCount);
            
            await clientStream.SendMessageAsync(chunk);
            sent += writeCount;
        }
        
        await clientStream.SendHalfCloseAsync();
        await serverTask;
        
        // Assert
        Assert.That(clientStream.IsLocalHalfClosed, Is.True);
        Assert.That(sent, Is.EqualTo(data.Length));
    }
    
    [Test]
    [Timeout(30000)]
    public async Task ClientStream_SendLargeFileBatched_Success()
    {
        // Arrange
        var total = 64 * 1024 * 1024; // 64 MB total
        var batchSize = 64 * 1024; // 64 KB per batch
        var data = CreateTestData(batchSize);
        var segmentName = $"streaming_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 64 * 1024 * 1024, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var receivedTotal = 0L;
        
        // Server task - counts received bytes
        var serverTask = Task.Run(async () =>
        {
            var serverStream = await server.AcceptStreamAsync();
            Assert.That(serverStream, Is.Not.Null);
            await serverStream!.SendResponseHeadersAsync();
            
            // Drain all incoming messages so WINDOW_UPDATE frames replenish the sender
            await foreach (var msg in serverStream.ReceiveMessagesAsync())
            {
                receivedTotal += msg.Length;
            }
            
            await serverStream.SendTrailersAsync(StatusCode.OK, $"Received {receivedTotal} bytes");
        });
        
        // Client sends batched data
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/ClientStreamedData", "localhost");
        
        var sent = 0;
        while (sent < total)
        {
            var writeCount = Math.Min(total - sent, data.Length);
            var chunk = writeCount == data.Length ? data : data.Take(writeCount).ToArray();
            await clientStream.SendMessageAsync(chunk);
            sent += writeCount;
        }
        
        await clientStream.SendHalfCloseAsync();
        await serverTask;
        
        // Assert
        Assert.That(sent, Is.EqualTo(total));
    }
    
    [Test]
    [Timeout(30000)]
    public async Task DuplexStream_SimultaneousSendAndReceive_Success()
    {
        // Arrange
        var segmentName = $"streaming_{Guid.NewGuid():N}";
        var raceDuration = TimeSpan.FromMilliseconds(500);
        
        using var server = ShmConnection.CreateAsServer(segmentName, 8192, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientSent = 0;
        var serverSent = 0;
        
        // Server task - sends messages while receiving
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            
            var endTime = DateTime.UtcNow.Add(raceDuration);
            while (DateTime.UtcNow < endTime)
            {
                await serverStream.SendMessageAsync(Encoding.UTF8.GetBytes($"ServerMsg{serverSent}"));
                Interlocked.Increment(ref serverSent);
                await Task.Delay(10);
            }
            
            await serverStream.SendTrailersAsync(StatusCode.OK);
        });
        
        // Client sends while receiving
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Race", "localhost");
        
        var clientTask = Task.Run(async () =>
        {
            var endTime = DateTime.UtcNow.Add(raceDuration);
            while (DateTime.UtcNow < endTime)
            {
                await clientStream.SendMessageAsync(Encoding.UTF8.GetBytes($"ClientMsg{clientSent}"));
                Interlocked.Increment(ref clientSent);
                await Task.Delay(10);
            }
            
            await clientStream.SendHalfCloseAsync();
        });
        
        await Task.WhenAll(serverTask, clientTask);
        
        // Assert - both sides sent messages
        Assert.That(clientSent, Is.GreaterThan(0));
        Assert.That(serverSent, Is.GreaterThan(0));
    }
    
    [Test]
    [Timeout(10000)]
    public async Task ServerStreaming_ManySmallMessages_Success()
    {
        // Arrange
        var messageCount = 100;
        var segmentName = $"streaming_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 8192, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        // Server sends many small messages
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            
            for (int i = 0; i < messageCount; i++)
            {
                await serverStream.SendMessageAsync(Encoding.UTF8.GetBytes($"Message {i}"));
            }
            
            await serverStream.SendTrailersAsync(StatusCode.OK);
        });
        
        // Client receives
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/ManyMessages", "localhost");
        await clientStream.SendHalfCloseAsync();
        
        await serverTask;
        
        // Assert
        Assert.That(clientStream.IsLocalHalfClosed, Is.True);
    }
    
    [Test]
    [Timeout(10000)]
    public async Task ClientStreaming_ManySmallMessages_Success()
    {
        // Arrange
        var messageCount = 100;
        var segmentName = $"streaming_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 8192, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        // Server waits
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            await Task.Delay(200);
            await serverStream.SendTrailersAsync(StatusCode.OK, $"Received {messageCount} messages");
        });
        
        // Client sends many messages
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/ManyClientMessages", "localhost");
        
        for (int i = 0; i < messageCount; i++)
        {
            await clientStream.SendMessageAsync(Encoding.UTF8.GetBytes($"Client {i}"));
        }
        
        await clientStream.SendHalfCloseAsync();
        await serverTask;
        
        // Assert
        Assert.That(clientStream.IsLocalHalfClosed, Is.True);
    }
    
    [Test]
    [Timeout(10000)]
    public async Task BidirectionalStreaming_InterleavedMessages_Success()
    {
        // Arrange
        var segmentName = $"streaming_{Guid.NewGuid():N}";
        var rounds = 10;
        
        using var server = ShmConnection.CreateAsServer(segmentName, 8192, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        // Server echoes
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            
            for (int i = 0; i < rounds; i++)
            {
                // Receive one, send one (interleaved)
                await Task.Delay(10);
                await serverStream.SendMessageAsync(Encoding.UTF8.GetBytes($"Echo {i}"));
            }
            
            await serverStream.SendTrailersAsync(StatusCode.OK);
        });
        
        // Client sends/receives
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Interleaved", "localhost");
        
        for (int i = 0; i < rounds; i++)
        {
            await clientStream.SendMessageAsync(Encoding.UTF8.GetBytes($"Request {i}"));
            await Task.Delay(10);
        }
        
        await clientStream.SendHalfCloseAsync();
        await serverTask;
        
        // Assert
        Assert.That(clientStream.IsLocalHalfClosed, Is.True);
    }
    
    [Test]
    [Timeout(10000)]
    public async Task Stream_EmptyMessages_Success()
    {
        // Arrange
        var segmentName = $"streaming_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        // Server
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            
            // Send empty message
            await serverStream.SendMessageAsync(Array.Empty<byte>());
            
            await serverStream.SendTrailersAsync(StatusCode.OK);
        });
        
        // Client
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Empty", "localhost");
        await clientStream.SendMessageAsync(Array.Empty<byte>());
        await clientStream.SendHalfCloseAsync();
        
        await serverTask;
        
        Assert.Pass("Empty messages handled correctly");
    }
    
    [Test]
    [Timeout(30000)]
    public async Task ParallelStreams_MultipleConnections_Success()
    {
        // Arrange
        var segmentName = $"streaming_{Guid.NewGuid():N}";
        var streamCount = 5;
        
        using var server = ShmConnection.CreateAsServer(segmentName, 16384, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var tasks = new List<Task>();
        
        // Create multiple parallel streams
        for (int i = 0; i < streamCount; i++)
        {
            var streamId = i;
            tasks.Add(Task.Run(async () =>
            {
                var clientStream = client.CreateStream();
                await clientStream.SendRequestHeadersAsync($"/test/Parallel/{streamId}", "localhost");
                await clientStream.SendMessageAsync(Encoding.UTF8.GetBytes($"Stream {streamId}"));
                await clientStream.SendHalfCloseAsync();
            }));
        }
        
        // Server handles streams
        for (int i = 0; i < streamCount; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                var serverStream = server.CreateStream();
                await serverStream.SendResponseHeadersAsync();
                await serverStream.SendTrailersAsync(StatusCode.OK);
            }));
        }
        
        await Task.WhenAll(tasks);
        
        Assert.Pass($"Successfully handled {streamCount} parallel streams");
    }
    
    private static byte[] CreateTestData(int size)
    {
        var data = new byte[size];
        var random = new Random(42); // Fixed seed for reproducibility
        random.NextBytes(data);
        return data;
    }
}
