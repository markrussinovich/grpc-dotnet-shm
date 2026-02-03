# Shared Memory Transport Gap Analysis

## Overview

This document tracks gaps between the current .NET shared memory transport implementation and the target state, comparing against:
1. **grpc-go-shmem** - The Go reference implementation for byte-level compatibility
2. **gRPC .NET TCP transport** - For feature parity and test coverage equivalence
3. **Core principles** - No polling, minimal memory copies, minimal kernel calls

## Gap Summary (Priority Order)

### ❌ Critical - Blocking Functionality

| # | Gap | Category | Description | Priority |
|---|-----|----------|-------------|----------|
| 1 | **True cross-process memory sharing** | Implementation | Segment.cs copies data to local buffer via `byte[]`, breaking actual shared memory. Ring operations work on copied data, not mapped memory. | P0 |
| 2 | **Unsafe pointer-based ring operations** | Implementation | ShmRing operates on `Memory<byte>` backed by managed array. Needs unsafe `byte*` to mapped region for true zero-copy. | P0 |
| 3 | **Linux futex implementation** | Implementation | LinuxRingSync is stubbed - uses polling fallback. Blocks Linux support. | P0 |
| 4 | **Server-side request routing** | Implementation | ShmConnectionListener lacks proper stream acceptance/routing. Current example uses polling. | P0 |
| 5 | **ASP.NET Core integration** | Implementation | No `IConnectionListenerFactory` implementation for Kestrel integration. | P1 |

### ⚠️ High - Feature Gaps vs grpc-go-shmem

| # | Gap | Category | Description | Priority |
|---|-----|----------|-------------|----------|
| 6 | **Flow control (WindowUpdate frames)** | Implementation | No flow control implementation. grpc-go-shmem has full send quota tracking. | P1 |
| 7 | **GOAWAY handling** | Implementation | No graceful shutdown with GOAWAY frames. | P1 |
| 8 | **Ping/Pong keepalive** | Implementation | No keepalive support. grpc-go-shmem has PING/PONG frames. | P1 |
| 9 | **Compression support** | Implementation | ShmHandler throws `NotSupportedException` for compressed messages. | P1 |
| 10 | **Cross-process verification** | Testing | No actual cross-process E2E tests. Only in-process simulation. | P1 |
| 11 | **Mixed transport fallback** | Implementation | grpc-go-shmem has automatic fallback to TCP. Not implemented. | P2 |
| 12 | **SHM-aware resolver/balancer** | Implementation | grpc-go-shmem has `balancer/shm/shm_lb.go`. Not implemented. | P2 |

### 📊 Test Equivalence Gaps (.NET TCP vs SHM)

The FunctionalTests directory contains extensive TCP transport tests that have no SHM equivalents:

| TCP Test File | SHM Equivalent | Gap |
|--------------|----------------|-----|
| `Client/UnaryTests.cs` | ❌ Missing | Full unary call scenarios |
| `Client/StreamingTests.cs` | Partial (EndToEndTests) | Additional streaming edge cases |
| `Client/CancellationTests.cs` | Partial (1 test) | Comprehensive cancellation scenarios |
| `Client/DeadlineTests.cs` | Partial (1 test) | Deadline propagation, server-side handling |
| `Client/MetadataTests.cs` | Partial (1 test) | Extensive metadata scenarios |
| `Client/CompressionTests.cs` | ❌ Missing | Compression support not implemented |
| `Client/RetryTests.cs` | ❌ Missing | Retry policy support |
| `Client/HedgingTests.cs` | ❌ Missing | Hedging support |
| `Client/ConnectionTests.cs` | ❌ Missing | Connection lifecycle |
| `Client/MaxMessageSizeTests.cs` | Partial (1 test) | Size limit enforcement |
| `Client/AuthorizationTests.cs` | ❌ Missing | Auth not applicable to SHM |
| `Client/InterceptorTests.cs` | ❌ Missing | Interceptor integration |
| `Client/TelemetryTests.cs` | ❌ Missing | Telemetry/tracing |
| `Client/EventSourceTests.cs` | ❌ Missing | EventSource integration |
| `Client/ClientFactoryTests.cs` | ❌ Missing | DI/factory integration |
| `Server/UnaryMethodTests.cs` | ❌ Missing | Server-side unary handling |
| `Server/ServerStreamingMethodTests.cs` | ❌ Missing | Server-side streaming |
| `Server/ClientStreamingMethodTests.cs` | ❌ Missing | Server-side client streaming |
| `Server/DuplexStreamingMethodTests.cs` | ❌ Missing | Server-side duplex |
| `Server/DeadlineTests.cs` | ❌ Missing | Server-side deadline handling |
| `Server/CompressionTests.cs` | ❌ Missing | Server-side compression |
| `Server/MaxMessageSizeTests.cs` | ❌ Missing | Server-side size limits |
| `Server/InterceptorOrderTests.cs` | ❌ Missing | Interceptor ordering |
| `Server/LifetimeTests.cs` | ❌ Missing | Server lifecycle |
| `Server/DiagnosticsTests.cs` | ❌ Missing | Diagnostics/observability |

