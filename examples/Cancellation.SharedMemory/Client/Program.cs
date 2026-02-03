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

const string SegmentName = "cancellation_shm";

Console.WriteLine("Cancellation Example - Shared Memory Client");
Console.WriteLine($"Connecting to shm://{SegmentName}");
Console.WriteLine();

using var connection = ShmConnection.ConnectAsClient(SegmentName);
Console.WriteLine("Connected to server");
Console.WriteLine();

// Create a stream for bidirectional communication
var stream = connection.CreateStream();

try
{
    // Send request headers
    await stream.SendRequestHeadersAsync("/echo.Echo/BidirectionalStreamingEcho", SegmentName);

    // Send a few messages
    for (int i = 1; i <= 3; i++)
    {
        var message = $"message {i}";
        Console.WriteLine($"Sending {message}");

        var messageBytes = Encoding.UTF8.GetBytes(message);
        var framedMessage = new byte[5 + messageBytes.Length];
        framedMessage[0] = 0;
        var lengthBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(messageBytes.Length));
        Buffer.BlockCopy(lengthBytes, 0, framedMessage, 1, 4);
        Buffer.BlockCopy(messageBytes, 0, framedMessage, 5, messageBytes.Length);

        await stream.SendMessageAsync(framedMessage);
        await Task.Delay(200); // Small delay between messages
    }

    // Cancel the stream
    Console.WriteLine("cancelling context");
    await stream.CancelAsync();

    Console.WriteLine("Stream cancelled successfully");
}
catch (OperationCanceledException)
{
    Console.WriteLine("Stream was cancelled");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
finally
{
    stream.Dispose();
}

Console.WriteLine();
Console.WriteLine("Cancellation example completed!");
