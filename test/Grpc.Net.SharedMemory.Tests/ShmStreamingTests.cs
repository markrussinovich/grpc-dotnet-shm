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
    [Test]
    [CancelAfter(30000)]
    public async Task DuplexStream_SendLargeFileBatched_Success()
    {
        // Arrange - create 1MB of test data
        var data = CreateTestData(1024 * 1024);
        var segmentName = $"streaming_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 64 * 1024 * 1024, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var serverReceivedTotal = 0L;
        
        // Server task - receives all client batches and responds
        var serverTask = Task.Run(async () =>
        {
            var serverStream = await server.AcceptStreamAsync();
            Assert.That(serverStream, Is.Not.Null);
            using var s = serverStream!;
            
            await s.SendResponseHeadersAsync();
            
            await foreach (var msg in s.ReceiveMessagesAsync())
            {
                Interlocked.Add(ref serverReceivedTotal, msg.Length);
            }
            
            await s.SendTrailersAsync(StatusCode.OK);
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
        
        await clientStream.ReceiveResponseHeadersAsync();
        await foreach (var _ in clientStream.ReceiveMessagesAsync()) { }
        
        await serverTask;
        
        // Assert
        Assert.That(clientStream.IsLocalHalfClosed, Is.True);
        Assert.That(sent, Is.EqualTo(data.Length));
        Assert.That(Interlocked.Read(ref serverReceivedTotal), Is.EqualTo(data.Length));
    }
    
    [Test]
    [CancelAfter(30000)]
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
    [CancelAfter(30000)]
    public async Task DuplexStream_SimultaneousSendAndReceive_Success()
    {
        // Arrange
        var segmentName = $"streaming_{Guid.NewGuid():N}";
        var messageCount = 20;
        
        using var server = ShmConnection.CreateAsServer(segmentName, 8192, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var serverReceived = new List<string>();
        var clientReceived = new List<string>();
        
        // Server task - reads client messages, then sends responses
        var serverTask = Task.Run(async () =>
        {
            var serverStream = await server.AcceptStreamAsync();
            Assert.That(serverStream, Is.Not.Null);
            using var s = serverStream!;
            
            await s.SendResponseHeadersAsync();
            
            // Read all client messages
            await foreach (var msg in s.ReceiveMessagesAsync())
            {
                lock (serverReceived)
                    serverReceived.Add(Encoding.UTF8.GetString(msg));
            }
            
            // Send server messages after reading
            for (int i = 0; i < messageCount; i++)
            {
                await s.SendMessageAsync(Encoding.UTF8.GetBytes($"ServerMsg{i}"));
            }
            
            await s.SendTrailersAsync(StatusCode.OK);
        });
        
        // Client sends messages then reads server responses
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Race", "localhost");
        
        for (int i = 0; i < messageCount; i++)
        {
            await clientStream.SendMessageAsync(Encoding.UTF8.GetBytes($"ClientMsg{i}"));
        }
        await clientStream.SendHalfCloseAsync();
        
        await clientStream.ReceiveResponseHeadersAsync();
        await foreach (var msg in clientStream.ReceiveMessagesAsync())
        {
            clientReceived.Add(Encoding.UTF8.GetString(msg));
        }
        
        await serverTask;
        
        // Assert - both sides sent and received messages
        Assert.That(serverReceived, Has.Count.EqualTo(messageCount));
        Assert.That(clientReceived, Has.Count.EqualTo(messageCount));
        Assert.That(serverReceived[0], Is.EqualTo("ClientMsg0"));
        Assert.That(clientReceived[0], Is.EqualTo("ServerMsg0"));
    }
    
    [Test]
    [CancelAfter(10000)]
    public async Task ServerStreaming_ManySmallMessages_Success()
    {
        // Arrange
        var messageCount = 100;
        var segmentName = $"streaming_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 8192, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientReceived = new List<string>();
        
        // Server sends many small messages
        var serverTask = Task.Run(async () =>
        {
            var serverStream = await server.AcceptStreamAsync();
            Assert.That(serverStream, Is.Not.Null);
            using var s = serverStream!;
            
            await s.SendResponseHeadersAsync();
            
            // Drain client half-close
            await foreach (var _ in s.ReceiveMessagesAsync()) { }
            
            for (int i = 0; i < messageCount; i++)
            {
                await s.SendMessageAsync(Encoding.UTF8.GetBytes($"Message {i}"));
            }
            
            await s.SendTrailersAsync(StatusCode.OK);
        });
        
        // Client sends half-close then receives
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/ManyMessages", "localhost");
        await clientStream.SendHalfCloseAsync();
        
        await clientStream.ReceiveResponseHeadersAsync();
        await foreach (var msg in clientStream.ReceiveMessagesAsync())
        {
            clientReceived.Add(Encoding.UTF8.GetString(msg));
        }
        
        await serverTask;
        
        // Assert
        Assert.That(clientReceived, Has.Count.EqualTo(messageCount));
        Assert.That(clientReceived[0], Is.EqualTo("Message 0"));
        Assert.That(clientReceived[99], Is.EqualTo("Message 99"));
    }
    
    [Test]
    [CancelAfter(10000)]
    public async Task ClientStreaming_ManySmallMessages_Success()
    {
        // Arrange
        var messageCount = 100;
        var segmentName = $"streaming_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 8192, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var serverReceived = new List<string>();
        
        // Server reads all client messages
        var serverTask = Task.Run(async () =>
        {
            var serverStream = await server.AcceptStreamAsync();
            Assert.That(serverStream, Is.Not.Null);
            using var s = serverStream!;
            
            await s.SendResponseHeadersAsync();
            
            await foreach (var msg in s.ReceiveMessagesAsync())
            {
                lock (serverReceived)
                    serverReceived.Add(Encoding.UTF8.GetString(msg));
            }
            
            await s.SendTrailersAsync(StatusCode.OK);
        });
        
        // Client sends many messages
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/ManyClientMessages", "localhost");
        
        for (int i = 0; i < messageCount; i++)
        {
            await clientStream.SendMessageAsync(Encoding.UTF8.GetBytes($"Client {i}"));
        }
        
        await clientStream.SendHalfCloseAsync();
        
        await clientStream.ReceiveResponseHeadersAsync();
        await foreach (var _ in clientStream.ReceiveMessagesAsync()) { }
        
        await serverTask;
        
        // Assert
        Assert.That(serverReceived, Has.Count.EqualTo(messageCount));
        Assert.That(serverReceived[0], Is.EqualTo("Client 0"));
        Assert.That(serverReceived[99], Is.EqualTo("Client 99"));
    }
    
    [Test]
    [CancelAfter(10000)]
    public async Task BidirectionalStreaming_InterleavedMessages_Success()
    {
        // Arrange
        var segmentName = $"streaming_{Guid.NewGuid():N}";
        var rounds = 10;
        
        using var server = ShmConnection.CreateAsServer(segmentName, 8192, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var serverReceived = new List<string>();
        var clientReceived = new List<string>();
        
        // Server reads all then echoes back
        var serverTask = Task.Run(async () =>
        {
            var serverStream = await server.AcceptStreamAsync();
            Assert.That(serverStream, Is.Not.Null);
            using var s = serverStream!;
            
            await s.SendResponseHeadersAsync();
            
            await foreach (var msg in s.ReceiveMessagesAsync())
            {
                serverReceived.Add(Encoding.UTF8.GetString(msg));
            }
            
            for (int i = 0; i < rounds; i++)
            {
                await s.SendMessageAsync(Encoding.UTF8.GetBytes($"Echo {i}"));
            }
            
            await s.SendTrailersAsync(StatusCode.OK);
        });
        
        // Client sends then reads
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Interleaved", "localhost");
        
        for (int i = 0; i < rounds; i++)
        {
            await clientStream.SendMessageAsync(Encoding.UTF8.GetBytes($"Request {i}"));
        }
        
        await clientStream.SendHalfCloseAsync();
        
        await clientStream.ReceiveResponseHeadersAsync();
        await foreach (var msg in clientStream.ReceiveMessagesAsync())
        {
            clientReceived.Add(Encoding.UTF8.GetString(msg));
        }
        
        await serverTask;
        
        // Assert
        Assert.That(serverReceived, Has.Count.EqualTo(rounds));
        Assert.That(clientReceived, Has.Count.EqualTo(rounds));
        Assert.That(serverReceived[0], Is.EqualTo("Request 0"));
        Assert.That(clientReceived[0], Is.EqualTo("Echo 0"));
    }
    
    [Test]
    [CancelAfter(10000)]
    public async Task Stream_EmptyMessages_Success()
    {
        // Arrange
        var segmentName = $"streaming_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        byte[]? serverReceivedMsg = null;
        byte[]? clientReceivedMsg = null;
        
        // Server
        var serverTask = Task.Run(async () =>
        {
            var serverStream = await server.AcceptStreamAsync();
            Assert.That(serverStream, Is.Not.Null);
            using var s = serverStream!;
            
            await s.SendResponseHeadersAsync();
            
            await foreach (var msg in s.ReceiveMessagesAsync())
            {
                serverReceivedMsg = msg;
            }
            
            // Echo back an empty message
            await s.SendMessageAsync(Array.Empty<byte>());
            await s.SendTrailersAsync(StatusCode.OK);
        });
        
        // Client
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Empty", "localhost");
        await clientStream.SendMessageAsync(Array.Empty<byte>());
        await clientStream.SendHalfCloseAsync();
        
        await clientStream.ReceiveResponseHeadersAsync();
        await foreach (var msg in clientStream.ReceiveMessagesAsync())
        {
            clientReceivedMsg = msg;
        }
        
        await serverTask;
        
        Assert.That(serverReceivedMsg, Is.Not.Null);
        Assert.That(serverReceivedMsg!.Length, Is.EqualTo(0));
        Assert.That(clientReceivedMsg, Is.Not.Null);
        Assert.That(clientReceivedMsg!.Length, Is.EqualTo(0));
    }
    
    [Test]
    [CancelAfter(30000)]
    public async Task ParallelStreams_MultipleConnections_Success()
    {
        // Arrange
        var segmentName = $"streaming_{Guid.NewGuid():N}";
        var streamCount = 5;
        
        using var server = ShmConnection.CreateAsServer(segmentName, 16384, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var serverReceivedMessages = new string[streamCount];
        
        var tasks = new List<Task>();
        
        // Server accepts each stream and reads its message
        for (int i = 0; i < streamCount; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(async () =>
            {
                var serverStream = await server.AcceptStreamAsync();
                Assert.That(serverStream, Is.Not.Null);
                using var s = serverStream!;
                
                await s.SendResponseHeadersAsync();
                
                await foreach (var msg in s.ReceiveMessagesAsync())
                {
                    serverReceivedMessages[idx] = Encoding.UTF8.GetString(msg);
                }
                
                await s.SendTrailersAsync(StatusCode.OK);
            }));
        }
        
        // Create multiple parallel client streams
        for (int i = 0; i < streamCount; i++)
        {
            var streamId = i;
            tasks.Add(Task.Run(async () =>
            {
                var clientStream = client.CreateStream();
                await clientStream.SendRequestHeadersAsync($"/test/Parallel/{streamId}", "localhost");
                await clientStream.SendMessageAsync(Encoding.UTF8.GetBytes($"Stream {streamId}"));
                await clientStream.SendHalfCloseAsync();
                
                await clientStream.ReceiveResponseHeadersAsync();
                await foreach (var _ in clientStream.ReceiveMessagesAsync()) { }
            }));
        }
        
        await Task.WhenAll(tasks);
        
        // Assert every server task received a message
        for (int i = 0; i < streamCount; i++)
        {
            Assert.That(serverReceivedMessages[i], Is.Not.Null, $"Server stream {i} received no message");
            Assert.That(serverReceivedMessages[i], Does.StartWith("Stream "));
        }
    }
    
    private static byte[] CreateTestData(int size)
    {
        var data = new byte[size];
        var random = new Random(42); // Fixed seed for reproducibility
        random.NextBytes(data);
        return data;
    }
}
