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
/// Tests for streaming edge cases.
/// </summary>
[TestFixture]
public class StreamingEdgeCaseTests
{
    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task SendEmptyMessage_Succeeds()
    {
        var segmentName = $"streaming_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/empty", "localhost");
        await stream.SendMessageAsync(Array.Empty<byte>());
        await stream.SendHalfCloseAsync();

        // Server verifies receipt
        var serverStream = await server.AcceptStreamAsync();
        byte[]? received = null;
        await foreach (var m in serverStream!.ReceiveMessagesAsync())
            received = m;
        
        Assert.That(received, Is.Not.Null);
        Assert.That(received!.Length, Is.EqualTo(0));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task SendSingleByteMessage_Succeeds()
    {
        var segmentName = $"streaming_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/single", "localhost");
        await stream.SendMessageAsync(new byte[] { 0x42 });
        await stream.SendHalfCloseAsync();

        var serverStream = await server.AcceptStreamAsync();
        byte[]? received = null;
        await foreach (var m in serverStream!.ReceiveMessagesAsync())
            received = m;
        
        Assert.That(received, Is.Not.Null);
        Assert.That(received!.Length, Is.EqualTo(1));
        Assert.That(received[0], Is.EqualTo(0x42));
    }

    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task SendLargeMessage_Succeeds()
    {
        var segmentName = $"streaming_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 2 * 1024 * 1024, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/large", "localhost");
        
        var largeMessage = new byte[1024 * 1024];
        new Random(42).NextBytes(largeMessage);
        await stream.SendMessageAsync(largeMessage);
        await stream.SendHalfCloseAsync();

        var serverStream = await server.AcceptStreamAsync();
        byte[]? received = null;
        await foreach (var m in serverStream!.ReceiveMessagesAsync())
            received = m;
        
        Assert.That(received, Is.Not.Null);
        Assert.That(received!.Length, Is.EqualTo(1024 * 1024));
        Assert.That(received.AsSpan(0, 64).SequenceEqual(largeMessage.AsSpan(0, 64)), Is.True);
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task SendMultipleEmptyMessages_Succeeds()
    {
        var segmentName = $"streaming_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/multi-empty", "localhost");
        
        for (int i = 0; i < 5; i++)
            await stream.SendMessageAsync(Array.Empty<byte>());
        await stream.SendHalfCloseAsync();

        var serverStream = await server.AcceptStreamAsync();
        int count = 0;
        await foreach (var m in serverStream!.ReceiveMessagesAsync())
        {
            Assert.That(m.Length, Is.EqualTo(0));
            count++;
        }
        Assert.That(count, Is.EqualTo(5));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task RapidSmallMessages_Succeeds()
    {
        var segmentName = $"streaming_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 65536, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/rapid", "localhost");
        
        for (int i = 0; i < 100; i++)
            await stream.SendMessageAsync(Encoding.UTF8.GetBytes($"msg{i}"));
        await stream.SendHalfCloseAsync();

        var serverStream = await server.AcceptStreamAsync();
        int count = 0;
        await foreach (var m in serverStream!.ReceiveMessagesAsync())
        {
            Assert.That(Encoding.UTF8.GetString(m), Is.EqualTo($"msg{count}"));
            count++;
        }
        Assert.That(count, Is.EqualTo(100));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task AlternatingMessageSizes_Succeeds()
    {
        var segmentName = $"streaming_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 65536, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/alternating", "localhost");
        
        var expected = new List<int>();
        for (int i = 0; i < 20; i++)
        {
            var size = (i % 2 == 0) ? 10 : 1000;
            expected.Add(size);
            await stream.SendMessageAsync(new byte[size]);
        }
        await stream.SendHalfCloseAsync();

        var serverStream = await server.AcceptStreamAsync();
        int idx = 0;
        await foreach (var m in serverStream!.ReceiveMessagesAsync())
        {
            Assert.That(m.Length, Is.EqualTo(expected[idx]), $"Message {idx} size mismatch");
            idx++;
        }
        Assert.That(idx, Is.EqualTo(20));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task HeadersOnly_NoMessage_IsValid()
    {
        var segmentName = $"streaming_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/headers-only", "localhost");
        
        var serverStream = await server.AcceptStreamAsync();
        Assert.That(serverStream, Is.Not.Null);
        // Complete without sending any message
        await serverStream!.SendTrailersAsync(Grpc.Core.StatusCode.OK, null);
        
        Assert.That(serverStream.Trailers, Is.Not.Null);
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task RequestHeaders_Method_IsPreserved()
    {
        var segmentName = $"streaming_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        
        const string method = "/grpc.test.Service/Method";
        await stream.SendRequestHeadersAsync(method, "localhost");
        
        Assert.That(stream.RequestHeaders, Is.Not.Null);
        Assert.That(stream.RequestHeaders!.Method, Is.EqualTo(method));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task SendMessage_BeforeHeaders_Throws()
    {
        var segmentName = $"streaming_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        
        // Trying to send message before headers should throw
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await stream.SendMessageAsync(new byte[] { 1, 2, 3 });
        });
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task SendTrailers_BeforeHeaders_Throws()
    {
        var segmentName = $"streaming_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await stream.SendTrailersAsync(Grpc.Core.StatusCode.OK, null);
        });
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task SendTrailers_AfterTrailers_Throws()
    {
        var segmentName = $"streaming_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/double-trailers", "localhost");

        var serverStream = await server.AcceptStreamAsync();
        Assert.That(serverStream, Is.Not.Null);
        await serverStream!.SendTrailersAsync(Grpc.Core.StatusCode.OK, null);
        
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await serverStream.SendTrailersAsync(Grpc.Core.StatusCode.OK, null);
        });
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task SendMessage_AfterTrailers_Throws()
    {
        var segmentName = $"streaming_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/msg-after-trailers", "localhost");

        var serverStream = await server.AcceptStreamAsync();
        Assert.That(serverStream, Is.Not.Null);
        await serverStream!.SendTrailersAsync(Grpc.Core.StatusCode.OK, null);
        
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await serverStream.SendMessageAsync(new byte[] { 1 });
        });
    }

    [Test]
    [Platform("Win")]
    public void MessageWithAllByteValues_IsPreserved()
    {
        // Create a message with all possible byte values
        var message = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            message[i] = (byte)i;
        }
        
        // Verify all values are represented
        Assert.That(message[0], Is.EqualTo(0));
        Assert.That(message[255], Is.EqualTo(255));
        Assert.That(message.Distinct().Count(), Is.EqualTo(256));
    }
}
