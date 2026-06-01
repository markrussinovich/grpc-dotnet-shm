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
/// Diagnostic probe (NOT a real assertion test). Runs unary RPCs at
/// several payload sizes and prints the per-RPC SignalData wake count
/// to confirm whether the wake-coalesce path holds across multi-frame
/// payloads (Fair mode 16K = 2 DATA frames, currently suspected to
/// break coalescing → 0.75x SHM/UDS Windows regression).
///
/// Run explicitly:
///   dotnet test --no-build -c Release ^
///     --filter "FullyQualifiedName~WakeCountProbeTests"
///
/// Output goes to TestContext.Out; capture with:
///   dotnet test ... --logger "console;verbosity=detailed"
/// </summary>
[TestFixture]
[Explicit("Diagnostic probe; runs only on explicit filter")]
public class WakeCountProbeTests
{
    private static long GetSignalDataCount()
    {
        var asm = typeof(Segment).Assembly;
        var ringType = asm.GetType("Grpc.Net.SharedMemory.ShmRing")
            ?? throw new InvalidOperationException("ShmRing type not found");
        var method = ringType.GetMethod("GetSignalDataCountForTest",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("GetSignalDataCountForTest not found");
        return (long)method.Invoke(null, null)!;
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
    /// Probe wakes/RPC for sizes that bracket the Fair mode max-frame
    /// boundary (16384 bytes). Expected wake counts pre-multi-frame-
    /// coalesce extension:
    ///   - 1K, 4K (single DATA frame): ~2 wakes/RPC (client coalesce + server coalesce)
    ///   - 16K (2 DATA frames in Fair):  ~4-6 wakes/RPC if coalesce breaks
    ///   - 64K (4 DATA frames in Fair):  ~6-10 wakes/RPC if coalesce breaks
    /// If 16K shows 2 wakes/RPC → coalescing holds → Windows regression is elsewhere.
    ///
    /// MUST be run with Fair env pre-set at process start (statics):
    ///   $env:SHM_FAIR_MAX_FRAME="16384"; $env:SHM_INITIAL_WINDOW="65535"
    ///   dotnet test --no-build -c Release ^
    ///     test/Grpc.Net.SharedMemory.Tests/Grpc.Net.SharedMemory.Tests.csproj ^
    ///     --filter "WakeCountProbe" --logger "console;verbosity=detailed"
    /// </summary>
    [Test]
    [CancelAfter(60000)]
    public async Task ProbeWakesPerRpc_AcrossSizes()
    {
        // Fail-fast: env vars MUST be set at process start because
        // ShmConstants.FairMaxFramePayload is a static readonly.
        var maxFrame = Environment.GetEnvironmentVariable("SHM_FAIR_MAX_FRAME");
        var initWin = Environment.GetEnvironmentVariable("SHM_INITIAL_WINDOW");
        TestContext.Out.WriteLine($"SHM_FAIR_MAX_FRAME = {maxFrame ?? "<unset, default>"}");
        TestContext.Out.WriteLine($"SHM_INITIAL_WINDOW = {initWin ?? "<unset, default>"}");
        if (maxFrame != "16384" || initWin != "65535")
        {
            TestContext.Out.WriteLine();
            TestContext.Out.WriteLine("WARNING: env vars not set to Fair-mode values. Probe will run");
            TestContext.Out.WriteLine("in DEFAULT (Jumbo) mode, so 16K probably stays single-frame and");
            TestContext.Out.WriteLine("we won't see the multi-frame coalesce break.");
            TestContext.Out.WriteLine("Re-run with:");
            TestContext.Out.WriteLine("  $env:SHM_FAIR_MAX_FRAME='16384'; $env:SHM_INITIAL_WINDOW='65535'");
        }

        int[] sizes = { 1024, 4096, 16384, 32768, 65536, 131072 };
        const int iterations = 50;

        var segmentName = $"wakeprobe_{Guid.NewGuid():N}";

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

            // Warm-up: 5 calls at 1K to amortize control-plane setup signals.
            var warmupBody = new byte[1024];
            new Random(7).NextBytes(warmupBody);
            var warmupReq = new BytesValue { Value = ByteString.CopyFrom(warmupBody) };
            for (var i = 0; i < 5; i++)
            {
                _ = await invoker.AsyncUnaryCall(method, host: null, new CallOptions(), warmupReq);
            }

            TestContext.Out.WriteLine();
            TestContext.Out.WriteLine("=== Wake/RPC probe (Fair mode, max-frame=16384) ===");
            TestContext.Out.WriteLine($"{"Size",-10} {"Wakes/RPC",10} {"Wakes total",12} {"Iters",6}");
            TestContext.Out.WriteLine(new string('-', 44));

            foreach (var size in sizes)
            {
                var body = new byte[size];
                new Random(size).NextBytes(body);
                var req = new BytesValue { Value = ByteString.CopyFrom(body) };

                // Burn one call at this size to flush any residual state.
                _ = await invoker.AsyncUnaryCall(method, host: null, new CallOptions(), req);

                var before = GetSignalDataCount();
                for (var i = 0; i < iterations; i++)
                {
                    var resp = await invoker.AsyncUnaryCall(method, host: null, new CallOptions(), req);
                    if (resp.Value.Length != size)
                    {
                        Assert.Fail($"Size {size}: expected {size} bytes, got {resp.Value.Length}");
                    }
                }
                var after = GetSignalDataCount();
                var delta = after - before;
                var perRpc = delta / (double)iterations;

                TestContext.Out.WriteLine($"{size,-10} {perRpc,10:F2} {delta,12} {iterations,6}");
            }

            TestContext.Out.WriteLine();
            TestContext.Out.WriteLine("Interpretation:");
            TestContext.Out.WriteLine("  ~2.0 wakes/RPC = client + server coalesce both firing (good)");
            TestContext.Out.WriteLine("  ~4-6 wakes/RPC = coalesce broken on this size (suspected for 16K Fair)");
        }
        finally
        {
            serverCts.Cancel();
            try { await serverTask; } catch { /* ignore */ }
        }

        // Probe always "passes"; the real result is the printed output.
        Assert.Pass("Probe complete; see test output for wakes/RPC table.");
    }

    /// <summary>
    /// Round-11 streaming probe. Counts wakes per WriteAsync in a
    /// server-streaming RPC where the server emits N messages of a
    /// given size back-to-back. Before round-11, each multi-chunk
    /// message (Fair 16K = 2 chunks, 32K = 3, etc) fires per-chunk
    /// wakes inside <c>ShmServerStreamWriter.WriteAsync</c> because
    /// no <c>BeginInlineBatch</c> wraps the call. After round-11
    /// each WriteAsync should produce ~1 wake regardless of chunk
    /// count (subject to SendQuota and 128 KiB cap).
    ///
    /// Run with Fair env vars exactly as the unary probe above.
    /// </summary>
    [Test]
    [CancelAfter(60000)]
    public async Task ProbeStreamingWakesPerWriteAsync()
    {
        var maxFrame = Environment.GetEnvironmentVariable("SHM_FAIR_MAX_FRAME");
        var initWin = Environment.GetEnvironmentVariable("SHM_INITIAL_WINDOW");
        TestContext.Out.WriteLine($"SHM_FAIR_MAX_FRAME = {maxFrame ?? "<unset, default>"}");
        TestContext.Out.WriteLine($"SHM_INITIAL_WINDOW = {initWin ?? "<unset, default>"}");

        int[] sizes = { 1024, 4096, 16384, 32768, 65536 };
        const int messagesPerRpc = 20;

        var segmentName = $"wakeprobe_stream_{Guid.NewGuid():N}";

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

            // Warm up.
            var warmupBody = new byte[1024];
            var warmupReq = new BytesValue { Value = ByteString.CopyFrom(warmupBody) };
            using (var warmCall = invoker.AsyncServerStreamingCall(method, host: null, new CallOptions(), warmupReq))
            {
                while (await warmCall.ResponseStream.MoveNext(CancellationToken.None)) { }
            }

            TestContext.Out.WriteLine();
            TestContext.Out.WriteLine($"=== Streaming wake probe ({messagesPerRpc} msgs/RPC) ===");
            TestContext.Out.WriteLine($"{"Size",-10} {"Wakes/msg",10} {"Wakes total",12} {"Iters",6}");
            TestContext.Out.WriteLine(new string('-', 44));

            foreach (var size in sizes)
            {
                var body = new byte[size];
                new Random(size).NextBytes(body);
                var req = new BytesValue { Value = ByteString.CopyFrom(body) };

                // Drain once at this size to flush residual state.
                using (var drainCall = invoker.AsyncServerStreamingCall(method, host: null, new CallOptions(), req))
                {
                    while (await drainCall.ResponseStream.MoveNext(CancellationToken.None)) { }
                }

                var before = GetSignalDataCount();
                const int rpcCount = 5;
                for (var r = 0; r < rpcCount; r++)
                {
                    using var call = invoker.AsyncServerStreamingCall(method, host: null, new CallOptions(), req);
                    var received = 0;
                    while (await call.ResponseStream.MoveNext(CancellationToken.None))
                    {
                        received++;
                        if (call.ResponseStream.Current.Value.Length != size)
                            Assert.Fail($"Size {size}: expected {size}, got {call.ResponseStream.Current.Value.Length}");
                    }
                    Assert.That(received, Is.EqualTo(messagesPerRpc));
                }
                var after = GetSignalDataCount();
                var totalMsgs = rpcCount * messagesPerRpc;
                var perMsg = (after - before) / (double)totalMsgs;

                TestContext.Out.WriteLine($"{size,-10} {perMsg,10:F2} {after - before,12} {totalMsgs,6}");
            }

            TestContext.Out.WriteLine();
            TestContext.Out.WriteLine("Interpretation (after round-11 multi-frame streaming coalesce):");
            TestContext.Out.WriteLine("  ~1.0 wakes/msg = optimal (each WriteAsync = 1 wake)");
            TestContext.Out.WriteLine("  >2 wakes/msg   = multi-chunk wakes not collapsed (regression)");
            TestContext.Out.WriteLine("Fair 64K: SendQuota=65535 < 65541, expect no coalesce (~5 wakes/msg).");
        }
        finally
        {
            serverCts.Cancel();
            try { await serverTask; } catch { /* ignore */ }
        }

        Assert.Pass("Streaming probe complete; see test output for wakes/msg table.");
    }

