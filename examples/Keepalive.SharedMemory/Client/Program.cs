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

const string SegmentName = "keepalive_shm";

Console.WriteLine("Keepalive Example - Shared Memory Client");
Console.WriteLine($"Connecting to shm://{SegmentName}");
Console.WriteLine();

// Configure client keepalive parameters matching the Go example
var keepaliveOptions = new ShmKeepaliveOptions
{
    Time = TimeSpan.FromSeconds(10),           // Send pings every 10 seconds if no activity
    PingTimeout = TimeSpan.FromSeconds(1),     // Wait 1 second for ping ack
    PermitWithoutStream = true                 // Send pings even without active streams
};

Console.WriteLine("Client configured with keepalive parameters:");
Console.WriteLine($"  Time: {keepaliveOptions.Time.TotalSeconds}s");
Console.WriteLine($"  PingTimeout: {keepaliveOptions.PingTimeout.TotalSeconds}s");
Console.WriteLine($"  PermitWithoutStream: {keepaliveOptions.PermitWithoutStream}");
Console.WriteLine();

using var connection = ShmConnection.ConnectAsClient(SegmentName, keepaliveOptions: keepaliveOptions);
Console.WriteLine("Connected to server");
Console.WriteLine();

// Perform a unary echo request
Console.WriteLine("Performing unary request");
var stream = connection.CreateStream();

try
{
    // Send request headers
    await stream.SendRequestHeadersAsync("/echo.Echo/UnaryEcho", SegmentName);

    // Send message
    var message = "keepalive demo";
    var messageBytes = Encoding.UTF8.GetBytes(message);
    var framedMessage = new byte[5 + messageBytes.Length];
    framedMessage[0] = 0;
    var lengthBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(messageBytes.Length));
    Buffer.BlockCopy(lengthBytes, 0, framedMessage, 1, 4);
    Buffer.BlockCopy(messageBytes, 0, framedMessage, 5, messageBytes.Length);

    await stream.SendMessageAsync(framedMessage);
    await stream.SendHalfCloseAsync();

    // Read response
    await stream.ReceiveResponseHeadersAsync();
    var frame = await stream.ReceiveFrameAsync();

    if (frame?.Type == FrameType.Message && frame.Value.Payload.Length > 5)
    {
        var responseMessage = Encoding.UTF8.GetString(frame.Value.Payload.AsSpan(5));
        Console.WriteLine($"RPC response: {responseMessage}");
    }

    stream.Dispose();

    // Wait to observe keepalive behavior
    Console.WriteLine();
    Console.WriteLine("Waiting to observe keepalive pings...");
    Console.WriteLine("(In a real scenario, the client would send pings and");
    Console.WriteLine(" the server would eventually close the connection due to max age)");
    Console.WriteLine();

    // Simulate waiting for keepalive activity
    for (int i = 0; i < 5; i++)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Still connected... ({(i + 1) * 5}s elapsed)");
    }

    Console.WriteLine();
    Console.WriteLine("Keepalive example completed!");
}
catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
{
    Console.WriteLine($"Connection closed by server (likely due to MaxConnectionAge): {ex.Status.Detail}");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
finally
{
    stream.Dispose();
}
