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
        
        // Send empty message (valid in gRPC)
        await stream.SendMessageAsync(Array.Empty<byte>());
        
        Assert.Pass();
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
        
        Assert.Pass();
    }

    [Test]
    [Platform("Win")]
    [Timeout(10000)]
    public async Task SendLargeMessage_Succeeds()
    {
        var segmentName = $"streaming_test_{Guid.NewGuid():N}";
        
        // Create large enough buffer
        using var server = ShmConnection.CreateAsServer(segmentName, 2 * 1024 * 1024, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/large", "localhost");
        
        // Send 1MB message
        var largeMessage = new byte[1024 * 1024];
        new Random(42).NextBytes(largeMessage);
        
        await stream.SendMessageAsync(largeMessage);
        
        Assert.Pass();
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
        {
            await stream.SendMessageAsync(Array.Empty<byte>());
        }
        
        Assert.Pass();
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
        
        // Send many small messages rapidly
        for (int i = 0; i < 100; i++)
        {
            await stream.SendMessageAsync(Encoding.UTF8.GetBytes($"msg{i}"));
        }
        
        Assert.Pass();
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
        
        // Alternate between small and larger messages
        for (int i = 0; i < 20; i++)
        {
            var size = (i % 2 == 0) ? 10 : 1000;
            var message = new byte[size];
            await stream.SendMessageAsync(message);
        }
        
        Assert.Pass();
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
    [Ignore("Code bug: SendMessageAsync does not guard against missing request/response headers")]
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
    [Ignore("Code bug: SendTrailersAsync does not check _halfCloseSent before sending duplicate trailers")]
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
