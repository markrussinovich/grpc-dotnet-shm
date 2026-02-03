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

using System.Net;
using System.Text;
using Grpc.Core;
using Grpc.Net.SharedMemory;

const string SegmentName = "keepalive_shm";

Console.WriteLine("Keepalive Example - Shared Memory Server");
Console.WriteLine($"Listening on shm://{SegmentName}");
Console.WriteLine();

// Configure keepalive options matching the Go example
// Note: ShmKeepaliveOptions has Time and PingTimeout for basic keepalive
// Server-specific options like MaxConnectionIdle/MaxConnectionAge would be
// configured at a higher level in a full gRPC server implementation
var keepaliveOptions = new ShmKeepaliveOptions
{
    Time = TimeSpan.FromSeconds(5),            // Ping if idle for 5 seconds
    PingTimeout = TimeSpan.FromSeconds(1),     // Wait 1 second for ping ack
    PermitWithoutStream = true                 // Allow pings without active streams
};

Console.WriteLine("Server configured with keepalive parameters:");
Console.WriteLine($"  Time: {keepaliveOptions.Time.TotalSeconds}s");
Console.WriteLine($"  PingTimeout: {keepaliveOptions.PingTimeout.TotalSeconds}s");
Console.WriteLine($"  PermitWithoutStream: {keepaliveOptions.PermitWithoutStream}");
Console.WriteLine();

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

var connectionStart = DateTime.UtcNow;

try
{
    // Accept streams directly from listener
    await foreach (var stream in listener.AcceptStreamsAsync(cts.Token))
    {
        connectionStart = DateTime.UtcNow;
        await HandleStreamAsync(stream, connectionStart);
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Server stopped.");
}

async Task HandleStreamAsync(ShmGrpcStream stream, DateTime connStart)
{
    try
    {
        // Read request headers
        var requestHeaders = await stream.ReceiveRequestHeadersAsync();
        var method = requestHeaders.Method ?? "unknown";
        Console.WriteLine($"Received request for: {method}");

        if (method == "/echo.Echo/UnaryEcho")
        {
            // Read request message
            var frame = await stream.ReceiveFrameAsync();
            if (frame?.Type == FrameType.Message && frame.Value.Payload.Length > 5)
            {
                var message = Encoding.UTF8.GetString(frame.Value.Payload.AsSpan(5));
                Console.WriteLine($"Received UnaryEcho: \"{message}\"");

                // Send response headers
                await stream.SendResponseHeadersAsync();

                // Echo back
                var responseBytes = Encoding.UTF8.GetBytes(message);
                var framedResponse = new byte[5 + responseBytes.Length];
                framedResponse[0] = 0;
                var lengthBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(responseBytes.Length));
                Buffer.BlockCopy(lengthBytes, 0, framedResponse, 1, 4);
                Buffer.BlockCopy(responseBytes, 0, framedResponse, 5, responseBytes.Length);

                await stream.SendMessageAsync(framedResponse);
                await stream.SendTrailersAsync(StatusCode.OK);
            }
        }
        else
        {
            await stream.SendTrailersAsync(StatusCode.Unimplemented, $"Unknown method: {method}");
        }

        // Log connection age
        var age = DateTime.UtcNow - connStart;
        Console.WriteLine($"Connection age: {age.TotalSeconds:F1}s");
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
