# Current Status — Benchmark Rewrite & SHM Bug Fixes

**Branch:** `sharedmemory`  
**Previous HEAD:** `0fd1e813` (transport-level benchmarks)  
**Date:** 2025-07-15  

## What Was Done

### 1. Benchmark Rewrite (Complete)
Rewrote the benchmark harness from raw transport-level tests to **actual gRPC client/server benchmarks** — exactly how an app would use gRPC:

- **Removed**: Named pipe transport entirely, raw transport-level ring buffer benchmarks
- **Added**: Real gRPC `UnaryCallAsync` and bidirectional streaming benchmarks over TCP and SHM
- **Created** `benchmark-shm/ringbench/Proto/benchmark.proto` with `BenchmarkService` (UnaryCall + StreamingCall RPCs)
- **Rewrote** `benchmark-shm/ringbench/RingBench.cs` (~400 lines):
  - TCP: ASP.NET Core Kestrel HTTP/2 h2c → `MapGrpcService<BenchmarkServiceImpl>()`
  - SHM: `ShmGrpcServer` with `MapUnary`/`MapDuplexStreaming`
  - Payload sizes and iteration counts match Go reference implementation exactly
  - Outputs: JSON, CSV, 3 SVG plots
- **Rewrote** `benchmark-shm/benchmark_runner.py` for the new 2-transport JSON format
- **Fixed** CA1852 build errors (added `sealed` to `BenchResult`, `PlotPoint`, `PlotSeries`, `BenchmarkServiceImpl`, `BenchEnv`)

### 2. SHM Flow Control Fix (Complete)
- **Bug**: `InitialWindowSize = 65535` (HTTP/2 TCP default) caused **deadlock at 64KB payloads** because 65536 > 65535 by 1 byte
- **Fix**: Changed to `16 * 1024 * 1024` (16 MiB) in `src/Grpc.Net.SharedMemory/FrameTypes.cs` — SHM is local high-bandwidth, doesn't need TCP's conservative default
- **Updated** `test/Grpc.Net.SharedMemory.Tests/FlowControlTests.cs` to match (renamed test, updated assertions)