**Current SHM test count: 78 tests**
**TCP FunctionalTests count: ~200+ tests**
**Gap: ~60% test coverage missing**

### 📱 E2E Example Equivalence Gaps

| TCP Example | SHM Equivalent | Gap |
|-------------|----------------|-----|
| `Greeter/` | ✅ `Greeter.SharedMemory/` | Basic example exists |
| `Counter/` | ❌ Missing | 4 RPC types demo |
| `Error/` | ❌ Missing | Error handling patterns |
| `Interceptor/` | ❌ Missing | Interceptor demo |
| `Compressor/` | ❌ Missing | Compression demo |
| `Transporter/` | ❌ Missing | Transport configuration |
| `Retrier/` | ❌ Missing | Retry demo |
| `Mailer/` | ❌ Missing | Long-running streams |
| `Progressor/` | ❌ Missing | Progress reporting |
| `Aggregator/` | ❌ Missing | Aggregation patterns |

**Current SHM examples: 1 (Greeter.SharedMemory)**
**TCP examples: 29**
**Gap: ~97% example coverage missing**

### 🎯 Principle Adherence Analysis

#### 1. No Polling ❌ VIOLATED

**Current State:**
- `ShmRing.WaitForData/WaitForSpace` use adaptive spin + blocking, which is good
- **But**: Sync primitives fall back to `Thread.SpinWait(1)` loops when `_sync` is null
- **LinuxRingSync** is stubbed - polling fallback
- Server example uses `Task.Delay(10)` polling loop

**Required Fix:**
- Implement proper futex on Linux
- Ensure Windows events always available
- Add event-driven stream acceptance on server

#### 2. Minimal Memory Copies ❌ VIOLATED

**Current State:**
- `Segment.cs` copies entire mapped region to `byte[] _memoryBuffer`
- `ShmRing` operates on this buffer, not the mapped memory
- Every `Refresh()` and `Flush()` copies entire segment
- `MemoryMappedViewAccessor.ReadArray/WriteArray` performs copies

**Required Fix:**
- Use `MemoryMappedViewAccessor.SafeMemoryMappedViewHandle.AcquirePointer()` to get raw pointer
- Create `Memory<byte>` directly over mapped region via `MemoryManager<byte>` implementation
- Rings should operate directly on mapped memory (zero-copy)

**grpc-go-shmem approach:**
```go
// In shm_mmap_windows.go
addr, err := windows.MapViewOfFile(h, ...)
data := unsafe.Slice((*byte)(unsafe.Pointer(addr)), size)
```

#### 3. Minimal Kernel Calls ⚠️ PARTIAL

**Current State:**
- Each `ShmRing.Write/Read` triggers `Volatile.Read/Write` (OK, not kernel)
- Signaling uses Windows events (kernel calls per signal)
- No batching of signals

**Room for Improvement:**
- Batch signals when multiple frames queued
- Use futex wake-all patterns
- Coalesce consecutive writes before signaling

### 🔧 Binary Protocol Verification

Current binary layouts appear compatible with grpc-go-shmem based on InteropTests:

| Structure | .NET | grpc-go-shmem | Match |
|-----------|------|---------------|-------|
| Segment Header | 128 bytes, "GRPCSHM\0" magic | 128 bytes, "GRPCSHM\0" magic | ✅ |
| Ring Header | 64 bytes, capacity@offset 0 | 64 bytes, capacity@offset 0 | ✅ |
| Frame Header | 16 bytes, LE | 16 bytes, LE | ✅ |
| HeadersV1 | Version + type + method + authority + deadline + metadata | Same layout | ✅ |
| TrailersV1 | Version + status code + message + metadata | Same layout | ✅ |

**Note:** Actual interop testing with Go not yet performed.

---

## Implementation Remediation Plan

### Phase 1: Fix Core Memory Architecture (P0)

1. **Create `MappedMemoryManager<T>` class**
   - Wraps `SafeMemoryMappedViewHandle`
   - Implements `MemoryManager<byte>` for zero-copy `Memory<byte>`
   - Uses `AcquirePointer()` / `ReleasePointer()`

2. **Update Segment.cs**
   - Remove `byte[] _memoryBuffer` copy
   - Create rings directly over mapped memory
   - Implement proper cross-process synchronization

3. **Update ShmRing.cs**
   - Accept `Memory<byte>` backed by mapped memory
   - Verify operations work with pinned memory
   - Add memory barriers where needed

