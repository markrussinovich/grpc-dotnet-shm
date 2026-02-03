# Deadline Shared Memory Example

This example demonstrates deadline/timeout handling over shared memory transport.

## Design

Mirrors the Go grpc-go-shmem `examples/shm/features/deadline` example.

The server:
- Echoes messages back normally
- Delays responses when message contains "delay"
- Propagates deadlines when message contains "[propagate me]"

The client:
- Sends requests with various deadlines
- Tests successful calls, exceeded deadlines, and deadline propagation

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
request 1: wanted = OK, got = OK
request 2: wanted = DeadlineExceeded, got = DeadlineExceeded
request 3: wanted = OK, got = OK
request 4: wanted = DeadlineExceeded, got = DeadlineExceeded
```