    /// <summary>
    /// Profiling target: a tight 16 KiB Fair Unary loop with a single
    /// SHM client+server pair in-process. Designed to be the only
    /// non-trivial workload running while dotnet-trace samples — so
    /// the resulting flamegraph reflects per-RPC hot paths cleanly.
    /// Run via:
    /// <code>
    ///   $env:SHM_FAIR_MAX_FRAME='16384'; $env:SHM_INITIAL_WINDOW='65535'
    ///   dotnet-trace collect --providers Microsoft-DotNETCore-SampleProfiler `
    ///     -o trace_16k.nettrace -- `
    ///     dotnet test --no-build -c Release `
    ///       test/Grpc.Net.SharedMemory.Tests/Grpc.Net.SharedMemory.Tests.csproj `
    ///       --filter "ProfileTarget_Fair16KUnary"
    /// </code>
    /// Then convert to Speedscope and inspect with the web viewer:
    /// <code>
    ///   dotnet-trace convert --format Speedscope trace_16k.nettrace
    /// </code>
    /// </summary>
    [Test]
    [Explicit("Profiling target; run only when collecting a trace.")]
    [CancelAfter(180000)]
    public async Task ProfileTarget_Fair16KUnary()
    {
        // Multi-size profiling: subtract size-invariant costs (frame overhead,
        // wake count, channel hop, marshaller dispatch) by comparing across
        // sizes. Per-size cost = (latency[N] - latency[0]) / N which gives
        // bytes/RPC throughput-equivalent overhead.
        int[] sizes = { 1, 1024, 4096, 16384, 32768, 65536 };
        const int iterations = 30000;  // ~6s at ~200us/RPC

        var segmentName = $"prof16k_{Guid.NewGuid():N}";

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

            // Warm-up at largest size to amortize JIT.
            var warmupBody = new byte[sizes[^1]];
            var warmupReq = new BytesValue { Value = ByteString.CopyFrom(warmupBody) };
            for (var i = 0; i < 500; i++)
            {
                _ = await invoker.AsyncUnaryCall(method, host: null, new CallOptions(), warmupReq);
            }

            TestContext.Out.WriteLine($"=== ProfileTarget_Fair Unary Multi-Size ===");
            TestContext.Out.WriteLine($"{"size",6} {"mean_us",10} {"sigs/RPC",10} {"hops/RPC",10} {"hop_us",10}");

            foreach (var size in sizes)
            {
                var body = new byte[size];
                if (size > 0) new Random(size).NextBytes(body);
                var req = new BytesValue { Value = ByteString.CopyFrom(body) };

                // Reset counters via burning a small batch + capture baseline.
                _ = await invoker.AsyncUnaryCall(method, host: null, new CallOptions(), req);
                var sigBefore = GetSignalDataCount();
                long hopTicksBefore = 0, hopCountBefore = 0;
                try
                {
                    var asm = typeof(Segment).Assembly;
                    var streamType = asm.GetType("Grpc.Net.SharedMemory.ShmGrpcStream");
                    var hopMethod = streamType?.GetMethod("GetHopDiag",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    if (hopMethod != null)
                    {
                        var hop = hopMethod.Invoke(null, null);
                        var t = hop!.GetType();
                        hopTicksBefore = (long)t.GetField("Item1")!.GetValue(hop)!;
                        hopCountBefore = (long)t.GetField("Item2")!.GetValue(hop)!;
                    }
                } catch { }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (var i = 0; i < iterations; i++)
                {
                    var resp = await invoker.AsyncUnaryCall(method, host: null, new CallOptions(), req);
                    if (resp.Value.Length != size) Assert.Fail($"size {size}: mismatch {resp.Value.Length}");
                }
                sw.Stop();
                var perRpc = sw.Elapsed.TotalMicroseconds / iterations;
                var sigAfter = GetSignalDataCount();
                var sigsPerRpc = (sigAfter - sigBefore) / (double)iterations;

                double hopsPerRpc = 0, hopUs = 0;
                try
                {
                    var asm = typeof(Segment).Assembly;
                    var streamType = asm.GetType("Grpc.Net.SharedMemory.ShmGrpcStream");
                    var hopMethod = streamType?.GetMethod("GetHopDiag",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    if (hopMethod != null)
                    {
                        var hop = hopMethod.Invoke(null, null);
                        var t = hop!.GetType();
                        var ticks = (long)t.GetField("Item1")!.GetValue(hop)! - hopTicksBefore;
                        var count = (long)t.GetField("Item2")!.GetValue(hop)! - hopCountBefore;
                        hopsPerRpc = count / (double)iterations;
                        if (count > 0)
                            hopUs = ticks / (double)System.Diagnostics.Stopwatch.Frequency * 1e6 / count;
                    }
                } catch { }

                TestContext.Out.WriteLine($"{size,6} {perRpc,10:F2} {sigsPerRpc,10:F2} {hopsPerRpc,10:F2} {hopUs,10:F2}");
            }
        }
        finally
        {
            serverCts.Cancel();
            try { await serverTask; } catch { /* ignore */ }
        }
        Assert.Pass();
    }
}
