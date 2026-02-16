# Go ↔ .NET Shared Memory Interop Testing

This directory contains tools for testing binary compatibility and E2E interop between
grpc-go-shmem and Grpc.Net.SharedMemory.

## Prerequisites

1. **Install Go** (1.21+):
   ```bash
   # On Ubuntu/Debian
   sudo apt install golang-go
   
   # Or download from https://go.dev/dl/
   ```

2. **Clone grpc-go-shmem**:
   ```bash
   git clone https://github.com/markrussinovich/grpc-go-shmem.git
   cd grpc-go-shmem
   ```

3. **Build the .NET examples**:
   ```bash
   cd /workspaces/grpc-dotnet-shm/examples/Greeter.SharedMemory
   dotnet build
   ```

## Test Scenarios

### Scenario 1: Go Server + .NET Client

1. **Start Go server** (in grpc-go-shmem directory):
   ```bash
   cd examples/shm/greeter
   go run server/main.go -segment greeter_interop
   ```

2. **Run .NET client**:
   ```bash
   cd /workspaces/grpc-dotnet-shm/examples/Greeter.SharedMemory/Client
   # Edit Program.cs to use segment name "greeter_interop"
   dotnet run
   ```

### Scenario 2: .NET Server + Go Client

1. **Start .NET server**:
   ```bash
   cd /workspaces/grpc-dotnet-shm/examples/Greeter.SharedMemory/Server
   dotnet run
   ```

2. **Run Go client** (in grpc-go-shmem directory):
   ```bash
   cd examples/shm/greeter
   go run client/main.go -segment greeter_shm_example
   ```

### Scenario 3: Binary Compatibility Verification

Generate Go binary dumps and verify .NET can read them:

```bash
# Generate binary dumps from Go
cd /workspaces/grpc-dotnet-shm/test/Grpc.Net.SharedMemory.Tests/GoInterop
go run binary_dump.go -output .

# Run .NET interop tests
cd /workspaces/grpc-dotnet-shm
dotnet test test/Grpc.Net.SharedMemory.Tests --filter "FullyQualifiedName~InteropTests"
```

## Creating Custom Interop Tests

### Option A: Use the existing Greeter proto

Both implementations should use the same proto:
```protobuf
syntax = "proto3";
package greet;

service Greeter {
  rpc SayHello (HelloRequest) returns (HelloReply);
}

message HelloRequest {
  string name = 1;
}

message HelloReply {
  string message = 1;
}
```

### Option B: Create a dedicated interop test

**Go Server (interop_server.go)**:
```go
package main

import (
    "flag"
    "log"
    "net"
    
    "google.golang.org/grpc"
    pb "path/to/greet"
    "github.com/markrussinovich/grpc-go-shmem/shm"
)

var segment = flag.String("segment", "interop_test", "Shared memory segment name")

type server struct {
    pb.UnimplementedGreeterServer
}

func (s *server) SayHello(ctx context.Context, req *pb.HelloRequest) (*pb.HelloReply, error) {
    return &pb.HelloReply{Message: "Hello from Go: " + req.Name}, nil
}

func main() {
    flag.Parse()
    
    lis, err := shm.Listen(*segment)
    if err != nil {
        log.Fatalf("failed to listen: %v", err)
    }
    
    s := grpc.NewServer()
    pb.RegisterGreeterServer(s, &server{})
    
    log.Printf("Go server listening on shm://%s", *segment)
    if err := s.Serve(lis); err != nil {
        log.Fatalf("failed to serve: %v", err)
    }
}
```

**.NET Client (InteropClient.cs)**:
```csharp
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;
using Greet;

var segmentName = args.Length > 0 ? args[0] : "interop_test";

using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
{
    HttpHandler = new ShmControlHandler(segmentName),
    DisposeHttpClient = true
});

var client = new Greeter.GreeterClient(channel);
var reply = await client.SayHelloAsync(new HelloRequest { Name = ".NET Client" });
Console.WriteLine($"Response: {reply.Message}");
```

## Troubleshooting

### Segment not found
- Ensure both sides use the same segment name
- On Windows, check `%TEMP%\grpc_shm_{name}` exists
- On Linux, check `/dev/shm/grpc_shm_{name}` exists

### Magic mismatch
- Verify both implementations use `GRPCSHM\0` (8 bytes)
- Run the binary dump tests to verify header layouts

### Timeout waiting for data
- Check that server is started before client
- Verify ring buffer sizes are compatible

### Platform-specific issues
- **Windows**: Uses named events for synchronization
- **Linux**: Uses futex syscalls
- Both must run on the same OS for interop

## Verification Checklist

- [ ] Segment header (128 bytes) layout matches
- [ ] Ring header (64 bytes) layout matches  
- [ ] Frame header (16 bytes) layout matches
- [ ] Headers/Trailers V1 encoding matches
- [ ] Futex/event synchronization works cross-process
- [ ] Unary RPC works
- [ ] Server streaming works
- [ ] Client streaming works
- [ ] Bidirectional streaming works
