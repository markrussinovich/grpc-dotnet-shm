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

Console.WriteLine("Metadata Example - Shared Memory Server");
Console.WriteLine($"Listening on shm://{SegmentName}");
Console.WriteLine();

using var listener = new ShmConnectionListener(SegmentName, ringCapacity: 1024 * 1024, maxStreams: 100);

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    while (!cts.Token.IsCancellationRequested)
    {
        var connection = await listener.AcceptAsync(cts.Token);
        if (connection == null) continue;

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var stream in connection.AcceptStreamsAsync(cts.Token))
                {
                    _ = HandleStream(stream, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                connection.Dispose();
            }
        });
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Server stopped.");
}

async Task HandleStream(ShmGrpcStream stream, CancellationToken ct)
{
    try
    {
        var method = stream.RequestHeaders?.Method ?? "";

        // Log incoming metadata
        if (stream.RequestHeaders?.Metadata != null)
        {
            Console.WriteLine("Received request metadata:");
            foreach (var kv in stream.RequestHeaders.Metadata)
            {
                foreach (var value in kv.Values)
                {
                    Console.WriteLine($"  {kv.Key} = {Encoding.UTF8.GetString(value)}");
                }
            }
        }

        // Receive message
        var frame = await stream.ReceiveFrameAsync(ct);
        if (frame?.Type != FrameType.Data)
        {
            await stream.SendTrailersAsync(StatusCode.InvalidArgument, "Expected data");
            return;
        }

        var message = Encoding.UTF8.GetString(frame.Value.Payload.AsSpan(5));
        Console.WriteLine($"Message: \"{message}\", sending echo");

        // Send response with metadata
        var responseMetadata = new Metadata
        {
            { "timestamp", DateTime.UtcNow.ToString(TimestampFormat) },
            { "server-location", "shared-memory" }
        };
        await stream.SendResponseHeadersAsync(responseMetadata);

        // Echo the message
        var responseMessage = message;
        var responseBytes = Encoding.UTF8.GetBytes(responseMessage);
        var framedResponse = new byte[5 + responseBytes.Length];
        framedResponse[0] = 0;
        var lengthBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(responseBytes.Length));
        Buffer.BlockCopy(lengthBytes, 0, framedResponse, 1, 4);
        Buffer.BlockCopy(responseBytes, 0, framedResponse, 5, responseBytes.Length);

        await stream.SendMessageAsync(framedResponse);

        // Send trailers with metadata
        var trailerMetadata = new Metadata
        {
            { "trailer-timestamp", DateTime.UtcNow.ToString(TimestampFormat) }
        };
        await stream.SendTrailersAsync(StatusCode.OK, null, trailerMetadata);

        Console.WriteLine();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error: {ex.Message}");
        try
        {
            await stream.SendTrailersAsync(StatusCode.Internal, ex.Message);
        }
        catch { }
    }
    finally
    {
        stream.Dispose();
    }
}
