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
using System.Buffers.Binary;
using System.Text;

const string SegmentName = "compression_example_shm";

Console.WriteLine("=== Compression SharedMemory Server ===");
Console.WriteLine($"Segment: {SegmentName}");
Console.WriteLine();

// Register additional compressors if needed
ShmCompressorRegistry.Register(GzipCompressor.Default);
ShmCompressorRegistry.Register(DeflateCompressor.Default);

Console.WriteLine("Registered compressors:");
foreach (var name in ShmCompressorRegistry.GetRegisteredNames())
{
    Console.WriteLine($"  - {name}");
}
Console.WriteLine();

// Create connection listener
using var listener = new ShmConnectionListener(SegmentName);

Console.WriteLine("Waiting for client connection...");
using var connection = await listener.AcceptAsync();
Console.WriteLine("Client connected!");

// Accept streams and handle RPCs
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await foreach (var stream in connection.AcceptStreamsAsync(cts.Token))
{
    _ = HandleStreamAsync(stream);
}

async Task HandleStreamAsync(ShmGrpcStream stream)
{
    try
    {
        // Receive request headers
        var requestHeaders = await stream.ReceiveRequestHeadersAsync();
        Console.WriteLine($"Received request: {requestHeaders.Method}");

        // Check accepted encodings from client
        var acceptEncoding = "identity";
        foreach (var kv in requestHeaders.Metadata)
        {
            if (kv.Key.Equals("grpc-accept-encoding", StringComparison.OrdinalIgnoreCase))
            {
                acceptEncoding = Encoding.UTF8.GetString(kv.Values[0]);
                Console.WriteLine($"Client accepts encodings: {acceptEncoding}");
            }
        }

        // Determine if we should use compression
        var useGzip = acceptEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase);
        var compressor = useGzip ? GzipCompressor.Default : IdentityCompressor.Instance;

        Console.WriteLine($"Using compressor: {compressor.Name}");

        // Send response headers with encoding info
        var responseHeaders = new HeadersV1
        {
            HeaderType = 1,
            Metadata = new[]
            {
                new MetadataKV("grpc-encoding", compressor.Name)
            }
        };
        await stream.SendResponseHeadersAsync(responseHeaders);

        // Receive the request message
        var (frameType, payload) = await stream.ReceiveFrameAsync();

        if (frameType == FrameType.Message && payload != null)
        {
            // H2 DATA frame body is a gRPC LPM blob: [compFlag(1)][len(4 BE)][body].
            // Strip the 5-byte prefix to recover the application-level bytes.
            var requestMessage = Encoding.UTF8.GetString(UnwrapLpm(payload));
            Console.WriteLine($"Received message ({payload.Length} bytes): {requestMessage.Substring(0, Math.Min(50, requestMessage.Length))}...");

            // Create a response with some data that benefits from compression
            var responseText = $"Echo: {requestMessage} | " + 
                new string('x', 1000); // Add padding to demonstrate compression

            var responseBytes = Encoding.UTF8.GetBytes(responseText);
            var originalSize = responseBytes.Length;

            // Compress if using gzip
            var compressedBytes = compressor.Compress(responseBytes);
            var compressionRatio = (double)compressedBytes.Length / originalSize;

            Console.WriteLine($"Response: {originalSize} bytes original -> {compressedBytes.Length} bytes compressed ({compressionRatio:P1})");

            // Send the (possibly compressed) response
            // Note: For real compression, the message framing would include the compression flag
            await stream.SendMessageAsync(WrapLpm(responseBytes));
        }

        // Send trailers
        await stream.SendTrailersAsync(new TrailersV1
        {
            GrpcStatusCode = (uint)StatusCode.OK,
            GrpcStatusMessage = "Success"
        });

        Console.WriteLine("Request completed");
        Console.WriteLine();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error handling stream: {ex.Message}");
    }
}

// gRPC length-prefixed message helpers. H2 DATA frame body is
// [compFlag(1)][len(4 BE)][body]; both sides must agree.
static byte[] WrapLpm(byte[] body)
{
    var buf = new byte[5 + body.Length];
    buf[0] = 0; // uncompressed
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(1, 4), (uint)body.Length);
    body.CopyTo(buf, 5);
    return buf;
}

static ReadOnlySpan<byte> UnwrapLpm(byte[] framed)
{
    if (framed.Length < 5) throw new InvalidDataException($"LPM blob too short: {framed.Length}");
    if (framed[0] != 0) throw new InvalidDataException($"Compressed LPM not supported (flag=0x{framed[0]:X2})");
    var len = (int)BinaryPrimitives.ReadUInt32BigEndian(framed.AsSpan(1, 4));
    if (5 + len > framed.Length) throw new InvalidDataException($"LPM declares {len} bytes, only {framed.Length - 5} available");
    return framed.AsSpan(5, len);
}
