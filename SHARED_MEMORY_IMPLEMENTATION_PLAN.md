# gRPC .NET Shared Memory Transport Implementation Plan

## Overview

This document tracks the incremental implementation of a shared memory transport for gRPC .NET, based on the [grpc-go-shmem](https://github.com/markrussinovich/grpc-go-shmem) implementation. The goal is byte-level framing compatibility with grpc-go-shmem for cross-language interoperability.

## Key Principles (from grpc-go-shmem)

1. **No polling** - Use event-driven blocking (Windows events / Linux futex)
2. **Minimal memory copies** - Ideally zero-copy operations
3. **Minimal kernel calls** - Batch operations, use futex efficiently

## Architecture Overview

### From grpc-go-shmem

```
┌─────────────────────────────────────────────────────────────────┐
│                         gRPC Layer                              │
├─────────────────────────────────────────────────────────────────┤
│  ShmClientTransport  │  ShmServerTransport │  ClientTransport   │
│  (ClientTransport)   │  (ServerTransport)  │  Provider Interface│
├─────────────────────────────────────────────────────────────────┤
│                     Frame Protocol Layer                        │
│  Frame Types: HEADERS, MESSAGE, TRAILERS, CANCEL, GOAWAY, etc.  │
│  16-byte header: [length:4][streamID:4][type:1][flags:1][rsv:6] │
├─────────────────────────────────────────────────────────────────┤
│                      Ring Buffer Layer                          │
│  SPSC Ring Buffer with futex/events synchronization             │
│  Bidirectional: Ring A (client→server), Ring B (server→client)  │
├─────────────────────────────────────────────────────────────────┤
│                     Shared Memory Segment                       │
│  Header: [version:4][maxStreams:4][ringA_offset:8]...           │
│  Ring A: [header:64][data:N]  Ring B: [header:64][data:N]       │
└─────────────────────────────────────────────────────────────────┘
```

## Implementation Phases

### Phase 1: Low-Level Transport Primitives ✅ Current
**Goal**: Implement ring buffer, segment, and frame protocol matching grpc-go byte layout

#### 1.1 Ring Buffer Header (64 bytes, matching Go)
```
Offset  Size  Field
0       8     writeIdx       (monotonic, atomic)
8       8     readIdx        (monotonic, atomic)
16      4     dataSeq        (futex/event signaling)
20      4     spaceSeq       (futex/event signaling)
24      4     contigSeq      (contiguity signaling)
28      4     closed         (atomic flag)
32      4     dataWaiters    (count of blocked readers)
36      4     spaceWaiters   (count of blocked writers)
40      8     capacity       (ring data area size, power of 2)
48      16    reserved       (future use)
```

#### 1.2 Frame Header (16 bytes, little-endian)
```
Offset  Size  Field
0       4     length         (payload length, excludes header)
4       4     streamID       (client=odd, server=even)
8       1     type           (FrameType enum)
9       1     flags          (per-type flags)
10      2     reserved       (zero)
12      4     reserved2      (zero)
```

#### 1.3 Frame Types
```csharp
enum FrameType : byte
{
    Pad = 0x00,           // Padding/alignment
    Headers = 0x01,       // Request/response headers
    Message = 0x02,       // Data payload
    Trailers = 0x03,      // Trailing metadata + status
    Cancel = 0x04,        // Stream cancellation
    GoAway = 0x05,        // Connection shutdown
    Ping = 0x06,          // Health check request
    Pong = 0x07,          // Health check response
    HalfClose = 0x08,     // Half-close stream
    WindowUpdate = 0x09   // Flow control
}
```

#### 1.4 Files to Create
- [ ] `src/Grpc.Net.SharedMemory/RingHeader.cs` - Ring buffer header structure
- [ ] `src/Grpc.Net.SharedMemory/ShmRing.cs` - SPSC ring buffer with blocking
- [ ] `src/Grpc.Net.SharedMemory/Segment.cs` - Shared memory segment management
- [ ] `src/Grpc.Net.SharedMemory/FrameHeader.cs` - Frame header encoding/decoding
- [ ] `src/Grpc.Net.SharedMemory/FrameProtocol.cs` - Frame read/write operations
- [ ] `src/Grpc.Net.SharedMemory/Synchronization/` - Platform-specific sync
  - [ ] `Futex_Linux.cs` - Linux futex wrapper
  - [ ] `NamedEvents_Windows.cs` - Windows named events wrapper
  - [ ] `ISyncPrimitive.cs` - Abstraction interface

#### 1.5 Tests to Create First (TDD)
- [ ] `test/Grpc.Net.SharedMemory.Tests/RingBufferTests.cs`
  - WriteBlocking/ReadBlocking basic operations
  - Wrap-around handling
  - Full buffer blocking
  - Close behavior (drain remaining data)
  - Context cancellation
- [ ] `test/Grpc.Net.SharedMemory.Tests/FrameProtocolTests.cs`
  - Frame header encoding/decoding
  - Frame round-trip
  - Headers/Trailers encoding
  - Chunked message handling
- [ ] `test/Grpc.Net.SharedMemory.Tests/SegmentTests.cs`
  - Create/Open segment
  - Cross-process sharing (separate test process)

### Phase 2: gRPC Transport Integration
**Goal**: Implement ClientTransport and ServerTransport interfaces

#### 2.1 Client Transport
- [ ] `src/Grpc.Net.SharedMemory/ShmClientTransport.cs`
  - Implement stream creation
  - Header/Trailer serialization (matching Go format)
  - Flow control (send quota)
  - Keepalive support
  - Graceful shutdown (GOAWAY)

#### 2.2 Server Transport  
- [ ] `src/Grpc.Net.SharedMemory/ShmServerTransport.cs`
  - Accept connections (via listener)
  - Stream handling
  - Response writing

#### 2.3 Integration Points
- [ ] `src/Grpc.Net.SharedMemory/ShmChannelHandler.cs` - HttpMessageHandler for GrpcChannel
- [ ] `src/Grpc.Net.SharedMemory/ShmListener.cs` - Listener for ASP.NET Core
- [ ] `src/Grpc.Net.SharedMemory/ShmGrpcServiceExtensions.cs` - Builder extensions

#### 2.4 Tests (matching Go test coverage)
- [ ] `test/Grpc.Net.SharedMemory.Tests/ShmClientTransportTests.cs`
- [ ] `test/Grpc.Net.SharedMemory.Tests/ShmServerTransportTests.cs`
- [ ] `test/Grpc.Net.SharedMemory.Tests/FlowControlTests.cs`
- [ ] `test/Grpc.Net.SharedMemory.Tests/GracefulShutdownTests.cs`

### Phase 3: E2E Examples & Full Test Coverage
**Goal**: Working examples matching existing gRPC examples

#### 3.1 Examples to Create
- [ ] `examples/Greeter.SharedMemory/` - Basic unary RPC
- [ ] `examples/Counter.SharedMemory/` - All 4 RPC types
- [ ] `examples/Streaming.SharedMemory/` - Focus on streaming

#### 3.2 Functional Tests
- [ ] `test/FunctionalTests/SharedMemory/`
  - Unary calls
  - Server streaming
  - Client streaming
  - Bidirectional streaming
  - Error handling
  - Cancellation
  - Deadline propagation
  - Metadata passing

### Phase 4: Interoperability Testing
**Goal**: Verify byte-level compatibility with grpc-go-shmem

- [ ] Cross-language test harness
- [ ] .NET client → Go server
- [ ] Go client → .NET server
- [ ] Round-trip message verification

### Phase 5: Performance Optimization
**Goal**: Match or exceed grpc-go-shmem performance characteristics

- [ ] Benchmarks comparing to TCP loopback
- [ ] Adaptive spin tuning
- [ ] Buffer pool integration
- [ ] Zero-copy optimizations

---

## Current Status

**Phase**: 1 - Low-Level Transport Primitives
**Status**: Phase 1.1 ✅ Complete, Phase 1.2 ✅ Complete
**Next Step**: Phase 2 - gRPC Transport Integration

### Phase 1.1 Completed (Commit: 99a3d75c)
- ✅ ShmRing SPSC ring buffer with blocking read/write
- ✅ FrameHeader encoding/decoding (16 bytes, little-endian)
- ✅ FrameTypes enum matching Go implementation
- ✅ Platform synchronization (IRingSync, WindowsRingSync, LinuxRingSync stubs)
- ✅ 34 unit tests passing

### Phase 1.2 Completed
- ✅ Segment.cs - Shared memory segment with dual ring buffers
- ✅ FrameProtocol.cs - Frame I/O operations (WriteFrame, ReadFrame, etc.)
- ✅ HeadersV1.cs - Binary encoding/decoding for gRPC headers
- ✅ TrailersV1.cs - Binary encoding/decoding for gRPC trailers
- ✅ HeadersTrailersTests.cs - Encode/decode round-trip tests
- ✅ SegmentTests.cs - Segment creation and initialization tests
- ✅ Updated FrameProtocolTests.cs - Full frame protocol integration tests
- ✅ 45 unit tests passing total

### Known Limitations (to address in Phase 2)
- Segment currently copies memory-mapped data to local buffer (breaks true sharing)
- Cross-process memory sharing needs unsafe/pointer-based implementation
- Linux futex implementation is stubbed (uses polling fallback)

---

## File Structure

```
src/
  Grpc.Net.SharedMemory/
    Grpc.Net.SharedMemory.csproj
    RingHeader.cs
    ShmRing.cs
    Segment.cs
    FrameHeader.cs
    FrameProtocol.cs
    FrameTypes.cs
    HeadersV1.cs
    TrailersV1.cs
    ShmClientTransport.cs
    ShmServerTransport.cs
    ShmListener.cs
    ShmChannelHandler.cs
    ShmGrpcServiceExtensions.cs
    Synchronization/
      ISyncPrimitive.cs
      Futex_Linux.cs
      NamedEvents_Windows.cs
      CrossPlatformSync.cs

test/
  Grpc.Net.SharedMemory.Tests/
    Grpc.Net.SharedMemory.Tests.csproj
    RingBufferTests.cs
    FrameProtocolTests.cs
    SegmentTests.cs
    ShmClientTransportTests.cs
    ShmServerTransportTests.cs
    FlowControlTests.cs

examples/
  Greeter.SharedMemory/
    Client/
    Server/
    Proto/
```

---

## Binary Protocol Details (for interoperability)

### Segment Header Layout (64 bytes)
```
Offset  Size  Field           Notes
0       4     magic           0x53484D31 ("SHM1")
4       4     version         1
8       4     maxStreams      Max concurrent streams
12      4     flags           Reserved
16      8     ringAOffset     Offset to Ring A in segment
24      8     ringACapacity   Data area capacity for Ring A
32      8     ringBOffset     Offset to Ring B in segment
40      8     ringBCapacity   Data area capacity for Ring B
48      16    reserved        Zero-filled
```

### Ring Layout (in segment)
```
[ RingHeader (64 bytes) ][ Data Area (capacity bytes) ]
```

### Headers V1 Payload Encoding
```
Offset  Size  Field
0       1     version (must be 1)
1       1     hdrType (0=client-initial, 1=server-initial)
2       4     methodLen (only if hdrType==0)
6       N     method bytes
...     4     authorityLen
...     N     authority bytes
...     8     deadlineUnixNano (0 if none)
...     2     metadataCount
... per metadata:
        2     keyLen
        N     key bytes
        2     valueCount
    per value:
        4     valueLen
        N     value bytes
```

### Trailers V1 Payload Encoding
```
Offset  Size  Field
0       1     version (must be 1)
1       4     grpcStatusCode
5       4     grpcStatusMsgLen
9       N     grpcStatusMsg bytes
...     2     metadataCount
... (same metadata encoding as headers)
```

---

## Dependencies

### .NET APIs Used
- `System.IO.MemoryMappedFiles` - Shared memory segments
- `System.Threading` - Synchronization primitives
- `System.Runtime.InteropServices` - P/Invoke for futex/events
- `System.Buffers` - Memory pooling

### Platform Support
- Windows 10+ (named events for cross-process sync)
- Linux (futex syscall for cross-process sync)
- .NET 8+ (for modern API support)

---

## Notes

### Key Differences from grpc-go-shmem
1. .NET uses async/await instead of goroutines - need ValueTask-based APIs
2. .NET has GrpcChannel abstraction - integrate via HttpMessageHandler
3. ASP.NET Core uses Kestrel - integrate via custom listener

### Testing Strategy
1. **Unit tests first** - Ring buffer, frame encoding
2. **Integration tests** - Transport layer
3. **Functional tests** - Full gRPC calls
4. **Interop tests** - Cross-language verification
