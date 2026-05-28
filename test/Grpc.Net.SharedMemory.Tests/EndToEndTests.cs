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
            await foreach (var m in s.ReceiveLpmMessagesAsync())
                received = m;

            // Send response
            await s.SendResponseHeadersAsync();
            await s.SendMessageAsync(LpmHelpers.WrapLpm(responseData));
            await s.SendTrailersAsync(StatusCode.OK, "Success");

            return received;
        });

        using var cs = clientConnection.CreateStream();
        var metadata = new Metadata { { "client-id", "test-client" } };
        await cs.SendRequestHeadersAsync("/greet.Greeter/SayHello", "localhost", metadata);
        await cs.SendMessageAsync(LpmHelpers.WrapLpm(requestData));
        await cs.SendHalfCloseAsync();

        // Read response
        await cs.ReceiveResponseHeadersAsync();
        byte[]? resp = null;
        await foreach (var m in cs.ReceiveLpmMessagesAsync())
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
            await foreach (var _ in s.ReceiveLpmMessagesAsync()) { }

            await s.SendResponseHeadersAsync();
            for (int i = 0; i < messageCount; i++)
            {
                var message = Encoding.UTF8.GetBytes($"Message {i}");
                await s.SendMessageAsync(LpmHelpers.WrapLpm(message));
            }
            await s.SendTrailersAsync(StatusCode.OK);
        });

        using var cs = clientConnection.CreateStream();
        await cs.SendRequestHeadersAsync("/test/ServerStream", "localhost");
        await cs.SendHalfCloseAsync();

        await cs.ReceiveResponseHeadersAsync();
        var received = new List<byte[]>();
        await foreach (var m in cs.ReceiveLpmMessagesAsync())
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
            await foreach (var m in s.ReceiveLpmMessagesAsync())
                received.Add(m);

            await s.SendResponseHeadersAsync();
            var summary = Encoding.UTF8.GetBytes($"Received {received.Count}");
            await s.SendMessageAsync(LpmHelpers.WrapLpm(summary));
            await s.SendTrailersAsync(StatusCode.OK);

            return received;
        });

        using var cs = clientConnection.CreateStream();
        await cs.SendRequestHeadersAsync("/test/ClientStream", "localhost");

        for (int i = 0; i < messageCount; i++)
        {
            var message = Encoding.UTF8.GetBytes($"Client message {i}");
            await cs.SendMessageAsync(LpmHelpers.WrapLpm(message));
        }
        await cs.SendHalfCloseAsync();

        await cs.ReceiveResponseHeadersAsync();
        byte[]? resp = null;
        await foreach (var m in cs.ReceiveLpmMessagesAsync())
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
            await foreach (var m in s.ReceiveLpmMessagesAsync())
                received.Add(m);

            // Echo them back
            await s.SendResponseHeadersAsync();
            foreach (var msg in received)
                await s.SendMessageAsync(LpmHelpers.WrapLpm(msg));
            await s.SendTrailersAsync(StatusCode.OK);

            return received;
        });

        using var cs = clientConnection.CreateStream();
        await cs.SendRequestHeadersAsync("/test/BiDi", "localhost");

        for (int i = 0; i < 3; i++)
        {
            var message = Encoding.UTF8.GetBytes($"Request {i}");
            await cs.SendMessageAsync(LpmHelpers.WrapLpm(message));
        }
        await cs.SendHalfCloseAsync();

        await cs.ReceiveResponseHeadersAsync();
        var clientReceived = new List<byte[]>();
        await foreach (var m in cs.ReceiveLpmMessagesAsync())
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
            await foreach (var _ in s.ReceiveLpmMessagesAsync()) { }

            // Return error
            await s.SendResponseHeadersAsync();
            await s.SendTrailersAsync(StatusCode.InvalidArgument, "Missing required field");
        });

        using var cs = clientConnection.CreateStream();
        await cs.SendRequestHeadersAsync("/test/Error", "localhost");
        await cs.SendHalfCloseAsync();

        await cs.ReceiveResponseHeadersAsync();
        await foreach (var _ in cs.ReceiveLpmMessagesAsync()) { }

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
        await foreach (var _ in serverStream!.ReceiveLpmMessagesAsync())
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
            await foreach (var _ in s.ReceiveLpmMessagesAsync()) { }
            await s.SendResponseHeadersAsync();
            await s.SendTrailersAsync(StatusCode.OK);

            return headers;
        });

        using var cs = clientConnection.CreateStream();
        await cs.SendRequestHeadersAsync("/test/Deadline", "localhost", deadline: deadline);
        await cs.SendHalfCloseAsync();

        await cs.ReceiveResponseHeadersAsync();
        await foreach (var _ in cs.ReceiveLpmMessagesAsync()) { }

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

            await foreach (var _ in s.ReceiveLpmMessagesAsync()) { }
            await s.SendResponseHeadersAsync();
            await s.SendTrailersAsync(StatusCode.OK);

            return headers;
        });

        using var cs = clientConnection.CreateStream();
        await cs.SendRequestHeadersAsync("/test/Metadata", "localhost", metadata);
        await cs.SendHalfCloseAsync();

        await cs.ReceiveResponseHeadersAsync();
        await foreach (var _ in cs.ReceiveLpmMessagesAsync()) { }

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
            await foreach (var m in s.ReceiveLpmMessagesAsync())
                received = m;

            // Echo it back
            await s.SendResponseHeadersAsync();
            await s.SendMessageAsync(LpmHelpers.WrapLpm(received!));
            await s.SendTrailersAsync(StatusCode.OK);

            return received;
        });

        using var cs = clientConnection.CreateStream();
        await cs.SendRequestHeadersAsync("/test/Large", "localhost");
        await cs.SendMessageAsync(LpmHelpers.WrapLpm(largeMessage));
        await cs.SendHalfCloseAsync();

        await cs.ReceiveResponseHeadersAsync();
        byte[]? resp = null;
        await foreach (var m in cs.ReceiveLpmMessagesAsync())
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

    /// <summary>
    /// Sub-threshold guard: a small unary RPC in DEFAULT window mode
    /// (32 MiB initial limit, drip at limit/4 = 8 MiB) MUST NOT emit
    /// any <c>WINDOW_UPDATE</c> wire frames because neither the
    /// per-stream drip nor the per-LPM pre-credit threshold is crossed.
    /// This guards the fast path (no FC overhead for typical RPC sizes)
    /// per the gRFC SHM HTTP/2 FC design (Phase A).
    ///
    /// A companion test (<c>WindowUpdate_EmittedForLargeTransfer</c>)
    /// verifies the positive case where WU IS emitted.
    /// </summary>
    [Test]
    [NonParallelizable] // Uses process-global s_wuFramesEmitted counter.
    [CancelAfter(10000)]
    public async Task NoWindowUpdate_EmittedForSmallUnary_SubDripThreshold()
    {
        // Snapshot before
        var before = ShmConnection.WindowUpdateFramesEmittedForTest();

        // Repeat the standard E2E unary RPC (same shape as
        // UnaryCall_SimpleRequestResponse_Works above).
        var segmentName = $"grpc_no_wu_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 4096, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        var requestData = Encoding.UTF8.GetBytes("GreeterClient");
        var responseData = Encoding.UTF8.GetBytes("Hello, World!");

        var serverTask = Task.Run(async () =>
        {
            var stream = await serverConnection.AcceptStreamAsync();
            using var s = stream!;
            byte[]? received = null;
            await foreach (var m in s.ReceiveLpmMessagesAsync()) received = m;
            await s.SendResponseHeadersAsync();
            await s.SendMessageAsync(LpmHelpers.WrapLpm(responseData));
            await s.SendTrailersAsync(StatusCode.OK, "Success");
            return received;
        });

        using var cs = clientConnection.CreateStream();
        await cs.SendRequestHeadersAsync("/greet.Greeter/SayHello", "localhost");
        await cs.SendMessageAsync(LpmHelpers.WrapLpm(requestData));
        await cs.SendHalfCloseAsync();
        await cs.ReceiveResponseHeadersAsync();
        byte[]? resp = null;
        await foreach (var m in cs.ReceiveLpmMessagesAsync()) resp = m;
        var serverReceived = await serverTask;

        Assert.That(serverReceived, Is.EqualTo(requestData), "request body interop");
        Assert.That(resp, Is.EqualTo(responseData), "response body interop");
        Assert.That(cs.Trailers!.GrpcStatusCode, Is.EqualTo(StatusCode.OK));

        // Small-RPC fast-path: payload is far below the 8 MiB drip
        // threshold and 32 MiB pre-credit headroom, so NO WU frames
        // should be emitted on the wire.
        var after = ShmConnection.WindowUpdateFramesEmittedForTest();
        Assert.That(after, Is.EqualTo(before),
            "Sub-drip-threshold RPCs must not emit WINDOW_UPDATE frames " +
            "(default initial window 32 MiB, drip threshold 8 MiB). " +
            "Seeing WU here means a fast-path FC optimization regressed.");
    }

    /// <summary>
    /// Positive WU guard: when the receiver drains more than the
    /// stream-level drip threshold (limit/4 = 8 MiB on default 32 MiB
    /// initial window), it MUST emit a stream-level
    /// <c>WINDOW_UPDATE</c>. This guards the gRFC SHM HTTP/2 FC
    /// implementation against regression to no-WU mode.
    /// </summary>
    [Test]
    [NonParallelizable] // Uses process-global s_wuFramesEmitted counter.
    [CancelAfter(30000)]
    public async Task WindowUpdate_EmittedForLargeTransfer_AboveDripThreshold()
    {
        var before = ShmConnection.WindowUpdateFramesEmittedForTest();

        var segmentName = $"grpc_yes_wu_{Guid.NewGuid():N}";
        // Larger ring (16 MiB) so we can actually push enough bytes to
        // cross the 8 MiB drip threshold without ring backpressure.
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 16 * 1024 * 1024, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        // Payload chosen > limit/4 = 8 MiB so the receiver's drip
        // threshold is crossed once the application drains it.
        var bigPayload = new byte[10 * 1024 * 1024];
        new Random(42).NextBytes(bigPayload);
        var responseData = Encoding.UTF8.GetBytes("ok");

        var serverTask = Task.Run(async () =>
        {
            var stream = await serverConnection.AcceptStreamAsync();
            using var s = stream!;
            byte[]? received = null;
            await foreach (var m in s.ReceiveLpmMessagesAsync()) received = m;
            await s.SendResponseHeadersAsync();
            await s.SendMessageAsync(LpmHelpers.WrapLpm(responseData));
            await s.SendTrailersAsync(StatusCode.OK, "Success");
            return received;
        });

        using var cs = clientConnection.CreateStream();
        await cs.SendRequestHeadersAsync("/big.Greeter/SayHello", "localhost");
        await cs.SendMessageAsync(LpmHelpers.WrapLpm(bigPayload));
        await cs.SendHalfCloseAsync();
        await cs.ReceiveResponseHeadersAsync();
        byte[]? resp = null;
        await foreach (var m in cs.ReceiveLpmMessagesAsync()) resp = m;
        var serverReceived = await serverTask;

        Assert.That(serverReceived!.Length, Is.EqualTo(bigPayload.Length), "request bytes received");
        Assert.That(resp, Is.EqualTo(responseData));
        Assert.That(cs.Trailers!.GrpcStatusCode, Is.EqualTo(StatusCode.OK));

        var after = ShmConnection.WindowUpdateFramesEmittedForTest();
        Assert.That(after, Is.GreaterThan(before),
            "Transferring 10 MiB through a 32 MiB initial window (drip at " +
            "8 MiB) MUST emit at least one WINDOW_UPDATE frame. Zero WU " +
            "emissions here means the FC drip path is not wired or is " +
            "miscounting.");
    }

    /// <summary>
    /// Verifies the contract of
    /// <see cref="ShmConnection.InlineReceiveContinuations"/>: when the
    /// flag is set on the connection BEFORE a stream is created, the
    /// stream's inbound channel must dispatch consumer continuations
    /// synchronously on a non-ThreadPool dispatch thread (the SHM
    /// reader thread, or, with the receive striper enabled, one of
    /// the four stripe Threads). This is the per-receive optimisation
    /// that saves ~17 us/hop on Windows; the test asserts the
    /// contract by checking <see cref="Thread.IsThreadPoolThread"/>
    /// on the continuation execution thread.
    ///
    /// Note: with the receive-side striper enabled (default), the
    /// inbound channel uses AllowSynchronousContinuations=true even
    /// when InlineReceiveContinuations is left at its default value,
    /// because the stripe Thread is a safe single-dispatch-point.
    /// So both halves of this test exercise the non-ThreadPool
    /// dispatch path; setting SHM_RECEIVE_STRIPER=0 restores the
    /// legacy reader-Thread-only behaviour where InlineReceiveContinuations
    /// is the sole knob.
    /// </summary>
    [Test]
    [Platform("Win")]
    [CancelAfter(15000)]
    public async Task InlineReceiveContinuations_OptIn_RunsConsumerOnReaderThread()
    {
        var inlineThreadIsPool = await RunInlineReceiveContractAsync(inline: true).ConfigureAwait(false);
        var legacyThreadIsPool = await RunInlineReceiveContractAsync(inline: false).ConfigureAwait(false);

        Assert.That(inlineThreadIsPool, Is.False,
            "InlineReceiveContinuations=true: consumer continuation should run " +
            "on the SHM dispatch thread (reader Thread, or stripe Thread when " +
            "the receive striper is enabled). " +
            "IsThreadPoolThread=true means the continuation was dispatched " +
            "via ThreadPool — i.e., AllowSynchronousContinuations was NOT " +
            "honoured on the inbound Channel.");

        // Striper default-on. With the striper enabled, the stripe
        // Thread is the inbound-channel writer and
        // AllowSynchronousContinuations is forced ON regardless of
        // the InlineReceiveContinuations flag (see ShmGrpcStream
        // ctor). So the default-environment branch dispatches inline
        // on the stripe Thread as well.
        var striperDisabled = string.Equals(
            Environment.GetEnvironmentVariable("SHM_RECEIVE_STRIPER"),
            "0", StringComparison.Ordinal);
        if (striperDisabled)
        {
            Assert.That(legacyThreadIsPool, Is.True,
                "InlineReceiveContinuations=false (default) with striper off: " +
                "consumer continuation should run on a ThreadPool worker. " +
                "IsThreadPoolThread=false means the continuation was inlined " +
                "despite the flag being off.");
        }
        else
        {
            Assert.That(legacyThreadIsPool, Is.False,
                "InlineReceiveContinuations=false (default) with striper on: " +
                "consumer continuation should still run on the stripe Thread " +
                "(non-ThreadPool). IsThreadPoolThread=true means the striper " +
                "did not engage AllowSynchronousContinuations on the per-stream " +
                "channel.");
        }

        static async Task<bool> RunInlineReceiveContractAsync(bool inline)
        {
            var segmentName = $"grpc_inlinerx_{Guid.NewGuid():N}";
            using var server = ShmConnection.CreateAsServer(segmentName, ringCapacity: 4096, maxStreams: 100);
            using var client = ShmConnection.ConnectAsClient(segmentName);

            // Flag MUST be set before any inbound channel is constructed —
            // Channel<T> options are immutable after construction.
            client.InlineReceiveContinuations = inline;

            var serverAcceptTask = Task.Run(async () => await server.AcceptStreamAsync().ConfigureAwait(false));
            using var cs = client.CreateStream();
            await cs.SendRequestHeadersAsync("/inlinerx.Test/Probe", "localhost").ConfigureAwait(false);

            var ss = (await serverAcceptTask.ConfigureAwait(false))!;
            using var srv = ss;

            // Park a consumer that captures the executing thread's
            // ThreadPool-membership the instant a frame arrives.
            var consumerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool? consumerThreadIsPool = null;
            var consumerTask = Task.Run(async () =>
            {
                consumerStarted.SetResult();
                await foreach (var _ in cs.ReceiveLpmMessagesAsync().ConfigureAwait(false))
                {
                    consumerThreadIsPool = Thread.CurrentThread.IsThreadPoolThread;
                    break;
                }
            });

            // Wait for the consumer task to be scheduled and parked on
            // the inbound channel's WaitToReadAsync. The 100 ms is well
            // above scheduler jitter on a 4-core Windows VM and ensures
            // the producer's TryWrite hits a registered awaiter — that's
            // the code path AllowSynchronousContinuations affects.
            await consumerStarted.Task.ConfigureAwait(false);
            await Task.Delay(100).ConfigureAwait(false);

            await srv.SendResponseHeadersAsync().ConfigureAwait(false);
            await srv.SendMessageAsync(LpmHelpers.WrapLpm(Encoding.UTF8.GetBytes("probe"))).ConfigureAwait(false);
            await srv.SendTrailersAsync(StatusCode.OK).ConfigureAwait(false);

            await consumerTask.ConfigureAwait(false);

            Assert.That(consumerThreadIsPool, Is.Not.Null,
                "Consumer never observed the inbound message — receive path is broken.");
            return consumerThreadIsPool!.Value;
        }
    }
}
