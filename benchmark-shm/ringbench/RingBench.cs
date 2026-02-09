// Transport-level benchmarks matching Go's internal/transport/shm_bench_test.go
// Compares: SHM ring buffer vs TCP loopback (raw socket)
// Output: JSON results file + console summary
//
// IMPORTANT: All SHM benchmarks use CROSS-THREAD patterns matching Go exactly:
//   - Streaming: writer thread + reader thread on same ring (concurrent)
//   - Roundtrip: two rings + echo thread (ping-pong pattern)
//
// Go equivalents:
//   BenchmarkShmRingWriteRead      → ShmRingWriteRead      (writer+reader goroutines, 1 ring)
//   BenchmarkShmRingRoundtrip      → ShmRingRoundtrip      (echo goroutine, 2 rings)
//   BenchmarkShmRingLargePayloads  → ShmRingLargePayloads  (writer+reader goroutines, 1 ring)
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

        var cpu = GetCpuInfo();
        Console.WriteLine($"CPU: {cpu}");
        Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"Ring capacity: {ringCapacity / 1024 / 1024} MB");
        Console.WriteLine();

        // === SHM Ring Write+Read (one-way, cross-thread) ===
        // Go: writer goroutine + reader goroutine on SAME ring, concurrent
        Console.WriteLine("=== SHM Ring Write+Read (= Go BenchmarkShmRingWriteRead) ===");
        Console.WriteLine("    [cross-thread: writer thread + reader thread on same ring]");
        PrintTableHeader();
        foreach (var size in StreamingSizes)
        {
            if ((ulong)size > ringCapacity / 2) continue;
            using var seg = Segment.Create($"ringbench_wr_{size}", ringCapacity);
            var r = BenchShmWriteRead(seg.RingA, size);
            AllResults[$"ShmRingWriteRead/size={size}"] = r;
            PrintRow(size, r);
        }
        Console.WriteLine();

        // === SHM Ring Roundtrip (cross-thread, 2 rings) ===
        // Go: echo goroutine on 2 rings (clientToServer + serverToClient)
        Console.WriteLine("=== SHM Ring Roundtrip (= Go BenchmarkShmRingRoundtrip) ===");
        Console.WriteLine("    [cross-thread: 2 rings, echo thread, client writes→reads]");
        PrintTableHeader();
        foreach (var size in RoundtripSizes)
        {
            using var seg = Segment.Create($"ringbench_rt_{size}", ringCapacity);
            var r = BenchShmRoundtrip(seg.RingA, seg.RingB, size);
            AllResults[$"ShmRingRoundtrip/size={size}"] = r;
            PrintRow(size, r);
        }
        Console.WriteLine();

        // === SHM Large Payload (cross-thread) ===
        // Go: writer goroutine ReserveWrite + reader goroutine ReadSlices, same ring
        Console.WriteLine("=== SHM Large Payloads (= Go BenchmarkShmRingLargePayloads) ===");
        Console.WriteLine("    [cross-thread: writer thread + reader thread on same ring]");
        PrintTableHeader();
        foreach (var size in LargePayloadSizes)
        {
            if ((ulong)size > ringCapacity / 2) continue;
            using var seg = Segment.Create($"ringbench_lp_{size}", ringCapacity);
            var r = BenchShmWriteRead(seg.RingA, size);
            AllResults[$"ShmRingLargePayloads/size={size}"] = r;
            PrintRow(size, r);
        }
        Console.WriteLine();

        // === SHM Large Payload Roundtrip (cross-thread, 2 rings) ===
        // Go: echo goroutine, ReadBlockingContext + WriteAll, 2 rings
        Console.WriteLine("=== SHM Large Payloads Roundtrip (= Go BenchmarkShmRingLargePayloadsRoundtrip) ===");
        Console.WriteLine("    [cross-thread: 2 rings, echo thread, chunked transfer]");
        PrintTableHeader();
        foreach (var size in LargePayloadSizes)
        {
            if ((ulong)size > ringCapacity / 2) continue;
            using var seg = Segment.Create($"ringbench_lprt_{size}", ringCapacity);
            var r = BenchShmRoundtrip(seg.RingA, seg.RingB, size);
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

        // Clean up stale segments
        foreach (var f in Directory.GetFiles("/dev/shm/", "grpc_shm_ringbench*"))
            File.Delete(f);
    }

    // ========================================================================
    // SHM Ring Benchmarks — cross-thread matching Go exactly
    // ========================================================================

    /// <summary>
    /// Cross-thread streaming benchmark matching Go BenchmarkShmRingWriteRead.
    /// Writer thread writes N messages, reader thread reads N messages, SAME ring,
    /// running concurrently. Measures throughput including cross-core sync overhead.
    /// </summary>
    static BenchResult BenchShmWriteRead(ShmRing ring, int size)
    {
        var payload = new byte[size];
        Random.Shared.NextBytes(payload);

        // Warmup with cross-thread pattern
        int warmupCount = Math.Min(1000, 100_000_000 / Math.Max(size, 1));
        RunCrossThreadWriteRead(ring, payload, size, warmupCount);

        // Calibrate iteration count (target ~2s)
        var sw = Stopwatch.StartNew();
        int calibOps = 0;
        while (sw.ElapsedMilliseconds < 300)
        {
            RunCrossThreadWriteRead(ring, payload, size, 100);
            calibOps += 100;
        }
        sw.Stop();
        double nsPerCalib = (double)sw.ElapsedTicks / calibOps * 1_000_000_000.0 / Stopwatch.Frequency;
        long iterations = Math.Max(100, (long)(2_000_000_000.0 / nsPerCalib));

        // Run 3 times, take best
        double bestNs = double.MaxValue;
        for (int run = 0; run < 3; run++)
        {
            double ns = RunCrossThreadWriteRead(ring, payload, size, iterations);
            if (ns < bestNs) bestNs = ns;
        }

        double mbps = size / bestNs * 1000.0;
        return new BenchResult(iterations, bestNs, mbps);
    }

    /// <summary>
    /// Runs writer thread + reader thread on the SAME ring concurrently.
    /// Returns ns/op (total time / ops).
    /// Matches Go BenchmarkShmRingWriteRead: separate goroutines for write and read.
    /// </summary>
    static double RunCrossThreadWriteRead(ShmRing ring, byte[] payload, int size, long iterations)
    {
        var readBuf = new byte[size];
        Exception? writerError = null;
        Exception? readerError = null;

        var readerReady = new ManualResetEventSlim(false);
        var startGun = new ManualResetEventSlim(false);

        // Reader thread
        var readerThread = new Thread(() =>
        {
            try
            {
                readerReady.Set();
                startGun.Wait();
                for (long i = 0; i < iterations; i++)
                {
                    ring.Read(readBuf);
                }
            }
            catch (Exception ex) { readerError = ex; }
        });
        readerThread.IsBackground = true;
        readerThread.Start();

        // Wait for reader to be ready
        readerReady.Wait();

        // Writer thread (this thread) — start timing when both are ready
        var sw = Stopwatch.StartNew();
        startGun.Set(); // release reader

        try
        {
            for (long i = 0; i < iterations; i++)
            {
                ring.Write(payload);
            }
        }
        catch (Exception ex) { writerError = ex; }

        readerThread.Join();
        sw.Stop();

        if (writerError != null) throw writerError;
        if (readerError != null) throw readerError;

        return (double)sw.ElapsedTicks / iterations * 1_000_000_000.0 / Stopwatch.Frequency;
    }

    /// <summary>
    /// Cross-thread roundtrip benchmark matching Go BenchmarkShmRingRoundtrip.
    /// Uses TWO rings (clientToServer + serverToClient) with an echo thread.
    /// Client writes to ringA, echo thread reads from ringA and writes to ringB,
    /// client reads from ringB. Measures full cross-thread ping-pong latency.
    /// </summary>
    static BenchResult BenchShmRoundtrip(ShmRing clientToServer, ShmRing serverToClient, int size)
    {
        var payload = new byte[size];
        Random.Shared.NextBytes(payload);

        // Warmup
        int warmupCount = Math.Min(500, 50_000_000 / Math.Max(size, 1));
        RunCrossThreadRoundtrip(clientToServer, serverToClient, payload, size, warmupCount);

        // Calibrate
        var sw = Stopwatch.StartNew();
        int calibOps = 0;
        while (sw.ElapsedMilliseconds < 300)
        {
            RunCrossThreadRoundtrip(clientToServer, serverToClient, payload, size, 50);
            calibOps += 50;
        }
        sw.Stop();
        double nsPerCalib = (double)sw.ElapsedTicks / calibOps * 1_000_000_000.0 / Stopwatch.Frequency;
        long iterations = Math.Max(100, (long)(2_000_000_000.0 / nsPerCalib));

        // Run 3 times, take best
        double bestNs = double.MaxValue;
        for (int run = 0; run < 3; run++)
        {
            double ns = RunCrossThreadRoundtrip(clientToServer, serverToClient, payload, size, iterations);
            if (ns < bestNs) bestNs = ns;
        }

        double mbps = (size * 2.0) / bestNs * 1000.0; // bidirectional
        return new BenchResult(iterations, bestNs, mbps);
    }

    /// <summary>
    /// Runs echo thread + client thread on TWO rings.
    /// Echo: reads from clientToServer, writes to serverToClient.
    /// Client: writes to clientToServer, reads from serverToClient.
    /// Returns ns/op. Matches Go BenchmarkShmRingRoundtrip exactly.
    /// </summary>
    static double RunCrossThreadRoundtrip(ShmRing clientToServer, ShmRing serverToClient,
        byte[] payload, int size, long iterations)
    {
        var readBuf = new byte[size];
        var echoBuf = new byte[size];
        Exception? echoError = null;

        var echoReady = new ManualResetEventSlim(false);
        var startGun = new ManualResetEventSlim(false);

        // Echo server thread (matches Go echo goroutine)
        var echoThread = new Thread(() =>
        {
            try
            {
                echoReady.Set();
                startGun.Wait();
                for (long i = 0; i < iterations; i++)
                {
                    // Read from client
                    clientToServer.Read(echoBuf);
                    // Echo back to client
                    serverToClient.Write(echoBuf);
                }
            }
            catch (Exception ex) { echoError = ex; }
        });
        echoThread.IsBackground = true;
        echoThread.Start();

        echoReady.Wait();

        // Client thread — timed
        var sw = Stopwatch.StartNew();
        startGun.Set();

        for (long i = 0; i < iterations; i++)
        {
            // Write to server
            clientToServer.Write(payload);
            // Read echo response
            serverToClient.Read(readBuf);
        }

        sw.Stop();
        echoThread.Join();

        if (echoError != null) throw echoError;

        return (double)sw.ElapsedTicks / iterations * 1_000_000_000.0 / Stopwatch.Frequency;
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
