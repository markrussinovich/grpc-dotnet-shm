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

using System.Diagnostics;
using System.Net;
using System.Text;
using Grpc.Core;
using Grpc.Net.SharedMemory;

const string SegmentName = "interceptor_shm";
const string ExpectedToken = "Bearer some-secret-token";

Console.WriteLine("Interceptor Example - Shared Memory Server");
Console.WriteLine($"Listening on shm://{SegmentName}");
Console.WriteLine();

// Logger helper
void Logger(string message)
{
    Console.WriteLine($"LOG:\t{message}");
}

using var listener = new ShmConnectionListener(SegmentName, ringCapacity: 1024 * 1024, maxStreams: 100);
Console.WriteLine("Server started, waiting for connections...");
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
    await foreach (var stream in listener.AcceptStreamsAsync(cts.Token))
    {
        _ = HandleStreamAsync(stream);
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Server stopped.");
}

async Task HandleStreamAsync(ShmGrpcStream stream)
{
    var stopwatch = Stopwatch.StartNew();

    try
    {
        // Read request headers
        var requestHeaders = await stream.ReceiveRequestHeadersAsync();
        var method = requestHeaders.Method ?? "unknown";

        Logger($"Received RPC: {method} at {DateTime.Now:O}");

        // Check authorization header (interceptor pattern)
        var authKV = requestHeaders.Metadata?.FirstOrDefault(h => h.Key == "authorization");
        var authBytes = authKV?.Values.FirstOrDefault();
        var authValue = authBytes != null ? Encoding.UTF8.GetString(authBytes) : null;
        if (authValue == null)
        {
            Logger("Authorization header missing!");
            await stream.SendTrailersAsync(StatusCode.Unauthenticated, "Missing authorization header");
            return;
        }

        Logger($"Authorization: {authValue}");

        if (authValue != ExpectedToken)
        {
            Logger($"Invalid token: {authValue}");
            await stream.SendTrailersAsync(StatusCode.Unauthenticated, "Invalid authorization token");
            return;
        }

        // Route based on method
        switch (method)
        {
            case "/echo.Echo/UnaryEcho":
                await HandleUnaryEcho(stream);
                break;
            case "/echo.Echo/BidirectionalStreamingEcho":
                await HandleBidiStreamingEcho(stream);
                break;
            default:
                await stream.SendTrailersAsync(StatusCode.Unimplemented, $"Unknown method: {method}");
                break;
        }

        stopwatch.Stop();
        Logger($"RPC completed in {stopwatch.ElapsedMilliseconds}ms");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Stream error: {ex.Message}");
    }
    finally
    {
        stream.Dispose();
    }
}

async Task HandleUnaryEcho(ShmGrpcStream stream)
{
    Logger($"Processing unary RPC at {DateTime.Now:O}");

    // Read request message
    var frame = await stream.ReceiveFrameAsync();
    if (frame?.Type != FrameType.Message)
    {
        await stream.SendTrailersAsync(StatusCode.InvalidArgument, "No request received");
        return;
    }

    // Skip 5-byte header
    var message = Encoding.UTF8.GetString(frame.Value.Payload.AsSpan(5));
    Console.WriteLine($"unary echoing message \"{message}\"");

    // Send response headers
    await stream.SendResponseHeadersAsync();

    // Send response message (echo back)
    var responseBytes = Encoding.UTF8.GetBytes(message);
    var framedResponse = new byte[5 + responseBytes.Length];
    framedResponse[0] = 0; // Not compressed
    var lengthBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(responseBytes.Length));
    Buffer.BlockCopy(lengthBytes, 0, framedResponse, 1, 4);
    Buffer.BlockCopy(responseBytes, 0, framedResponse, 5, responseBytes.Length);

    await stream.SendMessageAsync(framedResponse);

    // Send trailers
    await stream.SendTrailersAsync(StatusCode.OK);
}

async Task HandleBidiStreamingEcho(ShmGrpcStream stream)
{
    Logger($"Processing bidirectional streaming RPC at {DateTime.Now:O}");

    // Send response headers
    await stream.SendResponseHeadersAsync();

    // Echo messages until client closes
    while (true)
    {
        var frame = await stream.ReceiveFrameAsync();
        if (frame == null || frame.Value.Type == FrameType.HalfClose || frame.Value.Type == FrameType.Trailers)
        {
            break;
        }

        if (frame.Value.Type == FrameType.Message)
        {
            Logger($"RecvMsg: {frame.Value.Payload.Length} bytes");

            // Skip 5-byte header
            var message = Encoding.UTF8.GetString(frame.Value.Payload.AsSpan(5));
            Console.WriteLine($"bidi echoing message \"{message}\"");

            // Echo back
            var responseBytes = Encoding.UTF8.GetBytes(message);
            var framedResponse = new byte[5 + responseBytes.Length];
            framedResponse[0] = 0;
            var lengthBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(responseBytes.Length));
            Buffer.BlockCopy(lengthBytes, 0, framedResponse, 1, 4);
            Buffer.BlockCopy(responseBytes, 0, framedResponse, 5, responseBytes.Length);

            Logger($"SendMsg: {framedResponse.Length} bytes");
            await stream.SendMessageAsync(framedResponse);
        }
    }

    // Send trailers
    await stream.SendTrailersAsync(StatusCode.OK);
    Logger("Streaming RPC completed");
}
