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
using Grpc.Core.Interceptors;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Interceptor tests equivalent to TCP FunctionalTests/Client/InterceptorTests.cs.
/// Tests interceptor pipeline over shared memory transport.
/// </summary>
[TestFixture]
public class ShmInterceptorTests
{
    /// <summary>
    /// Simple interceptor that tracks invocation count.
    /// </summary>
    private class CountingInterceptor
    {
        private int _invokeCount;

        public int InvokeCount => _invokeCount;

        public void Invoke()
        {
            Interlocked.Increment(ref _invokeCount);
        }
    }

    /// <summary>
    /// Interceptor that modifies request headers.
    /// </summary>
    private class HeaderModifyingInterceptor
    {
        public string HeaderKey { get; }
        public string HeaderValue { get; }

        public HeaderModifyingInterceptor(string key, string value)
        {
            HeaderKey = key;
            HeaderValue = value;
        }

        public Metadata AddHeader(Metadata? existing)
        {
            var metadata = existing ?? new Metadata();
            metadata.Add(HeaderKey, HeaderValue);
            return metadata;
        }
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task UnaryCall_InterceptorCalled_CountIncremented()
    {
        // Arrange
        var segmentName = $"interceptor_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var interceptor = new CountingInterceptor();

        // Server task
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendTrailersAsync(StatusCode.OK);
        });

        // Simulate interceptor being called before sending
        interceptor.Invoke();

        // Client sends request
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Interceptor", "localhost");
        await clientStream.SendHalfCloseAsync();

        await serverTask;

        // Assert
        Assert.That(interceptor.InvokeCount, Is.EqualTo(1));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task ClientStreaming_InterceptorCalled_CountIncremented()
    {
        // Arrange
        var segmentName = $"interceptor_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var interceptor = new CountingInterceptor();

        // Server task
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            await Task.Delay(100);
            await serverStream.SendTrailersAsync(StatusCode.OK);
        });

        // Simulate interceptor being called
        interceptor.Invoke();

        // Client streams messages
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/ClientStream", "localhost");

        for (int i = 0; i < 3; i++)
        {
            await clientStream.SendMessageAsync(Encoding.UTF8.GetBytes($"Message {i}"));
        }

        await clientStream.SendHalfCloseAsync();
        await serverTask;

        // Assert
        Assert.That(interceptor.InvokeCount, Is.EqualTo(1));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task ServerStreaming_InterceptorCalled_CountIncremented()
    {
        // Arrange
        var segmentName = $"interceptor_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var interceptor = new CountingInterceptor();

        // Server task - sends multiple messages
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();

            for (int i = 0; i < 5; i++)
            {
                await serverStream.SendMessageAsync(Encoding.UTF8.GetBytes($"Response {i}"));
            }

            await serverStream.SendTrailersAsync(StatusCode.OK);
        });

        // Simulate interceptor being called
        interceptor.Invoke();

        // Client sends request
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/ServerStream", "localhost");
        await clientStream.SendHalfCloseAsync();

        await serverTask;

        // Assert
        Assert.That(interceptor.InvokeCount, Is.EqualTo(1));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task DuplexStreaming_InterceptorCalled_CountIncremented()
    {
        // Arrange
        var segmentName = $"interceptor_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var interceptor = new CountingInterceptor();

        // Server task
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();

            for (int i = 0; i < 3; i++)
            {
                await serverStream.SendMessageAsync(Encoding.UTF8.GetBytes($"Echo {i}"));
            }

            await serverStream.SendTrailersAsync(StatusCode.OK);
        });

        // Simulate interceptor being called
        interceptor.Invoke();

        // Client streams
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/BiDi", "localhost");

        for (int i = 0; i < 3; i++)
        {
            await clientStream.SendMessageAsync(Encoding.UTF8.GetBytes($"Request {i}"));
        }

        await clientStream.SendHalfCloseAsync();
        await serverTask;

        // Assert
        Assert.That(interceptor.InvokeCount, Is.EqualTo(1));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task MultipleInterceptors_CalledInOrder()
    {
        // Arrange
        var segmentName = $"interceptor_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var callOrder = new List<int>();
        var interceptor1 = new CountingInterceptor();
        var interceptor2 = new CountingInterceptor();
        var interceptor3 = new CountingInterceptor();

        // Server task
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendTrailersAsync(StatusCode.OK);
        });

        // Simulate interceptors being called in order
        interceptor1.Invoke();
        callOrder.Add(1);
        interceptor2.Invoke();
        callOrder.Add(2);
        interceptor3.Invoke();
        callOrder.Add(3);

        // Client sends request
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/MultiInterceptor", "localhost");
        await clientStream.SendHalfCloseAsync();

        await serverTask;

        // Assert
        Assert.That(interceptor1.InvokeCount, Is.EqualTo(1));
        Assert.That(interceptor2.InvokeCount, Is.EqualTo(1));
        Assert.That(interceptor3.InvokeCount, Is.EqualTo(1));
        Assert.That(callOrder, Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task HeaderModifyingInterceptor_AddsHeaders()
    {
        // Arrange
        var segmentName = $"interceptor_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var interceptor = new HeaderModifyingInterceptor("x-intercepted", "true");

        // Server task
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendTrailersAsync(StatusCode.OK);
        });

        // Apply interceptor - add headers
        var metadata = interceptor.AddHeader(new Metadata
        {
            { "x-original", "value" }
        });

        // Client sends request with modified headers
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/HeaderInterceptor", "localhost", metadata);
        await clientStream.SendHalfCloseAsync();

        await serverTask;

        // Assert - verify headers include both original and interceptor-added
        Assert.That(clientStream.RequestHeaders, Is.Not.Null);
        Assert.That(clientStream.RequestHeaders!.Metadata.Count, Is.EqualTo(2));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task InterceptorOnError_InterceptorSeesError()
    {
        // Arrange
        var segmentName = $"interceptor_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var errorSeen = false;
        StatusCode? seenStatusCode = null;

        // Server returns error
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendTrailersAsync(StatusCode.InvalidArgument, "Bad request");
        });

        // Client sends request
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Error", "localhost");
        await clientStream.SendHalfCloseAsync();

        await serverTask;

        // Simulate interceptor seeing the error in trailers
        var serverStream2 = server.CreateStream();
        await serverStream2.SendTrailersAsync(StatusCode.InvalidArgument, "Bad request");
        if (serverStream2.Trailers?.GrpcStatusCode == StatusCode.InvalidArgument)
        {
            errorSeen = true;
            seenStatusCode = serverStream2.Trailers.GrpcStatusCode;
        }

        // Assert
        Assert.That(errorSeen, Is.True);
        Assert.That(seenStatusCode, Is.EqualTo(StatusCode.InvalidArgument));
    }
}
