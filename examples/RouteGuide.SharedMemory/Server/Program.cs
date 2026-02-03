#region Copyright notice and license

// Copyright 2025 The gRPC Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

#endregion

using Grpc.Net.SharedMemory;
using Server.Services;

const string SegmentName = "routeguide_shm";

Console.WriteLine("RouteGuide Shared Memory Server");
Console.WriteLine($"Segment name: {SegmentName}");
Console.WriteLine();

// Create the RouteGuide service
var service = new RouteGuideService();

// Create the shared memory listener
// This mirrors the Go: transport.NewShmListener(&transport.ShmAddr{Name: name}, ...)
using var listener = new ShmConnectionListener(
    SegmentName,
    ringCapacity: 2 * 1024 * 1024, // 2MB ring buffer
    maxStreams: 100);

Console.WriteLine($"Server listening on shm://{SegmentName}");
Console.WriteLine("Press Ctrl+C to stop.");
Console.WriteLine();

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\nShutting down...");
    cts.Cancel();
};

try
{
    // Accept connections
    while (!cts.Token.IsCancellationRequested)
    {
        var connection = await listener.AcceptAsync(cts.Token);
        if (connection == null) continue;

        // Handle connection in background
        _ = Task.Run(async () =>
        {
            try
            {
                Console.WriteLine($"Client connected");

                // Accept streams from this connection
                await foreach (var stream in connection.AcceptStreamsAsync(cts.Token))
                {
                    _ = HandleStreamAsync(stream, service, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"Connection error: {ex.Message}");
            }
            finally
            {
                connection.Dispose();
            }
        }, cts.Token);
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Server stopped.");
}

async Task HandleStreamAsync(ShmGrpcStream stream, RouteGuideService svc, CancellationToken ct)
{
    try
    {
        var method = stream.RequestHeaders?.Method ?? "";
        Console.WriteLine($"Received call: {method}");

        // Route to appropriate handler based on method
        // In production, use proper gRPC server integration
        switch (method)
        {
            case "/routeguide.RouteGuide/GetFeature":
                await HandleGetFeature(stream, svc, ct);
                break;
            case "/routeguide.RouteGuide/ListFeatures":
                await HandleListFeatures(stream, svc, ct);
                break;
            case "/routeguide.RouteGuide/RecordRoute":
                await HandleRecordRoute(stream, svc, ct);
                break;
            case "/routeguide.RouteGuide/RouteChat":
                await HandleRouteChat(stream, svc, ct);
                break;
            default:
                Console.WriteLine($"Unknown method: {method}");
                await stream.SendTrailersAsync(Grpc.Core.StatusCode.Unimplemented, $"Unknown method: {method}");
                break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Stream error: {ex.Message}");
        try
        {
            await stream.SendTrailersAsync(Grpc.Core.StatusCode.Internal, ex.Message);
        }
        catch { }
    }
    finally
    {
        stream.Dispose();
    }
}

async Task HandleGetFeature(ShmGrpcStream stream, RouteGuideService svc, CancellationToken ct)
{
    // Receive request
    var frame = await stream.ReceiveFrameAsync(ct);
    if (frame?.Type != Grpc.Net.SharedMemory.FrameType.Data)
    {
        await stream.SendTrailersAsync(Grpc.Core.StatusCode.InvalidArgument, "Expected data frame");
        return;
    }

    var request = RouteGuide.Point.Parser.ParseFrom(frame.Value.Payload.AsSpan(5)); // Skip 5-byte header
    
    // Call service
    var context = new DummyServerCallContext();
    var response = await svc.GetFeature(request, context);

    // Send response headers
    await stream.SendResponseHeadersAsync();

    // Send response message
    var responseBytes = response.ToByteArray();
    var framedResponse = new byte[5 + responseBytes.Length];
    framedResponse[0] = 0; // Not compressed
    BitConverter.TryWriteBytes(framedResponse.AsSpan(1), (uint)System.Net.IPAddress.HostToNetworkOrder(responseBytes.Length));
    responseBytes.CopyTo(framedResponse, 5);
    await stream.SendMessageAsync(framedResponse);

    // Send trailers
    await stream.SendTrailersAsync(Grpc.Core.StatusCode.OK);
}

async Task HandleListFeatures(ShmGrpcStream stream, RouteGuideService svc, CancellationToken ct)
{
    // Receive request
    var frame = await stream.ReceiveFrameAsync(ct);
    if (frame?.Type != Grpc.Net.SharedMemory.FrameType.Data)
    {
        await stream.SendTrailersAsync(Grpc.Core.StatusCode.InvalidArgument, "Expected data frame");
        return;
    }

    var request = RouteGuide.Rectangle.Parser.ParseFrom(frame.Value.Payload.AsSpan(5));

    // Send response headers
    await stream.SendResponseHeadersAsync();

    // Stream features
    var writer = new ShmServerStreamWriter<RouteGuide.Feature>(stream);
    var context = new DummyServerCallContext();
    await svc.ListFeatures(request, writer, context);

    // Send trailers
    await stream.SendTrailersAsync(Grpc.Core.StatusCode.OK);
}

async Task HandleRecordRoute(ShmGrpcStream stream, RouteGuideService svc, CancellationToken ct)
{
    // Send response headers first (for client streaming)
    await stream.SendResponseHeadersAsync();

    var reader = new ShmAsyncStreamReader<RouteGuide.Point>(stream, ct);
    var context = new DummyServerCallContext();
    var response = await svc.RecordRoute(reader, context);

    // Send response message
    var responseBytes = response.ToByteArray();
    var framedResponse = new byte[5 + responseBytes.Length];
    framedResponse[0] = 0;
    BitConverter.TryWriteBytes(framedResponse.AsSpan(1), (uint)System.Net.IPAddress.HostToNetworkOrder(responseBytes.Length));
    responseBytes.CopyTo(framedResponse, 5);
    await stream.SendMessageAsync(framedResponse);

    await stream.SendTrailersAsync(Grpc.Core.StatusCode.OK);
}

async Task HandleRouteChat(ShmGrpcStream stream, RouteGuideService svc, CancellationToken ct)
{
    await stream.SendResponseHeadersAsync();

    var reader = new ShmAsyncStreamReader<RouteGuide.RouteNote>(stream, ct);
    var writer = new ShmServerStreamWriter<RouteGuide.RouteNote>(stream);
    var context = new DummyServerCallContext();
    await svc.RouteChat(reader, writer, context);

    await stream.SendTrailersAsync(Grpc.Core.StatusCode.OK);
}

// Minimal implementations for demo purposes
class DummyServerCallContext : Grpc.Core.ServerCallContext
{
    protected override string MethodCore => "";
    protected override string HostCore => "";
    protected override string PeerCore => "";
    protected override DateTime DeadlineCore => DateTime.MaxValue;
    protected override Grpc.Core.Metadata RequestHeadersCore => new();
    protected override CancellationToken CancellationTokenCore => CancellationToken.None;
    protected override Grpc.Core.Metadata ResponseTrailersCore => new();
    protected override Grpc.Core.Status StatusCore { get; set; }
    protected override Grpc.Core.WriteOptions? WriteOptionsCore { get; set; }
    protected override Grpc.Core.AuthContext AuthContextCore => null!;
    protected override IDictionary<object, object> UserStateCore => new Dictionary<object, object>();
    protected override Grpc.Core.ContextPropagationToken CreatePropagationTokenCore(Grpc.Core.ContextPropagationOptions? options) => null!;
    protected override Task WriteResponseHeadersAsyncCore(Grpc.Core.Metadata responseHeaders) => Task.CompletedTask;
}

class ShmServerStreamWriter<T> : Grpc.Core.IServerStreamWriter<T> where T : Google.Protobuf.IMessage
{
    private readonly ShmGrpcStream _stream;
    public Grpc.Core.WriteOptions? WriteOptions { get; set; }

    public ShmServerStreamWriter(ShmGrpcStream stream) => _stream = stream;

    public async Task WriteAsync(T message)
    {
        var bytes = message.ToByteArray();
        var framed = new byte[5 + bytes.Length];
        framed[0] = 0;
        BitConverter.TryWriteBytes(framed.AsSpan(1), (uint)System.Net.IPAddress.HostToNetworkOrder(bytes.Length));
        bytes.CopyTo(framed, 5);
        await _stream.SendMessageAsync(framed);
    }
}

class ShmAsyncStreamReader<T> : Grpc.Core.IAsyncStreamReader<T> where T : Google.Protobuf.IMessage<T>, new()
{
    private readonly ShmGrpcStream _stream;
    private readonly CancellationToken _ct;
    private T? _current;

    public ShmAsyncStreamReader(ShmGrpcStream stream, CancellationToken ct)
    {
        _stream = stream;
        _ct = ct;
    }

    public T Current => _current!;

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        var frame = await _stream.ReceiveFrameAsync(cancellationToken);
        if (frame == null || frame.Value.Type == Grpc.Net.SharedMemory.FrameType.Trailers)
            return false;
        if (frame.Value.Type == Grpc.Net.SharedMemory.FrameType.Data)
        {
            var parser = new Google.Protobuf.MessageParser<T>(() => new T());
            _current = parser.ParseFrom(frame.Value.Payload.AsSpan(5));
            return true;
        }
        return false;
    }
}
