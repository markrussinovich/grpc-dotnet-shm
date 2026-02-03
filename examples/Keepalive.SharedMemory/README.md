# Keepalive Shared Memory Example

This example demonstrates keepalive configuration with gRPC over shared memory transport.

## Concepts Demonstrated

- **Client Keepalive Pings**: Sending periodic pings to maintain connection health
- **Ping Timeout**: Detecting dead connections when pings aren't acknowledged
- **PermitWithoutStream**: Allowing pings even when no active streams exist
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

## Keepalive Parameters

### ShmKeepaliveOptions
- **Time**: Duration between keepalive pings (default: Infinite)
- **PingTimeout**: Time to wait for ping acknowledgment (default: 20s)
- **PermitWithoutStream**: Allow pings without active streams (default: false)

### Example Configuration
```csharp
var keepaliveOptions = new ShmKeepaliveOptions
{
    Time = TimeSpan.FromSeconds(10),
    PingTimeout = TimeSpan.FromSeconds(1),
    PermitWithoutStream = true
};
```

## Expected Output

Server:
```
Keepalive Example - Shared Memory Server
Listening on shm://keepalive_shm
Server configured with keepalive parameters:
  Time: 5s
  PingTimeout: 1s
  PermitWithoutStream: True

Server started, waiting for connections...
Received request for: /echo.Echo/UnaryEcho
Received UnaryEcho: "keepalive demo"
```

Client:
```
Keepalive Example - Shared Memory Client
Connecting to shm://keepalive_shm
Client configured with keepalive parameters:
  Time: 10s
  PingTimeout: 1s
  PermitWithoutStream: True

Connected to server
Performing unary request
RPC response: keepalive demo
Waiting to observe keepalive pings...
```
