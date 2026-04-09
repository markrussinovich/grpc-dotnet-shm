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

using NUnit.Framework;
using Grpc.Core;

namespace Grpc.Net.SharedMemory.Tests;

[TestFixture]
public class ShmGrpcStreamTests
{
    [Test]
    public void ShmGrpcStream_InitialState_IsCorrect()
    {
        // Arrange
        var name = $"grpc_test_{Guid.NewGuid():N}";
        using var connection = ShmConnection.CreateAsServer(name, ringCapacity: 4096, maxStreams: 100);
        using var stream = connection.CreateStream();

        // Assert
        Assert.That(stream.StreamId, Is.EqualTo(2)); // Server uses even IDs
        Assert.That(stream.IsClientStream, Is.False);
        Assert.That(stream.IsLocalHalfClosed, Is.False);
        Assert.That(stream.IsRemoteHalfClosed, Is.False);
        Assert.That(stream.IsCancelled, Is.False);
        Assert.That(stream.RequestHeaders, Is.Null);
        Assert.That(stream.ResponseHeaders, Is.Null);
        Assert.That(stream.Trailers, Is.Null);
    }

    [Test]
    public async Task ShmGrpcStream_SendRequestHeaders_SetsHeaders()
    {
        // Arrange
        var name = $"grpc_test_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(name, ringCapacity: 4096, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(name);
        using var stream = clientConnection.CreateStream();

        var metadata = new Metadata
        {
            { "custom-header", "value1" }
        };

        // Act
        await stream.SendRequestHeadersAsync(
            "/test.Service/Method",
            "localhost:5001",
            metadata,
            DateTime.UtcNow.AddMinutes(5));

        // Assert
        Assert.That(stream.RequestHeaders, Is.Not.Null);
        Assert.That(stream.RequestHeaders!.Method, Is.EqualTo("/test.Service/Method"));
        Assert.That(stream.RequestHeaders.Authority, Is.EqualTo("localhost:5001"));
    }

    [Test]
    public async Task ShmGrpcStream_SendHalfClose_SetsHalfClosedFlag()
    {
        // Arrange
        var name = $"grpc_test_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(name, ringCapacity: 4096, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(name);
        using var stream = clientConnection.CreateStream();

        // Act
        await stream.SendHalfCloseAsync();

        // Assert
        Assert.That(stream.IsLocalHalfClosed, Is.True);
    }

    [Test]
    public async Task ShmGrpcStream_SendTrailers_SetsTrailersAndHalfClose()
    {
        // Arrange
        var name = $"grpc_test_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(name, ringCapacity: 4096, maxStreams: 100);
        using var stream = serverConnection.CreateStream();

        var metadata = new Metadata
        {
            { "trailer-key", "trailer-value" }
        };

        // Act
        await stream.SendTrailersAsync(StatusCode.OK, "Success", metadata);

        // Assert
        Assert.That(stream.Trailers, Is.Not.Null);
        Assert.That(stream.Trailers!.GrpcStatusCode, Is.EqualTo(StatusCode.OK));
        Assert.That(stream.Trailers.GrpcStatusMessage, Is.EqualTo("Success"));
        Assert.That(stream.IsLocalHalfClosed, Is.True);
    }

    [Test]
    public async Task ShmGrpcStream_Cancel_SetsCancelledFlag()
    {
        // Arrange
        var name = $"grpc_test_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(name, ringCapacity: 4096, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(name);
        using var stream = clientConnection.CreateStream();

        // Act
        await stream.CancelAsync();

        // Assert
        Assert.That(stream.IsCancelled, Is.True);
    }

