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
// Compression is handled transparently at the SHM transport layer via ShmCompressionOptions.
// No special gRPC-level configuration is needed.

const string SegmentName = "compression_example_shm";

Console.WriteLine("=== Compression SharedMemory Client ===");
Console.WriteLine($"Segment: {SegmentName}");
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

    // Create a message large enough to benefit from compression (500+ chars)
    var largePayload = "Hello with compression! " + new string('A', 500);

    Console.WriteLine($"Sending message ({largePayload.Length} chars):");
    Console.WriteLine($"  Preview: {largePayload[..60]}...");
    Console.WriteLine();

    // Standard unary call — compression is transparent at the SHM layer
    var response = await client.UnaryEchoAsync(new EchoRequest { Message = largePayload });

    Console.WriteLine($"Received response ({response.Message.Length} chars):");
    Console.WriteLine($"  Preview: {response.Message[..Math.Min(60, response.Message.Length)]}...");
    Console.WriteLine();

    // Demonstrate that gRPC-level metadata still works alongside SHM compression
    var headers = new Metadata
    {
        { "grpc-accept-encoding", "identity,gzip" }
    };

    Console.WriteLine("Sending second request with grpc-accept-encoding metadata...");
    var response2 = await client.UnaryEchoAsync(
        new EchoRequest { Message = largePayload },
        new CallOptions(headers: headers));

    Console.WriteLine($"Received response: {response2.Message[..Math.Min(60, response2.Message.Length)]}...");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Make sure the server is running first:");
    Console.WriteLine("  cd examples/Compression.SharedMemory/Server");
    Console.WriteLine("  dotnet run");
}

Console.WriteLine();
Console.WriteLine("Compression example completed!");
