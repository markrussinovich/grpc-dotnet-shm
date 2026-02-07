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

using Echo;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;

// The ONLY transport difference from TCP is using ShmHandler instead of the default HttpHandler.
// All gRPC API usage (metadata, headers, trailers, etc.) remains identical.

const string SegmentName = "metadata_shm";
const string TimestampFormat = "MMM dd HH:mm:ss.fffffff";
const string Message = "this is examples/metadata";

Console.WriteLine("Metadata Example - Shared Memory Client");
Console.WriteLine($"Connecting to shm://{SegmentName}");
Console.WriteLine();

try
{
    using var handler = new ShmHandler(SegmentName);
    using var channel = GrpcChannel.ForAddress("shm://localhost", new GrpcChannelOptions
    {
        HttpHandler = handler
    });

    var client = new Echo.Echo.EchoClient(channel);
    Console.WriteLine("Connected to server");
    Console.WriteLine();

    // ============================================================
    // Unary Call with Metadata
    // ============================================================
    Console.WriteLine("=== Unary Call with Metadata ===");
    await UnaryCallWithMetadata(client, Message);
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Make sure the server is running first:");
    Console.WriteLine("  cd examples/Metadata.SharedMemory/Server");
    Console.WriteLine("  dotnet run");
}

Console.WriteLine();
Console.WriteLine("All metadata tests completed!");

async Task UnaryCallWithMetadata(Echo.Echo.EchoClient client, string message)
{
    // Create custom request metadata
    var headers = new Metadata
    {
        { "timestamp", DateTime.UtcNow.ToString(TimestampFormat, System.Globalization.CultureInfo.InvariantCulture) },
        { "client-id", "shm-client-1" }
    };

    Console.WriteLine("Sending request with metadata:");
    foreach (var entry in headers)
    {
        Console.WriteLine($"  {entry.Key} = {entry.Value}");
    }

    // Use AsyncUnaryCall to access response headers and trailers
    using var call = client.UnaryEchoAsync(
        new EchoRequest { Message = message },
        new CallOptions(headers: headers));

    // Read response headers (sent by the server before the response body)
    var responseHeaders = await call.ResponseHeadersAsync;
    Console.WriteLine();
    Console.WriteLine("Received response headers:");
    foreach (var entry in responseHeaders)
    {
        if (!entry.Key.StartsWith(':') && !entry.Key.StartsWith("grpc-", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(entry.IsBinary
                ? $"  {entry.Key} = (binary, {entry.ValueBytes.Length} bytes)"
                : $"  {entry.Key} = {entry.Value}");
        }
    }

    // Read the response message
    var response = await call.ResponseAsync;
    Console.WriteLine();
    Console.WriteLine($"Received response: {response.Message}");

    // Read response trailers (available after the response is fully received)
    var trailers = call.GetTrailers();
    Console.WriteLine();
    Console.WriteLine("Received trailers:");
    foreach (var entry in trailers)
    {
        if (!entry.Key.StartsWith("grpc-", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(entry.IsBinary
                ? $"  {entry.Key} = (binary, {entry.ValueBytes.Length} bytes)"
                : $"  {entry.Key} = {entry.Value}");
        }
    }
}
