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

const string SegmentName = "deadline_shm";

Console.WriteLine("Deadline Example - Shared Memory Client");
Console.WriteLine($"Connecting to shm://{SegmentName}");
Console.WriteLine();

using var connection = ShmConnection.ConnectAsClient(SegmentName);
Console.WriteLine("Connected to server");
Console.WriteLine();

// Test cases matching the Go example:
// 1. A successful request
await UnaryCall(connection, 1, "world", StatusCode.OK);

// 2. Exceeds deadline (message contains "delay")
await UnaryCall(connection, 2, "delay", StatusCode.DeadlineExceeded);

// 3. A successful request with propagated deadline
await UnaryCall(connection, 3, "[propagate me]world", StatusCode.OK);

// 4. Exceeds propagated deadline
await UnaryCall(connection, 4, "[propagate me][propagate me]world", StatusCode.DeadlineExceeded);

Console.WriteLine();
Console.WriteLine("All deadline tests completed!");

async Task UnaryCall(ShmConnection conn, int requestId, string message, StatusCode expectedCode)
{
    var stream = conn.CreateStream();
    var deadline = DateTime.UtcNow.AddSeconds(1); // 1 second deadline

    try
    {
        // Send request with deadline
        await stream.SendRequestHeadersAsync("/echo.Echo/UnaryEcho", SegmentName, null, deadline);

        // Send message
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var framedMessage = new byte[5 + messageBytes.Length];
        framedMessage[0] = 0;
        var lengthBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(messageBytes.Length));
        Buffer.BlockCopy(lengthBytes, 0, framedMessage, 1, 4);
        Buffer.BlockCopy(messageBytes, 0, framedMessage, 5, messageBytes.Length);
        await stream.SendMessageAsync(framedMessage);
        await stream.SendTrailersAsync(StatusCode.OK); // Half-close

        // Wait for response with timeout
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        try
        {
            // Receive headers
            await stream.ReceiveFrameAsync(cts.Token);

            // Receive response
            var frame = await stream.ReceiveFrameAsync(cts.Token);
            if (frame?.Type == FrameType.Data)
            {
                var response = Encoding.UTF8.GetString(frame.Value.Payload.AsSpan(5));
                Console.WriteLine($"request {requestId}: wanted = {expectedCode}, got = {StatusCode.OK}");

                if (expectedCode != StatusCode.OK)
                {
                    Console.WriteLine($"  WARNING: Expected {expectedCode} but got OK");
                }
            }
            else if (frame?.Type == FrameType.Trailers)
            {
                // Parse trailers for status
                var trailers = TrailersV1.Decode(frame.Value.Payload);
                Console.WriteLine($"request {requestId}: wanted = {expectedCode}, got = {trailers.GrpcStatusCode}");
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"request {requestId}: wanted = {expectedCode}, got = {StatusCode.DeadlineExceeded}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"request {requestId}: error - {ex.Message}");
    }
    finally
    {
        stream.Dispose();
    }
}
