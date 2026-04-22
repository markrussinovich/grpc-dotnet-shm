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

using Grpc.Core;
using Grpc.Net.SharedMemory;
using Greet;

Console.WriteLine("==========================================");
Console.WriteLine(".NET Greeter Server - Shared Memory Transport");
Console.WriteLine("==========================================");
Console.WriteLine();

// Get segment name from args or use default
var segmentName = args.Length > 0 ? args[0] : "interop_greeter";

Console.WriteLine($"Listening on segment: {segmentName}");
Console.WriteLine();
Console.WriteLine("To test with Go client:");
Console.WriteLine("  cd ../go/client");
Console.WriteLine($"  go run client.go -segment {segmentName}");
Console.WriteLine();
Console.WriteLine("Press Ctrl+C to stop the server.");
Console.WriteLine();

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Use ShmGrpcServer high-level API — handles gRPC LPM framing,
// protobuf serialization, and stream lifecycle automatically.
await using var server = new ShmGrpcServer(segmentName, ringCapacity: 1024 * 1024);

server.MapUnary<HelloRequest, HelloReply>(
    "/greet.Greeter/SayHello",
    (request, context) =>
    {
        Console.WriteLine($"Received request: /greet.Greeter/SayHello");
        Console.WriteLine($"  Name: {request.Name}");

        var reply = new HelloReply
        {
            Message = $"Hello {request.Name} from .NET server!"
        };

        Console.WriteLine($"  Response sent: {reply.Message}");
        return Task.FromResult(reply);
    });

try
{
    Console.WriteLine("Waiting for connections...");
    await server.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    // Normal shutdown
}

Console.WriteLine("Server stopped.");
