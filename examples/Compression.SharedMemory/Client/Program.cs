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
using Grpc.Net.SharedMemory.Compression;
using System.Text;

const string SegmentName = "compression_example_shm";

Console.WriteLine("=== Compression SharedMemory Client ===");
Console.WriteLine($"Segment: {SegmentName}");
Console.WriteLine();

// Configure compression options
var compressionOptions = new ShmCompressionOptions
{
    Enabled = true,
    SendCompress = "gzip",
    AcceptedCompressors = new List<string> { "gzip", "deflate", "identity" },
    MinSizeForCompression = 100 // Compress messages >= 100 bytes
};

Console.WriteLine("Compression options:");
Console.WriteLine($"  Send: {compressionOptions.SendCompress ?? "identity"}");
Console.WriteLine($"  Accept: {compressionOptions.GetAcceptEncoding()}");
Console.WriteLine($"  Min size: {compressionOptions.MinSizeForCompression} bytes");
Console.WriteLine();

// Connect to server
Console.WriteLine("Connecting to server...");
using var connection = await ShmConnection.ConnectAsClient(SegmentName);
Console.WriteLine("Connected!");
Console.WriteLine();

// Create a stream
var stream = connection.CreateStream();

// Create a message that will benefit from compression
var messageText = "Hello with compression! " + new string('A', 500);
var messageBytes = Encoding.UTF8.GetBytes(messageText);

Console.WriteLine($"Request message: {messageBytes.Length} bytes");

// Compress the message
var compressor = compressionOptions.GetSendCompressor();
var shouldCompress = compressionOptions.ShouldCompress(messageBytes.Length);

byte[] payloadToSend;
if (shouldCompress && !compressor.IsIdentity)
{
    payloadToSend = compressor.Compress(messageBytes);
    Console.WriteLine($"Compressed with {compressor.Name}: {messageBytes.Length} -> {payloadToSend.Length} bytes ({(double)payloadToSend.Length / messageBytes.Length:P1})");
}
else
{
    payloadToSend = messageBytes;
    Console.WriteLine("Sending uncompressed");
}

// Send request headers with compression info
var requestHeaders = new HeadersV1
{
    HeaderType = 0,
    Method = "/example.Compression/Echo",
    Authority = "localhost",
    Metadata = new[]
    {
        new MetadataKV("grpc-accept-encoding", compressionOptions.GetAcceptEncoding()),
        new MetadataKV("grpc-encoding", shouldCompress ? compressor.Name : "identity")
    }
};

await stream.SendRequestHeadersAsync(requestHeaders);
Console.WriteLine("Sent request headers");

// Send the message (use original bytes - in real implementation, framing would handle compression)
await stream.SendMessageAsync(messageBytes);
Console.WriteLine("Sent message");

// Signal half-close
await stream.SendHalfCloseAsync();

// Receive response headers
var responseHeaders = await stream.ReceiveResponseHeadersAsync();
Console.WriteLine($"Received response headers");

// Check server's encoding
foreach (var kv in responseHeaders.Metadata)
{
    if (kv.Key.Equals("grpc-encoding", StringComparison.OrdinalIgnoreCase))
    {
        var encoding = Encoding.UTF8.GetString(kv.Values[0]);
        Console.WriteLine($"Server encoding: {encoding}");
    }
}

// Receive response message
var (frameType, payload) = await stream.ReceiveFrameAsync();
if (frameType == FrameType.Message && payload != null)
{
    var responseText = Encoding.UTF8.GetString(payload);
    Console.WriteLine($"Response ({payload.Length} bytes): {responseText.Substring(0, Math.Min(80, responseText.Length))}...");
}

// Receive trailers
(frameType, payload) = await stream.ReceiveFrameAsync();
if (frameType == FrameType.Trailers && payload != null)
{
    var trailers = TrailersV1.Decode(payload);
    Console.WriteLine($"Status: {(StatusCode)trailers.GrpcStatusCode} - {trailers.GrpcStatusMessage}");
}

Console.WriteLine();
Console.WriteLine("Compression example completed!");
