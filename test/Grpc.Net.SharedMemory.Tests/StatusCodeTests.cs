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
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Tests for gRPC status codes and error handling.
/// Status codes are sent via trailers and available in TrailersV1.
/// </summary>
[TestFixture]
public class StatusCodeTests
{
    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task StatusCode_OK_InTrailers()
    {
        var segmentName = $"status_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/ok", "localhost");

        var serverStream = await server.AcceptStreamAsync();
        Assert.That(serverStream, Is.Not.Null);
        await serverStream!.SendTrailersAsync(Grpc.Core.StatusCode.OK, null);
        
        Assert.That(serverStream.Trailers, Is.Not.Null);
        Assert.That(serverStream.Trailers!.GrpcStatusCode, Is.EqualTo(Grpc.Core.StatusCode.OK));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task StatusCode_NotFound_InTrailers()
    {
        var segmentName = $"status_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/notfound", "localhost");

        var serverStream = await server.AcceptStreamAsync();
        Assert.That(serverStream, Is.Not.Null);
        await serverStream!.SendTrailersAsync(Grpc.Core.StatusCode.NotFound, "Resource not found");
        
        Assert.That(serverStream.Trailers, Is.Not.Null);
        Assert.That(serverStream.Trailers!.GrpcStatusCode, Is.EqualTo(Grpc.Core.StatusCode.NotFound));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task StatusMessage_InTrailers()
    {
        var segmentName = $"status_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/message", "localhost");
        
        var serverStream = await server.AcceptStreamAsync();
        Assert.That(serverStream, Is.Not.Null);

        const string message = "Detailed error description";
        await serverStream!.SendTrailersAsync(Grpc.Core.StatusCode.InvalidArgument, message);
        
        Assert.That(serverStream.Trailers, Is.Not.Null);
        Assert.That(serverStream.Trailers!.GrpcStatusMessage, Is.EqualTo(message));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task StatusCode_Cancelled_AfterCancel()
    {
        var segmentName = $"status_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/cancel", "localhost");
        
        await stream.CancelAsync();
        
