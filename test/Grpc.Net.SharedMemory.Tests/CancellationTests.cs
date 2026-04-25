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

using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
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
    [CancelAfter(10000)]
    public async Task UnaryCall_ClientCancellationBeforeHeaders_CancellationSentToServerContext()
    {
        var segmentName = $"cancel_contract_{Guid.NewGuid():N}";
        var serverStartedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverCancellationTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = new ShmGrpcServer(segmentName);
        server.MapUnary<StringValue, StringValue>(
            "/test.Cancellation/WaitForCancel",
            async (_, context) =>
            {
                serverStartedTcs.SetResult();
                using var registration = context.CancellationToken.Register(
                    static state => ((TaskCompletionSource)state!).TrySetResult(), serverCancellationTcs);

                await serverCancellationTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                return new StringValue { Value = "cancelled" };
            });

        using var serverCts = new CancellationTokenSource();
        var serverTask = Task.Run(() => server.RunAsync(serverCts.Token));
        await WaitForServerAsync(segmentName);

        using var handler = new ShmControlHandler(segmentName, new ShmClientTransportOptions
        {
            EnableMultipleConnections = false,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        });
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        using var requestCts = new CancellationTokenSource();
        using var request = CreateGrpcRequest(
            "/test.Cancellation/WaitForCancel",
            new StringValue { Value = "hello" });

        var sendTask = invoker.SendAsync(request, requestCts.Token);

        await serverStartedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        requestCts.Cancel();

        Assert.That(async () => await sendTask.WaitAsync(TimeSpan.FromSeconds(5)), Throws.InstanceOf<OperationCanceledException>());
        await serverCancellationTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        serverCts.Cancel();
        await Task.WhenAny(serverTask, Task.Delay(TimeSpan.FromSeconds(5)));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task UnaryCall_DeadlineExceeded_CancelsServerContextAndReturnsDeadlineExceeded()
    {
        var segmentName = $"deadline_contract_{Guid.NewGuid():N}";
        var serverStartedTcs = new TaskCompletionSource<DateTime>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverCancellationTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = new ShmGrpcServer(segmentName);
        server.MapUnary<StringValue, StringValue>(
            "/test.Cancellation/WaitForDeadline",
            async (_, context) =>
            {
                serverStartedTcs.SetResult(context.Deadline);
                using var registration = context.CancellationToken.Register(
                    static state => ((TaskCompletionSource)state!).TrySetResult(), serverCancellationTcs);

                await serverCancellationTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                return new StringValue { Value = "deadline" };
            });

        using var serverCts = new CancellationTokenSource();
        var serverTask = Task.Run(() => server.RunAsync(serverCts.Token));
        await WaitForServerAsync(segmentName);

        using var handler = new ShmControlHandler(segmentName, new ShmClientTransportOptions
        {
            EnableMultipleConnections = false,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        });
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        using var request = CreateGrpcRequest(
            "/test.Cancellation/WaitForDeadline",
            new StringValue { Value = "hello" });
        request.Headers.TryAddWithoutValidation("grpc-timeout", "100m");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        var observedDeadline = await serverStartedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(observedDeadline, Is.LessThan(DateTime.UtcNow.AddSeconds(5)));

        await serverCancellationTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await response.Content.ReadAsByteArrayAsync();

        Assert.That(response.TrailingHeaders.TryGetValues("grpc-status", out var statusValues), Is.True);
        Assert.That(statusValues!.Single(), Is.EqualTo(((int)StatusCode.DeadlineExceeded).ToString(CultureInfo.InvariantCulture)));

        serverCts.Cancel();
        await Task.WhenAny(serverTask, Task.Delay(TimeSpan.FromSeconds(5)));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(5000)]
    public async Task CancelStream_BeforeSendingData_SetsCancelledFlag()
    {
        var segmentName = $"cancel_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/Cancel", "localhost");
        
        // Server accepts the stream
        var serverStream = await server.AcceptStreamAsync();
        Assert.That(serverStream, Is.Not.Null);
        
        // Cancel from client side
        await stream.CancelAsync();
        Assert.That(stream.IsCancelled, Is.True);
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(5000)]
    public async Task CancelStream_AfterSendingHeaders_SetsCancelledFlag()
    {
        var segmentName = $"cancel_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/Cancel", "localhost");
        
        // Server accepts
        var serverStream = await server.AcceptStreamAsync();
        Assert.That(serverStream, Is.Not.Null);
        
        // Cancel after sending headers
        await stream.CancelAsync();
        Assert.That(stream.IsCancelled, Is.True);
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(5000)]
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
    [CancelAfter(5000)]
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
    [CancelAfter(5000)]
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
    [CancelAfter(5000)]
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
    [CancelAfter(5000)]
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
    [CancelAfter(5000)]
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
        
        // Server accepts all three
        var ss1 = await server.AcceptStreamAsync();
        var ss2 = await server.AcceptStreamAsync();
        var ss3 = await server.AcceptStreamAsync();
        
        // Cancel only stream2
        await stream2.CancelAsync();
        
        Assert.That(stream1.IsCancelled, Is.False);
        Assert.That(stream2.IsCancelled, Is.True);
        Assert.That(stream3.IsCancelled, Is.False);
        
        // Other streams should still work — send and verify on server
        await stream1.SendMessageAsync(Encoding.UTF8.GetBytes("still works"));
        await stream1.SendHalfCloseAsync();
        byte[]? recv1 = null;
        await foreach (var m in ss1!.ReceiveMessagesAsync())
            recv1 = m;
        Assert.That(Encoding.UTF8.GetString(recv1!), Is.EqualTo("still works"));

        await stream3.SendMessageAsync(Encoding.UTF8.GetBytes("also works"));
        await stream3.SendHalfCloseAsync();
        byte[]? recv3 = null;
        await foreach (var m in ss3!.ReceiveMessagesAsync())
            recv3 = m;
        Assert.That(Encoding.UTF8.GetString(recv3!), Is.EqualTo("also works"));
    }

    private static HttpRequestMessage CreateGrpcRequest(string method, IMessage message)
    {
        var payload = message.ToByteArray();
        var grpcFrame = new byte[5 + payload.Length];
        grpcFrame[0] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(grpcFrame.AsSpan(1), (uint)payload.Length);
        payload.CopyTo(grpcFrame.AsSpan(5));

        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost" + method)
        {
            Version = new Version(2, 0),
            Content = new ByteArrayContent(grpcFrame)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        return request;
    }

    private static async Task WaitForServerAsync(string segmentName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var _ = Segment.OpenControlSegment(segmentName);
                return;
            }
            catch (FileNotFoundException)
            {
                await Task.Delay(10);
            }
        }

        Assert.Fail($"Server did not create control segment for '{segmentName}'.");
    }
}
