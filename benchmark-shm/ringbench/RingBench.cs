// gRPC-level benchmark: SHM vs TCP transport, matching grpc-go-shmem/benchmark/shmemtcp/main.go.
//
// Runs actual gRPC UnaryCall and StreamingCall (bidi ping-pong) through the full
// gRPC stack — protobuf serialization, framing, transport — exactly as an
// application would use gRPC.
//
// Go equivalents:
//   measureUnary()     → MeasureUnary()      — client.UnaryCall() in a timed loop
//   measureStreaming()  → MeasureStreaming()   — client.StreamingCall() send+recv ping-pong
//   startBenchEnv(tcp) → StartTcpEnv()        — Kestrel HTTP/2 h2c server
//   startBenchEnv(shm) → StartShmEnv()        — ShmGrpcServer

using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;
using Grpc.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ============================================================================
// Main
// ============================================================================

// Catch unhandled exceptions from background threads
AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
{
    Console.Error.WriteLine($"[DIAG] UNHANDLED EXCEPTION (IsTerminating={e.IsTerminating}): {e.ExceptionObject}");
    Console.Error.Flush();
};

TaskScheduler.UnobservedTaskException += (sender, e) =>
{
    Console.Error.WriteLine($"[DIAG] UNOBSERVED TASK EXCEPTION: {e.Exception}");
    Console.Error.Flush();
    e.SetObserved();
};

// Ensure enough thread pool threads for in-process client+server operation
ThreadPool.SetMinThreads(200, 200);

string outDir = Path.Combine("benchmark-shm", "out");
string? platformOverride = null;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--output" || args[i] == "--out")
        outDir = args[++i];
    if (args[i] == "--platform")
        platformOverride = args[++i];
}

