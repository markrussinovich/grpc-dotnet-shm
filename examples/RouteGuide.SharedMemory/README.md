# RouteGuide Shared Memory Example

This example demonstrates all four gRPC RPC types using shared memory transport:

- **Unary RPC**: `GetFeature` - Get a feature at a given point
- **Server Streaming RPC**: `ListFeatures` - List features within a rectangle
- **Client Streaming RPC**: `RecordRoute` - Record a route and get a summary
- **Bidirectional Streaming RPC**: `RouteChat` - Send and receive route notes

## Design

This example matches the design of the Go grpc-go-shmem `examples/shm/route_guide` example.

## Prerequisites

- .NET 9.0 SDK or later
- Windows or Linux

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

**Server:**
```
RouteGuide server listening on shared memory segment: routeguide_shm
```

**Client:**
```
Feature: name: "Patriot's Path, Mendham, NJ 07945, USA", point:(409146138, -746188906)
Feature: name: "", point:(0, 0)
Looking for features between 40, -75 and 42, -73
...
Traversing 10 points.
Route summary: point_count:10 feature_count:4 distance:123456 elapsed_time:...
Got message "First message" at point(0, 1)
...
```
