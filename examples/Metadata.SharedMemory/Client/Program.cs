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

const string SegmentName = "metadata_shm";
const string TimestampFormat = "MMM dd HH:mm:ss.fffffff";
const string Message = "this is examples/metadata";

Console.WriteLine("Metadata Example - Shared Memory Client");
Console.WriteLine($"Connecting to shm://{SegmentName}");
Console.WriteLine();

using var connection = ShmConnection.ConnectAsClient(SegmentName);
Console.WriteLine("Connected to server");
Console.WriteLine();

// ============================================================
// Unary Call with Metadata
// ============================================================
Console.WriteLine("=== Unary Call with Metadata ===");
await UnaryCallWithMetadata(connection, Message);
Console.WriteLine();

Console.WriteLine("All metadata tests completed!");

async Task UnaryCallWithMetadata(ShmConnection conn, string message)
{
    var stream = conn.CreateStream();

    try
    {
        // Create metadata with timestamp
        var requestMetadata = new Metadata
        {
            { "timestamp", DateTime.UtcNow.ToString(TimestampFormat) },
            { "client-id", "shm-client-1" }
        };

        Console.WriteLine("Sending request with metadata:");
        foreach (var entry in requestMetadata)
        {
            Console.WriteLine($"  {entry.Key} = {entry.Value}");
        }

        // Send request with metadata
        await stream.SendRequestHeadersAsync("/echo.Echo/UnaryEcho", SegmentName, requestMetadata);

        // Send message
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var framedMessage = new byte[5 + messageBytes.Length];
        framedMessage[0] = 0;
        var lengthBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(messageBytes.Length));
        Buffer.BlockCopy(lengthBytes, 0, framedMessage, 1, 4);
        Buffer.BlockCopy(messageBytes, 0, framedMessage, 5, messageBytes.Length);
        await stream.SendMessageAsync(framedMessage);
        await stream.SendTrailersAsync(StatusCode.OK); // Half-close

        // Receive response headers with metadata
        var headerFrame = await stream.ReceiveFrameAsync();
        if (headerFrame?.Type == FrameType.Headers)
        {
            Console.WriteLine("\nReceived response headers:");
            if (stream.ResponseHeaders?.Metadata != null)
            {
                foreach (var kv in stream.ResponseHeaders.Metadata)
                {
                    foreach (var value in kv.Values)
                    {
                        Console.WriteLine($"  {kv.Key} = {Encoding.UTF8.GetString(value)}");
                    }
                }
            }
        }

        // Receive response message
        var dataFrame = await stream.ReceiveFrameAsync();
        if (dataFrame?.Type == FrameType.Data)
        {
            var response = Encoding.UTF8.GetString(dataFrame.Value.Payload.AsSpan(5));
            Console.WriteLine($"\nReceived response: {response}");
        }

        // Receive trailers with metadata
        var trailerFrame = await stream.ReceiveFrameAsync();
        if (trailerFrame?.Type == FrameType.Trailers)
        {
            var trailers = TrailersV1.Decode(trailerFrame.Value.Payload);
            Console.WriteLine("\nReceived trailers:");
            Console.WriteLine($"  status = {trailers.GrpcStatusCode}");
            if (trailers.Metadata != null)
            {
                foreach (var kv in trailers.Metadata)
                {
                    foreach (var value in kv.Values)
                    {
                        Console.WriteLine($"  {kv.Key} = {Encoding.UTF8.GetString(value)}");
                    }
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
    }
    finally
    {
        stream.Dispose();
    }
}