string platform = platformOverride
    ?? (OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other");
outDir = Path.Combine(outDir, platform);
Directory.CreateDirectory(outDir);

// Go benchmark sizes + extended: 0, 1, 1K, 4K, 16K, 64K, 256K, 512K, 1M, 2M, 4M, 16M, 32M, 128M
int[] sizes = { 0, 1, 1024, 4096, 16384, 65536, 262144, 524288,
                1048576, 2097152, 4194304, 16777216, 33554432, 134217728 };

string cpu = GetCpuInfo();
string runtime = RuntimeInformation.FrameworkDescription;
Console.WriteLine($"CPU: {cpu}");
Console.WriteLine($"Runtime: {runtime}");
Console.WriteLine();

var unaryResults = new List<BenchResult>();
var streamingResults = new List<BenchResult>();

// Run each transport independently to avoid idle-spin stack buildup in SHM frame reader
#pragma warning disable CS8321
foreach (var startEnv in new Func<Task<BenchEnv>>[] { StartTcpEnv, StartShmEnv })
{
    BenchEnv env;
    try
    {
        env = await startEnv();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[DIAG] Transport setup FAILED: {ex.GetType().Name}: {ex.Message}");
        Console.Error.WriteLine(ex.ToString());
        Console.Error.Flush();
        continue;
    }
    await using var envDisposable = env;

    Console.WriteLine($"=== {env.Transport.ToUpper()} Transport ===");
    Console.WriteLine();

    // Interleave unary + streaming per payload size — matches Go benchmark loop order
    Console.WriteLine($"  {"Type",-12} {"Payload",-12} {"Iters",-8} {"Avg µs",-14} {"Throughput MB/s",-18} {"Gen0",-6} {"Gen1",-6} {"Gen2",-6}");
    Console.WriteLine("  " + new string('-', 88));

    foreach (var size in sizes)
    {
        int iters = IterationsForSize(size);

        // Unary
        int gc0Before = GC.CollectionCount(0), gc1Before = GC.CollectionCount(1), gc2Before = GC.CollectionCount(2);
        var (uAvgUs, uThroughput) = await MeasureUnary(env.Client, size, iters);
        int gc0 = GC.CollectionCount(0) - gc0Before, gc1 = GC.CollectionCount(1) - gc1Before, gc2 = GC.CollectionCount(2) - gc2Before;
        unaryResults.Add(new BenchResult(env.Transport, size, iters, uAvgUs, uThroughput));
        Console.WriteLine($"  {"unary",-12} {FormatSize(size),-12} {iters,-8} {uAvgUs,-14:F3} {uThroughput,-18:F3} {gc0,-6} {gc1,-6} {gc2,-6}");

        // Streaming
        gc0Before = GC.CollectionCount(0); gc1Before = GC.CollectionCount(1); gc2Before = GC.CollectionCount(2);
        var (sAvgUs, sThroughput) = await MeasureStreaming(env.Client, size, iters);
        gc0 = GC.CollectionCount(0) - gc0Before; gc1 = GC.CollectionCount(1) - gc1Before; gc2 = GC.CollectionCount(2) - gc2Before;
        streamingResults.Add(new BenchResult(env.Transport, size, iters, sAvgUs, sThroughput));
        Console.WriteLine($"  {"streaming",-12} {FormatSize(size),-12} {iters,-8} {sAvgUs,-14:F3} {sThroughput,-18:F3} {gc0,-6} {gc1,-6} {gc2,-6}");
    }
    Console.WriteLine();
}

// Write results
var results = new
{
    timestamp = DateTime.UtcNow.ToString("o"),
    cpu,
    runtime,
    sizes_bytes = sizes,
    unary = unaryResults.Select(r => new
    {
        transport = r.Transport,
        size_bytes = r.SizeBytes,
        iterations = r.Iterations,
        avg_latency_us = Math.Round(r.AvgLatencyUs, 3),
        throughput_mb_per_s = Math.Round(r.ThroughputMBps, 3)
    }),
    streaming = streamingResults.Select(r => new
    {
        transport = r.Transport,
        size_bytes = r.SizeBytes,
        iterations = r.Iterations,
        avg_latency_us = Math.Round(r.AvgLatencyUs, 3),
        throughput_mb_per_s = Math.Round(r.ThroughputMBps, 3)
    }),
    notes = "BenchmarkService protobuf payloads; client and server in same process"
};

var jsonPath = Path.Combine(outDir, "results.json");
File.WriteAllText(jsonPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Results written to: {jsonPath}");

var csvPath = Path.Combine(outDir, "results.csv");
WriteCsv(csvPath, unaryResults, streamingResults);
Console.WriteLine($"CSV written to: {csvPath}");

// ============================================================================
// Benchmark Service Implementation (server side)
// ============================================================================

static Payload MakePayload(int size)
{
    if (size <= 0) return new Payload();
    return new Payload { Body = ByteString.CopyFrom(new byte[size]) };
}

// ============================================================================
// Environment Setup
// ============================================================================

async Task<BenchEnv> StartTcpEnv()
{
    var builder = WebApplication.CreateBuilder(Array.Empty<string>());
    builder.Logging.ClearProviders();
    builder.Services.AddGrpc(o =>
    {
        o.MaxReceiveMessageSize = 256 * 1024 * 1024;
        o.MaxSendMessageSize = 256 * 1024 * 1024;
    });
    builder.WebHost.ConfigureKestrel(k =>
    {
        k.Limits.MaxRequestBodySize = 512 * 1024 * 1024;
        k.Listen(IPAddress.Loopback, 0, lo =>
        {
            lo.Protocols = HttpProtocols.Http2;
        });
    });

    var app = builder.Build();
    app.MapGrpcService<BenchmarkServiceImpl>();

    await app.StartAsync();

    var address = app.Urls.First();
    var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
    {
        MaxReceiveMessageSize = 256 * 1024 * 1024,
        MaxSendMessageSize = 256 * 1024 * 1024
    });
    var client = new BenchmarkService.BenchmarkServiceClient(channel);

    return new BenchEnv("tcp", client, channel, async () =>
    {
        channel.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    });
}

async Task<BenchEnv> StartShmEnv()
{
    var segmentName = $"bench_shm_{Environment.ProcessId}";

    // Clean up stale segments
    Segment.TryRemoveSegment(segmentName);
    Segment.TryRemoveSegment(segmentName + "_ctl");

    var server = new ShmGrpcServer(segmentName, ringCapacity: 512 * 1024 * 1024);

    server.MapUnary<SimpleRequest, SimpleResponse>(
        "/grpc.testing.BenchmarkService/UnaryCall",
        (req, ctx) =>
        {
            var response = new SimpleResponse { Payload = MakePayload(req.ResponseSize) };
            return Task.FromResult(response);
        });

    server.MapDuplexStreaming<SimpleRequest, SimpleResponse>(
        "/grpc.testing.BenchmarkService/StreamingCall",
        async (reader, writer, ctx) =>
        {
            while (await reader.MoveNext(ctx.CancellationToken))
            {
                var req = reader.Current;
                var response = new SimpleResponse { Payload = MakePayload(req.ResponseSize) };
                await writer.WriteAsync(response);
            }
        });

    var cts = new CancellationTokenSource();
    Console.Error.WriteLine("[DIAG] Starting SHM server...");
    Console.Error.Flush();
    // RunAsync blocks synchronously inside ring.Read() waiting for a client
    // CONNECT frame — offload to a thread pool thread so the caller can proceed
    // to create the client channel and initiate the connection.
    var serverTask = Task.Run(() => server.RunAsync(cts.Token));

    // Give server time to set up control segment
    await Task.Delay(500);
    Console.Error.WriteLine("[DIAG] Delay done, checking serverTask...");
    Console.Error.Flush();

    if (serverTask.IsFaulted)
    {
        Console.Error.WriteLine($"[DIAG] Server task FAULTED: {serverTask.Exception?.GetBaseException().Message}");
        Console.Error.Flush();
        throw serverTask.Exception!.GetBaseException();
    }

    Console.Error.WriteLine("[DIAG] Creating channel...");
    Console.Error.Flush();
    var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
    {
        HttpHandler = new ShmControlHandler(segmentName),
        DisposeHttpClient = true,
        MaxReceiveMessageSize = 256 * 1024 * 1024,
        MaxSendMessageSize = 256 * 1024 * 1024
    });

    var client = new BenchmarkService.BenchmarkServiceClient(channel);

    // Smoke-test: verify a single SHM unary call completes
    Console.Error.Write("[DIAG] SHM smoke test...");
    Console.Error.Flush();
    var smokeReq = new SimpleRequest { ResponseSize = 0 };
    using var smokeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    try
    {
        var smokeResult = await client.UnaryCallAsync(smokeReq, cancellationToken: smokeCts.Token);
        Console.Error.WriteLine($"OK (resp size={smokeResult.Payload?.Body?.Length ?? 0})");
        Console.Error.Flush();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
        Console.Error.WriteLine(ex.ToString());
        Console.Error.Flush();
        throw;
    }

    return new BenchEnv("shm", client, channel, async () =>
    {
        channel.Dispose();
        cts.Cancel();
        server.Shutdown();
        try { await serverTask; } catch (OperationCanceledException) { }
        await server.DisposeAsync();
        Segment.TryRemoveSegment(segmentName);
        Segment.TryRemoveSegment(segmentName + "_ctl");
    });
}

