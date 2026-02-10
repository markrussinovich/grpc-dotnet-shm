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

// Ensure enough thread pool threads for in-process client+server operation
ThreadPool.SetMinThreads(32, 32);

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

// Go benchmark sizes: 0, 1, 1K, 4K, 16K, 64K, 256K, 512K, 1M, 2M
int[] sizes = { 0, 1, 1024, 4096, 16384, 65536, 262144, 524288, 1048576, 2097152 };

string cpu = GetCpuInfo();
string runtime = RuntimeInformation.FrameworkDescription;
Console.WriteLine($"CPU: {cpu}");
Console.WriteLine($"Runtime: {runtime}");
Console.WriteLine();

var unaryResults = new List<BenchResult>();
var streamingResults = new List<BenchResult>();

// Run each transport independently to avoid idle-spin stack buildup in SHM frame reader
foreach (var startEnv in new Func<Task<BenchEnv>>[] { StartTcpEnv, StartShmEnv })
{
    await using var env = await startEnv();

    Console.WriteLine($"=== {env.Transport.ToUpper()} Transport ===");
    Console.WriteLine();

    Console.WriteLine("  Unary ping-pong:");
    Console.WriteLine($"  {"Payload",-12} {"Iters",-8} {"Avg µs",-14} {"Throughput MB/s",-18}");
    Console.WriteLine("  " + new string('-', 52));

    foreach (var size in sizes)
    {
        int iters = IterationsForSize(size);
        Console.Error.WriteLine($"  [DBG] Starting unary {FormatSize(size)} x{iters}...");
        Console.Error.Flush();
        var (avgUs, throughputMBps) = await MeasureUnary(env.Client, size, iters);
        unaryResults.Add(new BenchResult(env.Transport, size, iters, avgUs, throughputMBps));
        Console.WriteLine($"  {FormatSize(size),-12} {iters,-8} {avgUs,-14:F3} {throughputMBps,-18:F3}");
    }
    Console.WriteLine();

    Console.WriteLine("  Streaming ping-pong:");
    Console.WriteLine($"  {"Payload",-12} {"Iters",-8} {"Avg µs",-14} {"Throughput MB/s",-18}");
    Console.WriteLine("  " + new string('-', 52));

    foreach (var size in sizes)
    {
        int iters = IterationsForSize(size);
        var (avgUs, throughputMBps) = await MeasureStreaming(env.Client, size, iters);
        streamingResults.Add(new BenchResult(env.Transport, size, iters, avgUs, throughputMBps));
        Console.WriteLine($"  {FormatSize(size),-12} {iters,-8} {avgUs,-14:F3} {throughputMBps,-18:F3}");
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

// Generate SVG plots (matching Go's output)
WriteSvgPlot(
    Path.Combine(outDir, "unary_latency.svg"),
    "Unary ping-pong latency", "Payload size", "Avg latency (µs)",
    GroupByTransport(unaryResults, r => r.AvgLatencyUs));

WriteSvgPlot(
    Path.Combine(outDir, "streaming_latency.svg"),
    "Streaming ping-pong latency", "Payload size", "Avg latency (µs)",
    GroupByTransport(streamingResults, r => r.AvgLatencyUs));

WriteSvgPlot(
    Path.Combine(outDir, "streaming_throughput.svg"),
    "Streaming throughput", "Payload size", "Throughput (MiB/s)",
    GroupByTransport(streamingResults, r => r.ThroughputMBps));

Console.WriteLine($"Plots written to: {outDir}");

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
    builder.Services.AddGrpc();
    builder.WebHost.ConfigureKestrel(k =>
    {
        k.Listen(IPAddress.Loopback, 0, lo => lo.Protocols = HttpProtocols.Http2);
    });

    var app = builder.Build();
    app.MapGrpcService<BenchmarkServiceImpl>();

    await app.StartAsync();

    var address = app.Urls.First();
    var channel = GrpcChannel.ForAddress(address);
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

    var server = new ShmGrpcServer(segmentName, ringCapacity: 64 * 1024 * 1024);

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
    var serverTask = server.RunAsync(cts.Token);

    // Give server time to set up control segment
    await Task.Delay(200);

    var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
    {
        HttpHandler = new ShmControlHandler(segmentName),
        DisposeHttpClient = true
    });

    var client = new BenchmarkService.BenchmarkServiceClient(channel);

    // Smoke-test: verify a single SHM unary call completes
    Console.Error.Write("SHM smoke test...");
    Console.Error.Flush();
    var smokeReq = new SimpleRequest { ResponseSize = 0 };
    using var smokeCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    try
    {
        await client.UnaryCallAsync(smokeReq, cancellationToken: smokeCts.Token);
        Console.Error.WriteLine("OK");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
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

    // Warmup
    Console.Error.Write($"    warmup...");
    Console.Error.Flush();
    for (int i = 0; i < Math.Min(10, iterations / 10 + 1); i++)
        await client.UnaryCallAsync(req);
    Console.Error.Write($"timed({iterations})...");
    Console.Error.Flush();

    var sw = Stopwatch.StartNew();
    for (int i = 0; i < iterations; i++)
        await client.UnaryCallAsync(req);
    sw.Stop();
    Console.Error.WriteLine("done");
    Console.Error.Flush();

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

    using var call = client.StreamingCall();

    // Warmup
    for (int i = 0; i < Math.Min(10, iterations / 10 + 1); i++)
    {
        await call.RequestStream.WriteAsync(req);
        await call.ResponseStream.MoveNext(CancellationToken.None);
    }

    var sw = Stopwatch.StartNew();
    for (int i = 0; i < iterations; i++)
    {
        await call.RequestStream.WriteAsync(req);
        await call.ResponseStream.MoveNext(CancellationToken.None);
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
    _ => 80
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
// SVG Plotting — matches Go's writeSVGPlot
// ============================================================================

static List<PlotSeries> GroupByTransport(List<BenchResult> results, Func<BenchResult, double> valueSelector)
{
    return results
        .GroupBy(r => r.Transport)
        .OrderBy(g => g.Key)
        .Select(g => new PlotSeries(
            g.Key,
            g.Key == "shm" ? "#d62728" : "#1f77b4",
            g.OrderBy(r => r.SizeBytes)
             .Select(r => new PlotPoint(r.SizeBytes, valueSelector(r)))
             .ToList()))
        .ToList();
}

static void WriteSvgPlot(string path, string title, string xLabel, string yLabel, List<PlotSeries> series)
{
    if (series.Count == 0) return;

    const double width = 960, height = 560;
    const double ml = 80, mr = 40, mt = 60, mb = 70;

    double xMax = series.SelectMany(s => s.Points).Max(p => p.X);
    double yMax = series.SelectMany(s => s.Points).Max(p => p.Y);
    if (xMax == 0) xMax = 1;
    if (yMax == 0) yMax = 1;

    double chartW = width - ml - mr;
    double chartH = height - mt - mb;
    double scaleX = chartW / xMax;
    double scaleY = chartH / yMax;

    var xTicks = NiceTicks(0, xMax, 6);
    var yTicks = NiceTicks(0, yMax, 6);
    double x0 = ml, y0 = height - mb;

    using var w = new StreamWriter(path);
    w.Write($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width:F0}\" height=\"{height:F0}\" viewBox=\"0 0 {width:F0} {height:F0}\">");
    w.Write("<style>text{font-family:Arial,sans-serif;font-size:12px;} .title{font-size:16px;font-weight:bold;}</style>");
    w.Write("<rect width=\"100%\" height=\"100%\" fill=\"white\"/>");

    // Title + labels
    w.Write($"<text x=\"{width / 2:F1}\" y=\"{mt / 2:F1}\" class=\"title\" text-anchor=\"middle\">{title}</text>");
    w.Write($"<text x=\"{width / 2:F1}\" y=\"{height - 20:F1}\" text-anchor=\"middle\">{xLabel}</text>");
    w.Write($"<text x=\"15\" y=\"{height / 2:F1}\" transform=\"rotate(-90 15,{height / 2:F1})\" text-anchor=\"middle\">{yLabel}</text>");

    // Axes
    w.Write($"<line x1=\"{x0:F1}\" y1=\"{y0:F1}\" x2=\"{width - mr:F1}\" y2=\"{y0:F1}\" stroke=\"black\"/>");
    w.Write($"<line x1=\"{x0:F1}\" y1=\"{y0:F1}\" x2=\"{x0:F1}\" y2=\"{mt:F1}\" stroke=\"black\"/>");

    // Ticks + grid
    foreach (var t in xTicks)
    {
        double px = x0 + t * scaleX;
        w.Write($"<line x1=\"{px:F1}\" y1=\"{y0:F1}\" x2=\"{px:F1}\" y2=\"{y0 + 6:F1}\" stroke=\"black\"/>");
        w.Write($"<text x=\"{px:F1}\" y=\"{y0 + 20:F1}\" text-anchor=\"middle\">{FormatSizeForPlot((int)t)}</text>");
        w.Write($"<line x1=\"{px:F1}\" y1=\"{y0:F1}\" x2=\"{px:F1}\" y2=\"{mt:F1}\" stroke=\"#dddddd\"/>");
    }
    foreach (var t in yTicks)
    {
        double py = y0 - t * scaleY;
        w.Write($"<line x1=\"{x0 - 6:F1}\" y1=\"{py:F1}\" x2=\"{x0:F1}\" y2=\"{py:F1}\" stroke=\"black\"/>");
        w.Write($"<text x=\"{x0 - 8:F1}\" y=\"{py + 4:F1}\" text-anchor=\"end\">{FormatNumber(t)}</text>");
        w.Write($"<line x1=\"{x0:F1}\" y1=\"{py:F1}\" x2=\"{width - mr:F1}\" y2=\"{py:F1}\" stroke=\"#ededed\"/>");
    }

    // Data series
    foreach (var s in series)
    {
        w.Write($"<polyline fill=\"none\" stroke=\"{s.Color}\" stroke-width=\"2\" points=\"");
        foreach (var p in s.Points)
        {
            double px = x0 + p.X * scaleX;
            double py = y0 - p.Y * scaleY;
            w.Write($"{px:F1},{py:F1} ");
        }
        w.Write("\"/>");

        foreach (var p in s.Points)
        {
            double px = x0 + p.X * scaleX;
            double py = y0 - p.Y * scaleY;
            w.Write($"<circle cx=\"{px:F1}\" cy=\"{py:F1}\" r=\"3\" fill=\"{s.Color}\"/>");
        }
    }

    // Legend
    double lx = width - mr - 150, ly = mt + 10;
    w.Write($"<rect x=\"{lx:F1}\" y=\"{ly:F1}\" width=\"140\" height=\"{series.Count * 22 + 10:F1}\" fill=\"white\" stroke=\"#ccc\"/>");
    for (int si = 0; si < series.Count; si++)
    {
        double ey = ly + 20 + si * 22;
        w.Write($"<line x1=\"{lx + 10:F1}\" y1=\"{ey - 5:F1}\" x2=\"{lx + 30:F1}\" y2=\"{ey - 5:F1}\" stroke=\"{series[si].Color}\" stroke-width=\"3\"/>");
        w.Write($"<text x=\"{lx + 40:F1}\" y=\"{ey - 2:F1}\">{series[si].Label}</text>");
    }

    w.Write("</svg>");
}

static string FormatSizeForPlot(int n) => n switch
{
    0 => "0 B",
    >= 1048576 => $"{n / 1048576.0:F1} MiB",
    >= 1024 => $"{n / 1024.0:F1} KiB",
    _ => $"{n} B"
};

static string FormatNumber(double v) => v switch
{
    >= 1_000_000 => $"{v / 1_000_000:F1}M",
    >= 1_000 => $"{v / 1_000:F1}k",
    >= 10 => $"{v:F0}",
    _ => $"{v:F2}"
};

static List<double> NiceTicks(double min, double max, int count)
{
    if (max <= min) max = min + 1;
    double rawStep = (max - min) / count;
    double step = NiceStep(rawStep);
    double start = Math.Floor(min / step) * step;
    double end = Math.Ceiling(max / step) * step;
    var ticks = new List<double>();
    for (double v = start; v <= end + step / 2; v += step)
    {
        if (v < 0 && min >= 0) continue;
        ticks.Add(v);
    }
    return ticks;
}

static double NiceStep(double step)
{
    if (step == 0) return 1;
    double pow = Math.Pow(10, Math.Floor(Math.Log10(step)));
    double scaled = step / pow;
    double nice = scaled switch
    {
        < 1.5 => 1,
        < 3 => 2,
        < 7 => 5,
        _ => 10
    };
    return nice * pow;
}

// ============================================================================
// Types
// ============================================================================

sealed record BenchResult(string Transport, int SizeBytes, int Iterations, double AvgLatencyUs, double ThroughputMBps);
sealed record PlotPoint(double X, double Y);
sealed record PlotSeries(string Label, string Color, List<PlotPoint> Points);

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
