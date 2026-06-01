// Standalone profile target: in-process Fair 16K Unary loop with a single
// SHM client+server pair. Designed to be CPU-sampled with PerfView /Process:<PID>
// so the resulting flamegraph reflects the per-RPC hot path cleanly.
//
// Run for profiling:
//   $env:SHM_FAIR_MAX_FRAME='16384'
//   $env:SHM_INITIAL_WINDOW='65535'
//   $proc = Start-Process -FilePath dotnet -ArgumentList @(
//       'benchmark-shm/probefair16k/bin/Release/net10.0/ProbeFair16K.dll',
//       '--iters', '500000') -PassThru
//   Start-Sleep 6  # warmup
//   & PerfView.exe /AcceptEula /NoGui /MaxCollectSec:20 /CircularMB:1024 `
//                  /Merge:true /Zip:true /Process:$($proc.Id) `
//                  collect C:\tmp\prof16k
//   Wait-Process -Id $proc.Id

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;

int iters = 500_000;
int payloadSize = 16384;
string mode = "inproc";
string segmentName = $"probe16k_{Guid.NewGuid():N}";
bool useTimeoutWrapper = false; // mimic RingBench's Task.WhenAny(call, Task.Delay) pattern
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--iters" && i + 1 < args.Length) iters = int.Parse(args[++i]);
    if (args[i] == "--size"  && i + 1 < args.Length) payloadSize = int.Parse(args[++i]);
    if (args[i] == "--mode"  && i + 1 < args.Length) mode = args[++i];  // inproc | server | client
    if (args[i] == "--segment" && i + 1 < args.Length) segmentName = args[++i];
    if (args[i] == "--with-timeout-wrapper") useTimeoutWrapper = true;
}

Console.WriteLine($"ProbeFair16K: PID={Environment.ProcessId} mode={mode} iters={iters} size={payloadSize} segment={segmentName}");
Console.WriteLine($"SHM_FAIR_MAX_FRAME={Environment.GetEnvironmentVariable("SHM_FAIR_MAX_FRAME")}");
Console.WriteLine($"SHM_INITIAL_WINDOW={Environment.GetEnvironmentVariable("SHM_INITIAL_WINDOW")}");
Console.WriteLine($"SHM_WIN_ALLOW_SPIN={Environment.GetEnvironmentVariable("SHM_WIN_ALLOW_SPIN")}");
Console.WriteLine($"SHM_RECEIVE_STRIPER={Environment.GetEnvironmentVariable("SHM_RECEIVE_STRIPER")}");

if (mode == "server")
{
    await RunServer();
    return;
}

ShmGrpcServer? localServer = null;
Task? localServerTask = null;
CancellationTokenSource? localServerCts = null;
if (mode == "inproc")
{
    localServer = new ShmGrpcServer(segmentName, singleStreamMode: true);
    localServer.MapUnary<BytesValue, BytesValue>(
        "/test.Echo/Unary",
        (req, _) => Task.FromResult(new BytesValue { Value = req.Value }));
    localServerCts = new CancellationTokenSource();
    localServerTask = Task.Run(() => localServer.RunAsync(localServerCts.Token));
}

// Wait for server control segment.
var deadline = DateTime.UtcNow.AddSeconds(30);
while (DateTime.UtcNow < deadline)
{
    try { using var _ = Segment.OpenControlSegment(segmentName); break; }
    catch (FileNotFoundException) { await Task.Delay(50); }
}

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

var body = new byte[payloadSize];
new Random(payloadSize).NextBytes(body);
var req = new BytesValue { Value = ByteString.CopyFrom(body) };

// Warmup
Console.WriteLine("Warmup 2000 calls...");
for (int i = 0; i < 2000; i++)
    _ = await invoker.AsyncUnaryCall(method, host: null, new CallOptions(), req);

Console.WriteLine($"Ready for profiling. Starting {iters} iterations... (timeoutWrapper={useTimeoutWrapper})");
Console.Out.Flush();

var stepTimeout = TimeSpan.FromSeconds(600);
var sw = Stopwatch.StartNew();
if (useTimeoutWrapper)
{
    for (int i = 0; i < iters; i++)
    {
        var call = invoker.AsyncUnaryCall(method, host: null, new CallOptions(), req);
        var respTask = call.ResponseAsync;
        var completed = await Task.WhenAny(respTask, Task.Delay(stepTimeout)).ConfigureAwait(false);
        if (completed != respTask) throw new TimeoutException("RPC timed out");
        var resp = await respTask.ConfigureAwait(false);
        if (resp.Value.Length != payloadSize) throw new Exception("size mismatch");
    }
}
else
{
    for (int i = 0; i < iters; i++)
    {
        var resp = await invoker.AsyncUnaryCall(method, host: null, new CallOptions(), req);
        if (resp.Value.Length != payloadSize) throw new Exception("size mismatch");
    }
}
sw.Stop();

var perRpc = sw.Elapsed.TotalMicroseconds / iters;
Console.WriteLine($"Done. iters={iters} elapsed={sw.Elapsed.TotalSeconds:F2}s mean={perRpc:F2} us/RPC");

if (localServerCts != null) localServerCts.Cancel();
if (localServerTask != null) try { await localServerTask; } catch { }
if (localServer != null) await localServer.DisposeAsync();

async Task RunServer()
{
    var srv = new ShmGrpcServer(segmentName, singleStreamMode: true);
    srv.MapUnary<BytesValue, BytesValue>(
        "/test.Echo/Unary",
        (req, _) => Task.FromResult(new BytesValue { Value = req.Value }));
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    Console.WriteLine($"Server running. Segment={segmentName}. Ctrl+C to stop.");
    Console.Out.Flush();
    await srv.RunAsync(cts.Token);
    await srv.DisposeAsync();
}
