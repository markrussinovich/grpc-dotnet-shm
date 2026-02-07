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
// All gRPC API usage (streaming, cancellation, metadata, etc.) remains identical.

const string SegmentName = "cancellation_shm";

Console.WriteLine("Cancellation Example - Shared Memory Client");
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

    // Create a CancellationTokenSource that we will cancel after sending 3 messages
    using var cts = new CancellationTokenSource();

    // Start a bidirectional streaming call
    using var call = client.BidirectionalStreamingEcho(cancellationToken: cts.Token);

    // Start a background task to read responses
    var readTask = Task.Run(async () =>
    {
        try
        {
            await foreach (var response in call.ResponseStream.ReadAllAsync(cts.Token))
            {
                Console.WriteLine($"Received: {response.Message}");
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            Console.WriteLine("Response stream cancelled as expected");
        }
    });

    // Send 3 messages then cancel
    for (int i = 1; i <= 3; i++)
    {
        var message = $"message {i}";
        Console.WriteLine($"Sending: {message}");

        await call.RequestStream.WriteAsync(new EchoRequest { Message = message }, cts.Token);
        await Task.Delay(200);
    }

    // Cancel the RPC
    Console.WriteLine();
    Console.WriteLine("Cancelling context...");
    cts.Cancel();

    await readTask;
    Console.WriteLine("Stream cancelled successfully");
}
catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
{
    Console.WriteLine($"RPC cancelled: {ex.Status.Detail}");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Make sure the server is running first:");
    Console.WriteLine("  cd examples/Cancellation.SharedMemory/Server");
    Console.WriteLine("  dotnet run");
}

Console.WriteLine();
Console.WriteLine("Cancellation example completed!");