// ============================================================================
// Measurement — matches Go's measureUnary / measureStreaming exactly
// ============================================================================

static async Task<(double avgUs, double throughputMBps)> MeasureUnary(
    BenchmarkService.BenchmarkServiceClient client, int payloadSize, int iterations)
{
    var payload = MakePayload(payloadSize);
    var req = new SimpleRequest { ResponseSize = payloadSize, Payload = payload };

    // Safety timeout: 5 minutes per measurement to prevent hangs
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

    var sw = Stopwatch.StartNew();
    for (int i = 0; i < iterations; i++)
        await client.UnaryCallAsync(req, cancellationToken: cts.Token);
    sw.Stop();

    double totalUs = sw.Elapsed.TotalMicroseconds;
    double avgUs = totalUs / iterations;
    double totalBytes = (double)iterations * payloadSize * 2; // request + response
    double throughputMBps = totalBytes > 0 && sw.Elapsed.TotalSeconds > 0
        ? totalBytes / (1024 * 1024) / sw.Elapsed.TotalSeconds
        : 0;

    return (avgUs, throughputMBps);
}

static async Task<(double avgUs, double throughputMBps)> MeasureStreaming(
    BenchmarkService.BenchmarkServiceClient client, int payloadSize, int iterations)
{
    var payload = MakePayload(payloadSize);
    var req = new SimpleRequest { ResponseSize = payloadSize, Payload = payload };

    // Safety timeout: 5 minutes per measurement to prevent hangs
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

    using var call = client.StreamingCall(cancellationToken: cts.Token);

    var sw = Stopwatch.StartNew();
    for (int i = 0; i < iterations; i++)
    {
        await call.RequestStream.WriteAsync(req);
        await call.ResponseStream.MoveNext(cts.Token);
    }
    sw.Stop();

    await call.RequestStream.CompleteAsync();

    double totalUs = sw.Elapsed.TotalMicroseconds;
    double avgUs = totalUs / iterations;
    double totalBytes = (double)iterations * payloadSize * 2; // request + response
    double throughputMBps = totalBytes > 0 && sw.Elapsed.TotalSeconds > 0
        ? totalBytes / (1024 * 1024) / sw.Elapsed.TotalSeconds
        : 0;

    return (avgUs, throughputMBps);
}

