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

[TestFixture]
public class HeadersTrailersTests
{
    [Test]
    public void HeadersV1_ClientInitial_EncodeDecodeRoundTrip()
    {
        // Arrange
        var headers = new HeadersV1
        {
            Version = 1,
            HeaderType = 0, // client-initial
            Method = "/grpc.test.TestService/UnaryCall",
            Authority = "localhost:5001",
            DeadlineUnixNano = 1234567890123456789UL,
            Metadata = new[]
            {
                new MetadataKV("content-type", "application/grpc"),
                new MetadataKV("grpc-encoding", "gzip"),
                new MetadataKV("custom-key", "value1", "value2")
            }
        };

        // Act
        var encoded = headers.EncodeToArray();
        var decoded = HeadersV1.Decode(encoded);

        // Assert
        Assert.That(decoded.Version, Is.EqualTo(1));
        Assert.That(decoded.HeaderType, Is.EqualTo(0));
        Assert.That(decoded.Method, Is.EqualTo("/grpc.test.TestService/UnaryCall"));
        Assert.That(decoded.Authority, Is.EqualTo("localhost:5001"));
        Assert.That(decoded.DeadlineUnixNano, Is.EqualTo(1234567890123456789UL));
        Assert.That(decoded.Metadata.Count, Is.EqualTo(3));
        Assert.That(decoded.Metadata[0].Key, Is.EqualTo("content-type"));
        Assert.That(decoded.Metadata[2].Values.Count, Is.EqualTo(2));
    }

    [Test]
    public void HeadersV1_ServerInitial_EncodeDecodeRoundTrip()
    {
        // Arrange
        var headers = new HeadersV1
        {
            Version = 1,
            HeaderType = 1, // server-initial
            Authority = "localhost:5001",
            DeadlineUnixNano = 0,
            Metadata = new[]
            {
                new MetadataKV("content-type", "application/grpc")
            }
        };

        // Act
        var encoded = headers.EncodeToArray();
        var decoded = HeadersV1.Decode(encoded);

        // Assert
        Assert.That(decoded.Version, Is.EqualTo(1));
        Assert.That(decoded.HeaderType, Is.EqualTo(1));
        Assert.That(decoded.Method, Is.Null); // Not present for server headers
        Assert.That(decoded.Authority, Is.EqualTo("localhost:5001"));
    }

    [Test]
    public void HeadersV1_EmptyMetadata_EncodeDecodeRoundTrip()
    {
        // Arrange
        var headers = new HeadersV1
        {
            Version = 1,
            HeaderType = 0,
            Method = "/test",
            Metadata = Array.Empty<MetadataKV>()
        };

        // Act
        var encoded = headers.EncodeToArray();
        var decoded = HeadersV1.Decode(encoded);

        // Assert
        Assert.That(decoded.Metadata.Count, Is.EqualTo(0));
    }

    [Test]
    public void HeadersV1_BinaryMetadata_EncodeDecodeRoundTrip()
    {
        // Arrange
        var binaryValue = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE };
        var headers = new HeadersV1
        {
            Version = 1,
            HeaderType = 0,
            Method = "/test",
            Metadata = new[]
            {
                new MetadataKV { Key = "binary-bin", Values = new[] { binaryValue } }
            }
        };

        // Act
        var encoded = headers.EncodeToArray();
        var decoded = HeadersV1.Decode(encoded);

