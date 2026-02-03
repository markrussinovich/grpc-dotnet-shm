# Retrier.SharedMemory Example

This example demonstrates retry policies over shared memory transport.

## Overview

This is the shared memory transport equivalent of the `Retrier` example.
It demonstrates:
- Automatic retry with configurable policies
- Exponential backoff
- Retryable status codes
- Zero-copy shared memory transport

## Running the Example

### 1. Start the Server

```bash
cd examples/Retrier.SharedMemory/Server
dotnet run
```

### 2. Run the Client

In a separate terminal:

```bash
cd examples/Retrier.SharedMemory/Client
dotnet run
```

## Key Concepts

### Retry Policy

The client configures a retry policy using `ShmRetryPolicy`:

```csharp
var retryPolicy = new ShmRetryPolicy
{
    MaxAttempts = 5,
    InitialBackoff = TimeSpan.FromMilliseconds(500),
    MaxBackoff = TimeSpan.FromSeconds(5),
    BackoffMultiplier = 2.0,
    RetryableStatusCodes = new HashSet<StatusCode> { StatusCode.Unavailable }
};
```

### Shared Memory Transport

The client uses `ShmHandler` for zero-copy communication:

```csharp
using var handler = new ShmHandler(segmentName);
using var channel = GrpcChannel.ForAddress("shm://localhost", new GrpcChannelOptions
{
    HttpHandler = handler
});
```

## Comparison with TCP Retrier

| Aspect | TCP Retrier | SHM Retrier |
|--------|-------------|-------------|
| Transport | HTTP/2 over TCP | Shared Memory |
| Retry Config | `ServiceConfig` | `ShmRetryPolicy` |
| Latency | Network round-trip | Zero-copy IPC |
| Use Case | Remote services | Same-machine services |

## Notes

- Windows-only for now (uses named memory sections)
- Server simulates 50% failure rate for demonstration
- Retry count is shown in the client output
