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
// All gRPC API usage (deadlines, cancellation, metadata, etc.) remains identical.

const string SegmentName = "deadline_shm";

Console.WriteLine("Deadline Example - Shared Memory Client");
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

    // Test cases matching the Go example:

    // 1. A successful request within the deadline
    await UnaryCall(client, 1, "world", StatusCode.OK);

    // 2. Exceeds deadline (server delays when message contains "delay")
    await UnaryCall(client, 2, "delay", StatusCode.DeadlineExceeded);

    // 3. A successful request with propagated deadline
    await UnaryCall(client, 3, "[propagate me]world", StatusCode.OK);

    // 4. Exceeds propagated deadline
    await UnaryCall(client, 4, "[propagate me][propagate me]world", StatusCode.DeadlineExceeded);
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Make sure the server is running first:");
    Console.WriteLine("  cd examples/Deadline.SharedMemory/Server");
    Console.WriteLine("  dotnet run");
}

Console.WriteLine();
Console.WriteLine("All deadline tests completed!");

async Task UnaryCall(Echo.Echo.EchoClient client, int requestId, string message, StatusCode expectedCode)
{
    try
    {
        // Set a 1-second deadline for the call
        var options = new CallOptions(deadline: DateTime.UtcNow.AddSeconds(1));

        var response = await client.UnaryEchoAsync(
            new EchoRequest { Message = message }, options);

        Console.WriteLine($"request {requestId}: wanted = {expectedCode}, got = OK");

        if (expectedCode != StatusCode.OK)
        {
            Console.WriteLine($"  WARNING: Expected {expectedCode} but got OK");
        }
    }
    catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded)
    {
        Console.WriteLine($"request {requestId}: wanted = {expectedCode}, got = DeadlineExceeded");

        if (expectedCode != StatusCode.DeadlineExceeded)
        {
            Console.WriteLine($"  WARNING: Expected {expectedCode} but got DeadlineExceeded");
        }
    }
    catch (RpcException ex)
    {
        Console.WriteLine($"request {requestId}: unexpected error - {ex.StatusCode}: {ex.Status.Detail}");
    }
}
