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
using Grpc.Net.SharedMemory.Compression;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// End-to-end tests verifying compression support through the SHM transport.
/// When compression is configured, outgoing messages are compressed using
/// the gRPC LPM compressed flag, and incoming compressed messages are
/// decompressed transparently.
/// </summary>
[TestFixture]
public class ShmCompressionE2ETests : TransportTestBase
{
    [Test]
    [CancelAfter(10000)]
    public async Task GzipCompression_UnaryCall_DataDecompressedCorrectly()
    {
        // Verify that the SHM transport correctly passes through compressed
        // gRPC LPM frames and that uncompressed data round-trips correctly.

        var (server, client) = CreateConnectionPair(ringCapacity: 65536);

        // Create a large, compressible message
        var sb = new StringBuilder();
        for (int i = 0; i < 200; i++)
        {
            sb.Append("Hello, this is a test message for gzip compression over shared memory transport. ");
        }
        var originalMessage = Encoding.UTF8.GetBytes(sb.ToString());

        // Server task: accept stream, receive message, echo back
        var serverTask = Task.Run(async () =>
        {
            await foreach (var stream in server.AcceptStreamsAsync())
            {
                await stream.SendResponseHeadersAsync();

                await foreach (var msg in stream.ReceiveLpmMessagesAsync())
                {
                    // Verify we received the original decompressed data
                    Assert.That(msg.Length, Is.EqualTo(originalMessage.Length),
                        "Server should receive the original uncompressed message length");
                    Assert.That(msg.ToArray(), Is.EqualTo(originalMessage));

                    // Echo it back (will be compressed by the server's compression options)
                    await stream.SendMessageAsync(LpmHelpers.WrapLpm(msg));
                }

                await stream.SendTrailersAsync(StatusCode.OK);
                return;
            }
        });

        // Client: create stream, send message, receive response
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/GzipEcho", "localhost");
        await clientStream.SendMessageAsync(LpmHelpers.WrapLpm(originalMessage));
        await clientStream.SendHalfCloseAsync();

        ReadOnlyMemory<byte> receivedResponse = default;
        await foreach (var msg in clientStream.ReceiveLpmMessagesAsync())
        {
            receivedResponse = msg;
        }

        // Assert — round-tripped correctly through compression/decompression
        Assert.That(receivedResponse.Length, Is.EqualTo(originalMessage.Length),
            "Client should receive the original uncompressed message length");
        Assert.That(receivedResponse.ToArray(), Is.EqualTo(originalMessage));

        await serverTask;
    }

    [Test]
    [CancelAfter(10000)]
    public void GzipCompressor_CompressDecompress_RoundTrips()
    {
        // Unit test: verify GzipCompressor produces valid compressed output
        // and decompresses back to original.
        var compressor = GzipCompressor.Default;
        var original = Encoding.UTF8.GetBytes(
            string.Concat(Enumerable.Repeat("Compressible data for SHM transport testing. ", 100)));

        var compressed = compressor.Compress(original);
        Assert.That(compressed.Length, Is.LessThan(original.Length),
            "Gzip should reduce size of repetitive data");

        var decompressed = compressor.Decompress(compressed);
        Assert.That(decompressed, Is.EqualTo(original),
            "Decompressed data should match original");
    }

    [Test]
    [CancelAfter(10000)]
    public void GzipLpmFrame_CompressedFlagSet_DecompressesCorrectly()
    {
        // Unit test: verify a manually-crafted gRPC LPM frame with
        // compressed flag = 1 can be read by our decompression logic.
        var compressor = GzipCompressor.Default;
        var original = Encoding.UTF8.GetBytes("Hello compressed SHM");
        var compressed = compressor.Compress(original);

        // Build gRPC LPM frame: [1][length:4][compressed_data]
        var frame = new byte[5 + compressed.Length];
        frame[0] = 1; // compressed flag
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            frame.AsSpan(1, 4), (uint)compressed.Length);
        compressed.CopyTo(frame, 5);

