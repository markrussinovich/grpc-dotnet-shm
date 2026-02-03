# Compression SharedMemory Example

This example demonstrates gzip compression over shared memory transport, matching the grpc-go-shmem compression example.

## Features

- Gzip compression for message payloads
- Compression ratio monitoring
- Server-side compression negotiation

## Running the Example

1. Start the server:
```bash
dotnet run --project Server
```

2. In another terminal, start the client:
```bash
dotnet run --project Client
```

## Implementation Details

The shared memory transport supports compression via the `grpc-encoding` header, similar to HTTP/2 gRPC:

- Client sends `grpc-accept-encoding: gzip,deflate,identity` in request headers
- Server negotiates and responds with chosen encoding
- Messages are compressed before writing to the ring buffer
- Compression is transparent to the application layer

## Compression Options

```csharp
var compressionOptions = new ShmCompressionOptions
{
    Enabled = true,
    SendCompress = "gzip",
    MinSizeForCompression = 1024  // Only compress messages > 1KB
};
```
