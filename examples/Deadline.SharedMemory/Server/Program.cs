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

Console.WriteLine("Deadline Example - Shared Memory Server");
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

        // Receive message
        var frame = await stream.ReceiveFrameAsync(ct);
        if (frame?.Type != FrameType.Data)
        {
            await stream.SendTrailersAsync(StatusCode.InvalidArgument, "Expected data");
            return;
        }

        var message = Encoding.UTF8.GetString(frame.Value.Payload.AsSpan(5));
        Console.WriteLine($"Received: {message}");

        // Check for delay request
        if (message.Contains("delay"))
        {
            // Delay longer than typical deadline to trigger timeout
            Console.WriteLine("  Delaying response for 2 seconds...");
            await Task.Delay(2000, ct);
        }

        // Check for propagation request
        if (message.Contains("[propagate me]"))
        {
            // Extract the rest of the message after [propagate me]
            var idx = message.IndexOf("[propagate me]") + "[propagate me]".Length;
            var remaining = message.Substring(idx);
            Console.WriteLine($"  Propagating request: {remaining}");

            // Make recursive call with remaining message (simulated)
            if (remaining.Contains("[propagate me]"))
            {
                // Double propagation will exceed deadline
                await Task.Delay(1500, ct);
            }
        }

        // Send response
        await stream.SendResponseHeadersAsync();

        var responseMessage = $"Echo: {message}";
        var responseBytes = Encoding.UTF8.GetBytes(responseMessage);
        var framedResponse = new byte[5 + responseBytes.Length];
        framedResponse[0] = 0;
        var lengthBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(responseBytes.Length));
        Buffer.BlockCopy(lengthBytes, 0, framedResponse, 1, 4);
        Buffer.BlockCopy(responseBytes, 0, framedResponse, 5, responseBytes.Length);

        await stream.SendMessageAsync(framedResponse);
        await stream.SendTrailersAsync(StatusCode.OK);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("  Request cancelled (deadline exceeded)");
        try
        {
            await stream.SendTrailersAsync(StatusCode.DeadlineExceeded, "Deadline exceeded");
        }
        catch { }
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
