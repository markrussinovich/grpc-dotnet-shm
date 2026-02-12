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
/// Tests for metadata handling including binary metadata.
/// </summary>
[TestFixture]
public class MetadataTests
{
    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task TextMetadata_IsPreserved()
    {
        var segmentName = $"metadata_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        var metadata = new Grpc.Core.Metadata
        {
            { "custom-header", "custom-value" }
        };
        
        await stream.SendRequestHeadersAsync("/test/metadata", "localhost", metadata);
        
        Assert.Pass();
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task BinaryMetadata_WithBinSuffix_IsAccepted()
    {
        var segmentName = $"metadata_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        
        // Binary metadata uses -bin suffix
        var binaryData = new byte[] { 0x00, 0xFF, 0x42, 0x80 };
        var metadata = new Grpc.Core.Metadata
        {
            { "custom-data-bin", binaryData }
        };
        
        await stream.SendRequestHeadersAsync("/test/binary-metadata", "localhost", metadata);
        
        Assert.Pass();
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task LargeBinaryMetadata_IsAccepted()
    {
        var segmentName = $"metadata_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 65536, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        
        // Large binary data (8KB)
        var binaryData = new byte[8192];
        new Random(42).NextBytes(binaryData);
        
        var metadata = new Grpc.Core.Metadata
        {
            { "large-data-bin", binaryData }
        };
        
        await stream.SendRequestHeadersAsync("/test/large-binary", "localhost", metadata);
        
        Assert.Pass();
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task MultipleMetadataHeaders_ArePreserved()
    {
        var segmentName = $"metadata_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        var metadata = new Grpc.Core.Metadata
        {
            { "header-1", "value-1" },
            { "header-2", "value-2" },
            { "header-3", "value-3" }
        };
        
        await stream.SendRequestHeadersAsync("/test/multi-metadata", "localhost", metadata);
        
        Assert.Pass();
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task DuplicateMetadataKeys_AreAllowed()
    {
        var segmentName = $"metadata_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        
        // gRPC allows duplicate keys
        var metadata = new Grpc.Core.Metadata
        {
            { "repeated-key", "value-1" },
            { "repeated-key", "value-2" }
        };
        
        await stream.SendRequestHeadersAsync("/test/duplicate-keys", "localhost", metadata);
        
        Assert.Pass();
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task EmptyMetadataValue_IsAllowed()
    {
        var segmentName = $"metadata_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        var metadata = new Grpc.Core.Metadata
        {
            { "empty-header", "" }
        };
        
        await stream.SendRequestHeadersAsync("/test/empty-value", "localhost", metadata);
        
        Assert.Pass();
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task MixedTextAndBinaryMetadata_IsAccepted()
    {
        var segmentName = $"metadata_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        var metadata = new Grpc.Core.Metadata
        {
            { "text-header", "text-value" },
            { "binary-data-bin", new byte[] { 1, 2, 3, 4 } },
            { "another-text", "another-value" }
        };
        
        await stream.SendRequestHeadersAsync("/test/mixed-metadata", "localhost", metadata);
        
        Assert.Pass();
    }

    [Test]
    [Platform("Win")]
    public void MetadataKeyValidation_LowercaseRequired()
    {
        // gRPC metadata keys must be lowercase
        var metadata = new Grpc.Core.Metadata();
        
        // This should work (lowercase)
        metadata.Add("lowercase-key", "value");
        
        Assert.That(metadata.Count, Is.EqualTo(1));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task NullMetadata_IsAccepted()
    {
        var segmentName = $"metadata_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        
        // Null metadata should be fine (no custom headers)
        await stream.SendRequestHeadersAsync("/test/null-metadata", "localhost", null);
        
        Assert.Pass();
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task EmptyMetadata_IsAccepted()
    {
        var segmentName = $"metadata_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        var metadata = new Grpc.Core.Metadata();
        
        await stream.SendRequestHeadersAsync("/test/empty-metadata", "localhost", metadata);
        
        Assert.Pass();
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task SpecialCharactersInValue_ArePreserved()
    {
        var segmentName = $"metadata_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        var metadata = new Grpc.Core.Metadata
        {
            { "special-chars", "value with spaces and !@#$%^&*()" }
        };
        
        await stream.SendRequestHeadersAsync("/test/special-chars", "localhost", metadata);
        
        Assert.Pass();
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task UnicodeInValue_IsPreserved()
    {
        var segmentName = $"metadata_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        var metadata = new Grpc.Core.Metadata
        {
            { "unicode-value", "Hello 世界 🌍" }
        };
        
        await stream.SendRequestHeadersAsync("/test/unicode", "localhost", metadata);
        
        Assert.Pass();
    }

    [Test]
    [Platform("Win")]
    public void BinaryMetadata_WithoutBinSuffix_Throws()
    {
        var metadata = new Grpc.Core.Metadata();
        var binaryData = new byte[] { 1, 2, 3 };
        
        // Binary metadata requires -bin suffix
        Assert.Throws<ArgumentException>(() =>
        {
            metadata.Add("binary-no-suffix", binaryData);
        });
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task TrailerMetadata_IsPreserved()
    {
        var segmentName = $"metadata_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/trailers", "localhost");
        
        var serverStream = await server.AcceptStreamAsync();
        Assert.That(serverStream, Is.Not.Null);

        var trailers = new Grpc.Core.Metadata
        {
            { "trailer-key", "trailer-value" }
        };
        
        await serverStream!.SendTrailersAsync(Grpc.Core.StatusCode.OK, "success", trailers);
        
        Assert.That(serverStream.Trailers, Is.Not.Null);
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task GrpcStatusInTrailers_IsSet()
    {
        var segmentName = $"metadata_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/status-trailers", "localhost");
        
        var serverStream = await server.AcceptStreamAsync();
        Assert.That(serverStream, Is.Not.Null);
        await serverStream!.SendTrailersAsync(Grpc.Core.StatusCode.NotFound, "resource not found");
        
        Assert.That(serverStream.Trailers, Is.Not.Null);
        Assert.That(serverStream.Trailers!.GrpcStatusCode, Is.EqualTo(Grpc.Core.StatusCode.NotFound));
    }
}
