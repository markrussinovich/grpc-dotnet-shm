# Comprehensive Gap Analysis: .NET Shared Memory Transport

## Overview

This document tracks gaps between the current .NET shared memory transport implementation and the target state, comparing against:
1. **grpc-go-shmem** - The Go reference implementation for byte-level compatibility
2. **gRPC .NET TCP transport** - For feature parity and test coverage equivalence
3. **A73 RFC** - The gRPC shared memory transport specification

---

## 1. TCP Tests vs SHM Equivalents

### Current SHM Test Coverage

| SHM Test File | Tests |
|--------------|-------|
| `EndToEndTests.cs` | Unary, ServerStreaming, ClientStreaming, BiDi, Error, Cancel, Deadline, Metadata, LargeMessage |
| `ShmConnectionTests.cs` | Connection lifecycle |
| `ShmGrpcStreamTests.cs` | Stream state |
| `FrameProtocolTests.cs` | Frame encoding |
| `RingBufferTests.cs` | Ring operations |
| `SegmentTests.cs` | Segment creation |
| `HeadersTrailersTests.cs` | Header/trailer encoding |
| **Total: ~40 tests** | |

### Missing TCP Test Categories (from `test/FunctionalTests/`)

| TCP Test Category | SHM Equivalent | Status |
|------------------|----------------|--------|
| `Client/UnaryTests.cs` | Partial (basic unary) | ⚠️ Missing advanced scenarios |
| `Client/StreamingTests.cs` | Partial | ⚠️ Missing edge cases |
| `Client/CancellationTests.cs` | 1 test | ❌ **Missing comprehensive cancellation** |
| `Client/DeadlineTests.cs` | 1 test | ❌ **Missing server-side deadline enforcement** |
| `Client/MetadataTests.cs` | 1 test | ❌ **Missing binary metadata** |
| `Client/CompressionTests.cs` | None | ❌ **Not implemented** |
| `Client/RetryTests.cs` | None | ❌ **Not implemented** |
| `Client/HedgingTests.cs` | None | ❌ **Not implemented** |
| `Client/ConnectionTests.cs` | None | ❌ **Missing connection lifecycle** |
| `Client/MaxMessageSizeTests.cs` | 1 test | ⚠️ Partial |
| `Client/InterceptorTests.cs` | None | ❌ **Missing interceptor integration** |
| `Client/TelemetryTests.cs` | None | ❌ **Missing tracing** |
| `Server/UnaryMethodTests.cs` | None | ❌ **Missing** |
| `Server/ServerStreamingMethodTests.cs` | None | ❌ **Missing** |
| `Server/ClientStreamingMethodTests.cs` | None | ❌ **Missing** |
| `Server/DuplexStreamingMethodTests.cs` | None | ❌ **Missing** |
| `Server/DeadlineTests.cs` | None | ❌ **Missing** |
| `Server/LifetimeTests.cs` | None | ❌ **Missing** |
| `Balancer/*Tests.cs` | None | ❌ **No SHM-aware balancer** |

**Gap: ~60% of TCP functional tests have no SHM equivalent**

---

## 2. Zero-Copy & Kernel Call Equivalence

### ✅ Zero-Copy: EQUIVALENT

| Aspect | Go grpc-go-shmem | .NET Implementation | Status |
|--------|-----------------|---------------------|--------|
| **Direct pointer to mmap** | `unsafe.Pointer(addr)` | `MappedMemoryManager` with `AcquirePointer()` | ✅ |
| **Zero-copy Span/Slice** | `unsafe.Slice((*byte)(ptr), size)` | `new Span<byte>(_pointer, _length)` | ✅ |
| **Ring buffer writes** | Direct `copy()` to mmap | `_data.Span.CopyTo()` over mmap | ✅ |
| **Wrap-around handling** | Copy only when spanning | Same behavior | ✅ |
| **Reservation pattern** | `ReserveWrite()` returns slices | `WriteReservation` struct | ✅ |

### ✅ Kernel Calls: EQUIVALENT (Linux)

| Syscall | Go | .NET | Status |
|---------|-----|------|--------|
| **futex WAIT** | `syscall(SYS_FUTEX, ptr, FUTEX_WAIT, val, timeout)` | `Syscall.SysCall(SYS_futex, ptr, FUTEX_WAIT, val, timeout)` | ✅ |
| **futex WAKE** | `syscall(SYS_FUTEX, ptr, FUTEX_WAKE, 1)` | `Syscall.SysCall(SYS_futex, ptr, FUTEX_WAKE, 1)` | ✅ |
| **Cross-process** | Non-PRIVATE futex | Non-PRIVATE futex | ✅ |
| **Adaptive spin** | Spin → block pattern | Same pattern | ✅ |

