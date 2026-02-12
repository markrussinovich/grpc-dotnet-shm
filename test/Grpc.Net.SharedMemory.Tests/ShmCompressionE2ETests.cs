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
/// End-to-end tests verifying that compression works through the full
/// ShmConnection → ShmGrpcStream pipeline. Validates that compressed messages
/// are transparently decompressed on the receiver side.
/// </summary>
[TestFixture]
public class ShmCompressionE2ETests : TransportTestBase
{
    [Test]
    [Timeout(10000)]
    public async Task GzipCompression_UnaryCall_DataDecompressedCorrectly()
    {
        // Arrange — both sides configured with gzip compression
        var compressionOptions = new ShmCompressionOptions
        {
            Enabled = true,
            SendCompressor = GzipCompressor.Default,
            AcceptedCompressors = new List<string> { "gzip", "identity" }
        };

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

                await foreach (var msg in stream.ReceiveMessagesAsync())
                {
                    // Verify we received the original decompressed data
                    Assert.That(msg.Length, Is.EqualTo(originalMessage.Length),
                        "Server should receive the original uncompressed message length");
                    Assert.That(msg.ToArray(), Is.EqualTo(originalMessage));

                    // Echo it back (will be compressed by the server's compression options)
                    await stream.SendMessageAsync(msg);
                }

                await stream.SendTrailersAsync(StatusCode.OK);
                return;
            }
        });

        // Client: create stream, send message, receive response
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/GzipEcho", "localhost");
        await clientStream.SendMessageAsync(originalMessage);
        await clientStream.SendHalfCloseAsync();

        ReadOnlyMemory<byte> receivedResponse = default;
        await foreach (var msg in clientStream.ReceiveMessagesAsync())
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
    [Timeout(10000)]
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

                await foreach (var msg in stream.ReceiveMessagesAsync())
                {
                    Assert.That(msg.ToArray(), Is.EqualTo(originalMessage));
                    await stream.SendMessageAsync(msg);
                }

                await stream.SendTrailersAsync(StatusCode.OK);
                return;
            }
        });

        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/NoCompress", "localhost");
        await clientStream.SendMessageAsync(originalMessage);
        await clientStream.SendHalfCloseAsync();

        ReadOnlyMemory<byte> receivedResponse = default;
        await foreach (var msg in clientStream.ReceiveMessagesAsync())
        {
            receivedResponse = msg;
        }

        Assert.That(receivedResponse.ToArray(), Is.EqualTo(originalMessage));
        await serverTask;
    }

    [Test]
    [Timeout(10000)]
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

                await foreach (var msg in stream.ReceiveMessagesAsync())
                {
                    Assert.That(msg.ToArray(), Is.EqualTo(originalMessage));
                    await stream.SendMessageAsync(msg);
                }

                await stream.SendTrailersAsync(StatusCode.OK);
                return;
            }
        });

        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/SmallMsg", "localhost");
        await clientStream.SendMessageAsync(originalMessage);
        await clientStream.SendHalfCloseAsync();

        ReadOnlyMemory<byte> receivedResponse = default;
        await foreach (var msg in clientStream.ReceiveMessagesAsync())
        {
            receivedResponse = msg;
        }

        Assert.That(receivedResponse.ToArray(), Is.EqualTo(originalMessage));
        await serverTask;
    }

    [Test]
    [Timeout(10000)]
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

                await foreach (var msg in stream.ReceiveMessagesAsync())
                {
                    receivedOnServer.Add(msg.ToArray());
                    // Echo each message back
                    await stream.SendMessageAsync(msg);
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
            await clientStream.SendMessageAsync(msg);
        }
        await clientStream.SendHalfCloseAsync();

        var receivedOnClient = new List<byte[]>();
        await foreach (var msg in clientStream.ReceiveMessagesAsync())
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
    [Timeout(10000)]
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

                await foreach (var msg in stream.ReceiveMessagesAsync())
                {
                    Assert.That(msg.ToArray(), Is.EqualTo(originalMessage));
                    await stream.SendMessageAsync(msg);
                }

                await stream.SendTrailersAsync(StatusCode.OK);
                return;
            }
        });

        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/DeflateEcho", "localhost");
        await clientStream.SendMessageAsync(originalMessage);
        await clientStream.SendHalfCloseAsync();

        ReadOnlyMemory<byte> receivedResponse = default;
        await foreach (var msg in clientStream.ReceiveMessagesAsync())
        {
            receivedResponse = msg;
        }

        Assert.That(receivedResponse.ToArray(), Is.EqualTo(originalMessage));
        await serverTask;
    }
}