4. **Implement Linux futex**
   - P/Invoke `SYS_futex` syscall
   - Match grpc-go-shmem `shm_futex_linux.go` exactly
   - Handle EAGAIN, ETIMEDOUT, EINTR

### Phase 2: Server Integration (P0-P1)

1. **Implement stream routing**
   - Frame dispatcher based on stream ID
   - Proper stream lifecycle (HEADERS → MESSAGE* → TRAILERS)
   - Stream table with concurrent access

2. **Create `ShmConnectionListenerFactory`**
   - Implement `IConnectionListenerFactory` for Kestrel
   - Bridge to ASP.NET Core middleware
   - Enable `UseSharedMemoryTransport()` extension

3. **Add flow control**
   - WindowUpdate frame handling
   - Send quota tracking per stream
   - Initial window size configuration

### Phase 3: Feature Completion (P1)

1. **GOAWAY handling** - Graceful connection shutdown
2. **PING/PONG** - Keepalive support
3. **Compression** - gzip, deflate support
4. **Deadlines** - Server-side enforcement

### Phase 4: Test Parity (P1)

1. **Port TCP client functional tests to SHM**
   - UnaryTests, StreamingTests, CancellationTests, etc.
   - Use same test patterns, swap transport

2. **Port TCP server functional tests to SHM**
   - Method-level tests
   - Lifecycle tests
   - Error handling

3. **Add cross-process tests**
   - Separate test process for true IPC verification
   - Match grpc-go-shmem `shm_cross_process_test.go`

### Phase 5: Examples (P2)

1. **Counter.SharedMemory** - All 4 RPC types
2. **Streaming.SharedMemory** - Focus streaming
3. **Error.SharedMemory** - Error handling
4. **Interceptor.SharedMemory** - Middleware

### Phase 6: Interoperability (P2)

1. **Cross-language testing** - .NET client ↔ Go server
2. **Mixed transport** - Auto-fallback to TCP
3. **SHM balancer** - Same-host connection preference

---

## Test Files Needed (Priority Order)

### Must Have (for feature parity)
```
test/Grpc.Net.SharedMemory.Tests/
├── CrossProcessTests.cs          # True IPC verification
├── FlowControlTests.cs           # WindowUpdate handling
├── GracefulShutdownTests.cs      # GOAWAY handling
├── KeepaliveTests.cs             # PING/PONG
├── ZeroCopyTests.cs              # Verify no copies in hot path
├── FunctionalTests/
│   ├── ShmUnaryTests.cs          # Port of TCP UnaryTests
│   ├── ShmStreamingTests.cs      # Port of TCP StreamingTests
│   ├── ShmCancellationTests.cs   # Port of TCP CancellationTests
│   ├── ShmDeadlineTests.cs       # Port of TCP DeadlineTests
│   ├── ShmMetadataTests.cs       # Port of TCP MetadataTests
│   ├── ShmConnectionTests.cs     # Port of TCP ConnectionTests
│   └── ShmMaxMessageSizeTests.cs # Port of TCP MaxMessageSizeTests
```

### Nice to Have
```
├── ShmRetryTests.cs               # Retry support
├── ShmHedgingTests.cs             # Hedging support
├── ShmInterceptorTests.cs         # Interceptor integration
├── ShmCompressionTests.cs         # Compression support
```

---

## Current Status Summary

| Category | Status | Progress |
|----------|--------|----------|
| Ring Buffer | ✅ Complete | 100% |
| Frame Protocol | ✅ Complete | 100% |
| Headers/Trailers Encoding | ✅ Complete | 100% |
| Binary Layout (Go-compatible) | ✅ Complete | 100% |
| Client Handler (ShmHandler) | ⚠️ Partial | 70% |
| Server Listener | ⚠️ Partial | 40% |
| True Cross-Process | ❌ Broken | 0% |
| Linux Support | ❌ Stubbed | 10% |
| Flow Control | ❌ Missing | 0% |
| Test Coverage | ⚠️ Partial | 40% |
| Example Coverage | ⚠️ Minimal | 3% |
| Principle Adherence | ❌ Violated | 30% |

**Overall Progress: ~45%**

---

## Next Steps

1. **Immediate (This Sprint)**
   - Fix Segment.cs to use true mapped memory (P0)
   - Complete Linux futex implementation (P0)
   - Add cross-process verification tests (P0)

2. **Short Term**
   - Server-side stream routing (P0)
   - ASP.NET Core integration (P1)
   - Flow control (P1)

3. **Medium Term**
   - Test parity with TCP functional tests (P1)
   - Additional examples (P2)
   - Interop testing with Go (P2)

---

*Last Updated: Phase 4 Analysis - February 2026*
