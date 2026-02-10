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

using System.Text;
using Grpc.Core;
using Grpc.Net.SharedMemory;

const string SegmentName = "cancellation_shm";

Console.WriteLine("Cancellation Example - Shared Memory Server");
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
    // Accept streams directly from listener
    await foreach (var stream in listener.AcceptStreamsAsync(cts.Token))
    {
        _ = HandleBidirectionalStream(stream, cts.Token);
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Server stopped.");
}

async Task HandleBidirectionalStream(ShmGrpcStream stream, CancellationToken ct)
{
    try
    {
        // Send response headers
        await stream.SendResponseHeadersAsync();

        // Receive and echo messages until client cancels
        while (!ct.IsCancellationRequested)
        {
            var frame = await stream.ReceiveFrameAsync(ct);

            if (frame == null)
            {
                Console.WriteLine("Stream ended by client");
                break;
            }

            if (frame.Value.Type == FrameType.Cancel)
            {
                Console.WriteLine("server: error receiving from stream: rpc error: code = Canceled desc = context canceled");
                break;
            }

            if (frame.Value.Type == FrameType.Trailers)
            {
                Console.WriteLine("Stream completed normally");
                break;
            }

            if (frame.Value.Type == FrameType.Message)
            {
                var message = Encoding.UTF8.GetString(frame.Value.Payload.AsSpan(5));
                Console.WriteLine($"Received: {message}");

                // Echo back after a short delay
                await Task.Delay(100, ct);
            }
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("server: error receiving from stream: rpc error: code = Canceled desc = context canceled");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Server stream error: {ex.Message}");
    }
    finally
    {
        stream.Dispose();
    }
}
