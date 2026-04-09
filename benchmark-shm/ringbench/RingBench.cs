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
using System.Globalization;
using System.Net;
using System.Net.Sockets;
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

// Ensure enough thread pool threads for benchmark operations
ThreadPool.SetMinThreads(200, 200);

var measurementTimeout = TimeSpan.FromMinutes(15);

string outDir = Path.Combine("benchmark-shm", "out");
string? platformOverride = null;
bool serverMode = false;
string? serverTransport = null;
int serverPort = 0;
string? serverSegment = null;
int parentPid = 0;
string? onlyTransport = null;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--output" || args[i] == "--out")
        outDir = args[++i];
    if (args[i] == "--platform")
        platformOverride = args[++i];
    if (args[i] == "--server")
        serverMode = true;
    if (args[i] == "--transport")
        serverTransport = args[++i];
    if (args[i] == "--port")
        serverPort = int.Parse(args[++i], CultureInfo.InvariantCulture);
    if (args[i] == "--segment")
        serverSegment = args[++i];
    if (args[i] == "--parent-pid")
        parentPid = int.Parse(args[++i], CultureInfo.InvariantCulture);
    if (args[i] == "--only")
        onlyTransport = args[++i].ToLowerInvariant();
}

if (serverMode)
{
    if (string.IsNullOrWhiteSpace(serverTransport))
    {
        throw new InvalidOperationException("Server mode requires --transport tcp|shm");
    }

    await RunServerModeAsync(serverTransport!, serverPort, serverSegment, parentPid);
    return;
}

string platform = platformOverride
    ?? (OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "other");
outDir = Path.Combine(outDir, platform);
Directory.CreateDirectory(outDir);

// Clean up stale SHM segments from previous crashed benchmark runs
var cleaned = Segment.TryRemoveSegmentsByPrefix("bench_shm_");
if (cleaned > 0)
    Console.WriteLine($"Cleaned {cleaned} stale SHM segment(s) from previous runs.");

// Go benchmark sizes: 0, 1, 1K, 4K, 16K, 64K, 256K, 512K, 1M, 2M, 4M
int[] sizes = { 0, 1, 1024, 4096, 16384, 65536, 262144, 524288, 1048576, 2097152, 4194304 };

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

    // --only tcp|shm: skip transports that don't match
    if (onlyTransport != null && env.Transport != onlyTransport)
    {
        await env.DisposeAsync();
        continue;
    }

    await using var envDisposable = env;

    Console.WriteLine($"=== {env.Transport.ToUpper()} Transport ===");
    Console.WriteLine();

    Console.WriteLine("  Unary ping-pong:");
    Console.WriteLine($"  {"Payload",-12} {"Iters",-8} {"Avg µs",-14} {"Throughput MB/s",-18} {"Gen0",-6} {"Gen1",-6} {"Gen2",-6}");
    Console.WriteLine("  " + new string('-', 76));

    foreach (var size in sizes)
    {
        int iters = IterationsForSize(size);
        Console.WriteLine($"  -> running {FormatSize(size),-8} ({iters} iters)...");
        int gc0Before = GC.CollectionCount(0), gc1Before = GC.CollectionCount(1), gc2Before = GC.CollectionCount(2);
        var (avgUs, throughputMBps) = await MeasureUnary(env.Client, size, iters, measurementTimeout);
        int gc0 = GC.CollectionCount(0) - gc0Before, gc1 = GC.CollectionCount(1) - gc1Before, gc2 = GC.CollectionCount(2) - gc2Before;
        unaryResults.Add(new BenchResult(env.Transport, size, iters, avgUs, throughputMBps));
        Console.WriteLine($"  {FormatSize(size),-12} {iters,-8} {avgUs,-14:F3} {throughputMBps,-18:F3} {gc0,-6} {gc1,-6} {gc2,-6}");
    }
    Console.WriteLine();

    Console.WriteLine("  Streaming ping-pong:");
    Console.WriteLine($"  {"Payload",-12} {"Iters",-8} {"Avg µs",-14} {"Throughput MB/s",-18} {"Gen0",-6} {"Gen1",-6} {"Gen2",-6}");
    Console.WriteLine("  " + new string('-', 76));

    foreach (var size in sizes)
    {
        int iters = IterationsForSize(size);
        Console.WriteLine($"  -> running {FormatSize(size),-8} ({iters} iters)...");
        int gc0Before = GC.CollectionCount(0), gc1Before = GC.CollectionCount(1), gc2Before = GC.CollectionCount(2);
        var (avgUs, throughputMBps) = await MeasureStreaming(env.Client, size, iters, measurementTimeout);
        int gc0 = GC.CollectionCount(0) - gc0Before, gc1 = GC.CollectionCount(1) - gc1Before, gc2 = GC.CollectionCount(2) - gc2Before;
        streamingResults.Add(new BenchResult(env.Transport, size, iters, avgUs, throughputMBps));
        Console.WriteLine($"  {FormatSize(size),-12} {iters,-8} {avgUs,-14:F3} {throughputMBps,-18:F3} {gc0,-6} {gc1,-6} {gc2,-6}");
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
    notes = "BenchmarkService protobuf payloads; client and server in separate processes"
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
TryGenerateRunnerPlots();

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
    int port = GetAvailablePort();
    var serverProcess = StartServerProcess("tcp", port: port);

    try
    {
        var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}", new GrpcChannelOptions
        {
            MaxReceiveMessageSize = 8 * 1024 * 1024,
            MaxSendMessageSize = 8 * 1024 * 1024
        });
        var client = new BenchmarkService.BenchmarkServiceClient(channel);

        await WaitForServerReadyAsync(client, TimeSpan.FromSeconds(20));

        return new BenchEnv("tcp", client, channel, async () =>
        {
            channel.Dispose();
            await StopServerProcessAsync(serverProcess).ConfigureAwait(false);
        });
    }
    catch
    {
        await StopServerProcessAsync(serverProcess).ConfigureAwait(false);
        throw;
    }
}

