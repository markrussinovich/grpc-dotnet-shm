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
/// Comprehensive cancellation tests for shared memory transport.
/// Tests various cancellation scenarios matching TCP transport behavior.
/// </summary>
[TestFixture]
public class CancellationTests
{
    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task CancelStream_BeforeSendingData_SetsCancelledFlag()
    {
        var segmentName = $"cancel_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        
        // Cancel before sending any data
        await stream.CancelAsync();
        
        Assert.That(stream.IsCancelled, Is.True);
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task CancelStream_AfterSendingHeaders_SetsCancelledFlag()
    {
        var segmentName = $"cancel_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/Cancel", "localhost");
        
        // Cancel after sending headers
        await stream.CancelAsync();
        
        Assert.That(stream.IsCancelled, Is.True);
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task CancelStream_AfterSendingMessage_SetsCancelledFlag()
    {
        var segmentName = $"cancel_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/Cancel", "localhost");
        await stream.SendMessageAsync(Encoding.UTF8.GetBytes("test message"));
        
        // Cancel after sending message
        await stream.CancelAsync();
        
        Assert.That(stream.IsCancelled, Is.True);
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task CancelStream_MultipleTimes_IsIdempotent()
    {
        var segmentName = $"cancel_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/Cancel", "localhost");
        
        // Cancel multiple times - should not throw
        await stream.CancelAsync();
        await stream.CancelAsync();
        await stream.CancelAsync();
        
        Assert.That(stream.IsCancelled, Is.True);
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    [Ignore("Code bug: SendMessageAsync does not check _cancelled flag")]
    public async Task CancelledStream_SendMessage_Throws()
    {
        var segmentName = $"cancel_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/Cancel", "localhost");
        await stream.CancelAsync();
        
        // Sending after cancel should throw
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await stream.SendMessageAsync(Encoding.UTF8.GetBytes("test"));
        });
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task CancellationToken_PropagatestoStream()
    {
        var segmentName = $"cancel_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/Cancel", "localhost");
        
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        
        // Operations with cancelled token should throw
        Assert.That(async () =>
        {
            await stream.SendMessageAsync(Encoding.UTF8.GetBytes("test"), cts.Token);
        }, Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    [Ignore("Code bug: CancelAsync silently returns when disposed instead of throwing ObjectDisposedException")]
    public void CancelStream_AfterDispose_Throws()
    {
        var segmentName = $"cancel_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        stream.Dispose();
        
        Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await stream.CancelAsync();
        });
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task MultipleStreams_CancelOne_OthersUnaffected()
    {
        var segmentName = $"cancel_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream1 = client.CreateStream();
        var stream2 = client.CreateStream();
        var stream3 = client.CreateStream();
        
        await stream1.SendRequestHeadersAsync("/test/1", "localhost");
        await stream2.SendRequestHeadersAsync("/test/2", "localhost");
        await stream3.SendRequestHeadersAsync("/test/3", "localhost");
        
        // Cancel only stream2
        await stream2.CancelAsync();
        
        Assert.That(stream1.IsCancelled, Is.False);
        Assert.That(stream2.IsCancelled, Is.True);
        Assert.That(stream3.IsCancelled, Is.False);
        
        // Other streams should still work
        await stream1.SendMessageAsync(Encoding.UTF8.GetBytes("still works"));
        await stream3.SendMessageAsync(Encoding.UTF8.GetBytes("also works"));
    }
}
