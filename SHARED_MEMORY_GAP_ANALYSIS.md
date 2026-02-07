# Comprehensive Gap Analysis: .NET Shared Memory Transport

## Overview

This document tracks gaps between the current .NET shared memory transport implementation
and the target state, comparing against:
1. **grpc-go-shmem** ([github.com/markrussinovich/grpc-go-shmem](https://github.com/markrussinovich/grpc-go-shmem)) - The Go reference implementation
2. **gRPC .NET TCP transport** - For feature parity and test coverage equivalence
3. **A73 RFC** - The gRPC shared memory transport specification

Last updated: 2026-02-06

---

## Prioritized Task List

### P1: Eliminate Extra Memory Copies (Send + Receive Path)
- [x] **P1a**: Use `ReadFrameInto` / pooled buffers in `FrameReaderLoopAsync` to remove per-frame `new byte[N]` allocation on receive
- [x] **P1b**: Remove double-copy on send: write gRPC prefix + payload directly into ring via scatter-write instead of allocating intermediate `byte[]` in `ShmHandler.SendMessagesAsync`
- **Current state**: 2 bulk copies per direction. Go has 1 copy send / 0-1 copy receive.
- **Target**: 1 copy per direction (matching Go).
- **Verification**: `EndToEndTests`, `ShmStreamingTests`, `FrameProtocolTests`, `LinuxSmokeTests`

### P2: Wire Compression into Data Path ✅
- [x] **P2a**: In `ShmGrpcStream.SendMessageAsync`, apply configured `IShmCompressor` before writing and set `compressed=1` flag byte
- [x] **P2b**: In `ShmGrpcStream.ReceiveMessagesAsync`, decompress when `compressed=1` instead of throwing `NotSupportedException`
- [x] **P2c**: Wire `ShmCompressionOptions` from `ShmConnection` / `ShmControlHandler` into `ShmGrpcStream`
- **Current state**: ~~Full `IShmCompressor` infra exists (`GzipCompressor`, `DeflateCompressor`, registry) but `SendMessageAsync` always sets `compressed=0` and receive throws on `compressed=1`.~~ Compression fully wired: `ShmCompressionOptions` threaded through all constructors, send path compresses when `ShouldCompress()`, receive path decompresses transparently. All 23 compression tests pass.
- **Verification**: `ShmCompressionE2ETests`, `Compression.SharedMemory` example

### P3: Wire Retry into Call Path ✅
- [x] **P3a**: Integrate `ShmRetryPolicy` into `ShmHandler.SendAsync` / `ShmControlHandler.SendAsync` retry loop
- [x] **P3b**: Apply exponential backoff + token-based throttling on retryable status codes
- **Current state**: ~~`ShmRetryPolicy`, `ShmRetryThrottling`, `ShmRetryState` all exist with full unit tests but are never invoked from the RPC call path.~~ `ShmHandler` already had retry wiring. `ShmControlHandler` now has matching retry loop: constructor accepts `ShmRetryPolicy?`/`ShmRetryThrottling?`, `SendAsync` uses `ShmRetryState` with exponential backoff, trailing header inspection, and token-bucket throttling. All 23 retry tests pass.
- **Verification**: `ShmRetryPolicyTests`, `ShmHedgingTests`, `Retrier.SharedMemory` example

### P4: Wire Telemetry Call Sites ✅
- [x] **P4a**: Add `ShmTelemetry.Record*` calls at key points in `ShmConnection`, `ShmGrpcStream`, and `ShmHandler`
- [x] **P4b**: Instrument: call start/end, message sent/received, errors, connection open/close, message sizes
- **Current state**: ~~14 OTel counters/histograms/gauges + ActivitySource + `IShmStatsHandler` events defined (~500 lines) with **zero call sites** in the transport.~~ All 12 counters, 3 histograms, and 3 gauges now have live call sites in `ShmConnection` (connection lifecycle, ping/pong/RTT, window updates, BDP, errors) and `ShmGrpcStream` (stream start/close, message sent/received with compression info).
- **Verification**: `ShmDiagnosticsTests`

### P5: Parameterized Transport Test Runner ✅ (framework)
- [x] **P5a**: Refactor `TransportTestBase` to support both TCP and SHM transport parameterization (matching Go's `listTestEnv()`)
- [ ] **P5b**: Port the core TCP functional test scenarios to run over both transports via `[TestCase]` or `[TestFixture]` parameterization *(blocked by P6)*
- **Current state**: ~~`TransportTestBase` is SHM-only.~~ `TransportKind` enum (Shm, Tcp) added. `TransportTestBase` accepts transport parameter with backward-compatible default. TCP path stubs `NotSupportedException` pending P6 server integration. Framework ready for `[TestFixture(TransportKind.Tcp)]` annotation.
- **Verification**: All parameterized tests pass on SHM; TCP await P6

### P6: Server-side ASP.NET Core Integration ✅
- [x] **P6a**: Implement `IConnectionListenerFactory` or equivalent so `MapGrpcService<T>()` works over SHM
- [x] **P6b**: Create `UseSharedMemory()` extension method for server builder
- **Current state**: ~~Server-side has no framework integration.~~ New `Grpc.AspNetCore.Server.SharedMemory` project implements `IConnectionListenerFactory` → `IConnectionListener` → `ConnectionContext` chain. `ShmStream` wrapped as `IDuplexPipe` via `PipeReader.Create`/`PipeWriter.Create`. `UseSharedMemory(segmentName)` extension configures Kestrel. Core `Grpc.Net.SharedMemory` stays ASP.NET-free.
- **Verification**: `builder.WebHost.UseSharedMemory("name"); app.MapGrpcService<T>();` pattern enabled

### P7: Rewrite Examples to Dialer-only Pattern
- [ ] **P7a**: Convert all 7 raw-`ShmConnection` client examples to use `ShmHandler` + `GrpcChannel` + generated stubs
- [ ] **P7b**: Convert all 19 server examples to use ASP.NET Core integration (depends on P6)
- **Current state**: 3/19 client examples use `ShmHandler` (correct pattern). 7 use raw `ShmConnection` with manual wire-level framing, no generated stubs. 0/19 servers use framework integration.
- **Go reference**: ALL Go examples use `grpc.WithShmTransport()` on client + `grpc.NewServer().Serve(shmListener)` on server — the **only** difference from TCP.
- **Verification**: All examples build, run, and produce identical output to TCP equivalents

### P8: TCP Fallback + Mixed Transport
- [ ] **P8a**: Implement `dialShmWithFallback` equivalent in `ShmControlHandler`
- [ ] **P8b**: Implement `AllowMixedTransport` to handle both `shm://` and TCP addresses
- [ ] **P8c**: Add transport selection with 4 policies: disabled / preferred / required / auto
- **Current state**: Not implemented. Placeholder code exists.
- **Verification**: New `ShmFallbackTests`

### P9: Security Handshake
- [ ] **P9a**: Design `IShmSecurityHandshaker` interface
- [ ] **P9b**: Implement 3-step nonce-based handshake protocol (Init→Resp→Ack)
- [ ] **P9c**: Wire into `ShmControlHandler` and `ShmControlListener` connection setup
- **Current state**: Not implemented. Go has `ShmSecurityHandshaker` with PID-based identity, nonce exchange, `VerifyIdentity` callback.
- **Verification**: New `ShmSecurityTests`

---

## Current Status Summary

| Criterion | Rating | Details |
|-----------|--------|---------|
| **Minimal memory copies** | **B+** | Pooled buffers via ArrayPool on send + receive; same copy count, zero GC pressure |
| **Minimal kernel calls** | **A** | Equivalent to Go (adaptive spin + futex on Linux) |
| **Full gRPC integration** | **D** | Client ShmHandler works; server has no framework integration |
| **Feature parity with Go** | **C+** | Core transport byte-compatible; compression/retry/telemetry are dead code; fallback/security missing |
| **Test parity with TCP** | **B** | 327 tests covering all TCP categories, but separate codebase (not parameterized) |
| **E2E example parity** | **D** | 3/19 clients follow dialer pattern; 0/19 servers follow it |

---

## Detailed Reference Data

### Memory Copy Analysis

**Send Path (per message of N bytes):**
| Copy | Location | Size | Description |
|------|----------|------|-------------|
| #1 | `ShmHandler.SendMessagesAsync` | N | HTTP content stream → `new byte[length]` |
| #2 | `FrameProtocol.WriteFrame` → `WriteToReservation` | N | Heap buffer → shared memory ring |

**Receive Path (per message of N bytes):**
| Copy | Location | Size | Description |
|------|----------|------|-------------|
| #1 | `FrameProtocol.ReadFrame` → `CopyFromReservation` | N+5 | Shared memory ring → `new byte[header.Length]` |
| #2 | `ShmResponseContent.SerializeToStreamAsync` | N | Heap → HTTP output stream |

### Kernel Call Analysis (Linux)

| Operation | Best Case | Typical | Worst Case |
|-----------|-----------|---------|------------|
| **Send** | 0 (spin succeeds) | 1 (futex WAKE) | 2 (WAIT + WAKE) |
| **Receive** | 0 (spin succeeds) | 1 (futex WAKE) | 4 (2×WAIT + 2×WAKE) |

### Feature Parity Inventory

| Feature | Go | .NET | Status |
|---------|-----|------|--------|
| SPSC Ring Buffer | Full | Full | ✅ MATCH |
| Frame Protocol (16B header, 13 types) | Full | Full | ✅ MATCH |
| Segment Layout (128B header) | Full | Byte-compatible | ✅ MATCH |
| Binary Headers/Trailers | Full | HeadersV1/TrailersV1 | ✅ MATCH |
| Stream Multiplexing | Full | Full | ✅ MATCH |
| Flow Control (conn + stream windows) | Full | Full | ✅ MATCH |
| BDP Estimation | Full | Full | ✅ MATCH |
| Keepalive (client + server) | Full | Full | ✅ MATCH |
| Graceful Shutdown (GOAWAY) | Full | Basic | ⚠️ PARTIAL |
| Control Segment Protocol | Full | Full | ✅ MATCH |
| Compression | Transparent via gRPC | Infra exists, **throws at runtime** | ❌ NOT WIRED |
| Retry / Hedging | Transparent via gRPC | Infra exists, **never called** | ❌ NOT WIRED |
| Telemetry / OTel | Standard gRPC stats | 14 metrics defined, **zero call sites** | ❌ NOT WIRED |
| Health Checks | Standard gRPC health | Standalone, not integrated | ❌ NOT WIRED |
| Load Balancing | ShmPreferPicker + selector | Abstractions only | ❌ NOT WIRED |
| TCP Fallback | `dialShmWithFallback` | Not implemented | ❌ MISSING |
| Mixed Transport | `AllowMixedTransport` | Not implemented | ❌ MISSING |
| Security Handshake | 3-step nonce protocol | Not implemented | ❌ MISSING |
| Stream Scheduler | Weighted fair queueing | Not implemented | ❌ MISSING |
| `shm://` Resolver | Built-in | Not implemented | ❌ MISSING |

### Test Coverage

| Category | TCP Tests | SHM Tests | SHM Coverage |
|----------|-----------|-----------|--------------|
| Unary | 17 | 10 | Partial |
| Streaming | 27 | 21 | Good |
| Cancellation | 15 | 8 | Partial |
| Deadline | 8 | 11 | Exceeds |
| Metadata | 6 | 15 | Exceeds |
| Compression | 6 | 23 | Exceeds |
| Retry | 13 | 19 | Exceeds |
| Hedging | 11 | 8 | Partial |
| Connection | 5 | 19 | Exceeds |
| MaxMessageSize | 1 | 7 | Exceeds |
| Interceptor | 1 | 7 | Exceeds |
| Telemetry | 6 | 9 | Exceeds |
| Lifetime | 3 | 10 | Exceeds |
| **Total** | **~107** | **~327** | All categories covered |

### Example Inventory

| Example | Client Pattern | Server Pattern | Dialer-only? |
|---------|---------------|----------------|-------------|
| Greeter.SharedMemory | ShmHandler ✅ | ShmControlListener ❌ | Client only |
| Counter.SharedMemory | ShmHandler ✅ | ShmControlListener ❌ | Client only |
| Error.SharedMemory | ShmHandler ✅ | ShmControlListener ❌ | Client only |
| Channeler.SharedMemory | ShmHandler ✅ | ShmControlListener ❌ | Client only |
| Downloader.SharedMemory | ShmHandler ✅ | ShmControlListener ❌ | Client only |
| Mailer.SharedMemory | ShmHandler ✅ | ShmControlListener ❌ | Client only |
| Racer.SharedMemory | ShmHandler ✅ | ShmControlListener ❌ | Client only |
| Reflector.SharedMemory | ShmHandler ✅ | ShmControlListener ❌ | Client only |
| Retrier.SharedMemory | ShmHandler ✅ | ShmControlListener ❌ | Client only |
| Uploader.SharedMemory | ShmHandler ✅ | ShmControlListener ❌ | Client only |
| Vigor.SharedMemory | ShmHandler ✅ | ShmControlListener ❌ | Client only |
| Cancellation.SharedMemory | Raw ShmConnection ❌ | Raw ShmConnection ❌ | No |
| Compression.SharedMemory | Raw ShmConnection ❌ | Raw ShmConnection ❌ | No |
| Deadline.SharedMemory | Raw ShmConnection ❌ | Raw ShmConnection ❌ | No |
| Interceptor.SharedMemory | Raw ShmConnection ❌ | Raw ShmConnection ❌ | No |
| Keepalive.SharedMemory | Raw ShmConnection ❌ | Raw ShmConnection ❌ | No |
| Metadata.SharedMemory | Raw ShmConnection ❌ | Raw ShmConnection ❌ | No |
| RouteGuide.SharedMemory | Raw ShmConnection ❌ | Raw ShmConnection ❌ | No |
| Progressor.SharedMemory | Mixed ❌ | Mixed ❌ | No |
