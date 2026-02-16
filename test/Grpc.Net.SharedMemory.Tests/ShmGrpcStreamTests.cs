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
using System.Text;

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
    [Timeout(10000)]
    public async Task ReceiveMessagesAsync_ReturnsOwnedIndependentBuffers()
    {
        // Arrange
        var segmentName = $"grpc_test_{Guid.NewGuid():N}";
        using var serverConnection = ShmConnection.CreateAsServer(segmentName, ringCapacity: 64 * 1024, maxStreams: 100);
        using var clientConnection = ShmConnection.ConnectAsClient(segmentName);

        var firstPayload = Encoding.UTF8.GetBytes("first-payload");
        var secondPayload = Encoding.UTF8.GetBytes("second-payload");

        var serverTask = Task.Run(async () =>
        {
            var serverStream = await serverConnection.AcceptStreamAsync();
            Assert.That(serverStream, Is.Not.Null);

            var received = new List<byte[]>();
            await foreach (var message in serverStream!.ReceiveMessagesAsync())
            {
                received.Add(message);
            }

            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendTrailersAsync(StatusCode.OK);
            return received;
        });

        var clientStream = clientConnection.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/ReceiveMessagesAsync_ReturnsOwnedIndependentBuffers", "localhost");
        await clientStream.SendMessageAsync(firstPayload);
        await clientStream.SendMessageAsync(secondPayload);
        await clientStream.SendHalfCloseAsync();

        var receivedMessages = await serverTask;

        // Assert payload correctness
        Assert.That(receivedMessages.Count, Is.EqualTo(2));
        Assert.That(receivedMessages[0], Is.EqualTo(firstPayload));
        Assert.That(receivedMessages[1], Is.EqualTo(secondPayload));

        // Assert ownership/lifetime independence across returned arrays.
        receivedMessages[1][0] ^= 0x1;
        Assert.That(receivedMessages[0], Is.EqualTo(firstPayload));
    }
}
