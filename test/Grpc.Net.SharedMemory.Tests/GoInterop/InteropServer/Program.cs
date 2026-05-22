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

using System.Buffers.Binary;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.SharedMemory;
using Helloworld;

// Parse command line args
var segmentName = args.Length > 0 ? args[0] : "interop_test_shm";

Console.WriteLine("===========================================");
Console.WriteLine(".NET Interop Server - Shared Memory");
Console.WriteLine("===========================================");
Console.WriteLine($"Segment: {segmentName}");
Console.WriteLine();

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    // Use ShmControlListener for grpc-go-shmem compatibility
    // This creates a control segment (_ctl suffix) like Go expects
    using var listener = new ShmControlListener(segmentName, ringCapacity: 64 * 1024, maxStreams: 100);
    Console.WriteLine($"Server listening on shm://{segmentName}");
    Console.WriteLine("Waiting for connections... (Ctrl+C to stop)");

    // Accept connections from the control listener
    await foreach (var connection in listener.AcceptConnectionsAsync(cts.Token))
    {
        Console.WriteLine($"New connection accepted: {connection.Name}");
        
        // Handle streams from this connection
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var stream in connection.AcceptStreamsAsync(cts.Token))
                {
                    await HandleStreamAsync(stream, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Connection error: {ex.Message}");
            }
        });
    }

    Console.WriteLine("Server stopped.");
    return 0;
}
catch (OperationCanceledException)
{
    Console.WriteLine("Server stopped.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    Console.Error.WriteLine(ex.ToString());
    return 1;
}

async Task HandleStreamAsync(ShmGrpcStream stream, CancellationToken ct)
{
    if (stream.RequestHeaders?.Method == null)
    {
        Console.WriteLine("Received stream without method");
        return;
    }
    
    var method = stream.RequestHeaders.Method;
    Console.WriteLine($"Received request: {method}");

    try
    {
        if (method == "/helloworld.Greeter/SayHello")
        {
            // Read request message frame
            var frame = await stream.ReceiveFrameAsync(ct);
            if (frame?.Type == FrameType.Message)
            {
                // Parse gRPC wire format: [compressed:1][length:4 BE][data]
                var payload = frame.Value.Payload;
                if (payload.Length >= 5)
                {
                    var msgLength = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(1, 4));
                    var requestBytes = payload.AsSpan(5, (int)msgLength).ToArray();
                    var request = HelloRequest.Parser.ParseFrom(requestBytes);
                    Console.WriteLine($"Name: {request.Name}");

                    // Send response headers
                    await stream.SendResponseHeadersAsync();

                    // Create and send response
                    var reply = new HelloReply
                    {
                        Message = $"Hello {request.Name} from .NET!"
                    };
                    var replyBytes = reply.ToByteArray();
                    var lpm = new byte[5 + replyBytes.Length];
                    lpm[0] = 0; // uncompressed
                    BinaryPrimitives.WriteUInt32BigEndian(lpm.AsSpan(1, 4), (uint)replyBytes.Length);
                    replyBytes.CopyTo(lpm, 5);
                    await stream.SendMessageAsync(lpm);
                    await stream.SendTrailersAsync(StatusCode.OK);

                    Console.WriteLine($"Sent response: {reply.Message}");
                }
            }
        }
        else
        {
            await stream.SendTrailersAsync(StatusCode.Unimplemented, $"Method not found: {method}");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error handling request: {ex.Message}");
        try
        {
            await stream.SendTrailersAsync(StatusCode.Internal, ex.Message);
        }
        catch { }
    }
}