        Assert.That(stream.IsCancelled, Is.True);
    }

    [Test]
    [Platform("Win")]
    public void AllStatusCodes_AreValid()
    {
        // Verify all standard gRPC status codes are defined
        var expectedCodes = new[]
        {
            Grpc.Core.StatusCode.OK,
            Grpc.Core.StatusCode.Cancelled,
            Grpc.Core.StatusCode.Unknown,
            Grpc.Core.StatusCode.InvalidArgument,
            Grpc.Core.StatusCode.DeadlineExceeded,
            Grpc.Core.StatusCode.NotFound,
            Grpc.Core.StatusCode.AlreadyExists,
            Grpc.Core.StatusCode.PermissionDenied,
            Grpc.Core.StatusCode.ResourceExhausted,
            Grpc.Core.StatusCode.FailedPrecondition,
            Grpc.Core.StatusCode.Aborted,
            Grpc.Core.StatusCode.OutOfRange,
            Grpc.Core.StatusCode.Unimplemented,
            Grpc.Core.StatusCode.Internal,
            Grpc.Core.StatusCode.Unavailable,
            Grpc.Core.StatusCode.DataLoss,
            Grpc.Core.StatusCode.Unauthenticated
        };
        
        Assert.That(expectedCodes.Length, Is.EqualTo(17));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task ResourceExhausted_Status()
    {
        var segmentName = $"status_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/resource-exhausted", "localhost");

        var serverStream = await server.AcceptStreamAsync();
        await serverStream!.SendTrailersAsync(Grpc.Core.StatusCode.ResourceExhausted, "Rate limit exceeded");
        
        Assert.That(serverStream.Trailers!.GrpcStatusCode, Is.EqualTo(Grpc.Core.StatusCode.ResourceExhausted));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task Internal_Status()
    {
        var segmentName = $"status_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/internal", "localhost");

        var serverStream = await server.AcceptStreamAsync();
        await serverStream!.SendTrailersAsync(Grpc.Core.StatusCode.Internal, "Internal server error");
        
        Assert.That(serverStream.Trailers!.GrpcStatusCode, Is.EqualTo(Grpc.Core.StatusCode.Internal));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task Unavailable_Status()
    {
        var segmentName = $"status_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/unavailable", "localhost");

        var serverStream = await server.AcceptStreamAsync();
        await serverStream!.SendTrailersAsync(Grpc.Core.StatusCode.Unavailable, "Service temporarily unavailable");
        
        Assert.That(serverStream.Trailers!.GrpcStatusCode, Is.EqualTo(Grpc.Core.StatusCode.Unavailable));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task PermissionDenied_Status()
    {
        var segmentName = $"status_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/permission-denied", "localhost");

        var serverStream = await server.AcceptStreamAsync();
        await serverStream!.SendTrailersAsync(Grpc.Core.StatusCode.PermissionDenied, "Access denied");
        
        Assert.That(serverStream.Trailers!.GrpcStatusCode, Is.EqualTo(Grpc.Core.StatusCode.PermissionDenied));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task Unauthenticated_Status()
    {
        var segmentName = $"status_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/unauthenticated", "localhost");

        var serverStream = await server.AcceptStreamAsync();
        await serverStream!.SendTrailersAsync(Grpc.Core.StatusCode.Unauthenticated, "Invalid credentials");
        
        Assert.That(serverStream.Trailers!.GrpcStatusCode, Is.EqualTo(Grpc.Core.StatusCode.Unauthenticated));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task EmptyStatusMessage_IsAllowed()
    {
        var segmentName = $"status_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/empty-message", "localhost");

        var serverStream = await server.AcceptStreamAsync();
        await serverStream!.SendTrailersAsync(Grpc.Core.StatusCode.NotFound, "");
        
        Assert.That(serverStream.Trailers!.GrpcStatusMessage, Is.Empty);
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task NullStatusMessage_IsAllowed()
    {
        var segmentName = $"status_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/null-message", "localhost");

        var serverStream = await server.AcceptStreamAsync();
        await serverStream!.SendTrailersAsync(Grpc.Core.StatusCode.OK, null);
        
        Assert.That(serverStream.Trailers!.GrpcStatusMessage, Is.Null.Or.Empty);
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task UnicodeStatusMessage_IsPreserved()
    {
        var segmentName = $"status_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/unicode-message", "localhost");
        
        var serverStream = await server.AcceptStreamAsync();
        Assert.That(serverStream, Is.Not.Null);

        const string unicodeMessage = "Error: 错误 🔥";
        await serverStream!.SendTrailersAsync(Grpc.Core.StatusCode.Internal, unicodeMessage);
        
        Assert.That(serverStream.Trailers!.GrpcStatusMessage, Is.EqualTo(unicodeMessage));
    }

    [Test]
    [Platform("Win")]
    public void StatusCode_NumericValues_MatchSpec()
    {
        // Verify numeric values match gRPC spec
        Assert.That((int)Grpc.Core.StatusCode.OK, Is.EqualTo(0));
        Assert.That((int)Grpc.Core.StatusCode.Cancelled, Is.EqualTo(1));
        Assert.That((int)Grpc.Core.StatusCode.Unknown, Is.EqualTo(2));
        Assert.That((int)Grpc.Core.StatusCode.InvalidArgument, Is.EqualTo(3));
        Assert.That((int)Grpc.Core.StatusCode.DeadlineExceeded, Is.EqualTo(4));
        Assert.That((int)Grpc.Core.StatusCode.NotFound, Is.EqualTo(5));
        Assert.That((int)Grpc.Core.StatusCode.AlreadyExists, Is.EqualTo(6));
        Assert.That((int)Grpc.Core.StatusCode.PermissionDenied, Is.EqualTo(7));
        Assert.That((int)Grpc.Core.StatusCode.ResourceExhausted, Is.EqualTo(8));
        Assert.That((int)Grpc.Core.StatusCode.FailedPrecondition, Is.EqualTo(9));
        Assert.That((int)Grpc.Core.StatusCode.Aborted, Is.EqualTo(10));
        Assert.That((int)Grpc.Core.StatusCode.OutOfRange, Is.EqualTo(11));
        Assert.That((int)Grpc.Core.StatusCode.Unimplemented, Is.EqualTo(12));
        Assert.That((int)Grpc.Core.StatusCode.Internal, Is.EqualTo(13));
        Assert.That((int)Grpc.Core.StatusCode.Unavailable, Is.EqualTo(14));
        Assert.That((int)Grpc.Core.StatusCode.DataLoss, Is.EqualTo(15));
        Assert.That((int)Grpc.Core.StatusCode.Unauthenticated, Is.EqualTo(16));
    }

    [Test]
    [Platform("Win")]
    public void TrailersV1_HasStatusFields()
    {
        var trailers = new TrailersV1
        {
            GrpcStatusCode = Grpc.Core.StatusCode.NotFound,
            GrpcStatusMessage = "not found"
        };
        
        Assert.That(trailers.GrpcStatusCode, Is.EqualTo(Grpc.Core.StatusCode.NotFound));
        Assert.That(trailers.GrpcStatusMessage, Is.EqualTo("not found"));
    }
}
