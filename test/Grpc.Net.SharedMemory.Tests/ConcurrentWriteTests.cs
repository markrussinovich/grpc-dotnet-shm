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

using Grpc.Core;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Tests for concurrent frame writes through <see cref="ShmConnection"/>.
/// Exercises the batched <see cref="ShmFrameWriter"/> write path through the
/// public <see cref="ShmGrpcStream"/> API to verify correctness under both
/// uncontended and highly concurrent scenarios.
/// </summary>
[TestFixture]
[Platform("Win")]
public class ConcurrentWriteTests
{
    private const ulong RingCapacity = 64 * 1024; // 64 KB — small enough for fast tests

    /// <summary>
    /// Creates a connected client/server pair over shared memory.
    /// </summary>
    private static (ShmConnection server, ShmConnection client) CreatePair(string? suffix = null)
    {
        var name = $"grpc_cw_test_{suffix ?? Guid.NewGuid().ToString("N")}";
        var server = ShmConnection.CreateAsServer(name, ringCapacity: RingCapacity, maxStreams: 500);
        var client = ShmConnection.ConnectAsClient(name);
        return (server, client);
    }

    [Test]
    public async Task SingleStream_UnaryPattern_Succeeds()
    {
        // Arrange — one stream, sequential sends (no lock contention).
        var (server, client) = CreatePair();
        using var s = server;
        using var c = client;

        var stream = client.CreateStream();
        using var d = stream;

        // Act
        await stream.SendRequestHeadersAsync("/test/Method", "localhost");
        await stream.SendMessageAsync(new byte[] { 1, 2, 3, 4 });
        await stream.SendHalfCloseAsync();

        // Assert — connection is still healthy.
        Assert.That(client.IsClosed, Is.False);
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task SingleStream_ZeroCopy_MessageReceivedOnServer()
    {
        // Verify that SendMessageZeroCopyAsync writes the correct payload
        // to the ring and the server can read it back.
        var (server, client) = CreatePair();
        using var s = server;
        using var c = client;

        var clientStream = client.CreateStream();
        using var cd = clientStream;

        var expectedPayload = new byte[] { 10, 20, 30, 40, 50 };

        await clientStream.SendRequestHeadersAsync("/test/ZeroCopy", "localhost");

        // Allocate a buffer and send via zero-copy.
        var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(64);
        expectedPayload.CopyTo(buf, 0);
        await clientStream.SendMessageZeroCopyAsync(
            buf.AsMemory(0, expectedPayload.Length),
            buf);
        await clientStream.SendHalfCloseAsync();

        // Server reads the stream and verifies payload content.
        // Note: headers are already decoded by AcceptStreamAsync;
        // use RequestHeaders property instead of ReceiveRequestHeadersAsync.
        var serverStream = await server.AcceptStreamAsync(TestContext.CurrentContext.CancellationToken);
        Assert.That(serverStream, Is.Not.Null);
        using var sd = serverStream!;
        Assert.That(sd.RequestHeaders?.Method, Is.EqualTo("/test/ZeroCopy"));

        var receivedMessages = new List<byte[]>();
        await foreach (var msg in sd.ReceiveMessagesAsync(TestContext.CurrentContext.CancellationToken))
        {
            receivedMessages.Add(msg);
        }

        Assert.That(receivedMessages, Has.Count.EqualTo(1));
        Assert.That(receivedMessages[0], Is.EqualTo(expectedPayload));
    }

    [Test]
    public void ConcurrentStreams_AllFramesWritten()
    {
        // Arrange — many streams writing concurrently to exercise batched writer contention.
        var (server, client) = CreatePair();
        using var _ = server;
        using var __ = client;

        const int streamCount = 32;
        const int messagesPerStream = 20;
        var barrier = new Barrier(streamCount);
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // Act — fire N threads that each create a stream and send messages.
        var tasks = new Task[streamCount];
        for (var i = 0; i < streamCount; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                try
                {
                    var stream = client.CreateStream();
                    using var d = stream;

                    await stream.SendRequestHeadersAsync("/test/Concurrent", "localhost");

                    barrier.SignalAndWait(); // synchronise for maximum contention

                    for (var m = 0; m < messagesPerStream; m++)
                    {
                        await stream.SendMessageAsync(new byte[] { (byte)m });
                    }

                    await stream.SendHalfCloseAsync();
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            });
        }

        Task.WaitAll(tasks);

        // Assert
        Assert.That(errors, Is.Empty,
            () => $"Errors: {string.Join("; ", errors.Select(e => e.ToString()))}");
        Assert.That(client.IsClosed, Is.False);
    }

