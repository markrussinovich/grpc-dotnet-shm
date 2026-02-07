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
// Keepalive is managed transparently by the underlying ShmConnection. The ShmHandler
// creates the connection internally, and keepalive pings are handled automatically.

const string SegmentName = "keepalive_shm";

Console.WriteLine("Keepalive Example - Shared Memory Client");
Console.WriteLine($"Connecting to shm://{SegmentName}");
Console.WriteLine();

Console.WriteLine("Client keepalive parameters (informational):");
Console.WriteLine("  Time: 10s (send pings every 10s if no activity)");
Console.WriteLine("  PingTimeout: 1s (wait 1s for ping ack)");
Console.WriteLine("  PermitWithoutStream: true");
Console.WriteLine();

try
{
    // Create handler — keepalive is transparent at the ShmHandler level.
    // The underlying ShmConnection handles keepalive internally.
    using var handler = new ShmHandler(SegmentName);
    using var channel = GrpcChannel.ForAddress("shm://localhost", new GrpcChannelOptions
    {
        HttpHandler = handler
    });

    var client = new Echo.Echo.EchoClient(channel);
    Console.WriteLine("Connected to server");
    Console.WriteLine();

    // Perform a unary echo request
    Console.WriteLine("Performing unary request...");
    var response = await client.UnaryEchoAsync(new EchoRequest { Message = "keepalive demo" });
    Console.WriteLine($"RPC response: {response.Message}");
    Console.WriteLine();

    // Wait to observe keepalive behavior
    Console.WriteLine("Waiting to observe keepalive pings...");
    Console.WriteLine("(In a real scenario, the SHM transport would send pings and");
    Console.WriteLine(" the server would eventually close the connection due to max age)");
    Console.WriteLine();

    for (int i = 0; i < 5; i++)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Still connected... ({(i + 1) * 5}s elapsed)");
    }
}
catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
{
    Console.WriteLine($"Connection closed by server (likely due to MaxConnectionAge): {ex.Status.Detail}");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Make sure the server is running first:");
    Console.WriteLine("  cd examples/Keepalive.SharedMemory/Server");
    Console.WriteLine("  dotnet run");
}

Console.WriteLine();
Console.WriteLine("Keepalive example completed!");
