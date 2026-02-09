// Transport-level benchmarks matching Go's internal/transport/shm_bench_test.go
// Compares: SHM ring buffer vs TCP loopback (raw socket)
// Output: JSON results file + console summary
//
// Go equivalents:
//   BenchmarkShmRingWriteRead      → ShmRingWriteRead
//   BenchmarkShmRingRoundtrip      → ShmRingRoundtrip
//   BenchmarkShmRingLargePayloads  → ShmRingLargePayloads
//   BenchmarkTCPLoopback           → TCPLoopback
//   BenchmarkTCPLoopbackRoundtrip  → TCPLoopbackRoundtrip
//   BenchmarkTCPLargePayloads      → TCPLargePayloads

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Grpc.Net.SharedMemory;

sealed class RingBench
{
    // Match Go benchmark sizes exactly
    static readonly int[] StreamingSizes = { 64, 256, 1024, 4096, 16384, 65536 };
    static readonly int[] RoundtripSizes = { 64, 256, 1024, 4096 };
    static readonly int[] LargePayloadSizes = { 65536, 262144, 1048576 };

    static readonly Dictionary<string, BenchResult> AllResults = new();

    sealed record BenchResult(long Ops, double NsPerOp, double MBps);

    static void Main(string[] args)
    {
        string outputFile = "results/ringbench_results.json";
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--output" && i + 1 < args.Length)
                outputFile = args[++i];
        }

        // Clean up stale segments
        foreach (var f in Directory.GetFiles("/dev/shm/", "grpc_shm_ringbench*"))
            File.Delete(f);

        // 4 MB ring capacity (fits in /dev/shm, large enough for 1MB messages)
        ulong ringCapacity = 4 * 1024 * 1024;
        var segmentName = "ringbench";

