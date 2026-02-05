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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;
using Grpc.Testing;

namespace BenchmarkClient;

public enum TransportMode
{
    Tcp,
    SharedMemory,
    Both
}

public enum WorkloadMode
{
    Unary,
    Streaming,
    Both
}

public class BenchmarkConfig
{
    public TransportMode Transport { get; set; } = TransportMode.Both;
    public WorkloadMode Workload { get; set; } = WorkloadMode.Both;
    public string TcpAddress { get; set; } = "http://localhost:50051";
    public string ShmSegmentName { get; set; } = "grpc_bench";
    public int WarmupSeconds { get; set; } = 3;
    public int BenchmarkSeconds { get; set; } = 10;
    public int[] MessageSizes { get; set; } = { 1, 64, 1024, 4096, 16384, 65536, 262144, 1048576 };
    public int[] ConcurrencyLevels { get; set; } = { 1, 8, 64 };
    public string OutputFile { get; set; } = "results/benchmark_results.json";
}

public class BenchmarkResult
{
    public string Transport { get; set; } = "";
    public string Workload { get; set; } = "";
    public int MessageSize { get; set; }
    public int Concurrency { get; set; }
    public long TotalOps { get; set; }
    public double OpsPerSecond { get; set; }
    public double ThroughputMBps { get; set; }
    public double LatencyP50Us { get; set; }
    public double LatencyP90Us { get; set; }
    public double LatencyP99Us { get; set; }
    public double LatencyAvgUs { get; set; }
    public double LatencyMinUs { get; set; }
    public double LatencyMaxUs { get; set; }
    public double DurationSeconds { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class BenchmarkRunner
{
    private readonly BenchmarkConfig _config;
    private readonly List<BenchmarkResult> _results = new();
    private readonly ConcurrentBag<long> _latencies = new();

    public BenchmarkRunner(BenchmarkConfig config)
    {
        _config = config;
    }

    public async Task<List<BenchmarkResult>> RunAllAsync()
    {
        var transports = _config.Transport switch
        {
            TransportMode.Tcp => new[] { "tcp" },
            TransportMode.SharedMemory => new[] { "shm" },
            TransportMode.Both => new[] { "tcp", "shm" },
            _ => new[] { "tcp", "shm" }
        };

        var workloads = _config.Workload switch
        {
            WorkloadMode.Unary => new[] { "unary" },
            WorkloadMode.Streaming => new[] { "streaming" },
            WorkloadMode.Both => new[] { "unary", "streaming" },
            _ => new[] { "unary", "streaming" }
        };

        foreach (var transport in transports)
        {
            foreach (var workload in workloads)
            {
                foreach (var messageSize in _config.MessageSizes)
                {
                    foreach (var concurrency in _config.ConcurrencyLevels)
                    {
                        Console.WriteLine($"\n=== Running: {transport.ToUpper()} / {workload} / {messageSize}B / {concurrency} concurrent ===");
                        
                        try
                        {
                            var result = await RunBenchmarkAsync(transport, workload, messageSize, concurrency);
                            _results.Add(result);
                            PrintResult(result);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  ERROR: {ex.Message}");
                        }
                    }
                }
            }
        }

        return _results;
    }

    private async Task<BenchmarkResult> RunBenchmarkAsync(string transport, string workload, int messageSize, int concurrency)
    {
        _latencies.Clear();

        // Create channel based on transport
        GrpcChannel channel;
        HttpMessageHandler? shmHandler = null;
        if (transport == "shm")
        {
            shmHandler = new ShmControlHandler(_config.ShmSegmentName);
            channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
            {
                HttpHandler = shmHandler,
                DisposeHttpClient = true
            });
        }
        else
        {
            channel = GrpcChannel.ForAddress(_config.TcpAddress, new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true,
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5)
                },
                DisposeHttpClient = true
            });
        }

        var client = new BenchmarkService.BenchmarkServiceClient(channel);
        var payload = new byte[messageSize];
        Random.Shared.NextBytes(payload);

        // Warmup
        Console.Write($"  Warming up ({_config.WarmupSeconds}s)...");
        var warmupCts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.WarmupSeconds));
        try
        {
            if (workload == "unary")
            {
                await RunUnaryWarmupAsync(client, payload, warmupCts.Token);
            }
            else
            {
                await RunStreamingWarmupAsync(client, payload, warmupCts.Token);
            }
        }
        catch (OperationCanceledException) { }
        Console.WriteLine(" done");

        // Benchmark
        Console.Write($"  Benchmarking ({_config.BenchmarkSeconds}s)...");
        var benchmarkCts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.BenchmarkSeconds));
        var stopwatch = Stopwatch.StartNew();
        long totalOps = 0;

        try
        {
            if (workload == "unary")
            {
                totalOps = await RunUnaryBenchmarkAsync(client, payload, concurrency, benchmarkCts.Token);
            }
            else
            {
                totalOps = await RunStreamingBenchmarkAsync(client, payload, concurrency, benchmarkCts.Token);
            }
        }
        catch (OperationCanceledException) { }

        stopwatch.Stop();
        Console.WriteLine(" done");

        await channel.ShutdownAsync();
        channel.Dispose();
        
        // Ensure SHM handler is fully cleaned up
        if (shmHandler != null)
        {
            shmHandler.Dispose();
            await Task.Delay(50); // Give time for cleanup
        }

        // Calculate statistics
        var latenciesArray = _latencies.ToArray();
        Array.Sort(latenciesArray);
        
        var durationSeconds = stopwatch.Elapsed.TotalSeconds;
        var opsPerSecond = totalOps / durationSeconds;
        var throughputMBps = (totalOps * messageSize * 2) / (durationSeconds * 1024 * 1024); // *2 for req+resp

        return new BenchmarkResult
        {
            Transport = transport,
            Workload = workload,
            MessageSize = messageSize,
            Concurrency = concurrency,
            TotalOps = totalOps,
            OpsPerSecond = opsPerSecond,
            ThroughputMBps = throughputMBps,
            LatencyP50Us = GetPercentile(latenciesArray, 50) / 10.0, // Ticks to microseconds
            LatencyP90Us = GetPercentile(latenciesArray, 90) / 10.0,
            LatencyP99Us = GetPercentile(latenciesArray, 99) / 10.0,
            LatencyAvgUs = latenciesArray.Length > 0 ? latenciesArray.Average() / 10.0 : 0,
            LatencyMinUs = latenciesArray.Length > 0 ? latenciesArray.Min() / 10.0 : 0,
            LatencyMaxUs = latenciesArray.Length > 0 ? latenciesArray.Max() / 10.0 : 0,
            DurationSeconds = durationSeconds
        };
    }

    private async Task RunUnaryWarmupAsync(BenchmarkService.BenchmarkServiceClient client, byte[] payload, CancellationToken ct)
    {
        var request = CreateRequest(payload);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await client.UnaryCallAsync(request, cancellationToken: ct);
            }
            catch (RpcException) { }
        }
    }

    private async Task RunStreamingWarmupAsync(BenchmarkService.BenchmarkServiceClient client, byte[] payload, CancellationToken ct)
    {
        var request = CreateRequest(payload);
        using var call = client.StreamingCall(cancellationToken: ct);
        
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await call.RequestStream.WriteAsync(request, ct);
                await call.ResponseStream.MoveNext(ct);
            }
            catch (RpcException) { break; }
        }
    }

    private async Task<long> RunUnaryBenchmarkAsync(BenchmarkService.BenchmarkServiceClient client, byte[] payload, int concurrency, CancellationToken ct)
    {
        long totalOps = 0;
        var tasks = new Task[concurrency];
        var request = CreateRequest(payload);
        var errorCount = 0;

        for (int i = 0; i < concurrency; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        await client.UnaryCallAsync(request, cancellationToken: ct);
                        sw.Stop();
                        _latencies.Add(sw.ElapsedTicks);
                        Interlocked.Increment(ref totalOps);
                    }
                    catch (RpcException ex) 
                    { 
                        if (Interlocked.Increment(ref errorCount) <= 3)
                            Console.Error.WriteLine($"  RPC Error: {ex.StatusCode} - {ex.Status.Detail}");
                    }
                    catch (OperationCanceledException) { break; }
                }
            });
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { }

        return totalOps;
    }

    private async Task<long> RunStreamingBenchmarkAsync(BenchmarkService.BenchmarkServiceClient client, byte[] payload, int concurrency, CancellationToken ct)
    {
        long totalOps = 0;
        var tasks = new Task[concurrency];
        var request = CreateRequest(payload);

        for (int i = 0; i < concurrency; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                try
                {
                    using var call = client.StreamingCall(cancellationToken: ct);
                    
                    while (!ct.IsCancellationRequested)
                    {
                        var sw = Stopwatch.StartNew();
                        try
                        {
                            await call.RequestStream.WriteAsync(request, ct);
                            await call.ResponseStream.MoveNext(ct);
                            sw.Stop();
                            _latencies.Add(sw.ElapsedTicks);
                            Interlocked.Increment(ref totalOps);
                        }
                        catch (RpcException) { break; }
                        catch (OperationCanceledException) { break; }
                    }
                }
                catch (RpcException) { }
                catch (OperationCanceledException) { }
            });
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { }

        return totalOps;
    }

    private static SimpleRequest CreateRequest(byte[] payload)
    {
        return new SimpleRequest
        {
            ResponseType = PayloadType.Compressable,
            ResponseSize = payload.Length,
            Payload = new Payload
            {
                Type = PayloadType.Compressable,
                Body = Google.Protobuf.ByteString.CopyFrom(payload)
            }
        };
    }

    private static double GetPercentile(long[] sortedData, int percentile)
    {
        if (sortedData.Length == 0) return 0;
        var index = (int)Math.Ceiling(percentile / 100.0 * sortedData.Length) - 1;
        return sortedData[Math.Max(0, Math.Min(index, sortedData.Length - 1))];
    }

    private static void PrintResult(BenchmarkResult result)
    {
        Console.WriteLine($"  Ops: {result.TotalOps:N0} | {result.OpsPerSecond:N0} ops/s | {result.ThroughputMBps:N1} MB/s");
        Console.WriteLine($"  Latency: avg={result.LatencyAvgUs:N0}µs p50={result.LatencyP50Us:N0}µs p90={result.LatencyP90Us:N0}µs p99={result.LatencyP99Us:N0}µs");
    }
}

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var config = new BenchmarkConfig();

        // Parse command line arguments
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--transport":
                    config.Transport = args[++i].ToLower() switch
                    {
                        "tcp" => TransportMode.Tcp,
                        "shm" => TransportMode.SharedMemory,
                        _ => TransportMode.Both
                    };
                    break;
                case "--workload":
                    config.Workload = args[++i].ToLower() switch
                    {
                        "unary" => WorkloadMode.Unary,
                        "streaming" => WorkloadMode.Streaming,
                        _ => WorkloadMode.Both
                    };
                    break;
                case "--tcp-address":
                    config.TcpAddress = args[++i];
                    break;
                case "--shm-segment":
                    config.ShmSegmentName = args[++i];
                    break;
                case "--warmup":
                    config.WarmupSeconds = int.Parse(args[++i]);
                    break;
                case "--duration":
                    config.BenchmarkSeconds = int.Parse(args[++i]);
                    break;
                case "--sizes":
                    config.MessageSizes = args[++i].Split(',').Select(int.Parse).ToArray();
                    break;
                case "--concurrency":
                    config.ConcurrencyLevels = args[++i].Split(',').Select(int.Parse).ToArray();
                    break;
                case "--output":
                    config.OutputFile = args[++i];
                    break;
                case "--help":
                    PrintUsage();
                    return 0;
            }
        }

        Console.WriteLine("=== gRPC .NET Shared Memory Benchmark ===");
        Console.WriteLine($"Transport: {config.Transport}");
        Console.WriteLine($"Workload: {config.Workload}");
        Console.WriteLine($"Message sizes: {string.Join(", ", config.MessageSizes)}");
        Console.WriteLine($"Concurrency levels: {string.Join(", ", config.ConcurrencyLevels)}");
        Console.WriteLine($"Warmup: {config.WarmupSeconds}s, Duration: {config.BenchmarkSeconds}s");

        var runner = new BenchmarkRunner(config);
        var results = await runner.RunAllAsync();

        // Ensure output directory exists
        var outputDir = Path.GetDirectoryName(config.OutputFile);
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        // Write results to JSON
        var json = JsonSerializer.Serialize(results, new JsonSerializerOptions 
        { 
            WriteIndented = true 
        });
        await File.WriteAllTextAsync(config.OutputFile, json);
        Console.WriteLine($"\nResults written to: {config.OutputFile}");

        // Print summary
        PrintSummary(results);

        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(@"
gRPC .NET Shared Memory Benchmark Client

Usage: BenchmarkClient [options]

Options:
  --transport <tcp|shm|both>     Transport mode (default: both)
  --workload <unary|streaming|both>  Workload type (default: both)
  --tcp-address <url>            TCP server address (default: http://localhost:50051)
  --shm-segment <name>           Shared memory segment name (default: grpc_bench)
  --warmup <seconds>             Warmup duration (default: 3)
  --duration <seconds>           Benchmark duration per test (default: 10)
  --sizes <n1,n2,...>            Message sizes in bytes (default: 1,64,1024,4096,16384,65536,262144,1048576)
  --concurrency <n1,n2,...>      Concurrency levels (default: 1,8,64)
  --output <file>                Output JSON file (default: results/benchmark_results.json)
  --help                         Show this help message
");
    }

    private static void PrintSummary(List<BenchmarkResult> results)
    {
        Console.WriteLine("\n=== SUMMARY ===");
        Console.WriteLine($"{"Transport",-10} {"Workload",-12} {"Size",-10} {"Conc",-6} {"Ops/s",-12} {"MB/s",-10} {"P50 µs",-10} {"P99 µs",-10}");
        Console.WriteLine(new string('-', 90));

        foreach (var r in results)
        {
            var sizeStr = r.MessageSize >= 1024 * 1024 
                ? $"{r.MessageSize / (1024 * 1024)}MB"
                : r.MessageSize >= 1024 
                    ? $"{r.MessageSize / 1024}KB" 
                    : $"{r.MessageSize}B";
            
            Console.WriteLine($"{r.Transport,-10} {r.Workload,-12} {sizeStr,-10} {r.Concurrency,-6} {r.OpsPerSecond,-12:N0} {r.ThroughputMBps,-10:N1} {r.LatencyP50Us,-10:N0} {r.LatencyP99Us,-10:N0}");
        }

        // Calculate speedup for SHM vs TCP
        Console.WriteLine("\n=== SHM vs TCP Speedup ===");
        var tcpResults = results.Where(r => r.Transport == "tcp").ToList();
        var shmResults = results.Where(r => r.Transport == "shm").ToList();

        foreach (var shm in shmResults)
        {
            var tcp = tcpResults.FirstOrDefault(t => 
                t.Workload == shm.Workload && 
                t.MessageSize == shm.MessageSize && 
                t.Concurrency == shm.Concurrency);

            if (tcp != null)
            {
                var speedup = shm.OpsPerSecond / tcp.OpsPerSecond;
                var latencyRatio = tcp.LatencyP50Us / shm.LatencyP50Us;
                var sizeStr = shm.MessageSize >= 1024 * 1024 
                    ? $"{shm.MessageSize / (1024 * 1024)}MB"
                    : shm.MessageSize >= 1024 
                        ? $"{shm.MessageSize / 1024}KB" 
                        : $"{shm.MessageSize}B";
                
                Console.WriteLine($"{shm.Workload,-12} {sizeStr,-10} {shm.Concurrency,-6} concur: {speedup:N2}x throughput, {latencyRatio:N2}x lower latency");
            }
        }
    }
}
