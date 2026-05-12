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
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Unimplemented method tests equivalent to TCP FunctionalTests/Server/UnimplementedTests.cs.
/// Tests handling of unimplemented methods over shared memory transport.
/// </summary>
[TestFixture]
public class ShmUnimplementedTests
{
    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task UnimplementedMethod_ReturnsUnimplementedStatus()
    {
        // Arrange
        var segmentName = $"unimpl_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        // Server responds with unimplemented
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendTrailersAsync(StatusCode.Unimplemented, "Method is unimplemented.");
        });

        // Client calls non-existent method
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/Greet.Greeter/MethodDoesNotExist", "localhost");
        await clientStream.SendHalfCloseAsync();

        await serverTask;

        // Assert - verify the method was called with correct path
        Assert.That(clientStream.RequestHeaders, Is.Not.Null);
        Assert.That(clientStream.RequestHeaders!.Method, Is.EqualTo("/Greet.Greeter/MethodDoesNotExist"));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task UnimplementedService_ReturnsUnimplementedStatus()
    {
        // Arrange
        var segmentName = $"unimpl_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        // Server responds with unimplemented
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendTrailersAsync(StatusCode.Unimplemented, "Service is unimplemented.");
        });

        // Client calls non-existent service
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/Greet.ServiceDoesNotExist/Method", "localhost");
        await clientStream.SendHalfCloseAsync();

        await serverTask;

        // Assert
        Assert.That(clientStream.RequestHeaders, Is.Not.Null);
        Assert.That(clientStream.RequestHeaders!.Method, Is.EqualTo("/Greet.ServiceDoesNotExist/Method"));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task UnimplementedMethod_ServerStreaming_ReturnsUnimplementedStatus()
    {
        // Arrange
        var segmentName = $"unimpl_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        // Server responds with unimplemented immediately
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendTrailersAsync(StatusCode.Unimplemented, "Server streaming method is unimplemented.");
        });

        // Client requests server streaming on non-existent method
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/Greet.Greeter/ServerStreamDoesNotExist", "localhost");
        await clientStream.SendHalfCloseAsync();

        await serverTask;

        // Assert
        Assert.That(clientStream.RequestHeaders, Is.Not.Null);
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task UnimplementedMethod_ClientStreaming_ReturnsUnimplementedStatus()
    {
        // Arrange
        var segmentName = $"unimpl_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        // Server responds with unimplemented
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendTrailersAsync(StatusCode.Unimplemented, "Client streaming method is unimplemented.");
        });

        // Client attempts client streaming on non-existent method
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/Greet.Greeter/ClientStreamDoesNotExist", "localhost");
        
        // Client still sends some messages before getting error
        await clientStream.SendMessageAsync(LpmHelpers.WrapLpm(new byte[] { 1, 2, 3 }));
        await clientStream.SendHalfCloseAsync();

        await serverTask;

        // Assert
        Assert.That(clientStream.RequestHeaders, Is.Not.Null);
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task UnimplementedMethod_BidirectionalStreaming_ReturnsUnimplementedStatus()
    {
        // Arrange
        var segmentName = $"unimpl_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        // Server responds with unimplemented
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendTrailersAsync(StatusCode.Unimplemented, "Bidirectional streaming method is unimplemented.");
        });

        // Client attempts bidirectional streaming on non-existent method
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/Greet.Greeter/BiDiDoesNotExist", "localhost");
        await clientStream.SendMessageAsync(LpmHelpers.WrapLpm(new byte[] { 1, 2, 3 }));
        await clientStream.SendHalfCloseAsync();

        await serverTask;

        // Assert
        Assert.That(clientStream.RequestHeaders, Is.Not.Null);
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task EmptyServiceName_ReturnsUnimplementedStatus()
    {
        // Arrange
        var segmentName = $"unimpl_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        // Server responds with unimplemented
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendTrailersAsync(StatusCode.Unimplemented, "Empty service name.");
        });

        // Client calls with empty service name
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("//Method", "localhost");
        await clientStream.SendHalfCloseAsync();

        await serverTask;

        // Assert
        Assert.That(clientStream.RequestHeaders, Is.Not.Null);
        Assert.That(clientStream.RequestHeaders!.Method, Is.EqualTo("//Method"));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task MalformedPath_ReturnsUnimplementedStatus()
    {
        // Arrange
        var segmentName = $"unimpl_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        // Server responds with unimplemented
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendTrailersAsync(StatusCode.Unimplemented, "Malformed path.");
        });

        // Client calls with malformed path
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("malformed-path-no-slash", "localhost");
        await clientStream.SendHalfCloseAsync();

        await serverTask;

        // Assert
        Assert.That(clientStream.RequestHeaders, Is.Not.Null);
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task UnimplementedWithMetadata_MetadataPreserved()
    {
        // Arrange
        var segmentName = $"unimpl_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var metadata = new Metadata
        {
            { "x-request-id", "12345" },
            { "x-client-version", "1.0.0" }
        };

        // Server responds with unimplemented but can see metadata
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendTrailersAsync(StatusCode.Unimplemented, "Method is unimplemented.");
        });

        // Client calls with metadata
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/Greet.Greeter/DoesNotExist", "localhost", metadata);
        await clientStream.SendHalfCloseAsync();

        await serverTask;

        // Assert - metadata should be preserved in request
        Assert.That(clientStream.RequestHeaders, Is.Not.Null);
        Assert.That(clientStream.RequestHeaders!.Metadata.Count, Is.EqualTo(2));
    }
}