async Task<BenchEnv> StartShmEnv()
{
    var segmentName = $"bench_shm_{Environment.ProcessId}_{Guid.NewGuid():N}";

    Segment.TryRemoveSegment(segmentName);
    Segment.TryRemoveSegment(segmentName + "_ctl");

    var serverProcess = StartServerProcess("shm", segmentName: segmentName);

    try
    {
        var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = new ShmControlHandler(segmentName),
            DisposeHttpClient = true,
            MaxReceiveMessageSize = 8 * 1024 * 1024,
            MaxSendMessageSize = 8 * 1024 * 1024
        });

        var client = new BenchmarkService.BenchmarkServiceClient(channel);

        await WaitForServerReadyAsync(client, TimeSpan.FromSeconds(25));

        return new BenchEnv("shm", client, channel, async () =>
        {
            channel.Dispose();
            await StopServerProcessAsync(serverProcess).ConfigureAwait(false);
            Segment.TryRemoveSegment(segmentName);
            Segment.TryRemoveSegment(segmentName + "_ctl");
        });
    }
    catch
    {
        await StopServerProcessAsync(serverProcess).ConfigureAwait(false);
        Segment.TryRemoveSegment(segmentName);
        Segment.TryRemoveSegment(segmentName + "_ctl");
        throw;
    }
}

static int GetAvailablePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}

static Process StartServerProcess(string transport, int? port = null, string? segmentName = null)
{
    int currentPid = Environment.ProcessId;
    var assemblyPath = typeof(BenchmarkServiceImpl).Assembly.Location;
    var argParts = new List<string>
    {
        $"\"{assemblyPath}\"",
        "--server",
        "--transport",
        transport,
        "--parent-pid",
        currentPid.ToString(CultureInfo.InvariantCulture)
    };

    if (port.HasValue)
    {
        argParts.Add("--port");
        argParts.Add(port.Value.ToString(CultureInfo.InvariantCulture));
    }

    if (!string.IsNullOrWhiteSpace(segmentName))
    {
        argParts.Add("--segment");
        argParts.Add(segmentName!);
    }

    var psi = new ProcessStartInfo
    {
        FileName = "dotnet",
        Arguments = string.Join(" ", argParts),
        UseShellExecute = false,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        CreateNoWindow = true
    };

    var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

    process.OutputDataReceived += (_, e) =>
    {
        if (!string.IsNullOrWhiteSpace(e.Data))
        {
            Console.WriteLine($"[SRV] {e.Data}");
        }
    };
    process.ErrorDataReceived += (_, e) =>
    {
        if (!string.IsNullOrWhiteSpace(e.Data))
        {
            Console.WriteLine($"[SRV] {e.Data}");
        }
    };

    if (!process.Start())
    {
        throw new InvalidOperationException($"Failed to start {transport} server process");
    }

    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    return process;
}

static async Task StopServerProcessAsync(Process process)
{
    try
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
    }
    catch
    {
    }
    finally
    {
        process.Dispose();
    }
}

