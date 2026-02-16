# Shared Memory Interop Tests

This directory contains cross-language interop tests between **grpc-go-shmem** (Go) and **Grpc.Net.SharedMemory** (.NET).

## Quick Start

### Prerequisites

- Go 1.21+ (check: `go version`)
- .NET 9.0+ (check: `dotnet --version`)
- protobuf-compiler (check: `protoc --version`)

### Build Go Binaries (from grpc-go-shmem)

```bash
# Clone grpc-go-shmem if not already done
cd /workspaces
git clone https://github.com/markrussinovich/grpc-go-shmem.git

# Build Go server and client
cd /workspaces/grpc-go-shmem/examples/shm/helloworld/greeter_server
go build -o /tmp/greeter_server .

cd /workspaces/grpc-go-shmem/examples/shm/helloworld/greeter_client  
go build -o /tmp/greeter_client .
```

### Build .NET Components

```bash
cd /workspaces/grpc-dotnet-shm/test/InteropTests.SharedMemory

# Build .NET client
cd dotnet-client && dotnet build

# Build .NET server
cd ../dotnet-server && dotnet build
```

## Test Scenarios

### Scenario 1: Go Server + Go Client (Verify Go works)

```bash
# Terminal 1: Start Go server
/tmp/greeter_server -addr "shm://test_segment"

# Terminal 2: Run Go client
/tmp/greeter_client -addr "shm://test_segment" -name "World"

# Expected output:
# Greeting: Hello World
```

### Scenario 2: .NET Server + .NET Client (Verify .NET works)

```bash
# Terminal 1: Start .NET server
cd dotnet-server && dotnet run -- test_segment

# Terminal 2: Run .NET client
cd dotnet-client && dotnet run -- test_segment
```

### Scenario 3: Go Server + .NET Client (Cross-language)

```bash
# Terminal 1: Start Go server
/tmp/greeter_server -addr "shm://interop_segment"

# Terminal 2: Run .NET client
cd dotnet-client && dotnet run -- interop_segment
```

### Scenario 4: .NET Server + Go Client (Cross-language)

```bash
# Terminal 1: Start .NET server
cd dotnet-server && dotnet run -- interop_segment

# Terminal 2: Run Go client
/tmp/greeter_client -addr "shm://interop_segment" -name "GoClient"
```

## Protocol Compatibility

Both implementations use the same shared memory protocol:

| Component | Size | Description |
|-----------|------|-------------|
| Segment Header | 128 bytes | Magic "GRPCSHM\0", version, ring offsets |
| Ring Header | 64 bytes | Capacity, write/read indices, futex sequences |
| Frame Header | 16 bytes | Length, stream ID, type, flags |

### Frame Types
- HEADERS (0x01) - Request/response headers
- MESSAGE (0x02) - Protobuf payload  
- TRAILERS (0x03) - Status and trailing metadata
- CANCEL (0x04) - Stream cancellation
- GOAWAY (0x05) - Connection shutdown

## Troubleshooting

### "Segment not found"
- Ensure the server is started before the client
- Check both use the same segment name
- On Linux: Check /dev/shm/grpc_shm_<name> exists

### Build errors
```bash
# Regenerate Go protobuf
cd go
protoc --go_out=greetpb --go-grpc_out=greetpb \
       --go_opt=paths=source_relative \
       --go-grpc_opt=paths=source_relative \
       -I .. ../greet.proto
```

## Notes

- The Go examples use internal packages from grpc-go-shmem
- For interop testing, use pre-built binaries from grpc-go-shmem repo
- The .NET implementation uses ShmControlHandler for canonical GrpcChannel integration
