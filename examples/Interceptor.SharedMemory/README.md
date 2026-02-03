# Interceptor Shared Memory Example

This example demonstrates interceptors with gRPC over shared memory transport.

## Concepts Demonstrated

- **Unary Interceptors**: Intercepting and modifying unary RPC calls
- **Stream Interceptors**: Intercepting and modifying streaming RPC calls
- **Authentication**: Adding auth tokens via interceptors
- **Logging**: Logging RPC timing and method information
- **Shared Memory Transport**: Zero-copy IPC for local communication

## Running the Example

1. Start the server:
   ```bash
   cd Server
   dotnet run
   ```

2. In another terminal, run the client:
   ```bash
   cd Client
   dotnet run
   ```

## What This Shows

### Server-Side Interceptor
- Validates `authorization` header (expects "Bearer some-secret-token")
- Logs each incoming RPC with method name and timing
- Wraps stream to intercept send/receive operations

### Client-Side Interceptor
- Adds authorization token to all outgoing requests
- Logs method invocation timing
- Wraps streams to log message send/receive

## Expected Output

Server:
```
Interceptor Example - Shared Memory Server
Listening on shm://interceptor_shm

LOG:    Received RPC: /echo.Echo/UnaryEcho at 2025-01-01T12:00:00
LOG:    Authorization: Bearer some-secret-token
unary echoing message "hello world"
LOG:    RPC completed in 5ms

LOG:    Received streaming RPC: /echo.Echo/BidirectionalStreamingEcho at 2025-01-01T12:00:01
bidi echoing message "Request 1"
bidi echoing message "Request 2"
...
```

Client:
```
Interceptor Example - Shared Memory Client
Connecting to shm://interceptor_shm

LOG:    RPC: /echo.Echo/UnaryEcho, start time: 12:00:00, end time: 12:00:00, err: (null)
UnaryEcho: hello world

LOG:    Starting streaming RPC: /echo.Echo/BidirectionalStreamingEcho
LOG:    SendMsg: Request 1
LOG:    RecvMsg: Request 1
...
```