### ⚠️ Windows: Slight Difference

- **Go**: Uses `WaitOnAddress` API when available (in-process optimization)
- **.NET**: Uses named events exclusively
- **Impact**: Minimal for cross-process IPC (the primary use case)

---

## 3. TCP E2E Examples vs SHM Equivalents

### Current State

| TCP Example | SHM Equivalent | Coverage |
|-------------|----------------|----------|
| **Greeter** (Unary) | ✅ `Greeter.SharedMemory/` | Basic unary RPC |
| **Counter** (4 RPC types) | ❌ | Streaming demos |
| **Mailer** (Bidirectional) | ❌ | Long-running streams |
| **Racer** (Bidirectional) | ❌ | Concurrent streams |
| **Interceptor** | ❌ | Middleware patterns |
| **Retrier** | ❌ | Retry policies |
| **Compressor** | ❌ | Compression |
| **Progressor** | ❌ | Progress reporting |
| **Downloader/Uploader** | ❌ | Large binary payloads |
| **Channeler** | ❌ | Multi-threaded |
| **Error** | ❌ | Error handling |
| **Vigor** (Health) | ❌ | Health checks |
| **Reflector** | ❌ | Server reflection |

**Gap: 1/30 examples (3%) have SHM equivalents**

---

## 4. A73 RFC Feature Compliance

Based on the gRPC A73 RFC (shared memory transport proposal):

### ✅ Fully Implemented (26 features)

| Category | Features |
|----------|----------|
| **Core Architecture** | Segment header (128B), Dual ring buffers, Power-of-2 capacity |
| **Zero-Copy** | MappedMemoryManager, Direct pointer operations, Reservation pattern |
| **Frame Types** | All 10 types: PAD, HEADERS, MESSAGE, TRAILERS, CANCEL, GOAWAY, PING, PONG, HALFCLOSE, WINDOW_UPDATE |
| **Flow Control** | Connection-level window, Stream-level window, WINDOW_UPDATE handling |
| **Stream Multiplexing** | Stream IDs (odd=client, even=server), Max streams enforcement |
| **Graceful Shutdown** | GOAWAY frame, Drain mode |
| **Error Handling** | CANCEL frame (RST_STREAM equiv), Trailers with error status |
| **Synchronization** | Linux futex (FUTEX_WAIT/WAKE), Windows named events |

### ❌ Missing A73 Features (Priority Order)

| # | Feature | A73 Requirement | Priority |
|---|---------|-----------------|----------|
| 1 | **BDP Estimation** | Dynamic window sizing based on bandwidth-delay product | P1 |
| 2 | **Keepalive Task** | Background task sending periodic PINGs | P1 |
| 3 | **Keepalive Parameters** | Configurable Time, Timeout, PermitWithoutStream | P1 |
| 4 | **Ping Strike Enforcement** | Detect "too many pings" → GOAWAY | P2 |

### ⚠️ Partially Implemented

| Feature | Status |
|---------|--------|
| **BDP PING flag** | Flag defined but unused |
| **GOAWAY active stream handling** | Basic support, no stream count tracking on shutdown |
| **Cross-process E2E tests** | In-process only, no true cross-process verification |

---

## 5. Summary of Gaps

| Area | Status | Gap Severity |
|------|--------|--------------|
| **Zero-copy implementation** | ✅ Equivalent to Go | ✅ No gap |
| **Kernel call efficiency** | ✅ Equivalent to Go | ✅ No gap |
| **A73 Core features** | ~86% implemented | ⚠️ Missing BDP, Keepalive |
| **Test coverage vs TCP** | ~40% equivalent | ❌ **60% gap** |
| **E2E examples** | 3% equivalent | ❌ **97% gap** |

---

## 6. Recommended Priority Actions

### Phase 1: A73 Feature Gaps (P1)

1. **BDP Estimation** - Dynamic window sizing based on measured bandwidth
2. **Keepalive Background Task** - Periodic PING sending with configurable parameters
3. **Keepalive Configuration** - Time, Timeout, PermitWithoutStream options

### Phase 2: Test Coverage (P1)

1. Add cross-process E2E tests (true IPC verification)
2. Port key TCP functional tests: `StreamingTests`, `CancellationTests`, `ConnectionTests`
3. Add flow control stress tests

### Phase 3: Examples (P2)

1. `Counter.SharedMemory` - All 4 RPC types
2. `Streaming.SharedMemory` - Focus streaming patterns
3. `Error.SharedMemory` - Error handling patterns

### Phase 4: Advanced Features (P2)

1. Ping strike enforcement
2. Compression support
3. SHM-aware load balancer/resolver
