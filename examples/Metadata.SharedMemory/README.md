# Metadata Shared Memory Example

This example demonstrates metadata (headers/trailers) handling over shared memory transport.

## Design

Mirrors the Go grpc-go-shmem `examples/shm/features/metadata` example.

Features demonstrated:
- Sending custom metadata from client
- Receiving metadata on server
- Sending response metadata (headers and trailers) from server
- Receiving response metadata on client
- Timestamp metadata for timing analysis

## Running

### Start the server

```bash
cd Server
dotnet run
```

### Start the client

```bash
cd Client
dotnet run
```

## Expected output

**Client:**
```
=== Unary Call with Metadata ===
Sending request with timestamp metadata
Received response: Echo: this is examples/metadata
Response headers received with timestamp

=== Server Streaming with Metadata ===
...
```

**Server:**
```
Received metadata: timestamp = Jan 15 15:30:45.123456789
Echoing message: this is examples/metadata
```
