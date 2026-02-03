# Cancellation Shared Memory Example

This example demonstrates client-side cancellation of RPC calls over shared memory transport.

## Design

Mirrors the Go grpc-go-shmem `examples/shm/features/cancellation` example.

The client:
- Starts a bidirectional streaming RPC
- Sends a few messages
- Cancels the context mid-stream
- Observes cancellation propagated to server

The server:
- Receives messages until cancellation
- Logs when cancellation is detected

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
Sending message 1
Sending message 2
Sending message 3
Cancelling context
Stream cancelled successfully
```

**Server:**
```
Received: message 1
Received: message 2
Received: message 3
Server: error receiving from stream: context canceled
```