        var cpu = GetCpuInfo();
        Console.WriteLine($"CPU: {cpu}");
        Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"Ring capacity: {ringCapacity / 1024 / 1024} MB");
        Console.WriteLine();

        var segment = Segment.Create(segmentName, ringCapacity);
        try
        {
            // === SHM Ring Write+Read (one-way) ===
            Console.WriteLine("=== SHM Ring Write+Read (= Go BenchmarkShmRingWriteRead) ===");
            PrintTableHeader();
            foreach (var size in StreamingSizes)
            {
                if ((ulong)size > ringCapacity / 2) continue;
                var r = BenchShmWriteRead(segment.RingA, size);
                AllResults[$"ShmRingWriteRead/size={size}"] = r;
                PrintRow(size, r);
            }
            Console.WriteLine();

            // === SHM Ring Roundtrip ===
            Console.WriteLine("=== SHM Ring Roundtrip (= Go BenchmarkShmRingRoundtrip) ===");
            PrintTableHeader();
            foreach (var size in RoundtripSizes)
            {
                var r = BenchShmWriteRead(segment.RingA, size);
                AllResults[$"ShmRingRoundtrip/size={size}"] = r;
                PrintRow(size, r);
            }
            Console.WriteLine();

            // === SHM Large Payload ===
            Console.WriteLine("=== SHM Large Payloads (= Go BenchmarkShmRingLargePayloads) ===");
            PrintTableHeader();
            foreach (var size in LargePayloadSizes)
            {
                if ((ulong)size > ringCapacity / 2) continue;
                var r = BenchShmLargePayload(segment.RingA, size);
                AllResults[$"ShmRingLargePayloads/size={size}"] = r;
                PrintRow(size, r);
            }
            Console.WriteLine();

            // === SHM Large Payload Roundtrip ===
            Console.WriteLine("=== SHM Large Payloads Roundtrip (= Go BenchmarkShmRingLargePayloadsRoundtrip) ===");
            PrintTableHeader();
            foreach (var size in LargePayloadSizes)
            {
                if ((ulong)size > ringCapacity / 2) continue;
                var r = BenchShmLargePayload(segment.RingA, size);
                AllResults[$"ShmRingLargePayloadsRoundtrip/size={size}"] = r;
                PrintRow(size, r);
            }
            Console.WriteLine();

            // === TCP Loopback (one-way) ===
            Console.WriteLine("=== TCP Loopback (= Go BenchmarkTCPLoopback) ===");
            PrintTableHeader();
            foreach (var size in StreamingSizes)
            {
                var r = BenchTcpOneWay(size);
                AllResults[$"TCPLoopback/size={size}"] = r;
                PrintRow(size, r);
            }
            Console.WriteLine();

            // === TCP Roundtrip ===
            Console.WriteLine("=== TCP Roundtrip (= Go BenchmarkTCPLoopbackRoundtrip) ===");
            PrintTableHeader();
            foreach (var size in RoundtripSizes)
            {
                var r = BenchTcpRoundtrip(size);
                AllResults[$"TCPLoopbackRoundtrip/size={size}"] = r;
                PrintRow(size, r);
            }
            Console.WriteLine();

            // === TCP Large Payload ===
            Console.WriteLine("=== TCP Large Payloads (= Go BenchmarkTCPLargePayloads) ===");
            PrintTableHeader();
            foreach (var size in LargePayloadSizes)
            {
                var r = BenchTcpOneWay(size);
                AllResults[$"TCPLargePayloads/size={size}"] = r;
                PrintRow(size, r);
            }
            Console.WriteLine();

            // === TCP Large Payload Roundtrip ===
            Console.WriteLine("=== TCP Large Payloads Roundtrip (= Go BenchmarkTCPLargePayloadsRoundtrip) ===");
            PrintTableHeader();
            foreach (var size in LargePayloadSizes)
            {
                var r = BenchTcpRoundtrip(size);
                AllResults[$"TCPLargePayloadsRoundtrip/size={size}"] = r;
                PrintRow(size, r);
            }
            Console.WriteLine();

            // Write JSON results
            var jsonObj = new
            {
                timestamp = DateTime.UtcNow.ToString("o"),
                cpu,
                runtime = RuntimeInformation.FrameworkDescription,
                ring_capacity_mb = ringCapacity / 1024 / 1024,
                benchmarks = AllResults.ToDictionary(
                    kv => kv.Key,
                    kv => new { ns_per_op = Math.Round(kv.Value.NsPerOp, 1), mb_per_s = Math.Round(kv.Value.MBps, 2), ops = kv.Value.Ops }
                )
            };

            var dir = Path.GetDirectoryName(Path.GetFullPath(outputFile));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(outputFile, JsonSerializer.Serialize(jsonObj, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Results written to: {outputFile}");
        }
        finally
        {
            segment.Dispose();
            foreach (var f in Directory.GetFiles("/dev/shm/", "grpc_shm_ringbench*"))
                File.Delete(f);
        }
    }

    // ========================================================================
    // SHM Ring Benchmarks
    // ========================================================================

    static BenchResult BenchShmWriteRead(ShmRing ring, int size)
    {
        var payload = new byte[size];
        Random.Shared.NextBytes(payload);
        var readBuf = new byte[size];

        // Warmup
        for (int i = 0; i < Math.Min(1000, 100_000_000 / Math.Max(size, 1)); i++)
        {
            ring.Write(payload);
            ring.Read(readBuf);
        }

        // Calibrate iteration count (target ~2s)
        var sw = Stopwatch.StartNew();
        int calibOps = 0;
        while (sw.ElapsedMilliseconds < 200)
        {
            ring.Write(payload);
            ring.Read(readBuf);
            calibOps++;
        }
        sw.Stop();
        double nsPerCalib = (double)sw.ElapsedTicks / calibOps * 1_000_000_000.0 / Stopwatch.Frequency;
        long iterations = Math.Max(100, (long)(2_000_000_000.0 / nsPerCalib));

        // Run 3 times, take best
        double bestNs = double.MaxValue;
        for (int run = 0; run < 3; run++)
        {
            sw.Restart();
            for (long i = 0; i < iterations; i++)
            {
                ring.Write(payload);
                ring.Read(readBuf);
            }
            sw.Stop();
            double ns = (double)sw.ElapsedTicks / iterations * 1_000_000_000.0 / Stopwatch.Frequency;
            if (ns < bestNs) bestNs = ns;
        }

        double mbps = size / bestNs * 1000.0;
        return new BenchResult(iterations, bestNs, mbps);
    }

    static BenchResult BenchShmLargePayload(ShmRing ring, int size)
    {
        var payload = new byte[size];
        Random.Shared.NextBytes(payload);
        var readBuf = new byte[size];

        // Warmup
        for (int i = 0; i < 100; i++)
        {
            ring.Write(payload);
            ring.Read(readBuf);
        }

        // Calibrate
        var sw = Stopwatch.StartNew();
        int calibOps = 0;
        while (sw.ElapsedMilliseconds < 200)
        {
            ring.Write(payload);
            ring.Read(readBuf);
            calibOps++;
        }
        sw.Stop();
        double nsPerCalib = (double)sw.ElapsedTicks / calibOps * 1_000_000_000.0 / Stopwatch.Frequency;
        long iterations = Math.Max(50, (long)(2_000_000_000.0 / nsPerCalib));

        // Batch write/read for throughput (like Go)
        int batchSize = Math.Min(16, (int)((long)ring.Capacity / size / 2));
        if (batchSize < 1) batchSize = 1;

        double bestNs = double.MaxValue;
        long bestOps = iterations;
        for (int run = 0; run < 3; run++)
        {
            long totalOps = 0;
            sw.Restart();
            while (totalOps < iterations)
            {
                int batch = (int)Math.Min(batchSize, iterations - totalOps);
                for (int i = 0; i < batch; i++)
                    ring.Write(payload);
                for (int i = 0; i < batch; i++)
                    ring.Read(readBuf);
                totalOps += batch;
            }
            sw.Stop();
            double ns = (double)sw.ElapsedTicks / totalOps * 1_000_000_000.0 / Stopwatch.Frequency;
            if (ns < bestNs)
            {
                bestNs = ns;
                bestOps = totalOps;
            }
        }

        double mbps = size / bestNs * 1000.0;
        return new BenchResult(bestOps, bestNs, mbps);
    }

    // ========================================================================
    // TCP Loopback Benchmarks (matching Go's BenchmarkTCPLoopback*)
    // ========================================================================

    static BenchResult BenchTcpOneWay(int size)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var data = new byte[size];
        Random.Shared.NextBytes(data);
        var recvBuf = new byte[size];

        // Calibrate
        long iterations;
        using (var calibClient = new TcpClient())
        {
            calibClient.NoDelay = true;
            calibClient.Connect(IPAddress.Loopback, port);
            var calibConn = listener.AcceptTcpClient();
            calibConn.NoDelay = true;
            var serverStream = calibConn.GetStream();
            var clientStream = calibClient.GetStream();

            var serverDone = new ManualResetEventSlim(false);
            var serverThread = new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        int read = 0;
                        while (read < size)
                        {
                            int n = serverStream.Read(recvBuf, read, size - read);
                            if (n == 0) return;
                            read += n;
                        }
                    }
                }
                catch { }
                finally { serverDone.Set(); }
            });
            serverThread.IsBackground = true;
            serverThread.Start();

            var sw = Stopwatch.StartNew();
            int calibOps = 0;
            while (sw.ElapsedMilliseconds < 200)
            {
                clientStream.Write(data, 0, size);
                calibOps++;
            }
            sw.Stop();

            calibClient.Close();
            calibConn.Close();
            serverDone.Wait(1000);

            double nsPerCalib = (double)sw.ElapsedTicks / calibOps * 1_000_000_000.0 / Stopwatch.Frequency;
            iterations = Math.Max(100, (long)(2_000_000_000.0 / nsPerCalib));
        }

        // Benchmark: 3 runs, take best
        double bestNs = double.MaxValue;
        for (int run = 0; run < 3; run++)
        {
            using var client = new TcpClient();
            client.NoDelay = true;
            client.Connect(IPAddress.Loopback, port);
            var serverConn = listener.AcceptTcpClient();
            serverConn.NoDelay = true;
            var serverStream = serverConn.GetStream();
            var clientStream = client.GetStream();

            var serverDone = new ManualResetEventSlim(false);
            var serverThread = new Thread(() =>
            {
                try
                {
                    for (long i = 0; i < iterations; i++)
                    {
                        int read = 0;
                        while (read < size)
                        {
                            int n = serverStream.Read(recvBuf, read, size - read);
                            if (n == 0) return;
                            read += n;
                        }
                    }
                }
                catch { }
                finally { serverDone.Set(); }
            });
            serverThread.IsBackground = true;
            serverThread.Start();

            Thread.Sleep(10);

            var sw = Stopwatch.StartNew();
            for (long i = 0; i < iterations; i++)
            {
                clientStream.Write(data, 0, size);
            }
            sw.Stop();

            serverDone.Wait(5000);
            client.Close();
            serverConn.Close();

            double ns = (double)sw.ElapsedTicks / iterations * 1_000_000_000.0 / Stopwatch.Frequency;
            if (ns < bestNs) bestNs = ns;
        }

        listener.Stop();
        double mbps = size / bestNs * 1000.0;
        return new BenchResult(iterations, bestNs, mbps);
    }

    static BenchResult BenchTcpRoundtrip(int size)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var data = new byte[size];
        Random.Shared.NextBytes(data);
        var recvBuf = new byte[size];

        // Calibrate
        long iterations;
        using (var calibClient = new TcpClient())
        {
            calibClient.NoDelay = true;
            calibClient.Connect(IPAddress.Loopback, port);
            var calibConn = listener.AcceptTcpClient();
            calibConn.NoDelay = true;
            var serverStream = calibConn.GetStream();
            var clientStream = calibClient.GetStream();

            var echoThread = new Thread(() =>
            {
                var buf = new byte[size];
                try
                {
                    while (true)
                    {
                        int read = 0;
                        while (read < size)
                        {
                            int n = serverStream.Read(buf, read, size - read);
                            if (n == 0) return;
                            read += n;
                        }
                        serverStream.Write(buf, 0, size);
                    }
                }
                catch { }
            });
            echoThread.IsBackground = true;
            echoThread.Start();

            var sw = Stopwatch.StartNew();
            int calibOps = 0;
            while (sw.ElapsedMilliseconds < 200)
            {
                clientStream.Write(data, 0, size);
                int read = 0;
                while (read < size)
                {
                    int n = clientStream.Read(recvBuf, read, size - read);
                    if (n == 0) break;
                    read += n;
                }
                calibOps++;
            }
            sw.Stop();

            calibClient.Close();
            calibConn.Close();

            double nsPerCalib = (double)sw.ElapsedTicks / calibOps * 1_000_000_000.0 / Stopwatch.Frequency;
            iterations = Math.Max(100, (long)(2_000_000_000.0 / nsPerCalib));
        }

        // Benchmark: 3 runs, take best
        double bestNs = double.MaxValue;
        for (int run = 0; run < 3; run++)
        {
            using var client = new TcpClient();
            client.NoDelay = true;
            client.Connect(IPAddress.Loopback, port);
            var serverConn = listener.AcceptTcpClient();
            serverConn.NoDelay = true;
            var serverStream = serverConn.GetStream();
            var clientStream = client.GetStream();

            var echoErr = new ManualResetEventSlim(false);
            var echoThread = new Thread(() =>
            {
                var buf = new byte[size];
                try
                {
                    for (long i = 0; i < iterations; i++)
                    {
                        int read = 0;
                        while (read < size)
                        {
                            int n = serverStream.Read(buf, read, size - read);
                            if (n == 0) return;
                            read += n;
                        }
                        serverStream.Write(buf, 0, size);
                    }
                }
                catch { }
                finally { echoErr.Set(); }
            });
            echoThread.IsBackground = true;
            echoThread.Start();

            Thread.Sleep(10);

            var sw = Stopwatch.StartNew();
            for (long i = 0; i < iterations; i++)
            {
                clientStream.Write(data, 0, size);
                int read = 0;
                while (read < size)
                {
                    int n = clientStream.Read(recvBuf, read, size - read);
                    if (n == 0) break;
                    read += n;
                }
            }
            sw.Stop();

            echoErr.Wait(5000);
            client.Close();
            serverConn.Close();

            double ns = (double)sw.ElapsedTicks / iterations * 1_000_000_000.0 / Stopwatch.Frequency;
            if (ns < bestNs) bestNs = ns;
        }

        listener.Stop();
        double mbps = (size * 2.0) / bestNs * 1000.0; // bidirectional
        return new BenchResult(iterations, bestNs, mbps);
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    static void PrintTableHeader()
    {
        Console.WriteLine($"{"Size",-10} {"Ops",-12} {"ns/op",-15} {"MB/s",-15}");
        Console.WriteLine(new string('-', 52));
    }

    static void PrintRow(int size, BenchResult r)
    {
        Console.WriteLine($"{FormatSize(size),-10} {r.Ops,-12} {r.NsPerOp,-15:F1} {r.MBps,-15:F2}");
    }

    static string FormatSize(int bytes)
    {
        if (bytes >= 1048576) return $"{bytes / 1048576}MB";
        if (bytes >= 1024) return $"{bytes / 1024}KB";
        return $"{bytes}B";
    }

    static string GetCpuInfo()
    {
        try
        {
            var lines = File.ReadAllLines("/proc/cpuinfo");
            foreach (var line in lines)
                if (line.StartsWith("model name"))
                    return line.Split(':')[1].Trim();
        }
        catch { }
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Unknown";
    }
}
