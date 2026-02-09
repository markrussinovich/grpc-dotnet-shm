# Comprehensive Research Report: markrussinovich/grpc-go-shmem

> **Source Repository**: https://github.com/markrussinovich/grpc-go-shmem  
> **Language**: Go  
> **Build Constraint**: `//go:build linux || windows`  
> **RFC**: A73 — Shared Memory Transport for gRPC (authored 2026-02-02)  
> **Primary Package**: `internal/transport`

---

## Table of Contents

1. [Transport Features](#1-transport-features)
2. [Test Coverage](#2-test-coverage)
3. [Activation API](#3-activation-api)
4. [Examples](#4-examples)
5. [TCP Fallback Mechanism](#5-tcp-fallback-mechanism)
6. [Memory Copy Analysis](#6-memory-copy-analysis)
7. [Kernel Call Analysis](#7-kernel-call-analysis)
8. [Flow Control & BDP Estimation](#8-flow-control--bdp-estimation)
9. [Keepalive Implementation](#9-keepalive-implementation)
10. [Security Handshake](#10-security-handshake)

---

## 1. Transport Features

### 1.1 Shared Memory Segment Layout

The transport uses POSIX shared memory (`/dev/shm` on Linux) with a fixed binary layout:

```
┌──────────────────────────────────────────────────────────────┐
│ SegmentHeader (128 bytes)                                    │
│   - Magic, Version, SegmentSize, RingAOffset, RingASize,    │
│     RingBOffset, RingBSize, ServerReady, ClientConnected,    │
│     ServerPID, ClientPID                                     │
├──────────────────────────────────────────────────────────────┤
│ Ring A: Client → Server (default 64 MiB)                     │
│   ┌─ RingHeader (64 bytes) ─────────────────────────────────┐│
│   │ capacity, widx, ridx, dataSeq, spaceSeq, closed,       ││
│   │ contigSeq, spaceWaiters, contigWaiters, dataWaiters     ││
│   ├─────────────────────────────────────────────────────────┤│
│   │ Data Area (power-of-2 capacity)                         ││
│   └─────────────────────────────────────────────────────────┘│
├──────────────────────────────────────────────────────────────┤
│ Ring B: Server → Client (default 64 MiB)                     │
│   ┌─ RingHeader (64 bytes) ─────────────────────────────────┐│
│   │ (same structure as Ring A)                               ││
│   ├─────────────────────────────────────────────────────────┤│
│   │ Data Area                                                ││
│   └─────────────────────────────────────────────────────────┘│
└──────────────────────────────────────────────────────────────┘
```

### 1.2 SPSC Ring Buffer (`ring.go`)

Lock-free **Single-Producer Single-Consumer** (SPSC) circular buffer with:

| Feature | Detail |
|---------|--------|
| **Capacity** | Power-of-2, wraps via bitmask |
| **Indices** | Monotonic `widx` / `ridx`, 32-bit (wraps modulo capacity) |
| **Atomics** | `atomic.Load` (acquire) / `atomic.Store` (release) for SPSC correctness |
| **Signaling** | Three sequence counters: `dataSeq`, `spaceSeq`, `contigSeq` — each backed by futex |
| **Adaptive Spin** | Writer spins on `spaceSpinCutoff` iterations before falling back to futex sleep |
| **Zero-copy Write** | `ReserveWrite()` returns `WriteReservation{First, Second []byte}` — direct mmap pointers |
| **Read Modes** | `ReadBlocking()` (copy), `ReadSlices()` (zero-copy), `ReadExact()` |
| **Close** | `Close()` sets `closed` flag and wakes all waiters |

**Key methods:**
- `WriteBlocking(data []byte) error` — copy-based write
- `ReserveWrite(ctx, size) (WriteReservation, error)` — zero-copy reservation
- `ReserveFrameHeader(ctx) (WriteReservation, error)` — reserve exactly 16 bytes
- `Commit(n int) error` — publish written bytes
- `ReadSlices(ctx, size) (first, second []byte, CommitFunc, error)` — zero-copy read

### 1.3 Frame Protocol (`frame.go`)

Custom 16-byte little-endian frame header:

```
Offset  Size    Field
0       4       Length    (payload bytes, excludes 16-byte header)
4       4       StreamID  (client=odd, server=even)
8       1       Type      (FrameType enum)
9       1       Flags     (per-type flags)
10      2       Reserved
12      4       Reserved2
```

**Data-plane frame types:**

| Type | Value | Description | Flags |
|------|-------|-------------|-------|
| `PAD` | 0x00 | Padding (skip) | — |
| `HEADERS` | 0x01 | Initial headers (method, authority, metadata) | `INITIAL=0x01` |
| `MESSAGE` | 0x02 | gRPC message payload | `MORE=0x01` (chunking) |
| `TRAILERS` | 0x03 | Final status + trailing metadata | `EndStream=0x01` |
| `CANCEL` | 0x04 | Stream cancellation | — |
| `GOAWAY` | 0x05 | Connection shutdown | `DRAINING=0x01`, `IMMEDIATE=0x02` |
| `PING` | 0x06 | Keepalive / BDP ping | `BDP=0x01`, `ACK=0x02` |
| `PONG` | 0x07 | Keepalive response | — |
| `HALFCLOSE` | 0x08 | Client finished sending | — |
| `WindowUpdate` | 0x09 | Flow control window update | — |

**Control-plane frame types** (on separate control segment, `control_wire.go`):

| Type | Value | Description |
|------|-------|-------------|
| `CONNECT` | 0x10 | Client connect request |
| `ACCEPT` | 0x11 | Server accepts connection |
| `REJECT` | 0x12 | Server rejects connection |

**Security handshake frame types** (`shm_security.go`):

| Type | Value | Description |
|------|-------|-------------|
| `HandshakeInit` | 0x20 | Client → Server: identity + nonce |
| `HandshakeResp` | 0x21 | Server → Client: identity + nonce |
| `HandshakeAck` | 0x22 | Client → Server: success |
| `HandshakeFail` | 0x23 | Either → Either: failure |

**Headers payload (Version 1):**
```go
type HeadersV1 struct {
    Version          uint8
    HdrType          uint8   // 0=client-initial, 1=server-initial
    Method           string  // e.g., "/package.Service/Method"
    Authority        string
    DeadlineUnixNano uint64  // RPC deadline (0 if none)
    Metadata         []KV    // key-value pairs
}
```

**Frame I/O functions:**
- `writeFrame(ctx, tx *ShmRing, fh FrameHeader, payload []byte) error` — atomic header+payload reservation
- `readFrame(ctx, rx *ShmRing) (FrameHeader, []byte, error)` — blocking read, auto-skips PAD frames
- `readFrameView(ctx, rx *ShmRing) (FrameHeader, mem.Buffer, error)` — zero-copy variant returning `mem.Buffer`
- `writeFrameBuffers(ctx, tx, fh, header, data mem.BufferSlice) error` — scatter-gather write avoiding intermediate copies

### 1.4 Transport Layer

**Client transport** (`shm_client_transport.go`): Implements `ClientTransport` interface.
- `NewStream(ctx, *CallHdr) (*ClientStream, error)` — allocates odd-numbered stream ID, sends HEADERS
- `processIncomingData(ctx)` — background reader goroutine dispatching frames to streams
- Flow control: `acquireSendQuota()`, `sendWindowUpdate()`, `updateFlowControl()`
- BDP: `sendBDPPing()` integration in message receive path
- Keepalive: `keepalive()` goroutine with dormancy support
- GOAWAY handling with draining mode

**Server transport** (`shm_server_transport.go`): Implements `ServerTransport` interface.
- `HandleStreams(handler, traceCtx func)` — starts reader, passes decoded streams to handler
- `handleHeaders(ctx, streamID, payload)` — creates `ServerStream` with deadline propagation
- `processIncomingData(ctx)` — background reader with frame dispatch (HEADERS, MESSAGE, CANCEL, GOAWAY, WindowUpdate)
- `ConfigureKeepalive(kp, kep)` — applies keepalive params/enforcement, starts keepalive goroutine
- `rejectNewStream(streamID, msg)` — sends GOAWAY + TRAILERS(Unavailable) for rejected streams
- `StreamScheduler` integration for weighted fair queueing

**Streaming support** (`streaming_client.go`, `streaming_server.go`):
- `ShmStreamingClient` / `ShmStreamingServer` — lower-level streaming abstractions
- Per-stream goroutines: `runStreamSender()` reads from `sendQueue` channel
- Frame dispatch: `dispatchHeaders()`, `dispatchMessage()`, `dispatchTrailers()`, `dispatchCancel()`, `dispatchHalfClose()`
- `StreamingClientStream.SendMsg()`, `RecvMsg()`, `CloseSend()`, `RecvHeaders()`

**Unary client** (`client.go`):
- `ShmUnaryClient` — simplified single-RPC client with `UnaryCall()` method

### 1.5 Connection Establishment (`shm_dialer.go`, `shm_listener.go`)

**Client-side dial flow:**
1. Open control segment by name
2. `WaitForServer()` — spin until `ServerReady` flag is set
3. Write `CONNECT` frame on control ring
4. Read `ACCEPT` / `REJECT` response
5. Open data segment (the main segment with Ring A + Ring B)
6. Optional security handshake (if `Handshaker` configured)
7. Create `ShmClientTransport`, `ConfigureKeepalive()`

**Server-side accept flow** (`ShmListener`):
1. Create control segment, set `ServerReady = true`
2. `Accept()` — read `CONNECT` frame, write `ACCEPT`, create data segment
3. Optional security handshake integration
4. Return `ShmConn` implementing `net.Conn`

**Dial options:**
```go
type DialOptions struct {
    SegmentSize     uint64
    RingASize       uint64
    RingBSize       uint64
    ConnectTimeout  time.Duration
    KeepaliveParams keepalive.ClientParameters
    Handshaker      *ShmSecurityHandshaker
}
```

### 1.6 Platform Support

- **Linux**: Futex via `SYS_FUTEX` syscall, `/dev/shm` for POSIX shared memory
- **Windows**: `WaitOnAddress` / `WakeByAddressSingle` for synchronization, named shared memory sections
- Build tag: `//go:build linux || windows` on all SHM source files

### 1.7 Feature Summary

| Feature | Status |
|---------|--------|
| Zero-copy SPSC ring buffers | ✅ |
| Custom 16-byte frame protocol | ✅ |
| Bidirectional streaming (all 4 RPC types) | ✅ |
| Multiplexed streams (odd client / even server IDs) | ✅ |
| HTTP/2-style flow control with WINDOW_UPDATE | ✅ |
| BDP estimation (bandwidth-delay product) | ✅ |
| Keepalive (PING/PONG) — client & server | ✅ |
| Security handshake (nonce-based identity) | ✅ |
| TCP fallback | ✅ |
| `shm://` resolver scheme | ✅ |
| Service config (`ShmTransportPolicy`) | ✅ |
| Deadline propagation | ✅ |
| Metadata propagation | ✅ |
| Cancellation (CANCEL frame) | ✅ |
| Graceful shutdown (GOAWAY draining) | ✅ |
| Stream scheduler (weighted fair queueing) | ✅ |
| Cross-process support | ✅ |
| Adaptive spinning before futex | ✅ |
| Message chunking (MORE flag) | ✅ |
| Large messages (>ring capacity via chunking) | ✅ |
| Linux + Windows | ✅ |
| Credentials package (`credentials/shm`) | ✅ |

---

## 2. Test Coverage

### 2.1 Complete Test File Inventory

#### Internal Transport Tests (`internal/transport/`)

| # | Test File | Test Functions | Category |
|---|-----------|---------------|----------|
| 1 | `shm_test.go` | `TestSegmentHeaderSize`, `TestRingHeaderSize`, `TestSegmentHeaderFieldOffsets`, `TestRingHeaderFieldOffsets`, `TestIsPowerOfTwo`, `TestNextPowerOfTwo`, `TestCalculateSegmentLayout`, `TestRingInvariants`, `TestSegmentHeaderAtomicAccess`, `TestCreateAndOpenSegment`, `TestCreateSegmentAlreadyExists`, `TestOpenSegmentNotExists`, `TestSegmentUtilities`, `TestRingViewOperations`, `TestFutexBasic`, `TestFutexWaitWake`, `TestFutexMultipleWaiters`, `TestFutexValueChange`, `TestFutexSpuriousWake`, `TestFutexWithSharedMemory` | Segment layout, ring headers, atomics, futex |
| 2 | `shm_ring_test.go` | `TestShmRingBasics`, `TestShmRingEmpty`, `TestShmRingWrapAround`, `TestShmRingConcurrent`, `TestShmRingUtilities`, `TestShmRingFullBuffer`, `TestShmRingNoPolling`, `TestShmRingStressSPSC` | Ring buffer operations |
| 3 | `shm_ringbuf_test.go` | `TestRing_WriteRead_Simple`, `TestRing_Write_Wrap`, `TestRing_Write_Closed`, `TestRing_Read_Empty`, `TestRing_WriteRead_Wrap`, `TestRing_Read_PartialRead` | In-memory ring buffer (no shared memory) |
| 4 | `shm_frame_test.go` | `TestWriteReadFrame_MessageChunkingWithMoreFlag`, + frame encode/decode tests | Frame protocol |
| 5 | `shm_bench_test.go` | `BenchmarkShmRingWriteRead`, `BenchmarkShmRingThroughput`, `BenchmarkTCPLoopback`, `BenchmarkUnixSocketRoundtrip`, `BenchmarkTCPLoopbackRoundtrip`, `BenchmarkShmRingRoundtrip`, `BenchmarkUnixSocketLoopback`, `BenchmarkShmRingLargePayloads` | Performance benchmarks |
| 6 | `shm_cross_process_test.go` | `TestCrossProcessRingBuffer`, `TestCrossProcessGRPC` | Cross-process validation |
| 7 | `shm_advanced_test.go` | `TestShmPingPongSizes` (1B/1KB/64KB/1MB), `TestShmConcurrentStreams`, `TestShmStreamError`, `TestShmClientErrorNotify`, `TestShmInflightStreamClosing`, `TestShmContextCanceledOnClose` | Advanced transport scenarios |
| 8 | `shm_coverage_test.go` | `TestShmClientWithMisbehavedServer`, `TestShmServerWithMisbehavedClient`, `TestShmStreamIDExhaustion`, `TestShmGoAwayDrainingCompletesGracefully`, `TestShmPingPong`, `TestShmClientMix`, `TestShmLargeMessageWithDelayRead` | Protocol coverage / edge cases |
| 9 | `shm_connection_test.go` | `TestShmListener`, `TestShmDialer`, `TestShmConnectionEstablishment`, `TestShmConnectionProperties`, `TestShmDialOptions`, `TestShmAddr` | Connection lifecycle |
| 10 | `shm_transport_test.go` | Historical reference only (removed tests, delegates to integration tests) | Transport lifecycle |
| 11 | `shm_fallback_test.go` | TCP fallback handler tests | Fallback mechanism |
| 12 | `shm_aware_dialer_test.go` | `TestTransportSelectorWithDetails`, `TestGetSegmentName`, `TestIsFallbackAllowed`, `TestCanUseShmForAddress`, `TestMustUseShmForAddress`, `TestShmAwareDialerShouldUseShm`, `TestNewTransportSelector` | Transport selection |
| 13 | `shm_attributes_test.go` | `TestShmCapabilitySetGet`, `TestShmCapabilityDisabled`, `TestShmCapabilityEqual`, `TestShmTransportHintSetGet` | Resolver attributes |
| 14 | `shm_resolver_test.go` | `TestShmResolverBuild`, `TestShmResolverRFCA73Attributes` | `shm://` resolver |
| 15 | `shm_service_config_test.go` | `TestDefaultShmServiceConfig`, `TestParseShmServiceConfig`, `TestShmServiceConfigValidate`, `TestShmServiceConfigShouldUseShm` | Service config |
| 16 | `shm_handshake_test.go` | `TestHandshake`, `TestHandshakeTimeout`, `TestShmConn`, `TestShmConnLargeData` | Handshake protocol |
| 17 | `shm_conn_test.go` | `TestShmConnImplementation`, `TestShmConnHandshakeScenarios` | ShmConn net.Conn impl |
| 18 | `shm_selection_test.go` | `TestSelection_ChoosesSHM_and_ExecutesUnary` | Transport selection + unary exec |
| 19 | `shm_stress_test.go` | `TestDebug256MBRoundtrip`, `TestShmStreamSchedulerFairness`, `TestShmBDPEstimation` | Stress + BDP |
| 20 | `shm_simple_cancel_test.go` | `TestSimpleCancellation` | Cancellation |
| 21 | `shm_keepalive_test.go` | Keepalive PING/PONG client/server tests | Keepalive |
| 22 | `shm_flow_control_test.go` | `TestShmFlowControlBlocksUntilWindowUpdate`, `TestShmFlowControlWindowUpdate` | Flow control |
| 23 | `shm_security_test.go` | `TestShmSecurityHandshakeSuccess`, `TestShmSecurityHandshakeIdentityRejected`, `TestShmSecurityHandshakeClientRejectsServer`, `TestShmAuthInfoValidateAuthority`, `TestHandshakeFrameEncoding`, `TestDefaultShmHandshaker`, `TestGenerateNonce` | Security handshake |
| 24 | `shm_chunking_test.go` | Message chunking tests, `BenchmarkChunkedWriteSmallRing` | Large message chunking |
| 25 | `shm_grpc_bench_test.go` | `BenchmarkShmVsTCPComparison` + gRPC-level SHM/TCP comparison benchmarks | gRPC-level benchmarks |
| 26 | `shm_futex_simple_test.go` | Basic futex validation | Futex |
| 27 | `shm_transport_factory_test.go` | Transport factory tests | Factory pattern |
| 28 | `shm_grpc_integration_test.go` | Full gRPC integration with SHM transport | Integration |
| 29 | `shm_client_transport_test.go` | Client transport unit tests | Client transport |

#### Root-Level Tests

| # | Test File | Test Functions | Category |
|---|-----------|---------------|----------|
| 30 | `shm_fullgrpc_test.go` | `TestFullGRPCWithSHM` | Full gRPC server/client with SHM |
| 31 | `shm_grpc_helpers_test.go` | `TestWithShmTransport`, `TestShmDialerIntegration` | API helper tests |

#### Example Tests

| # | Test File | Covered Examples |
|---|-----------|-----------------|
| 32 | `examples/shm/examples_test.sh` | helloworld, route_guide, cancellation, deadline, interceptor, keepalive, metadata |

### 2.2 Test Coverage by Category

| Category | Test Count (approx) | Files |
|----------|---------------------|-------|
| Ring buffer / segment | ~30 | `shm_test.go`, `shm_ring_test.go`, `shm_ringbuf_test.go` |
| Frame protocol | ~10 | `shm_frame_test.go`, `shm_chunking_test.go` |
| Futex / synchronization | ~8 | `shm_test.go`, `shm_futex_simple_test.go` |
| Transport lifecycle | ~15 | `shm_connection_test.go`, `shm_advanced_test.go`, `shm_coverage_test.go` |
| Handshake / conn | ~8 | `shm_handshake_test.go`, `shm_conn_test.go` |
| Cross-process | ~2 | `shm_cross_process_test.go` |
| Flow control / BDP | ~5 | `shm_flow_control_test.go`, `shm_stress_test.go` |
| Keepalive | ~4 | `shm_keepalive_test.go` |
| Security | ~7 | `shm_security_test.go` |
| Fallback / selection | ~15 | `shm_fallback_test.go`, `shm_aware_dialer_test.go`, `shm_selection_test.go` |
| Resolver / config | ~10 | `shm_resolver_test.go`, `shm_service_config_test.go`, `shm_attributes_test.go` |
| Integration | ~5 | `shm_grpc_integration_test.go`, `shm_fullgrpc_test.go`, `shm_grpc_helpers_test.go` |
| Benchmarks | ~15 | `shm_bench_test.go`, `shm_grpc_bench_test.go`, `shm_chunking_test.go` |
| **Total** | **~130+** | **32 test files** |

---

## 3. Activation API

### 3.1 Client-Side API (gRPC DialOptions)

The transport integrates via standard gRPC `DialOption` functions in `shm_grpc_helpers.go`:

```go
// Basic activation — auto-detects segment name from target
grpc.WithShmTransport()

// With custom dial options
grpc.WithShmTransportAndOptions(transport.DialOptions{
    SegmentSize:     128 * 1024 * 1024,
    RingASize:       64 * 1024 * 1024,
    RingBSize:       64 * 1024 * 1024,
    ConnectTimeout:  5 * time.Second,
    KeepaliveParams: keepalive.ClientParameters{Time: 10*time.Second},
    Handshaker:      transport.DefaultShmHandshaker(),
})

// Full config including fallback
grpc.WithShmTransportConfig(transport.ShmTransportConfig{
    DialOptions:        transport.DialOptions{...},
    FallbackEnabled:    true,
    TCPFallbackAddr:    "localhost:50051",
    AllowMixedTransport: true,
})
```

### 3.2 Server-Side API

```go
// Create shared memory listener
lis, err := transport.NewShmListener(
    &transport.ShmAddr{Name: "my_service"},
    segmentSize,   // e.g., 64*1024*1024
    ringASize,     // client→server ring
    ringBSize,     // server→client ring
)

// Use with standard gRPC server
srv := grpc.NewServer()
srv.Serve(lis)
```

### 3.3 Resolver Scheme

The `shm://` resolver (`resolver.go`) resolves targets to SHM addresses with capability attributes:

```go
// Client connects via shm:// scheme
conn, err := grpc.Dial("shm://my_segment_name", grpc.WithShmTransport())
```

The resolver:
1. Extracts segment name from the target URL
2. Sets `ShmCapability{Enabled: true, SegmentName: name}` on the resolved address
3. Enables the load balancer to select the correct transport

### 3.4 Transport Selector

`ShmAwareDialer` (`shm_aware_dialer.go`) provides automatic transport selection:

```go
type TransportType int
const (
    HTTP2 TransportType = iota
    Shm
)

type TransportSelector struct { ... }
// Decides HTTP2 vs Shm based on resolver attributes + service config
```

`NewShmClient()` creates a gRPC client that auto-selects SHM when available.

### 3.5 Service Config

```go
type ShmTransportPolicy string
const (
    ShmPolicyDisabled  ShmTransportPolicy = "disabled"
    ShmPolicyPreferred ShmTransportPolicy = "preferred"
    ShmPolicyRequired  ShmTransportPolicy = "required"
    ShmPolicyAuto      ShmTransportPolicy = "auto"
)
```

Service config JSON: `{"shmPolicy": "auto"}` — allows the load balancer to decide per-address.

---

## 4. Examples

All examples live under `examples/shm/` and demonstrate every major feature:

| Example | Directory | RPC Types | Key Feature Demonstrated |
|---------|-----------|-----------|--------------------------|
| **Hello World** | `examples/shm/helloworld/` | Unary | Basic SHM transport usage |
| **Route Guide** | `examples/shm/route_guide/` | All 4 (Unary, ServerStreaming, ClientStreaming, BiDi) | Complete streaming over SHM |
| **Cancellation** | `examples/shm/features/cancellation/` | BiDi streaming | `context.WithCancel()` propagation |
| **Deadline** | `examples/shm/features/deadline/` | Unary | Deadline propagation via `DeadlineUnixNano` |
| **Interceptor** | `examples/shm/features/interceptor/` | Unary + Streaming | Unary & stream interceptors with auth token |
| **Metadata** | `examples/shm/features/metadata/` | All 4 types | Metadata propagation in headers/trailers |
| **Keepalive** | `examples/shm/features/keepalive/` | — | Client/server keepalive parameters |
| **Error Details** | `examples/shm/features/error_details/` | — | Rich error status propagation |

### 4.1 Example Pattern

All SHM examples follow a consistent pattern:

**Client:**
```go
conn, err := grpc.NewClient(
    "shm://helloworld_shm",
    grpc.WithTransportCredentials(insecure.NewCredentials()),
    grpc.WithShmTransport(),
)
```

**Server:**
```go
lis, err := transport.NewShmListener(
    &transport.ShmAddr{Name: "helloworld_shm"},
    64*1024*1024, 64*1024*1024, 64*1024*1024,
)
srv := grpc.NewServer()
pb.RegisterGreeterServer(srv, &server{})
srv.Serve(lis)
```

### 4.2 Automated Example Tests

`examples/shm/examples_test.sh` validates all examples with expected output:

```bash
declare -A EXPECTED_OUTPUT=(
    ["helloworld"]="Greeting: Hello world"
    ["route_guide"]="Feature: name: \"\", point:(416851321, -742674555)"
    ["features/cancellation"]="cancelling context"
    ["features/deadline"]="wanted = DeadlineExceeded, got = DeadlineExceeded"
    ["features/interceptor"]="UnaryEcho:  hello world"
    ["features/keepalive"]=""
    ["features/metadata"]="this is examples/metadata"
)
```

Additional scripts: `run_shmem_examples.sh`, `run_shmem_examples.ps1`.

---

## 5. TCP Fallback Mechanism

### 5.1 Architecture

TCP fallback is implemented across several files:

| File | Component | Role |
|------|-----------|------|
| `shm_fallback.go` | `ShmFallbackHandler` | Manages fallback state, counts, error classification |
| `shm_grpc_helpers.go` | `dialShmWithFallback()` | Orchestrates SHM dial with TCP fallback |
| `shm_aware_dialer.go` | `ShmAwareDialer`, `TransportSelector` | Per-address transport selection |
| `shm_attributes.go` | `ShmCapability`, `ShmTransportHint` | Resolver attribute propagation |
| `shm_service_config.go` | `ShmTransportPolicy` | Policy-driven transport selection |

### 5.2 Fallback Flow

```
dialShmWithFallback(target, opts)
    │
    ├─ Try DialShm(target, shmOpts)
    │   ├─ Success → return ShmClientTransport
    │   └─ Failure
    │       ├─ IsShmErrorPermanent(err)?
    │       │   ├─ Yes → fallbackHandler.HandleShmError(err)
    │       │   │       → FallbackTransportCreator(TCPFallbackAddr)
    │       │   │       → return TCP transport
    │       │   └─ No  → retry or return error
    │       └─ IsFallbackAllowed(addr, config)?
    │           ├─ Yes → TCP fallback
    │           └─ No  → return error
    └─ atomic fallbackCount tracking
```

### 5.3 Error Classification

`IsShmErrorPermanent(err)` classifies errors as permanent (trigger fallback) vs transient (retry):

- **Permanent**: segment not found, permission denied, platform unsupported
- **Transient**: timeout, connect timeout, temporary resource contention

### 5.4 Transport Policies

| Policy | Behavior |
|--------|----------|
| `disabled` | Never use SHM |
| `preferred` | Try SHM first, fallback to TCP on failure |
| `required` | SHM only, fail if unavailable |
| `auto` | Use SHM if resolver provides capability, else TCP |

### 5.5 Per-Address Selection

`ShmCapability` and `ShmTransportHint` are attached to resolver addresses via `resolver.Address.Attributes`:

```go
// Set by resolver
addr = SetShmCapability(addr, ShmCapability{Enabled: true, SegmentName: "seg"})
addr = SetShmTransportHint(addr, ShmTransportHint{PreferShm: true, FallbackAllowed: true})

// Queried by dialer
cap, ok := GetShmCapability(addr)
hint, ok := GetShmTransportHint(addr)
```

---

## 6. Memory Copy Analysis

### 6.1 SHM Transport: 1 Copy Per Direction

| Step | Operation | Copies |
|------|-----------|--------|
| **Send** | App calls `Write(data)` | |
| | `ReserveWrite(size)` → returns mmap pointers | 0 |
| | `copy(reservation.First, data)` | **1** (app → mmap) |
| | `Commit()` → atomic store + futex wake | 0 |
| **Receive** | `ReadSlices()` → returns mmap pointers | 0 |
| | Reader copies from mmap to app buffer | **1** (mmap → app) |
| | `Commit()` → atomic store + futex wake | 0 |
| **Total per direction** | | **1 copy** |

### 6.2 Zero-Copy Variants

The `readFrameView()` function returns a `mem.Buffer` backed by the ring's mmap, enabling **true zero-copy reads** when the payload doesn't straddle the wrap boundary. The caller must `Free()` the buffer to release the ring reservation.

However, the current implementation notes: *"Always copy and commit immediately to ensure correct ring buffer index tracking. The deferred commit pattern via ringCommitPool is unsafe because it can cause sharedReadIdx to be set incorrectly when multiple..."* — suggesting zero-copy reads are currently disabled for safety.

### 6.3 TCP Transport: 4 Copies Per Direction (Reference)

| Step | Operation | Copies |
|------|-----------|--------|
| 1 | App → gRPC send buffer | 1 |
| 2 | gRPC buffer → kernel socket buffer | 1 |
| 3 | Kernel socket buffer → kernel receive buffer | 1 |
| 4 | Kernel receive buffer → App | 1 |
| **Total per direction** | | **4 copies** |

### 6.4 Summary

| Transport | Copies/Direction | Kernel Involvement |
|-----------|------------------|--------------------|
| **SHM** | **1** | None (direct mmap) |
| **TCP loopback** | **4** | Full kernel networking stack |
| **Improvement** | **4x fewer copies** | **No kernel data path** |

---

## 7. Kernel Call Analysis

### 7.1 Synchronization Primitives

| Operation | Linux | Windows |
|-----------|-------|---------|
| Wait (sleep) | `futex(FUTEX_WAIT)` | `WaitOnAddress()` |
| Wake (signal) | `futex(FUTEX_WAKE)` | `WakeByAddressSingle()` |

### 7.2 Adaptive Spinning

Before making a kernel call, the ring buffer uses adaptive spinning:

```go
// Simplified from ring.go
for spin := 0; spin < spaceSpinCutoff; spin++ {
    if atomic.Load(&r.ridx) != cachedRidx {
        return // data available, no syscall needed
    }
    runtime.Gosched() // yield to scheduler
}
// Only now fall through to futex
futexWait(&r.spaceSeq, currentSeq, timeout)
```

### 7.3 Syscalls Per Operation

| Scenario | Kernel Calls | Description |
|----------|-------------|-------------|
| **Fast path (data ready)** | **0** | Atomic load finds data, no futex needed |
| **Slow path (must wait)** | **1** | `futex(FUTEX_WAIT)` on sequence counter |
| **Wake peer** | **0-1** | Only if `*Waiters > 0` flag indicates sleeping peer |
| **Typical steady-state** | **0-1 per send/receive** | Adaptive spin avoids most futex calls |

### 7.4 Sequence Counter Protocol

Three independent futex-backed counters minimize contention:

| Counter | Waiter | Purpose |
|---------|--------|---------|
| `dataSeq` | Reader | Incremented when new data is committed |
| `spaceSeq` | Writer | Incremented when space is freed after read |
| `contigSeq` | Writer (contiguous) | Incremented when contiguous space changes |

Each counter has a corresponding `*Waiters` atomic flag. The producer/consumer only calls `futex_wake` if the waiter flag is set, avoiding unnecessary syscalls when the peer is actively spinning.

### 7.5 Comparison with TCP Loopback

| Operation | TCP Syscalls | SHM Syscalls |
|-----------|-------------|-------------|
| Send | `write()` / `sendmsg()` | 0 (or 1 futex_wake if peer sleeping) |
| Receive | `read()` / `recvmsg()` / `epoll_wait()` | 0 (or 1 futex_wait if ring empty) |
| Per RPC (unary) | ~6-10 syscalls | **0-2 syscalls** |

---

## 8. Flow Control & BDP Estimation

### 8.1 Window-Based Flow Control

The SHM transport implements HTTP/2-style flow control at both connection and stream levels:

**Connection-level:**
- `connSendQuota uint32` — total bytes allowed across all streams
- `sendWindowUpdate()` sends `FrameTypeWindowUpdate` with 4-byte LE delta

**Stream-level:**
- `streamSendQuota map[uint32]uint32` — per-stream send quota
- `streamInFlow map[uint32]*inFlow` — per-stream receive flow control (reuses HTTP/2 `inFlow` type)
- `quotaSignal chan struct{}` — channel-based notification when quota becomes available

**Quota acquisition (`acquireSendQuota`):**
```go
func (t *ShmClientTransport) acquireSendQuota(streamID uint32, size uint32) error {
    for {
        // Check connection-level quota
        if t.connSendQuota >= size {
            // Check stream-level quota
            if t.streamSendQuota[streamID] >= size {
                t.connSendQuota -= size
                t.streamSendQuota[streamID] -= size
                return nil
            }
        }
        // Wait for WINDOW_UPDATE
        <-t.quotaSignal
    }
}
```

**Window update dispatch:**
- Incoming `FrameTypeWindowUpdate` → `addSendQuota(streamID, delta)` → signal `quotaSignal`
- `updateFlowControl(initialWindowSize)` — adjusts all stream limits when settings change

### 8.2 BDP Estimation (`shm_flow_control.go`)

The `shmBDPEstimator` measures bandwidth-delay product to optimize window sizes:

```go
type shmBDPEstimator struct {
    // Atomic fast-path fields
    settled     atomic.Bool
    sampleAtomic atomic.Uint32
    isSentAtomic atomic.Bool

    // Mutex-protected slow path
    mu          sync.Mutex
    bdp         uint32
    bwMax       float64
    sentAt      time.Time
    sampleCount uint64
    rtt         float64
}
```

**Constants (shared with HTTP/2):**

| Constant | Value | Meaning |
|----------|-------|---------|
| `alpha` | 0.9 | EMA weight for RTT |
| `beta` | 0.66 | Threshold for BDP growth |
| `gamma` | 2 | Multiplicative increase factor |
| `bdpLimit` | 16 MB | Maximum BDP window |

**BDP Protocol:**
1. After receiving sufficient bytes, `add(n)` returns `true` → trigger BDP ping
2. `sendBDPPing()` writes `PING` frame with `PingFlagBDP` and timestamp payload
3. `timesnap(data)` records send time
4. Peer responds with `PONG` containing the original timestamp
5. `calculate()` computes:
   - RTT via exponential moving average: `rtt = alpha * rtt + (1-alpha) * sample`
   - BDP = `bwMax * rtt`
   - If new BDP exceeds `beta * current_bdp`, grow by `gamma` factor

The BDP estimator integrates into the message receive path:
```go
// In processIncomingData(), on FrameTypeMESSAGE:
if t.bdpEst != nil {
    sendBDPPing = t.bdpEst.add(messageSize)
}
if sendBDPPing {
    go t.sendBDPPing()
}
```

### 8.3 Stream Scheduler (`StreamScheduler`)

Implements **Weighted Fair Queueing (WFQ)** for multiplexed streams:

```go
type StreamScheduler struct {
    streams    map[uint32]*StreamPriority
    mu         sync.Mutex
}

type StreamPriority struct {
    StreamID    uint32
    Weight      uint32    // higher = more bandwidth
    VirtualTime float64   // for WFQ scheduling
}
```

- `AddStream(id, weight)` — register stream with priority
- `RemoveStream(id)` — deregister
- `NextStream()` — returns stream with lowest virtual time (most underserved)
- Virtual time advances by `bytes_sent / weight` after each send

Tested by `TestShmStreamSchedulerFairness` in `shm_stress_test.go`.

---

## 9. Keepalive Implementation

### 9.1 Overview

Keepalive uses PING (0x06) / PONG (0x07) frames over the SHM rings, fully mirroring HTTP/2 gRPC keepalive semantics.

### 9.2 Client Keepalive (`ShmClientTransport.keepalive()`)

**Parameters** (from `keepalive.ClientParameters`):

| Parameter | Default | Description |
|-----------|---------|-------------|
| `Time` | ∞ | How often to send PING when no activity |
| `Timeout` | 20s | How long to wait for PONG before closing |
| `PermitWithoutStream` | false | Send PINGs even with no active streams |

**Dormancy support:**
- When no active streams and `PermitWithoutStream=false`, keepalive enters dormancy
- `kpDormancyCond *sync.Cond` — signaled when new streams are created
- On dormancy exit, immediately sends a PING

**Algorithm:**
```
loop:
    if no streams && !PermitWithoutStream:
        sleep on kpDormancyCond
    wait(Time) since last activity
    if activity during wait:
        continue
    send PING
    wait(Timeout) for PONG
    if PONG received:
        continue
    else:
        Close(connectionTimedOut)
```

**Implementation:** `sendPing()` writes 8-byte timestamp payload via `writeFrame(ctx, tx, FrameHeader{Type: FrameTypePING}, data[:])`.

### 9.3 Server Keepalive (`ShmServerTransport.keepalive()`)

**Parameters** (from `keepalive.ServerParameters`):

| Parameter | Default | Description |
|-----------|---------|-------------|
| `MaxConnectionIdle` | ∞ | Close connections idle this long |
| `MaxConnectionAge` | ∞ | Close connections older than this |
| `MaxConnectionAgeGrace` | ∞ | Grace period after MaxConnectionAge |
| `Time` | 2h | How often to send keepalive PING |
| `Timeout` | 20s | How long to wait for PONG |

**Defaults applied in `ConfigureKeepalive()`:**
```go
if kp.MaxConnectionIdle == 0 { kp.MaxConnectionIdle = infinity }
if kp.MaxConnectionAge == 0  { kp.MaxConnectionAge = infinity }
if kp.Time == 0              { kp.Time = defaultServerKeepaliveTime }  // 2h
if kp.Timeout == 0           { kp.Timeout = defaultServerKeepaliveTimeout } // 20s
```

**Algorithm (4 concurrent timers):**
```
start:
    idle_timer = MaxConnectionIdle
    age_timer = MaxConnectionAge
    keepalive_timer = Time

    select:
    case <-idle_timer:
        if no active streams since last check:
            send GOAWAY(DRAINING) → close
    case <-age_timer:
        send GOAWAY(DRAINING)
        wait(MaxConnectionAgeGrace)
        close
    case <-keepalive_timer:
        send PING
        wait(Timeout) for PONG
        if no PONG: close
```

### 9.4 Server PING Enforcement (`handlePing()`)

**Enforcement policy** (from `keepalive.EnforcementPolicy`):

| Parameter | Default | Description |
|-----------|---------|-------------|
| `MinTime` | 5min | Minimum allowed PING interval from client |
| `PermitWithoutStream` | false | Allow PINGs when no streams |

**Enforcement algorithm:**
```
handlePing(payload):
    respond with PONG immediately
    if time_since_last_ping < MinTime:
        pingStrikes++
    if !PermitWithoutStream && no_active_streams:
        pingStrikes++
    if pingStrikes > maxPingStrikes:
        send GOAWAY(ENHANCE_YOUR_CALM)
        close
```

Constants: `defaultPingTimeout = 20s`, `maxPingStrikes` triggers GOAWAY with `ENHANCE_YOUR_CALM` debug data.

---

## 10. Security Handshake

### 10.1 Overview

The `ShmSecurityHandshaker` (`shm_security.go`) provides mutual identity verification over the SHM rings using a 3-step nonce-based protocol. It is **optional** — if no handshaker is configured, the transport operates without identity verification.

### 10.2 Protocol

```
Client                        Server
  │                              │
  │  HandshakeInit (0x20)        │
  │  [version, identity, nonce]  │
  │─────────────────────────────>│
  │                              │  VerifyIdentity(client_identity)
  │  HandshakeResp (0x21)        │
  │  [version, identity, nonce]  │
  │<─────────────────────────────│
  │                              │
  │  VerifyIdentity(server_id)   │
  │  HandshakeAck (0x22)         │
  │─────────────────────────────>│
  │                              │
  │     [Connection established] │
  │                              │
```

**On failure at any step:** `HandshakeFail (0x23)` with error code.

### 10.3 Data Structures

```go
type ShmSecurityHandshaker struct {
    Identity       string                    // e.g., "pid:12345"
    VerifyIdentity func(string) error        // custom verification
}

type ShmAuthInfo struct {
    CommonAuthInfo credentials.CommonAuthInfo  // SecurityLevel = PrivacyAndIntegrity
    LocalIdentity  string
    RemoteIdentity string
    Nonce          []byte
}
```

### 10.4 Constants

| Constant | Value | Description |
|----------|-------|-------------|
| `HandshakeVersion` | 1 | Protocol version |
| `NonceSize` | 16 bytes | Cryptographic nonce size |
| `MaxIdentitySize` | 256 bytes | Maximum identity string length |
| `HandshakeTimeout` | 5 seconds | Overall handshake timeout |

### 10.5 Error Codes

| Code | Name | Description |
|------|------|-------------|
| 1 | `VersionMismatch` | Protocol version incompatible |
| 2 | `IdentityInvalid` | `VerifyIdentity()` returned error |
| 3 | `NonceMismatch` | Nonce replay/mismatch detected |
| 4 | `Timeout` | Handshake exceeded 5s |
| 5 | `Internal` | Unexpected internal error |

### 10.6 Default Handshaker

```go
func DefaultShmHandshaker() *ShmSecurityHandshaker {
    return &ShmSecurityHandshaker{
        Identity: fmt.Sprintf("pid:%d", os.Getpid()),
    }
}
```

Uses PID-based identity — the `VerifyIdentity` callback is nil, accepting any identity.

### 10.7 Credentials Package (`credentials/shm/shm.go`)

Implements `credentials.TransportCredentials`:

```go
type shmTC struct {
    options Options
}

type Options struct {
    Identity       string
    VerifyIdentity func(string) error
}

type Info struct {
    credentials.CommonAuthInfo   // SecurityLevel = PrivacyAndIntegrity
    LocalIdentity  string
    RemoteIdentity string
}

func NewCredentials() TransportCredentials
func NewCredentialsWithOptions(opts Options) TransportCredentials
```

`ClientHandshake` / `ServerHandshake` check if the underlying transport has already completed the SHM security handshake (via `ShmAuthInfo` on the connection). If so, they return the existing auth info. Otherwise, they create PID-based identity info.

### 10.8 Integration Points

- `DialOptions.Handshaker` — set to enable client-side handshake
- `ShmListener.Accept()` — integrates server-side handshake after CONNECT/ACCEPT
- Security handshake occurs **after** the control-plane handshake (CONNECT/ACCEPT) and **before** creating the transport layer

---

## Appendix: Key File Index

| File | LOC (est) | Purpose |
|------|-----------|---------|
| `ring.go` | ~1640 | SPSC ring buffer with futex sync |
| `frame.go` | ~680 | Frame protocol (encode/decode/read/write) |
| `shm_client_transport.go` | ~900 | Client transport (ClientTransport impl) |
| `shm_server_transport.go` | ~1240 | Server transport (ServerTransport impl) |
| `shm_flow_control.go` | ~300 | BDP estimator + StreamScheduler |
| `shm_security.go` | ~420 | Security handshake protocol |
| `shm_dialer.go` | ~200 | Client-side connection establishment |
| `shm_listener.go` | ~150 | Server-side listener/accept |
| `shm_grpc_helpers.go` | ~200 | gRPC DialOption integration |
| `shm_fallback.go` | ~150 | TCP fallback handler |
| `shm_aware_dialer.go` | ~200 | Transport selector |
| `shm_attributes.go` | ~100 | Resolver attribute types |
| `shm_service_config.go` | ~100 | Service config policy |
| `resolver.go` | ~100 | `shm://` resolver |
| `control_wire.go` | ~100 | CONNECT/ACCEPT/REJECT framing |
| `streaming_client.go` | ~360 | Low-level streaming client |
| `streaming_server.go` | ~440 | Low-level streaming server |
| `client.go` | ~360 | Unary client (`ShmUnaryClient`) |
| `credentials/shm/shm.go` | ~150 | Transport credentials |
| `SHM_TRANSPORT_DESIGN.md` | ~790 | Design document |
| `HTTP2_INTEGRATION.md` | ~100 | HTTP/2 integration notes |
