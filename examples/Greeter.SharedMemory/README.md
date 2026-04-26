# Greeter.SharedMemory

This example demonstrates gRPC communication using shared memory transport instead of HTTP/2 over TCP. This provides ultra-low latency communication between processes on the same machine.

## Overview

The shared memory transport uses:
- **SPSC Ring Buffers**: Lock-free single-producer single-consumer ring buffers for data transfer
- **Frame Protocol**: 16-byte frame headers with length, stream ID, type, and flags
- **Blocking I/O**: Uses Windows events (or Linux futex) for synchronization with zero polling
- **Stream Multiplexing**: Multiple concurrent gRPC streams over a single shared memory segment

## Projects

- **Server**: Listens on a shared memory segment and handles Greeter requests
- **Client**: Connects to the shared memory segment using `ShmControlHandler` with standard `GrpcChannel`

## Running the Example

### Prerequisites
- .NET 9.0 SDK
- Windows (Linux support for futex is in development)

### Start the Server
```bash
cd Server
dotnet run
```

### Run the Client (in a separate terminal)
```bash
cd Client
dotnet run
```

## Key Components

### Client Side (ShmControlHandler)

```csharp
// Create handler that connects to shared memory segment
using var handler = new ShmControlHandler("greeter-shm");

// Use with standard GrpcChannel
using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
{
    HttpHandler = handler
});

// Create typed client and make calls
var client = new Greeter.GreeterClient(channel);
var reply = await client.SayHelloAsync(new HelloRequest { Name = "World" });
```

### Server Side (ShmConnectionListener)

```csharp
using var listener = new ShmConnectionListener("greeter-shm",
    ringCapacity: 1024 * 1024,
    maxStreams: 100);

// Accept and process incoming streams
// See Program.cs for the handling loop
```

## Performance Characteristics

- **Zero kernel calls**: Uses user-space synchronization primitives
- **Minimal memory copies**: Data is read directly from shared memory buffers
- **No serialization overhead**: gRPC messages are written directly to shared memory
- **Sub-microsecond latency**: Typical for small messages on same-machine communication

## Segment Naming

The shared memory segment name (e.g., "greeter-shm") must match between client and server. The server creates the segment, and client(s) connect to it.

## Current Limitations

- Windows only (Linux futex implementation pending)
- Single client per segment (multiple streams supported)
- No authentication/authorization (use for same-machine trusted communication)
- Example uses simplified request handling (full ASP.NET Core integration in progress)

## Related Documentation

See [SHARED_MEMORY_IMPLEMENTATION_PLAN.md](../../../SHARED_MEMORY_IMPLEMENTATION_PLAN.md) for the complete implementation roadmap.