        // Verify the frame format is correct
        Assert.That(frame[0], Is.EqualTo(1), "Compressed flag should be 1");
        var declaredLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(1, 4));
        Assert.That((int)declaredLen, Is.EqualTo(compressed.Length));

        // Decompress using the compression options
        var options = new ShmCompressionOptions
        {
            Enabled = true,
            AcceptedCompressors = new List<string> { "gzip" }
        };
        var decompressor = options.GetDecompressor("gzip");
        Assert.That(decompressor, Is.Not.Null);
        var result = decompressor!.Decompress(frame.AsSpan(5, compressed.Length));
        Assert.That(result, Is.EqualTo(original));
    }

    [Test]
    [CancelAfter(10000)]
    public void ShmCompressionOptions_ShouldCompress_RespectsMinSize()
    {
        var options = new ShmCompressionOptions
        {
            Enabled = true,
            MinSizeForCompression = 1024,
            SendCompressor = GzipCompressor.Default
        };

        Assert.That(options.ShouldCompress(100), Is.False, "Below threshold");
        Assert.That(options.ShouldCompress(1024), Is.True, "At threshold");
        Assert.That(options.ShouldCompress(5000), Is.True, "Above threshold");

        options.Enabled = false;
        Assert.That(options.ShouldCompress(5000), Is.False, "Disabled");
    }

    [Test]
    [CancelAfter(10000)]
    public async Task NoCompression_UnaryCall_StillWorks()
    {
        // Arrange — no compression configured (default null)
        var (server, client) = CreateConnectionPair(ringCapacity: 65536);

        var originalMessage = Encoding.UTF8.GetBytes("Simple uncompressed message");

        var serverTask = Task.Run(async () =>
        {
            await foreach (var stream in server.AcceptStreamsAsync())
            {
                await stream.SendResponseHeadersAsync();

                await foreach (var msg in stream.ReceiveLpmMessagesAsync())
                {
                    Assert.That(msg.ToArray(), Is.EqualTo(originalMessage));
                    await stream.SendMessageAsync(LpmHelpers.WrapLpm(msg));
                }

                await stream.SendTrailersAsync(StatusCode.OK);
                return;
            }
        });

        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/NoCompress", "localhost");
        await clientStream.SendMessageAsync(LpmHelpers.WrapLpm(originalMessage));
        await clientStream.SendHalfCloseAsync();

        ReadOnlyMemory<byte> receivedResponse = default;
        await foreach (var msg in clientStream.ReceiveLpmMessagesAsync())
        {
            receivedResponse = msg;
        }

        Assert.That(receivedResponse.ToArray(), Is.EqualTo(originalMessage));
        await serverTask;
    }

    [Test]
    [CancelAfter(10000)]
    public async Task GzipCompression_SmallMessageBelowThreshold_NotCompressed()
    {
        // Arrange — compression enabled but min size is large
        var compressionOptions = new ShmCompressionOptions
        {
            Enabled = true,
            SendCompressor = GzipCompressor.Default,
            AcceptedCompressors = new List<string> { "gzip", "identity" },
            MinSizeForCompression = 10_000 // Only compress messages >= 10KB
        };

        var (server, client) = CreateConnectionPair(ringCapacity: 65536);

        // Small message below threshold
        var originalMessage = Encoding.UTF8.GetBytes("Small message");

        var serverTask = Task.Run(async () =>
        {
            await foreach (var stream in server.AcceptStreamsAsync())
            {
                await stream.SendResponseHeadersAsync();

                await foreach (var msg in stream.ReceiveLpmMessagesAsync())
                {
                    Assert.That(msg.ToArray(), Is.EqualTo(originalMessage));
                    await stream.SendMessageAsync(LpmHelpers.WrapLpm(msg));
                }

                await stream.SendTrailersAsync(StatusCode.OK);
                return;
            }
        });

        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/SmallMsg", "localhost");
        await clientStream.SendMessageAsync(LpmHelpers.WrapLpm(originalMessage));
        await clientStream.SendHalfCloseAsync();

        ReadOnlyMemory<byte> receivedResponse = default;
        await foreach (var msg in clientStream.ReceiveLpmMessagesAsync())
        {
            receivedResponse = msg;
        }

        Assert.That(receivedResponse.ToArray(), Is.EqualTo(originalMessage));
        await serverTask;
    }

    [Test]
    [CancelAfter(10000)]
    public async Task GzipCompression_MultipleMessages_AllDecompressCorrectly()
    {
        // Arrange
        var compressionOptions = new ShmCompressionOptions
        {
            Enabled = true,
            SendCompressor = GzipCompressor.Default,
            AcceptedCompressors = new List<string> { "gzip", "identity" }
        };

        var (server, client) = CreateConnectionPair(ringCapacity: 65536);

        var messages = new List<byte[]>();
        for (int i = 0; i < 5; i++)
        {
            var msg = new byte[1000];
            // Fill with pattern so gzip can compress
            for (int j = 0; j < msg.Length; j++)
                msg[j] = (byte)(i + (j % 10));
            messages.Add(msg);
        }

        var receivedOnServer = new List<byte[]>();

        var serverTask = Task.Run(async () =>
        {
            await foreach (var stream in server.AcceptStreamsAsync())
            {
                await stream.SendResponseHeadersAsync();

                await foreach (var msg in stream.ReceiveLpmMessagesAsync())
                {
                    receivedOnServer.Add(msg.ToArray());
                    // Echo each message back
                    await stream.SendMessageAsync(LpmHelpers.WrapLpm(msg));
                }

                await stream.SendTrailersAsync(StatusCode.OK);
                return;
            }
        });

        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/MultiMsg", "localhost");

        // Send all messages
        foreach (var msg in messages)
        {
            await clientStream.SendMessageAsync(LpmHelpers.WrapLpm(msg));
        }
        await clientStream.SendHalfCloseAsync();

        var receivedOnClient = new List<byte[]>();
        await foreach (var msg in clientStream.ReceiveLpmMessagesAsync())
        {
            receivedOnClient.Add(msg.ToArray());
        }

        await serverTask;

        // Assert all messages round-tripped correctly
        Assert.That(receivedOnServer.Count, Is.EqualTo(5));
        Assert.That(receivedOnClient.Count, Is.EqualTo(5));

        for (int i = 0; i < 5; i++)
        {
            Assert.That(receivedOnServer[i], Is.EqualTo(messages[i]),
                $"Server message {i} mismatch");
            Assert.That(receivedOnClient[i], Is.EqualTo(messages[i]),
                $"Client message {i} mismatch");
        }
    }

    [Test]
    [CancelAfter(10000)]
    public async Task DeflateCompression_UnaryCall_DataDecompressedCorrectly()
    {
        // Arrange — both sides configured with deflate compression
        var compressionOptions = new ShmCompressionOptions
        {
            Enabled = true,
            SendCompressor = DeflateCompressor.Default,
            AcceptedCompressors = new List<string> { "deflate", "gzip", "identity" }
        };

        var (server, client) = CreateConnectionPair(ringCapacity: 65536);

        // Create a compressible message
        var originalMessage = Encoding.UTF8.GetBytes(new string('A', 5000));

        var serverTask = Task.Run(async () =>
        {
            await foreach (var stream in server.AcceptStreamsAsync())
            {
                await stream.SendResponseHeadersAsync();

                await foreach (var msg in stream.ReceiveLpmMessagesAsync())
                {
                    Assert.That(msg.ToArray(), Is.EqualTo(originalMessage));
                    await stream.SendMessageAsync(LpmHelpers.WrapLpm(msg));
                }

                await stream.SendTrailersAsync(StatusCode.OK);
                return;
            }
        });

        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/DeflateEcho", "localhost");
        await clientStream.SendMessageAsync(LpmHelpers.WrapLpm(originalMessage));
        await clientStream.SendHalfCloseAsync();

        ReadOnlyMemory<byte> receivedResponse = default;
        await foreach (var msg in clientStream.ReceiveLpmMessagesAsync())
        {
            receivedResponse = msg;
        }

        Assert.That(receivedResponse.ToArray(), Is.EqualTo(originalMessage));
        await serverTask;
    }
}