// ============================================================================
// Iteration count — matches Go's iterationsForSize exactly
// ============================================================================

static int IterationsForSize(int size) => size switch
{
    <= 0 => 2000,
    <= 1024 => 2000,
    <= 16384 => 1200,
    <= 65536 => 800,
    <= 262144 => 400,
    <= 524288 => 250,
    <= 1048576 => 150,
    <= 2097152 => 80,       // 2 MB
    <= 4194304 => 40,       // 4 MB
    <= 16777216 => 20,      // 16 MB
    <= 33554432 => 10,      // 32 MB
    _ => 5                  // 128 MB+
};

// ============================================================================
// Output helpers
// ============================================================================

static void WriteCsv(string path, List<BenchResult> unary, List<BenchResult> streaming)
{
    using var w = new StreamWriter(path);
    w.WriteLine("type,transport,size_bytes,iterations,avg_latency_us,throughput_mb_per_s");
    foreach (var r in unary)
        w.WriteLine($"unary,{r.Transport},{r.SizeBytes},{r.Iterations},{r.AvgLatencyUs:F3},{r.ThroughputMBps:F3}");
    foreach (var r in streaming)
        w.WriteLine($"streaming,{r.Transport},{r.SizeBytes},{r.Iterations},{r.AvgLatencyUs:F3},{r.ThroughputMBps:F3}");
}

static string FormatSize(int bytes) => bytes switch
{
    0 => "0B",
    >= 1048576 => $"{bytes / 1048576}MB",
    >= 1024 => $"{bytes / 1024}KB",
    _ => $"{bytes}B"
};

static string GetCpuInfo()
{
    try
    {
        foreach (var line in File.ReadAllLines("/proc/cpuinfo"))
            if (line.StartsWith("model name"))
                return line.Split(':')[1].Trim();
    }
    catch { }
    return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Unknown";
}


// ============================================================================
// Types
// ============================================================================

sealed record BenchResult(string Transport, int SizeBytes, int Iterations, double AvgLatencyUs, double ThroughputMBps);

/// <summary>
/// Benchmark gRPC service implementation.
/// Matches Go's BenchmarkService: echoes a response with the requested payload size.
/// </summary>
sealed class BenchmarkServiceImpl : BenchmarkService.BenchmarkServiceBase
{
    public override Task<SimpleResponse> UnaryCall(SimpleRequest request, ServerCallContext context)
    {
        return Task.FromResult(new SimpleResponse { Payload = MakePayload(request.ResponseSize) });
    }

    public override async Task StreamingCall(
        IAsyncStreamReader<SimpleRequest> requestStream,
        IServerStreamWriter<SimpleResponse> responseStream,
        ServerCallContext context)
    {
        while (await requestStream.MoveNext(context.CancellationToken))
        {
            var req = requestStream.Current;
            await responseStream.WriteAsync(new SimpleResponse { Payload = MakePayload(req.ResponseSize) });
        }
    }

    static Payload MakePayload(int size)
    {
        if (size <= 0) return new Payload();
        return new Payload { Body = ByteString.CopyFrom(new byte[size]) };
    }
}

// ============================================================================
// Environment wrapper
// ============================================================================

sealed class BenchEnv : IAsyncDisposable
{
    public string Transport { get; }
    public BenchmarkService.BenchmarkServiceClient Client { get; }
    public GrpcChannel Channel { get; }
    private readonly Func<Task> _cleanup;

    public BenchEnv(string transport, BenchmarkService.BenchmarkServiceClient client, GrpcChannel channel, Func<Task> cleanup)
    {
        Transport = transport;
        Client = client;
        Channel = channel;
        _cleanup = cleanup;
    }

    public async ValueTask DisposeAsync() => await _cleanup();
}
