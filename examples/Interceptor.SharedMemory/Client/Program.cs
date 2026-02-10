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

using System.Globalization;
using System.Net;
using System.Text;
using Grpc.Core;
using Grpc.Net.SharedMemory;

const string SegmentName = "interceptor_shm";
const string FallbackToken = "some-secret-token";

Console.WriteLine("Interceptor Example - Shared Memory Client");
Console.WriteLine($"Connecting to shm://{SegmentName}");
Console.WriteLine();

// Logger helper (mocking a sophisticated logging system)
void Logger(string format, params object[] args)
{
    Console.WriteLine("LOG:\t" + string.Format(CultureInfo.InvariantCulture, format, args));
}

using var connection = ShmConnection.ConnectAsClient(SegmentName);
Console.WriteLine("Connected to server");
Console.WriteLine();

// Call unary echo with interceptor
await CallUnaryEcho(connection, "hello world");
Console.WriteLine();

// Call bidirectional streaming echo with interceptor
await CallBidiStreamingEcho(connection);
Console.WriteLine();

Console.WriteLine("Interceptor example completed!");

async Task CallUnaryEcho(ShmConnection conn, string message)
{
    Console.WriteLine($"Calling UnaryEcho with message: \"{message}\"");

    var stream = conn.CreateStream();
    try
    {
        var start = DateTime.Now;

        // Create metadata with auth token
        var metadata = new Metadata
        {
            { "authorization", $"Bearer {FallbackToken}" }
        };

        // Send request headers with auth token
        await stream.SendRequestHeadersAsync("/echo.Echo/UnaryEcho", SegmentName, metadata);

        // Send message
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var framedMessage = new byte[5 + messageBytes.Length];
        framedMessage[0] = 0;
        var lengthBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(messageBytes.Length));
        Buffer.BlockCopy(lengthBytes, 0, framedMessage, 1, 4);
        Buffer.BlockCopy(messageBytes, 0, framedMessage, 5, messageBytes.Length);

        await stream.SendMessageAsync(framedMessage);
        await stream.SendHalfCloseAsync();

        // Read response headers
        await stream.ReceiveResponseHeadersAsync();
        
        // Read response message
        var frame = await stream.ReceiveFrameAsync();

        var end = DateTime.Now;
        Logger("RPC: /echo.Echo/UnaryEcho, start time: {0}, end time: {1}",
            start.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), 
            end.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));

        if (frame?.Type == FrameType.Message && frame.Value.Payload.Length > 5)
        {
            var responseMessage = Encoding.UTF8.GetString(frame.Value.Payload.AsSpan(5));
            Console.WriteLine($"UnaryEcho: {responseMessage}");
        }
    }
    finally
    {
        stream.Dispose();
    }
}

async Task CallBidiStreamingEcho(ShmConnection conn)
{
    Console.WriteLine("Calling BidirectionalStreamingEcho");

    var stream = conn.CreateStream();
    try
    {
        // Create metadata with auth token
        var metadata = new Metadata
        {
            { "authorization", $"Bearer {FallbackToken}" }
        };

        Logger("Starting streaming RPC: /echo.Echo/BidirectionalStreamingEcho", Array.Empty<object>());

        // Send request headers with auth token
        await stream.SendRequestHeadersAsync("/echo.Echo/BidirectionalStreamingEcho", SegmentName, metadata);

        // Read response headers first (server sends them before echoing)
        await stream.ReceiveResponseHeadersAsync();

        // Send 5 messages
        for (int i = 1; i <= 5; i++)
        {
            var message = $"Request {i}";
            Logger("SendMsg: {0}", message);

            var messageBytes = Encoding.UTF8.GetBytes(message);
            var framedMessage = new byte[5 + messageBytes.Length];
            framedMessage[0] = 0;
            var lengthBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(messageBytes.Length));
            Buffer.BlockCopy(lengthBytes, 0, framedMessage, 1, 4);
            Buffer.BlockCopy(messageBytes, 0, framedMessage, 5, messageBytes.Length);

            await stream.SendMessageAsync(framedMessage);
            
            // In real bidirectional, we'd receive response here
            var responseFrame = await stream.ReceiveFrameAsync();
            if (responseFrame?.Type == FrameType.Message && responseFrame.Value.Payload.Length > 5)
            {
                var responseMessage = Encoding.UTF8.GetString(responseFrame.Value.Payload.AsSpan(5));
                Logger("RecvMsg: {0}", responseMessage);
                Console.WriteLine($"BidiStreaming Echo: {responseMessage}");
            }
        }

        // Signal we're done sending
        await stream.SendHalfCloseAsync();
    }
    finally
    {
        stream.Dispose();
    }
}