### 3. SHM Stack Overflow Fix (In Progress — UNTESTED)
- **Bug**: `ShmConnection.FrameReaderLoopAsync` does `await Task.Run(() => ReadFrame(...))` in a while loop. When `ReadFrame` returns quickly (SHM is fast), TPL inlines continuations, causing `MoveNext()` to recurse and overflow the stack. Confirmed via Windows Event Log: exception code `0xc00000fd` (STATUS_STACK_OVERFLOW).
- **Current fix** (attempt #10): Added `await Task.Yield()` every 200 frames in `FrameReaderLoopAsync` to break continuation chains
- **File**: `src/Grpc.Net.SharedMemory/ShmConnection.cs` lines ~334-355
- **Status**: Build succeeds but **this fix has never been tested together with the InitialWindowSize fix**

#### Previous Fix Attempts (all failed)
1. Start environments on-demand → SHM hung immediately
2. Run SHM first → crashed after 2 sizes
3. `await Task.Yield()` before Task.Run → still crashed
4. `TCS(RunContinuationsAsynchronously)` + `UnsafeQueueUserWorkItem` → deadlocked at 64KB (thread pool starvation)
5. Dedicated `Thread` + `Channel<T>` → still crashed
6. Revert + `<StackSize>8388608</StackSize>` → TCP OK, SHM crashed silently
7. `ConfigureAwait(ConfigureAwaitOptions.ForceYielding)` → 5 sizes OK then deadlocked at 64KB (**this was the separate InitialWindowSize bug!**)
8. `Task.Factory.StartNew(LongRunning)` synchronous → StackOverflow
9. Explicit 8MB Thread + Unbounded Channel → no crash but ~50ms/call latency (100x too slow)
10. **Current**: Original `Task.Run` loop + `await Task.Yield()` every 200 frames → **UNTESTED**

**Key insight**: Fix #7 (ForceYielding) got through 5 sizes before deadlocking at 64KB. That 64KB deadlock was actually the *InitialWindowSize bug* (now fixed). So ForceYielding + 16MB window was never tested and might actually work. The simpler Task.Yield-every-200-frames approach in the current code is worth testing first.

### 4. Example File Changes
- Whitespace/formatting changes in 4 example files (Channeler, Mailer, Progressor, Racer SharedMemory clients)

## Files Modified

| File | Change |
|------|--------|
| `benchmark-shm/ringbench/RingBench.cs` | Complete rewrite — gRPC-level benchmarks. **Contains debug output (Console.Error `[DBG]`) that needs removal.** |
| `benchmark-shm/ringbench/RingBench.csproj` | Added proto compilation, package refs |
| `benchmark-shm/ringbench/Proto/benchmark.proto` | **NEW** — BenchmarkService proto definition |
| `benchmark-shm/benchmark_runner.py` | Complete rewrite for 2-transport JSON format |
| `src/Grpc.Net.SharedMemory/FrameTypes.cs` | `InitialWindowSize`: 65535 → 16 MiB |
| `src/Grpc.Net.SharedMemory/ShmConnection.cs` | Task.Yield every 200 frames in FrameReaderLoopAsync |
| `test/Grpc.Net.SharedMemory.Tests/FlowControlTests.cs` | Updated assertions for 16 MiB window |
| `examples/Channeler.SharedMemory/Client/Program.cs` | Whitespace |
| `examples/Mailer.SharedMemory/Client/Program.cs` | Whitespace |
| `examples/Progressor.SharedMemory/Client/Program.cs` | Whitespace |
| `examples/Racer.SharedMemory/Client/Program.cs` | Whitespace |

## What to Do Next

### Immediate: Test Current Fix
1. Run the benchmark: `dotnet run --configuration Release --project benchmark-shm/ringbench/RingBench.csproj`
2. The **combination** of Task.Yield-every-200-frames + 16MB InitialWindowSize has **never been tested together**
3. TCP benchmarks should work fine (they always have)
4. Watch for: SHM stack overflow crash (Event Log 0xc00000fd) or deadlock at 64KB+

### If Current Fix Works
1. Remove debug output from `RingBench.cs` (search for `Console.Error.WriteLine` with `[DBG]`, `warmup`, `timed`, `done`)
2. Run final benchmark end-to-end
3. Verify output files (JSON, CSV, SVGs)
4. Commit clean results

### If Current Fix Fails
- **Try ForceYielding + 16MB window**: Fix #7 (`ConfigureAwait(ConfigureAwaitOptions.ForceYielding)`) got furthest — it only failed at 64KB which was the *separate* InitialWindowSize bug (now fixed). Change the `Task.Run` pattern to use ForceYielding instead of periodic Yield.
- **Fallback**: `<StackSize>8388608</StackSize>` in RingBench.csproj (8MB stack). This only affects the benchmark exe, not the library. Combined with the 16MB InitialWindowSize fix, it might work. This combo was never tested.
- **Nuclear option**: Rewrite `FrameReaderLoopAsync` to not use `Task.Run` in a loop — use a dedicated reader thread that posts frames to an async Channel, but ensure the thread has adequate stack and doesn't go through ThreadPool dispatch.

### Reference Values
- **TCP typical**: Unary 0B ~130-180µs, 2MB ~12-17ms; Streaming 0B ~45-70µs, 2MB ~12-16ms
- **SHM partial** (from fix attempts): Unary 0B ~52-75µs, 1B ~46-71µs (SHM is faster for small payloads)
- **Go reference**: `c:\source\grpc-go-shmem\benchmark\shmemtcp\main.go`
- **Payload sizes**: `[0, 1, 1024, 4096, 16384, 65536, 262144, 524288, 1048576, 2097152]`
- **Iterations**: 2000 (≤1KB), 1200 (≤16KB), 800 (≤64KB), 400 (≤256KB), 250 (≤512KB), 150 (≤1MB), 80 (>1MB)

### Key Code Locations
- **Stack overflow site**: `ShmConnection.cs` → `FrameReaderLoopAsync` → `while (!_cts.IsCancellationRequested)` loop with `Task.Run(() => ReadFrame(...))`
- **InitialWindowSize**: `FrameTypes.cs` → `public const int InitialWindowSize = 16 * 1024 * 1024;`
- **Benchmark main**: `RingBench.cs` → `Main()` → iterates `StartTcpEnv`/`StartShmEnv`, calls `MeasureUnary`/`MeasureStreaming`
- **SHM server setup**: `RingBench.cs` → `StartShmEnv()` — uses smoke test with 10s timeout to verify SHM is working
- **ThreadPool config**: `RingBench.cs` top of Main → `ThreadPool.SetMinThreads(32, 32)`