        // Assert
        Assert.That(decoded.Metadata[0].Values[0], Is.EqualTo(binaryValue));
    }

    [Test]
    public void TrailersV1_Success_EncodeDecodeRoundTrip()
    {
        // Arrange
        var trailers = new TrailersV1
        {
            Version = 1,
            GrpcStatusCode = StatusCode.OK,
            GrpcStatusMessage = "",
            Metadata = Array.Empty<MetadataKV>()
        };

        // Act
        var encoded = trailers.EncodeToArray();
        var decoded = TrailersV1.Decode(encoded);

        // Assert
        Assert.That(decoded.Version, Is.EqualTo(1));
        Assert.That(decoded.GrpcStatusCode, Is.EqualTo(StatusCode.OK));
        Assert.That(decoded.GrpcStatusMessage, Is.Null.Or.Empty);
    }

    [Test]
    public void TrailersV1_Error_EncodeDecodeRoundTrip()
    {
        // Arrange
        var trailers = new TrailersV1
        {
            Version = 1,
            GrpcStatusCode = StatusCode.InvalidArgument,
            GrpcStatusMessage = "Missing required field: name",
            Metadata = new[]
            {
                new MetadataKV("error-details-bin", new byte[] { 1, 2, 3 })
            }
        };

        // Act
        var encoded = trailers.EncodeToArray();
        var decoded = TrailersV1.Decode(encoded);

        // Assert
        Assert.That(decoded.Version, Is.EqualTo(1));
        Assert.That(decoded.GrpcStatusCode, Is.EqualTo(StatusCode.InvalidArgument));
        Assert.That(decoded.GrpcStatusMessage, Is.EqualTo("Missing required field: name"));
        Assert.That(decoded.Metadata.Count, Is.EqualTo(1));
        Assert.That(decoded.Metadata[0].Key, Is.EqualTo("error-details-bin"));
    }

    [Test]
    public void TrailersV1_AllStatusCodes_EncodeDecodeCorrectly()
    {
        var statusCodes = new[]
        {
            StatusCode.OK,
            StatusCode.Cancelled,
            StatusCode.Unknown,
            StatusCode.InvalidArgument,
            StatusCode.DeadlineExceeded,
            StatusCode.NotFound,
            StatusCode.AlreadyExists,
            StatusCode.PermissionDenied,
            StatusCode.ResourceExhausted,
            StatusCode.FailedPrecondition,
            StatusCode.Aborted,
            StatusCode.OutOfRange,
            StatusCode.Unimplemented,
            StatusCode.Internal,
            StatusCode.Unavailable,
            StatusCode.DataLoss,
            StatusCode.Unauthenticated
        };

        foreach (var code in statusCodes)
        {
            var trailers = new TrailersV1
            {
                GrpcStatusCode = code,
                GrpcStatusMessage = $"Status: {code}"
            };

            var encoded = trailers.EncodeToArray();
            var decoded = TrailersV1.Decode(encoded);

            Assert.That(decoded.GrpcStatusCode, Is.EqualTo(code), $"Failed for status code {code}");
        }
    }

    [Test]
    public void TrailersV1_WithTrailingMetadata_EncodeDecodeRoundTrip()
    {
        // Arrange
        var trailers = new TrailersV1
        {
            GrpcStatusCode = StatusCode.OK,
            Metadata = new[]
            {
                new MetadataKV("trailer-key-1", "value1"),
                new MetadataKV("trailer-key-2", "value2a", "value2b"),
                new MetadataKV("timing-bin", new byte[] { 0x01, 0x02, 0x03, 0x04 })
            }
        };

        // Act
        var encoded = trailers.EncodeToArray();
        var decoded = TrailersV1.Decode(encoded);

        // Assert
        Assert.That(decoded.Metadata.Count, Is.EqualTo(3));
        Assert.That(decoded.Metadata[0].Key, Is.EqualTo("trailer-key-1"));
        Assert.That(decoded.Metadata[1].Values.Count, Is.EqualTo(2));
        Assert.That(decoded.Metadata[2].Values[0], Is.EqualTo(new byte[] { 0x01, 0x02, 0x03, 0x04 }));
    }

    [Test]
    public void HeadersV1_Decode_TooShort_ThrowsException()
    {
        Assert.Throws<InvalidDataException>(() => HeadersV1.Decode(new byte[] { 1 }));
    }

    [Test]
    public void TrailersV1_Decode_TooShort_ThrowsException()
    {
        Assert.Throws<InvalidDataException>(() => TrailersV1.Decode(new byte[] { 1, 0, 0 }));
    }

    [Test]
    public void HeadersV1_Decode_InvalidVersion_ThrowsException()
    {
        var data = new byte[20];
        data[0] = 2; // Invalid version
        Assert.Throws<InvalidDataException>(() => HeadersV1.Decode(data));
    }

    [Test]
    public void TrailersV1_Decode_InvalidVersion_ThrowsException()
    {
        var data = new byte[20];
        data[0] = 2; // Invalid version
        Assert.Throws<InvalidDataException>(() => TrailersV1.Decode(data));
    }
}