    [Test]
    public void HighContention_NoDeadlock()
    {
        // Regression test: 64 threads × 10 messages — must complete, no deadlock.
        var (server, client) = CreatePair();
        using var _ = server;
        using var __ = client;

        const int threadCount = 64;
        const int messagesPerThread = 10;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var tasks = Enumerable.Range(0, threadCount).Select(i => Task.Run(async () =>
        {
            var stream = client.CreateStream();
            using var d = stream;

            await stream.SendRequestHeadersAsync($"/test/Thread{i}", "localhost");

            for (var m = 0; m < messagesPerThread; m++)
            {
                cts.Token.ThrowIfCancellationRequested();
                await stream.SendMessageAsync(new byte[] { (byte)i, (byte)m });
            }

            await stream.SendHalfCloseAsync();
        }, cts.Token)).ToArray();

        // Should finish well within the 30s timeout.
        Assert.DoesNotThrow(() => Task.WaitAll(tasks));
        Assert.That(client.IsClosed, Is.False);
    }

    [Test]
    public void ServerSide_ConcurrentResponses_Succeed()
    {
        // Server-side streams also share one TxRing; test that path too.
        var (server, client) = CreatePair();
        using var _ = server;
        using var __ = client;

        const int streamCount = 16;
        var barrier = new Barrier(streamCount);
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var tasks = new Task[streamCount];
        for (var i = 0; i < streamCount; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                try
                {
                    var stream = server.CreateStream();
                    using var d = stream;

                    await stream.SendResponseHeadersAsync();

                    barrier.SignalAndWait();

                    for (var m = 0; m < 10; m++)
                    {
                        await stream.SendMessageAsync(new byte[] { (byte)m });
                    }

                    await stream.SendTrailersAsync(StatusCode.OK);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            });
        }

        Task.WaitAll(tasks);

