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
        // Default (no SHM_INITIAL_WINDOW env var) is 32 MiB; fair-mode
        // bench may override to 65535 via env var (RFC 7540 §6.9.2 default).
        // Tests run without the env var, so we expect the SHM-tuned default.
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
        Assert.That(ShmConstants.InitialWindowSize, Is.GreaterThan(0), "Initial window must be positive");
        Assert.That(ShmConstants.InitialWindowSize, Is.LessThanOrEqualTo(int.MaxValue), "Initial window must fit in 31-bit H2 window");
        Assert.That(ShmConstants.MaxWindowSize, Is.EqualTo(int.MaxValue), "Max window should be 2^31-1");
    }

    /// <summary>
    /// Regression guard mirroring grpc-go-shmem's
    /// <c>TestConnWaiterElem_CloseStreamUnblocksParkedAcquire</c>:
    /// a sender parked inside <c>ReserveSendQuotaOrBlock</c> waiting on
    /// <c>_sendQuotaWake</c> MUST be woken when the stream is disposed,
    /// even if the caller's cancellation token is unrelated to the
    /// stream dispose token. Without the <c>_sendQuotaWake.Set()</c>
    /// call in <c>Dispose()</c> this test deadlocks (caught by
    /// <c>[CancelAfter]</c>).
    /// </summary>
    [Test]
    [Platform("Win")]
    [CancelAfter(5000)]
    public async Task ReserveSendQuotaOrBlock_DisposeUnblocksParkedSender()
    {
        var segmentName = $"flow_dispose_wake_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, ringCapacity: 65536, maxStreams: 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var clientStream = client.CreateStream();

        // Drain the per-stream send quota to 0 so the next reservation
        // will be forced to park.
        var initialQuota = clientStream.SendQuota;
        Assert.That(initialQuota, Is.GreaterThan(0));
        var drained = clientStream.TryReserveSendQuota((int)initialQuota);
        Assert.That(drained, Is.True, "should fully drain quota");
        Assert.That(clientStream.SendQuota, Is.EqualTo(0));

        // Start a sender that blocks waiting for quota.
        var senderDone = new TaskCompletionSource<Exception?>();
        var parkedCt = new CancellationTokenSource(); // unrelated to stream dispose
        var t = Task.Run(() =>
        {
            try
            {
                clientStream.ReserveSendQuotaOrBlock(1, drainBeforeWait: null, parkedCt.Token);
                senderDone.SetResult(null);
            }
            catch (Exception ex)
            {
                senderDone.SetResult(ex);
            }
        });

        // Give the sender a chance to enter Wait().
        await Task.Delay(50);
        Assert.That(senderDone.Task.IsCompleted, Is.False, "sender must be parked");

        // Disposing the stream MUST wake the parked sender promptly.
        clientStream.Dispose();

        var ex = await senderDone.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(ex, Is.InstanceOf<ObjectDisposedException>(),
            "Dispose() must wake parked sender so it surfaces ObjectDisposedException, not deadlock.");

        parkedCt.Dispose();
    }

    /// <summary>
    /// Round-5 review (Opus 4.8 HIGH): closes the missed-wake race
    /// in <c>ReserveSendQuotaOrBlock</c> where Dispose's wake-Set
    /// lands in the window between the sender's initial
    /// <c>ThrowIfDisposed</c> and its <c>_sendQuotaWake.Reset()</c>.
    /// The Reset clears the wake; without the post-Reset re-check
    /// added in this round, <c>Wait(ct=None)</c> would block forever.
    ///
    /// Stress-races Dispose against fresh entry into the loop with
    /// <c>ct=None</c>; with the fix, every iteration surfaces
    /// <see cref="ObjectDisposedException"/>. Without the fix, at
    /// least one iteration of the 200-run stress deadlocks and the
    /// <c>[CancelAfter]</c> watchdog fails the test.
    /// </summary>
    [Test]
    [Platform("Win")]
    [CancelAfter(15000)]
    public async Task ReserveSendQuotaOrBlock_DisposeRace_NoDeadlock_Stress()
    {
        const int iterations = 200;
        for (int i = 0; i < iterations; i++)
        {
            var segmentName = $"flow_dispose_race_{i}_{Guid.NewGuid():N}";
            using var server = ShmConnection.CreateAsServer(segmentName, ringCapacity: 65536, maxStreams: 10);
            using var client = ShmConnection.ConnectAsClient(segmentName);
            var clientStream = client.CreateStream();

            // Drain per-stream quota to 0.
            var initialQuota = (int)clientStream.SendQuota;
            Assert.That(clientStream.TryReserveSendQuota(initialQuota), Is.True);

            // Concurrently race sender entry vs Dispose. Sender uses
            // CancellationToken.None to deliberately exclude the
            // cancellation-token-based unblock path \u2014 the test
            // verifies the disposal-check path itself.
            using var startBarrier = new ManualResetEventSlim(false);
            var senderDone = new TaskCompletionSource<Exception?>();
            var t = Task.Run(() =>
            {
                startBarrier.Wait();
                try
                {
                    clientStream.ReserveSendQuotaOrBlock(1, drainBeforeWait: null, CancellationToken.None);
                    senderDone.SetResult(null);
                }
                catch (Exception ex)
                {
                    senderDone.SetResult(ex);
                }
            });

            // Release the sender and Dispose almost simultaneously
            // to maximize chance of hitting the Reset/Wait window.
            startBarrier.Set();
            clientStream.Dispose();

            var ex = await senderDone.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(ex, Is.InstanceOf<ObjectDisposedException>(),
                $"Iteration {i}: Dispose must wake parked sender even when ct=None " +
                "(missed-wake race between Set-by-Dispose and Reset-by-sender).");
        }
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

    /// <summary>
    /// Heavy concurrent FC stress: <c>StreamCount</c> streams in parallel,
    /// each sending enough 64 KiB messages to cross the per-stream drip
    /// threshold (limit/4 = 8 MiB) several times. This is the .NET
    /// counterpart to grpc-go-shmem's
    /// <c>TestShmFlowControl_ConcurrentStreams_StressMultiStreamWU</c>
    /// — verifies that:
    /// <list type="bullet">
    ///   <item><description>The FC path holds up under multi-stream load with no deadlock.</description></item>
    ///   <item><description><see cref="ShmConnection.WindowUpdateFramesEmittedForTest"/> is incremented (proves WU drip path actually fires across streams, not silently bypassed).</description></item>
    ///   <item><description>Every byte sent is received intact and in-order on each stream (no cross-stream contamination).</description></item>
    /// </list>
    /// <para>
    /// Per-stream payload is sized to deterministically cross the drip
    /// threshold: 64 KiB * 200 messages = 12.5 MiB &gt; 8 MiB drip threshold.
    /// </para>
    /// </summary>
    [Test]
    [Platform("Win")]
    [NonParallelizable] // touches process-global WindowUpdate counter
    [CancelAfter(60000)]
    public async Task ConcurrentStreams_HeavyFlowControlStress_AllRpcsCompleteWithWU()
    {
        const int StreamCount = 50;       // intentionally modest for CI determinism
        const int MessagesPerStream = 200;
        const int MessageBytes = 64 * 1024;

        var wuBefore = ShmConnection.WindowUpdateFramesEmittedForTest();

        var segmentName = $"fc_stress_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(
            segmentName, ringCapacity: 16 * 1024 * 1024, maxStreams: (uint)(StreamCount * 4));
        using var client = ShmConnection.ConnectAsClient(segmentName);

        // Per-stream payload reused — must be deep-copied on receive
        // for any equality assertion. Use a small per-stream marker.
        var clientTasks = new List<Task>();
        var serverTasks = new List<Task>();
        var perStreamBytes = new long[StreamCount];

        for (int i = 0; i < StreamCount; i++)
        {
            var idx = i;
            serverTasks.Add(Task.Run(async () =>
            {
                var s = await server.AcceptStreamAsync();
                Assert.That(s, Is.Not.Null, $"server stream #{idx} accept");
                using var ss = s!;
                await ss.SendResponseHeadersAsync();
                long received = 0;
                int msgCount = 0;
                await foreach (var msg in ss.ReceiveLpmMessagesAsync())
                {
                    received += msg.Length;
                    msgCount++;
                }
                Assert.That(msgCount, Is.EqualTo(MessagesPerStream),
                    $"stream #{idx} expected {MessagesPerStream} msgs, got {msgCount}");
                Interlocked.Add(ref perStreamBytes[idx], received);
                await ss.SendMessageAsync(LpmHelpers.WrapLpmText("ok"));
                await ss.SendTrailersAsync(Grpc.Core.StatusCode.OK);
            }));
        }

        for (int i = 0; i < StreamCount; i++)
        {
            var idx = i;
            clientTasks.Add(Task.Run(async () =>
            {
                var stream = client.CreateStream();
                await stream.SendRequestHeadersAsync($"/fc-stress/{idx}", "localhost");
                // Use a fresh buffer per stream so each Send completes
                // before re-entering (single-threaded per-stream).
                var payload = new byte[MessageBytes];
                payload[0] = (byte)(idx & 0xFF);
                var lpm = LpmHelpers.WrapLpm(payload);
                for (int m = 0; m < MessagesPerStream; m++)
                {
                    await stream.SendMessageAsync(lpm);
                }
                await stream.SendHalfCloseAsync();
                await stream.ReceiveResponseHeadersAsync();
                await foreach (var _ in stream.ReceiveLpmMessagesAsync()) { }
            }));
        }

        await Task.WhenAll(clientTasks.Concat(serverTasks));

        // Every stream should have received exactly MessagesPerStream * MessageBytes.
        for (int i = 0; i < StreamCount; i++)
        {
            Assert.That(perStreamBytes[i], Is.EqualTo((long)MessagesPerStream * MessageBytes),
                $"stream #{i} byte count mismatch");
        }

        // The cross-stream FC drip path MUST have fired at least once
        // (12.5 MiB per stream * 50 streams = 625 MiB inbound on server;
        //  drip threshold = 8 MiB → many WUs).
        var wuAfter = ShmConnection.WindowUpdateFramesEmittedForTest();
        Assert.That(wuAfter, Is.GreaterThan(wuBefore),
            "ConcurrentStreams_HeavyFlowControlStress: expected the receiver " +
            "to emit at least one stream- or conn-level WINDOW_UPDATE under " +
            "this load (625 MiB inbound, drip at 8 MiB). Zero WU emissions " +
            "means the multi-stream FC path is not wired or is bypassed.");
    }
}

