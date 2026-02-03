# Counter.SharedMemory Example

This example demonstrates unary, client streaming, and server streaming over shared memory transport.

## Overview

This is the shared memory transport equivalent of the `Counter` example.
It demonstrates:
- Unary RPC (IncrementCount)
- Client streaming RPC (AccumulateCount)
- Server streaming RPC (Countdown)
- Zero-copy shared memory transport

## Running the Example

### 1. Start the Server

```bash
cd examples/Counter.SharedMemory/Server
dotnet run
```

### 2. Run the Client

In a separate terminal:

```bash
cd examples/Counter.SharedMemory/Client
dotnet run
```

## Key Concepts

### Streaming Patterns

The example demonstrates all three streaming patterns:

1. **Unary RPC**: Simple request/response
   ```csharp
   var reply = await client.IncrementCountAsync(new Empty());
   ```

2. **Client Streaming**: Client sends multiple messages
   ```csharp
   using var call = client.AccumulateCount();
   await call.RequestStream.WriteAsync(new CounterRequest { Count = 5 });
   await call.RequestStream.CompleteAsync();
   var response = await call;
   ```

3. **Server Streaming**: Server sends multiple messages
   ```csharp
   using var call = client.Countdown(new Empty());
   await foreach (var message in call.ResponseStream.ReadAllAsync())
   {
       Console.WriteLine($"Count: {message.Count}");
   }
   ```

### Shared Memory Transport

```csharp
using var handler = new ShmHandler(segmentName);
using var channel = GrpcChannel.ForAddress("shm://localhost", new GrpcChannelOptions
{
    HttpHandler = handler
});
```

## Comparison with TCP Counter

| Aspect | TCP Counter | SHM Counter |
|--------|-------------|-------------|
| Transport | HTTP/2 over TCP | Shared Memory |
| Latency | Network round-trip | Zero-copy IPC |
| Streaming | Full duplex | Full duplex |
| Use Case | Remote services | Same-machine services |

## Notes

- Windows-only for now (uses named memory sections)
- Counter state is maintained on the server
- Countdown streams from current count to 0