        Assert.That(errors, Is.Empty,
            () => $"Errors: {string.Join("; ", errors.Select(e => e.ToString()))}");
        Assert.That(server.IsClosed, Is.False);
    }

    [Test]
    public void SendGoAway_DuringConcurrentWrites_NoCorruption()
    {
        // Verify that GoAway interleaves correctly with concurrent
        // stream writes without corrupting the ring buffer.
        var (server, client) = CreatePair();
        using var _ = server;
        using var __ = client;

        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // Keep writing messages on several streams.
        var writing = true;
        var writerTasks = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            try
            {
                var stream = client.CreateStream();
                using var d = stream;
                await stream.SendRequestHeadersAsync($"/test/GoAway{i}", "localhost");

                while (Volatile.Read(ref writing))
                {
                    try
                    {
                        await stream.SendMessageAsync(new byte[] { (byte)i });
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (RingClosedException)
                    {
                        break;
                    }
                    catch (InvalidOperationException)
                    {
                        break; // Connection closing
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        })).ToArray();

        // Let writers run briefly, then send GoAway.
        Thread.Sleep(50);
        client.SendGoAway("test shutdown");
        Volatile.Write(ref writing, false);

        Task.WaitAll(writerTasks);

        // GoAway should not cause crashes or corruption.
        Assert.That(errors, Is.Empty,
            () => $"Errors: {string.Join("; ", errors.Select(e => e.ToString()))}");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task FrameWriter_SingleStream_FrameOrdering_Preserved()
    {
        // Verify that frames from a single stream arrive in order even
        // though they pass through the ShmFrameWriter channel.
        var (server, client) = CreatePair();
        using var _ = server;
        using var __ = client;

        var clientStream = client.CreateStream();
        using var ___ = clientStream;

        await clientStream.SendRequestHeadersAsync("/test/Ordering", "localhost");

        const int messageCount = 100;
        for (var i = 0; i < messageCount; i++)
        {
            await clientStream.SendMessageAsync(new byte[] { (byte)(i & 0xFF) });
        }

        await clientStream.SendHalfCloseAsync();

        // Server side: verify messages arrive in the correct order.
        var ct = TestContext.CurrentContext.CancellationToken;
        var serverStream = await server.AcceptStreamAsync(ct);
        Assert.That(serverStream, Is.Not.Null);
        using var ____ = serverStream!;

        var index = 0;
        await foreach (var msg in serverStream!.ReceiveMessagesAsync(ct))
        {
            Assert.That(msg[0], Is.EqualTo((byte)(index & 0xFF)),
                $"Message {index} arrived out of order");
            index++;
        }

        Assert.That(index, Is.EqualTo(messageCount),
            $"Expected {messageCount} messages, received {index}");
    }

    [Test]
    [CancelAfter(15_000)]
    public void FrameWriter_RapidFireMessages_NoHang()
    {
        // Stress-test: many streams send bursts of messages as fast as
        // possible.  Should complete quickly and never hang.
        var (server, client) = CreatePair();
        using var _ = server;
        using var __ = client;

        const int streamCount = 16;
        const int messagesPerStream = 50;
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, streamCount).Select(i => Task.Run(async () =>
        {
            try
            {
                var s = client.CreateStream();
                using var d = s;

                await s.SendRequestHeadersAsync($"/test/Rapid{i}", "localhost");

                for (var m = 0; m < messagesPerStream; m++)
                {
                    await s.SendMessageAsync(new byte[] { (byte)i, (byte)m });
                }

                await s.SendHalfCloseAsync();
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        })).ToArray();

        Assert.DoesNotThrow(() => Task.WaitAll(tasks));
        Assert.That(errors, Is.Empty,
            () => $"Errors: {string.Join("; ", errors.Select(e => e.ToString()))}");
    }

    [Test]
    [CancelAfter(15_000)]
    public async Task FrameWriter_ServerAndClient_BothWriteSimultaneously()
    {
        // Both sides write through their own ShmFrameWriter.
        // Verify no deadlock or data corruption.
        var (server, client) = CreatePair();
        using var _ = server;
        using var __ = client;

        const int streamCount = 8;
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // Client streams
        var clientTasks = Enumerable.Range(0, streamCount).Select(i => Task.Run(async () =>
        {
            try
            {
                var s = client.CreateStream();
                using var d = s;
                await s.SendRequestHeadersAsync($"/test/Bidi{i}", "localhost");
                for (var m = 0; m < 20; m++)
                    await s.SendMessageAsync(new byte[] { (byte)i, (byte)m });
                await s.SendHalfCloseAsync();
            }
            catch (Exception ex) { errors.Add(ex); }
        })).ToArray();

        // Server streams (writing responses concurrently)
        var serverTasks = Enumerable.Range(0, streamCount).Select(i => Task.Run(async () =>
        {
            try
            {
                var s = server.CreateStream();
                using var d = s;
                await s.SendResponseHeadersAsync();
                for (var m = 0; m < 20; m++)
                    await s.SendMessageAsync(new byte[] { (byte)(i + 100), (byte)m });
                await s.SendTrailersAsync(StatusCode.OK);
            }
            catch (Exception ex) { errors.Add(ex); }
        })).ToArray();

        await Task.WhenAll(clientTasks.Concat(serverTasks));

        Assert.That(errors, Is.Empty,
            () => $"Errors: {string.Join("; ", errors.Select(e => e.ToString()))}");
    }

    [Test]
    [CancelAfter(30_000)]
    public void FrameWriter_HighVolumeSmallRing_NoDeadlock()
    {
        // Regression test for the ring-full deadlock: when the writer thread
        // blocks inside ReserveWrite waiting for ring space, it must not
        // prevent other frames from being enqueued — otherwise WindowUpdate
        // frames from the receive path cannot be written, and the remote
        // side's ring fills up → mutual deadlock.
        // The batched ShmFrameWriter solves this by writing to the ring
        // without holding any lock, using a single-reader Channel instead.
        var (server, client) = CreatePair();
        using var _ = server;
        using var __ = client;

        const int streamCount = 16;
        const int messagesPerStream = 30;
        // 1 KB payload × 16 streams × 30 msgs = 480 KB total, well above
        // the 64 KB ring capacity — guarantees ring-full conditions.
        var payload = new byte[1024];
        Array.Fill(payload, (byte)0xAB);

        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, streamCount).Select(i => Task.Run(async () =>
        {
            try
            {
                var s = client.CreateStream();
                using var d = s;

                await s.SendRequestHeadersAsync($"/test/Deadlock{i}", "localhost");

                for (var m = 0; m < messagesPerStream; m++)
                {
                    await s.SendMessageAsync(payload);
                }

                await s.SendHalfCloseAsync();
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        })).ToArray();

        // Must complete within the timeout.  A deadlock will cause
        // the test to hang and be cancelled by [CancelAfter].
        Assert.DoesNotThrow(() => Task.WaitAll(tasks));
        Assert.That(errors, Is.Empty,
            () => $"Errors: {string.Join("; ", errors.Select(e => e.ToString()))}");
    }

    [Test]
    [CancelAfter(15_000)]
    public void Dispose_WhenRingFull_DoesNotHang()
    {
        // Regression test: Dispose must complete even when the ring is full
        // and the remote side is not reading.  A blocking GoAway write with
        // CancellationToken.None would hang here; the 1-second timeout in
        // the GoAway write path prevents that.
        var name = $"grpc_cw_test_{Guid.NewGuid():N}";
        var server = ShmConnection.CreateAsServer(name, ringCapacity: RingCapacity, maxStreams: 500);
        var client = ShmConnection.ConnectAsClient(name);

        // Fill the client→server ring by writing without the server reading.
        // Use a small payload and loop until the enqueue starts failing or
        // we've written enough to fill the 64 KB ring.
        var stream = client.CreateStream();
        using var sd = stream;
        try
        {
            stream.SendRequestHeadersAsync("/test/FillRing", "localhost").Wait();
            var payload = new byte[4096];
            for (var i = 0; i < 100; i++)
            {
                try
                {
                    stream.SendMessageAsync(payload).Wait(TimeSpan.FromMilliseconds(500));
                }
                catch
                {
                    break; // ring full or flow control blocked — expected
                }
            }
        }
        catch { /* ignore — we just want the ring full */ }

        // Now dispose the server WITHOUT having it read anything.
        // The server's TxRing (server→client) should still have space since
        // we only wrote to client→server.  But we also dispose the client
        // whose TxRing may be full.
        // Both Dispose calls must complete within the [CancelAfter] timeout.
        Assert.DoesNotThrow(() =>
        {
            server.Dispose();
            client.Dispose();
        });
    }
}