static async Task WaitForServerReadyAsync(BenchmarkService.BenchmarkServiceClient client, TimeSpan timeout)
{
    var started = Stopwatch.StartNew();
    Exception? lastError = null;

    while (started.Elapsed < timeout)
    {
        using var attemptCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            using var call = client.UnaryCallAsync(new SimpleRequest { ResponseSize = 0 }, cancellationToken: attemptCts.Token);
            var completed = await Task.WhenAny(call.ResponseAsync, Task.Delay(TimeSpan.FromSeconds(2), attemptCts.Token)).ConfigureAwait(false);
            if (completed != call.ResponseAsync)
            {
                throw new TimeoutException("Server readiness probe timed out.");
            }

            await call.ResponseAsync.ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            lastError = ex;
            await Task.Delay(150).ConfigureAwait(false);
        }
    }

    throw new TimeoutException($"Server was not ready within {timeout.TotalSeconds:F0}s.", lastError);
}

static async Task RunServerModeAsync(string transport, int port, string? segmentName, int parentPid)
{
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    if (parentPid > 0)
    {
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    using var parent = Process.GetProcessById(parentPid);
                    if (parent.HasExited)
                    {
                        cts.Cancel();
                        return;
                    }
                }
                catch
                {
                    cts.Cancel();
                    return;
                }

                try
                {
                    await Task.Delay(500, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
    }

    if (transport.Equals("tcp", StringComparison.OrdinalIgnoreCase))
    {
        if (port <= 0)
        {
            throw new InvalidOperationException("TCP server mode requires --port");
        }

        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Logging.ClearProviders();
        builder.Services.AddGrpc(o =>
        {
            o.MaxReceiveMessageSize = 8 * 1024 * 1024;
            o.MaxSendMessageSize = 8 * 1024 * 1024;
        });
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, port, lo => lo.Protocols = HttpProtocols.Http2);
        });

        var app = builder.Build();
        app.MapGrpcService<BenchmarkServiceImpl>();

        await app.StartAsync(cts.Token).ConfigureAwait(false);
        Console.WriteLine($"[SERVER] TCP ready on 127.0.0.1:{port}");

        try
        {
            await app.WaitForShutdownAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await app.StopAsync().ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
        return;
    }

    if (!transport.Equals("shm", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"Unknown transport '{transport}'");
    }

    if (string.IsNullOrWhiteSpace(segmentName))
    {
        throw new InvalidOperationException("SHM server mode requires --segment");
    }

    Segment.TryRemoveSegment(segmentName);
    Segment.TryRemoveSegment(segmentName + "_ctl");

    var server = new ShmGrpcServer(segmentName, ringCapacity: 64 * 1024 * 1024);

    server.MapUnary<SimpleRequest, SimpleResponse>(
        "/grpc.testing.BenchmarkService/UnaryCall",
        (req, _) => Task.FromResult(new SimpleResponse { Payload = MakePayload(req.ResponseSize) }));

    server.MapDuplexStreaming<SimpleRequest, SimpleResponse>(
        "/grpc.testing.BenchmarkService/StreamingCall",
        async (reader, writer, ctx) =>
        {
            while (await reader.MoveNext(ctx.CancellationToken).ConfigureAwait(false))
            {
                var req = reader.Current;
                await writer.WriteAsync(new SimpleResponse { Payload = MakePayload(req.ResponseSize) }).ConfigureAwait(false);
            }
        });

    Console.WriteLine($"[SERVER] SHM ready on segment: {segmentName}");
    try
    {
        await server.RunAsync(cts.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
    }
    finally
    {
        server.Shutdown();
        await server.DisposeAsync().ConfigureAwait(false);
        Segment.TryRemoveSegment(segmentName);
        Segment.TryRemoveSegment(segmentName + "_ctl");
    }
}

// ============================================================================
// Measurement — matches Go's measureUnary / measureStreaming exactly
// ============================================================================