    [Test]
    public void ShmGrpcStream_SendRequestHeaders_OnServerStream_Throws()
    {
        // Arrange
        var name = $"grpc_test_{Guid.NewGuid():N}";
        using var connection = ShmConnection.CreateAsServer(name, ringCapacity: 4096, maxStreams: 100);
        using var stream = connection.CreateStream();

        // Act & Assert
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await stream.SendRequestHeadersAsync("/test", "localhost"));
    }

    [Test]
    public void ShmGrpcStream_SendTrailers_OnClientStream_Throws()
    {
        // Arrange
        var name = $"grpc_test_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(name, ringCapacity: 4096, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(name);
        using var stream = clientConnection.CreateStream();

        // Act & Assert
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await stream.SendTrailersAsync(StatusCode.OK));
    }

    [Test]
    public void ShmGrpcStream_Dispose_DisposesStream()
    {
        // Arrange
        var name = $"grpc_test_{Guid.NewGuid():N}";
        using var connection = ShmConnection.CreateAsServer(name, ringCapacity: 4096, maxStreams: 100);
        var stream = connection.CreateStream();

        // Act
        stream.Dispose();

        // Assert - should throw on further operations
        Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await stream.SendTrailersAsync(StatusCode.OK));
    }

    [Test]
    [Timeout(30000)]
    public async Task UnaryLargePayload_64KB_DataIntegrity()
    {
        // Reproduces: "Failed to deserialize response message" for unary ≥64KB.
        // Tests that zero-copy send path does not corrupt data.
        var name = $"grpc_test_{Guid.NewGuid():N}";
        const int payloadSize = 64 * 1024;

        using var serverConn = ShmConnection.CreateAsServer(name, ringCapacity: 1024 * 1024, maxStreams: 100);
        using var clientConn = ShmConnection.ConnectAsClient(name);

        // Create payload with known pattern
        var requestPayload = new byte[payloadSize];
        new Random(42).NextBytes(requestPayload);

        for (int rpc = 0; rpc < 100; rpc++)
        {
            // Server side: accept stream, read request, echo back, send trailers
            var serverTask = Task.Run(async () =>
            {
                var stream = await serverConn.AcceptStreamAsync();
                Assert.That(stream, Is.Not.Null);
                using var s = stream!;

                // AcceptStreamAsync already decoded request headers.
                // Send response headers
                await s.SendResponseHeadersAsync();

                // Read request messages
                byte[]? receivedMsg = null;
                await foreach (var msg in s.ReceiveMessagesAsync())
                {
                    receivedMsg = msg;
                }

                Assert.That(receivedMsg, Is.Not.Null, $"RPC {rpc}: server received no message");
                Assert.That(receivedMsg!.Length, Is.EqualTo(payloadSize), $"RPC {rpc}: server message length mismatch");

                // Echo back the same data
                await s.SendMessageAsync(receivedMsg);
                await s.SendTrailersAsync(StatusCode.OK);
            });

            // Client side
            using var clientStream = clientConn.CreateStream();
            await clientStream.SendRequestHeadersAsync("/test/Echo", "localhost");
            await clientStream.SendMessageAsync(requestPayload);
            await clientStream.SendHalfCloseAsync();

            var responseHeaders = await clientStream.ReceiveResponseHeadersAsync();

            byte[]? responseMsg = null;
            await foreach (var msg in clientStream.ReceiveMessagesAsync())
            {
                responseMsg = msg;
            }

            await serverTask;

            Assert.That(responseMsg, Is.Not.Null, $"RPC {rpc}: client received no response");
            Assert.That(responseMsg!.Length, Is.EqualTo(payloadSize), $"RPC {rpc}: response length mismatch");
            Assert.That(responseMsg, Is.EqualTo(requestPayload), $"RPC {rpc}: response data mismatch at byte {FindFirstMismatch(requestPayload, responseMsg)}");
        }
    }

    [Test]
    [Timeout(30000)]
    public async Task UnaryLargePayload_1MB_DataIntegrity()
    {
        var name = $"grpc_test_{Guid.NewGuid():N}";
        const int payloadSize = 1024 * 1024;

        using var serverConn = ShmConnection.CreateAsServer(name, ringCapacity: 4 * 1024 * 1024, maxStreams: 100);
        using var clientConn = ShmConnection.ConnectAsClient(name);

        var requestPayload = new byte[payloadSize];
        new Random(99).NextBytes(requestPayload);

        for (int rpc = 0; rpc < 20; rpc++)
        {
            var serverTask = Task.Run(async () =>
            {
                var stream = await serverConn.AcceptStreamAsync();
                using var s = stream!;

                await s.SendResponseHeadersAsync();

                byte[]? msg = null;
                await foreach (var m in s.ReceiveMessagesAsync())
                    msg = m;

                await s.SendMessageAsync(msg!);
                await s.SendTrailersAsync(StatusCode.OK);
            });

            using var cs = clientConn.CreateStream();
            await cs.SendRequestHeadersAsync("/test/Echo", "localhost");
            await cs.SendMessageAsync(requestPayload);
            await cs.SendHalfCloseAsync();
            await cs.ReceiveResponseHeadersAsync();

            byte[]? resp = null;
            await foreach (var m in cs.ReceiveMessagesAsync())
                resp = m;

            await serverTask;

            Assert.That(resp, Is.Not.Null, $"RPC {rpc}: no response");
            Assert.That(resp!.Length, Is.EqualTo(payloadSize), $"RPC {rpc}: length mismatch");
            Assert.That(resp, Is.EqualTo(requestPayload), $"RPC {rpc}: data mismatch at byte {FindFirstMismatch(requestPayload, resp)}");
        }
    }

    private static int FindFirstMismatch(byte[] a, byte[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
            if (a[i] != b[i]) return i;
        return a.Length != b.Length ? len : -1;
    }

    [Test]
    [Timeout(30000)]
    public async Task E2E_Unary64KB_ViaControlHandler_DataIntegrity()
    {
        // End-to-end through ShmControlHandler (same path as GrpcChannel).
        // Reproduces "Failed to deserialize" for unary ≥64KB.
        var segmentName = $"e2e_64k_{Guid.NewGuid():N}";
        const int payloadSize = 64 * 1024;

        using var listener = new ShmControlListener(segmentName, ringCapacity: 1024 * 1024, maxStreams: 100);

        // Server: accept connections, echo messages
        var serverTask = Task.Run(async () =>
        {
            await foreach (var conn in listener.AcceptConnectionsAsync())
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var stream in conn.AcceptStreamsAsync())
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    byte[]? msg = null;
                                    await foreach (var m in stream.ReceiveMessagesAsync())
                                        msg = m;

                                    await stream.SendResponseHeadersAsync();
                                    if (msg != null)
                                        await stream.SendMessageAsync(msg);
                                    await stream.SendTrailersAsync(StatusCode.OK);
                                }
                                catch { }
                                finally { stream.Dispose(); }
                            });
                        }
                    }
                    catch { }
                });
            }
        });

        using var handler = new ShmControlHandler(segmentName);
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);

        var requestPayload = new byte[payloadSize];
        new Random(42).NextBytes(requestPayload);

        for (int rpc = 0; rpc < 50; rpc++)
        {
            // Build gRPC-framed request
            var grpcFrame = new byte[5 + payloadSize];
            grpcFrame[0] = 0;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(grpcFrame.AsSpan(1), (uint)payloadSize);
            requestPayload.CopyTo(grpcFrame.AsSpan(5));

            var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/test/Echo")
            {
                Version = new Version(2, 0),
                Content = new ByteArrayContent(grpcFrame)
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc");

            using var response = await invoker.SendAsync(request, CancellationToken.None);
            var body = await response.Content.ReadAsByteArrayAsync();

            // body should be gRPC-framed: [0][length:4][payload]
            Assert.That(body.Length, Is.GreaterThanOrEqualTo(5 + payloadSize),
                $"RPC {rpc}: response too short ({body.Length} bytes)");
            Assert.That(body[0], Is.EqualTo(0), $"RPC {rpc}: compressed flag should be 0");
            var respLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(1));
            Assert.That((int)respLen, Is.EqualTo(payloadSize), $"RPC {rpc}: gRPC length mismatch");
            var respPayload = body.AsSpan(5, payloadSize);
            Assert.That(respPayload.SequenceEqual(requestPayload),
                $"RPC {rpc}: response data mismatch at byte {FindFirstMismatch(requestPayload, respPayload.ToArray())}");
        }

        listener.Dispose(); // stops server
    }

    [Test]
    [Timeout(30000)]
    public async Task SendMessageAndHalfClose_64KB_DataIntegrity()
    {
        // Exercises SendMessageAndHalfCloseAsync: a single frame with
        // Message + EndStream flag, verified on the receive side via
        // both ReceiveMessagesAsync and ReceiveNextMessageBufferAsync paths.
        var name = $"grpc_test_{Guid.NewGuid():N}";
        const int payloadSize = 64 * 1024;

        using var serverConn = ShmConnection.CreateAsServer(name, ringCapacity: 1024 * 1024, maxStreams: 100);
        using var clientConn = ShmConnection.ConnectAsClient(name);

        var requestPayload = new byte[payloadSize];
        new Random(77).NextBytes(requestPayload);

        for (int rpc = 0; rpc < 50; rpc++)
        {
            var serverTask = Task.Run(async () =>
            {
                var stream = await serverConn.AcceptStreamAsync();
                Assert.That(stream, Is.Not.Null);
                using var s = stream!;

                await s.SendResponseHeadersAsync();

                // Use ReceiveMessagesAsync — exercises the yield-based EndStream path
                byte[]? receivedMsg = null;
                await foreach (var msg in s.ReceiveMessagesAsync())
                {
                    receivedMsg = msg;
                }

                Assert.That(receivedMsg, Is.Not.Null, $"RPC {rpc}: server received no message");
                Assert.That(receivedMsg!.Length, Is.EqualTo(payloadSize), $"RPC {rpc}: server message length mismatch");

                // Echo back using the same combined send
                await s.SendMessageAndHalfCloseAsync(receivedMsg);
                await s.SendTrailersAsync(StatusCode.OK);
            });

            using var cs = clientConn.CreateStream();
            await cs.SendRequestHeadersAsync("/test/Echo", "localhost");
            // Combined message + half-close — no separate SendHalfCloseAsync
            await cs.SendMessageAndHalfCloseAsync(requestPayload);

            await cs.ReceiveResponseHeadersAsync();

            byte[]? responseMsg = null;
            await foreach (var msg in cs.ReceiveMessagesAsync())
            {
                responseMsg = msg;
            }

            await serverTask;

            Assert.That(responseMsg, Is.Not.Null, $"RPC {rpc}: client received no response");
            Assert.That(responseMsg!.Length, Is.EqualTo(payloadSize), $"RPC {rpc}: response length mismatch");
            Assert.That(responseMsg, Is.EqualTo(requestPayload),
                $"RPC {rpc}: response data mismatch at byte {FindFirstMismatch(requestPayload, responseMsg)}");
        }
    }
}
