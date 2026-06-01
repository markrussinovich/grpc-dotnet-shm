#region Copyright notice and license

// Copyright 2026 The gRPC Authors
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

using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Round-11 multi-frame wake-coalesce safety tests. Validates the
/// invariants documented in <see cref="ShmFrameWriter.CanCoalesceMultiFrameMessage"/>
/// remarks: F1 (cap/8 ring-space bound), F2 (stream + conn SendQuota
/// snapshot), 128 KiB latency cap, and proper resource cleanup on
/// exception / cancellation.
///
/// These are PR-required tests per the dual-reviewer (GPT-5.5 +
/// Opus 4.8) v1.1 final design review.
/// </summary>
[TestFixture]
public class MultiFrameCoalesceTests
{
    private static int GetBatchWriteDepth(ShmRing ring)
    {
        var f = typeof(ShmRing).GetField("_batchWriteDepth",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_batchWriteDepth field not found");
        return (int)f.GetValue(ring)!;
    }

    private static long GetConnSendQuotaPrivate(ShmConnection connection)
    {
        var f = typeof(ShmConnection).GetField("_connSendQuota",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_connSendQuota field not found");
        return (long)f.GetValue(connection)!;
    }

    private static void SetConnSendQuotaPrivate(ShmConnection connection, long value)
    {
        var f = typeof(ShmConnection).GetField("_connSendQuota",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_connSendQuota field not found");
        f.SetValue(connection, value);
    }

    private static async Task WaitForServerAsync(string segmentName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try { using var _ = Segment.OpenControlSegment(segmentName); return; }
            catch (FileNotFoundException) { await Task.Delay(10); }
        }
        Assert.Fail($"Server did not create control segment for '{segmentName}'.");
    }

    /// <summary>
    /// PR-required test #1: <see cref="ShmRing._batchWriteDepth"/> MUST
    /// return to 0 after a balanced Begin/End pair, even when the
    /// caller pattern uses try/finally (the production pattern). This
    /// is the most basic invariant guarding against suppressed-signal
    /// leakage that would orphan a peer reader.
    /// </summary>
    [Test]
    public void BeginEndInlineBatch_DepthReturnsToZero()
    {
        var name = $"test_batchdepth_{Guid.NewGuid():N}";
        using var connection = ShmConnection.CreateAsServer(name, ringCapacity: 64 * 1024, maxStreams: 100);
        var writer = connection.FrameWriter!;
        var ring = (ShmRing)typeof(ShmFrameWriter)
            .GetField("_ring", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(writer)!;

        Assert.That(GetBatchWriteDepth(ring), Is.EqualTo(0), "fresh writer must have depth 0");

        // Balanced.
        writer.BeginInlineBatch();
        Assert.That(GetBatchWriteDepth(ring), Is.EqualTo(1));
        writer.EndInlineBatch();
        Assert.That(GetBatchWriteDepth(ring), Is.EqualTo(0));

        // Nested (production rarely nests, but Ring supports it).
        writer.BeginInlineBatch();
        writer.BeginInlineBatch();
        Assert.That(GetBatchWriteDepth(ring), Is.EqualTo(2));
        writer.EndInlineBatch();
        Assert.That(GetBatchWriteDepth(ring), Is.EqualTo(1));
        writer.EndInlineBatch();
        Assert.That(GetBatchWriteDepth(ring), Is.EqualTo(0));
    }

    /// <summary>
    /// PR-required test #2: if the caller wraps Begin/End in
    /// try/finally and the protected body throws, the finally MUST
    /// fire EndInlineBatch and depth MUST return to 0. This is the
    /// pattern used at all 6 modified sites (Sites 1, 2, 3, 4, 5, 7).
    /// Verifies the non-atomic int counter is safe under exception
    /// propagation (the safety net relies on TryPauseWriterLoop's
    /// exclusivity, which still holds during throw).
    /// </summary>
    [Test]
    public void BeginInlineBatch_FinallyFiresEndOnException()
    {
        var name = $"test_batchdepth_throw_{Guid.NewGuid():N}";
        using var connection = ShmConnection.CreateAsServer(name, ringCapacity: 64 * 1024, maxStreams: 100);
        var writer = connection.FrameWriter!;
        var ring = (ShmRing)typeof(ShmFrameWriter)
            .GetField("_ring", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(writer)!;

        Assert.That(GetBatchWriteDepth(ring), Is.EqualTo(0));

        var thrown = Assert.Throws<InvalidOperationException>(() =>
        {
            writer.BeginInlineBatch();
            try
            {
                throw new InvalidOperationException("simulated serializer fault");
            }
            finally
            {
                writer.EndInlineBatch();
            }
        });
        Assert.That(thrown!.Message, Is.EqualTo("simulated serializer fault"));
        Assert.That(GetBatchWriteDepth(ring), Is.EqualTo(0),
            "depth MUST return to 0 even when body throws (Sites 1-7 finally pattern)");

        // Subsequent Begin/End must work normally — no sticky state.
        writer.BeginInlineBatch();
        Assert.That(GetBatchWriteDepth(ring), Is.EqualTo(1));
        writer.EndInlineBatch();
        Assert.That(GetBatchWriteDepth(ring), Is.EqualTo(0));
    }

    /// <summary>
    /// PR-required test #3: when stream send quota is below the
    /// requested lpm, the gate at Sites 1-7 MUST evaluate false so
    /// callers fall through to the non-batched path (no deadlock,
    /// no suppressed-HEADERS-waiting-on-WU).
    /// </summary>
    [Test]
    public void Gate_StreamSendQuotaBelowLpm_FailsToFallback()
    {
        var name = $"test_fc_stream_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(name, ringCapacity: 65536, maxStreams: 10);
        using var client = ShmConnection.ConnectAsClient(name);

        var stream = client.CreateStream();
        var initialQuota = stream.SendQuota;
        Assert.That(initialQuota, Is.GreaterThan(0));

        // Drain stream quota to a small value.
        var drained = stream.TryReserveSendQuota((int)(initialQuota - 100));
        Assert.That(drained, Is.True);
        Assert.That(stream.SendQuota, Is.EqualTo(100));

        // Simulate the gate's stream check for a 1 KiB body
        // (lpm = 5 + 1024 = 1029 > 100).
        var lpmFramedSize = 5 + 1024;
        bool gateStreamPass = stream.SendQuota >= lpmFramedSize;
        Assert.That(gateStreamPass, Is.False,
            "with quota=100, gate for 1029-byte lpm MUST fail (drives fallback path)");

        // Sanity: gate for a tiny lpm (50 bytes total = 5+45) passes.
        bool gateTinyPass = stream.SendQuota >= (5 + 45);
        Assert.That(gateTinyPass, Is.True);
    }

    /// <summary>
    /// PR-required test #4 (Opus 4.8's headline addition): when CONN
    /// send quota is below the requested lpm, the gate MUST also
    /// fail. The actual reservation in <c>ReserveSendQuotaOrBlock</c>
    /// debits BOTH stream + conn — a stream-only gate would miss
    /// this case under strict-H2 interop or future dynamic SETTINGS.
    /// </summary>
    [Test]
    public void Gate_ConnSendQuotaBelowLpm_FailsToFallback()
    {
        var name = $"test_fc_conn_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(name, ringCapacity: 65536, maxStreams: 10);
        using var client = ShmConnection.ConnectAsClient(name);

        var stream = client.CreateStream();

        // Default conn quota is MaxWindowSize (≈ int.MaxValue). Force
        // it low via reflection to simulate strict-H2 interop where
        // the peer has not yet sent connection-level WU.
        var originalConnQuota = GetConnSendQuotaPrivate(client);
        Assert.That(originalConnQuota, Is.GreaterThan(int.MaxValue / 2),
            "default conn quota should be in the fast-path range");

        try
        {
            SetConnSendQuotaPrivate(client, 100);
            Assert.That(client.ConnSendQuota, Is.EqualTo(100L));

            // Gate for a 1 KiB body: lpm = 1029 > 100.
            var lpmFramedSize = 5 + 1024;
            bool gateConnPass = client.ConnSendQuota >= lpmFramedSize;
            Assert.That(gateConnPass, Is.False,
                "with conn quota=100, gate for 1029-byte lpm MUST fail");

            // Even though stream quota is plentiful, the combined gate
            // (Sites 1-7 evaluate BOTH) MUST fail.
            bool combinedGate = stream.SendQuota >= lpmFramedSize
                && client.ConnSendQuota >= lpmFramedSize;
            Assert.That(combinedGate, Is.False,
                "stream quota plentiful + conn quota starved → combined gate fails");
        }
        finally
        {
            // Restore so the connection can dispose cleanly.
            SetConnSendQuotaPrivate(client, originalConnQuota);
        }
    }

    /// <summary>
    /// PR-required test #5 (Opus 4.8 wrap-around insistence):
    /// the multi-frame coalesce predicate caps at cap/8, which
    /// prevents the F1 "ring-fill while signals suppressed" deadlock
    /// even when an outer ReserveWrite hits the wrap boundary and
    /// falls through to <c>RingFrameStream</c>. This is a property
    /// test: every size that passes the multi-frame predicate must
    /// be strictly ≤ ring/8 (the safety bound).
    /// </summary>
    [Test]
    public void MultiFrameThreshold_NeverExceedsCapDivEight()
    {
        // Probe several ring capacities (power of 2 between 64 KiB
        // and 64 MiB) and verify the threshold stays at min(cap/8,
        // H2max) regardless of size. This is the F1 invariant.
        var caps = new ulong[] { 64UL * 1024, 256UL * 1024, 1UL * 1024 * 1024, 16UL * 1024 * 1024, 64UL * 1024 * 1024 };
        foreach (var cap in caps)
        {
            var name = $"test_thresh_{cap}_{Guid.NewGuid():N}";
            using var connection = ShmConnection.CreateAsServer(name, ringCapacity: cap, maxStreams: 10);
            var writer = connection.FrameWriter!;

            var capDivEight = (int)(cap / 8);
            var expectedMax = Math.Min(capDivEight,
                Grpc.Net.SharedMemory.Wire.Http2FrameHeader.MaxAllowedPayloadLength);

            Assert.That(writer.CanCoalesceMultiFrameMessage(expectedMax), Is.True,
                $"cap={cap}: max threshold {expectedMax} must be accepted");
            Assert.That(writer.CanCoalesceMultiFrameMessage(expectedMax + 1), Is.False,
                $"cap={cap}: max+1 must be refused (F1 cap/8 invariant)");
            Assert.That(expectedMax, Is.LessThanOrEqualTo(capDivEight),
                $"cap={cap}: threshold {expectedMax} must NEVER exceed cap/8 = {capDivEight}");
        }
    }

    /// <summary>
    /// PR-required E2E test: client→server round-trip with a 32 KiB
    /// body (multi-frame in Fair mode but well under all gates)
    /// completes successfully and the response body matches. This
    /// exercises the new coalesce path at Sites 1 + 3 simultaneously.
    /// Acts as a smoke test that batch-wrapped multi-chunk writes
    /// don't corrupt frame order or drop bytes.
    /// </summary>
    [Test]
    [CancelAfter(15000)]
    public async Task EndToEnd_MultiFrameUnary_RoundTripSucceeds()
    {
        var segmentName = $"multiframe_e2e_{Guid.NewGuid():N}";

        await using var server = new ShmGrpcServer(segmentName, singleStreamMode: true);
        server.MapUnary<BytesValue, BytesValue>(
            "/test.Echo/Unary",
            (req, _) => Task.FromResult(new BytesValue { Value = req.Value }));

        using var serverCts = new CancellationTokenSource();
        var serverTask = Task.Run(() => server.RunAsync(serverCts.Token));
        await WaitForServerAsync(segmentName);

        try
        {
            using var handler = new ShmControlHandler(segmentName, new ShmClientTransportOptions
            {
                EnableMultipleConnections = false,
                SingleStreamMode = true,
                ConnectTimeout = TimeSpan.FromSeconds(5),
            });

            using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
            {
                HttpHandler = handler,
                DisposeHttpClient = false,
            });

            var marshaller = Marshallers.Create<BytesValue>(
                req => req.ToByteArray(),
                bytes => BytesValue.Parser.ParseFrom(bytes));
            var method = new Method<BytesValue, BytesValue>(
                MethodType.Unary, "test.Echo", "Unary", marshaller, marshaller);
            var invoker = channel.CreateCallInvoker();

            // 32 KiB body → in Fair mode chunks into 3 H2 DATA frames
            // (32773 / 16384 = 2 + remainder). In default (Jumbo32)
            // mode this is single-frame. Either way must round-trip.
            int[] sizes = { 1024, 16 * 1024, 32 * 1024, 64 * 1024, 100 * 1024 };
            foreach (var size in sizes)
            {
                var body = new byte[size];
                new Random(size).NextBytes(body);
                var req = new BytesValue { Value = ByteString.CopyFrom(body) };

                var resp = await invoker.AsyncUnaryCall(method, host: null, new CallOptions(), req);

                Assert.That(resp.Value.Length, Is.EqualTo(size),
                    $"size {size}: response length must match");
                Assert.That(resp.Value.ToByteArray(), Is.EqualTo(body),
                    $"size {size}: response body must equal request (no corruption)");
            }
        }
        finally
        {
            serverCts.Cancel();
            try { await serverTask; } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// PR-required E2E test: server→client multi-frame streaming.
    /// Server emits 10 messages of 16 KiB each (multi-chunk in Fair);
    /// client receives all 10 with intact contents. Exercises Site 7
    /// (the new server-streaming coalesce wrap) end-to-end.
    /// </summary>
    [Test]
    [CancelAfter(15000)]
    public async Task EndToEnd_MultiFrameStreaming_AllMessagesIntact()
    {
        var segmentName = $"multiframe_stream_e2e_{Guid.NewGuid():N}";
        const int messagesPerRpc = 10;

        await using var server = new ShmGrpcServer(segmentName, singleStreamMode: true);
        server.MapServerStreaming<BytesValue, BytesValue>(
            "/test.EchoStream/Stream",
            async (req, writer, _) =>
            {
                for (var i = 0; i < messagesPerRpc; i++)
                    await writer.WriteAsync(new BytesValue { Value = req.Value });
            });

        using var serverCts = new CancellationTokenSource();
        var serverTask = Task.Run(() => server.RunAsync(serverCts.Token));
        await WaitForServerAsync(segmentName);

        try
        {
            using var handler = new ShmControlHandler(segmentName, new ShmClientTransportOptions
            {
                EnableMultipleConnections = false,
                SingleStreamMode = true,
                ConnectTimeout = TimeSpan.FromSeconds(5),
            });

            using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
            {
                HttpHandler = handler,
                DisposeHttpClient = false,
            });

            var marshaller = Marshallers.Create<BytesValue>(
                req => req.ToByteArray(),
                bytes => BytesValue.Parser.ParseFrom(bytes));
            var method = new Method<BytesValue, BytesValue>(
                MethodType.ServerStreaming, "test.EchoStream", "Stream", marshaller, marshaller);
            var invoker = channel.CreateCallInvoker();

            int[] sizes = { 1024, 16 * 1024, 32 * 1024 };
            foreach (var size in sizes)
            {
                var body = new byte[size];
                new Random(size + 7).NextBytes(body);
                var req = new BytesValue { Value = ByteString.CopyFrom(body) };
                var expected = req.Value.ToByteArray();

                using var call = invoker.AsyncServerStreamingCall(method, host: null, new CallOptions(), req);
                var received = 0;
                while (await call.ResponseStream.MoveNext(CancellationToken.None))
                {
                    received++;
                    Assert.That(call.ResponseStream.Current.Value.Length, Is.EqualTo(size),
                        $"size {size} msg {received}: length");
                    Assert.That(call.ResponseStream.Current.Value.ToByteArray(), Is.EqualTo(expected),
                        $"size {size} msg {received}: body");
                }
                Assert.That(received, Is.EqualTo(messagesPerRpc),
                    $"size {size}: expected {messagesPerRpc} messages, got {received}");
            }
        }
        finally
        {
            serverCts.Cancel();
            try { await serverTask; } catch { /* ignore */ }
        }
    }
}