static async Task<(double avgUs, double throughputMBps)> MeasureUnary(
    BenchmarkService.BenchmarkServiceClient client, int payloadSize, int iterations, TimeSpan timeout)
{
    var payload = MakePayload(payloadSize);
    var req = new SimpleRequest { ResponseSize = payloadSize, Payload = payload };
    var stepTimeout = TimeSpan.FromSeconds(120);
    var started = Stopwatch.StartNew();

    // Warmup
    for (int i = 0; i < Math.Min(10, iterations / 10 + 1); i++)
        await UnaryCallWithHardTimeoutAsync(client, req, stepTimeout).ConfigureAwait(false);

    var sw = Stopwatch.StartNew();
    for (int i = 0; i < iterations; i++)
    {
        if (started.Elapsed > timeout)
        {
            throw new TimeoutException($"Unary measurement exceeded timeout of {timeout.TotalSeconds:F0}s.");
        }

        await UnaryCallWithHardTimeoutAsync(client, req, stepTimeout).ConfigureAwait(false);
    }
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
    BenchmarkService.BenchmarkServiceClient client, int payloadSize, int iterations, TimeSpan timeout)
{
    var payload = MakePayload(payloadSize);
    var req = new SimpleRequest { ResponseSize = payloadSize, Payload = payload };
    var stepTimeout = TimeSpan.FromSeconds(120);
    var started = Stopwatch.StartNew();

    using var call = client.StreamingCall();

    // Warmup
    for (int i = 0; i < Math.Min(10, iterations / 10 + 1); i++)
    {
        await call.RequestStream.WriteAsync(req).WaitAsync(stepTimeout).ConfigureAwait(false);
        await call.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(stepTimeout).ConfigureAwait(false);
    }

    var sw = Stopwatch.StartNew();
    for (int i = 0; i < iterations; i++)
    {
        if (started.Elapsed > timeout)
        {
            throw new TimeoutException($"Streaming measurement exceeded timeout of {timeout.TotalSeconds:F0}s.");
        }

        await call.RequestStream.WriteAsync(req).WaitAsync(stepTimeout).ConfigureAwait(false);
        await call.ResponseStream.MoveNext(CancellationToken.None).WaitAsync(stepTimeout).ConfigureAwait(false);
    }
    sw.Stop();

    await call.RequestStream.CompleteAsync().WaitAsync(stepTimeout).ConfigureAwait(false);

    double totalUs = sw.Elapsed.TotalMicroseconds;
    double avgUs = totalUs / iterations;
    double totalBytes = (double)iterations * payloadSize * 2; // request + response
    double throughputMBps = totalBytes > 0 && sw.Elapsed.TotalSeconds > 0
        ? totalBytes / (1024 * 1024) / sw.Elapsed.TotalSeconds
        : 0;

    return (avgUs, throughputMBps);
}

static async Task UnaryCallWithHardTimeoutAsync(
    BenchmarkService.BenchmarkServiceClient client,
    SimpleRequest request,
    TimeSpan timeout)
{
    using var call = client.UnaryCallAsync(request, cancellationToken: CancellationToken.None);
    var completed = await Task.WhenAny(call.ResponseAsync, Task.Delay(timeout)).ConfigureAwait(false);
    if (completed != call.ResponseAsync)
    {
        call.Dispose();
        throw new TimeoutException($"Unary gRPC call exceeded timeout of {timeout.TotalSeconds:F0}s.");
    }

    await call.ResponseAsync.ConfigureAwait(false);
}

// ============================================================================
// Iteration count — preserves Go-style weighting, scaled up for run stability.
// ============================================================================

const int IterationMultiplier = 3;
const int LargePayloadIterationMultiplier = 2;

static int IterationsForSize(int size)
{
    int baseline = size switch
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

    int multiplier = size >= 524288 ? LargePayloadIterationMultiplier : IterationMultiplier;
    return baseline * multiplier;
}

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

static void TryGenerateRunnerPlots()
{
    var scriptPath = Path.Combine("benchmark-shm", "benchmark_runner.py");
    if (!File.Exists(scriptPath))
    {
        Console.WriteLine("Plot generation skipped: benchmark_runner.py not found.");
        return;
    }

    var pythonCandidates = new[]
    {
        Path.Combine(".venv", "Scripts", "python.exe"),
        "python",
        "py"
    };

    foreach (var candidate in pythonCandidates)
    {
        var args = candidate.Equals("py", StringComparison.OrdinalIgnoreCase)
            ? $"-3 \"{scriptPath}\" --plot-only"
            : $"\"{scriptPath}\" --plot-only";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = candidate,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                continue;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode == 0)
            {
                Console.WriteLine("Generated PNG benchmark plots via benchmark_runner.py.");
                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    Console.WriteLine(stdout.Trim());
                }
                return;
            }

            Console.WriteLine($"Plot generation attempt with '{candidate}' failed (exit {process.ExitCode}).");
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                Console.WriteLine(stderr.Trim());
            }
        }
        catch
        {
        }
    }

    Console.WriteLine("Plot generation skipped: no usable Python runtime found.");
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
