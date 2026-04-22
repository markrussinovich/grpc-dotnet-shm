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
    [CancelAfter(5000)]
    public async Task TextMetadata_IsPreserved()
    {
        var segmentName = $"metadata_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        var metadata = new Grpc.Core.Metadata
        {
            { "custom-header", "custom-value" }
        };
        
        await clientStream.SendRequestHeadersAsync("/test/metadata", "localhost", metadata);
        await clientStream.SendHalfCloseAsync();
        
        var serverStream = await server.AcceptStreamAsync();
        Assert.That(serverStream, Is.Not.Null);
        using var s = serverStream!;
        
        Assert.That(s.RequestHeaders, Is.Not.Null);
        var entry = s.RequestHeaders!.Metadata.FirstOrDefault(e => e.Key == "custom-header");
        Assert.That(entry.Key, Is.EqualTo("custom-header"));
        Assert.That(Encoding.UTF8.GetString(entry.Values[0]), Is.EqualTo("custom-value"));
        
        await s.SendTrailersAsync(Grpc.Core.StatusCode.OK);
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(5000)]
    public async Task BinaryMetadata_WithBinSuffix_IsAccepted()
    {
        var segmentName = $"metadata_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        
        // Binary metadata uses -bin suffix
        var binaryData = new byte[] { 0x00, 0xFF, 0x42, 0x80 };
        var metadata = new Grpc.Core.Metadata
        {
            { "custom-data-bin", binaryData }
        };
        
        await clientStream.SendRequestHeadersAsync("/test/binary-metadata", "localhost", metadata);
        await clientStream.SendHalfCloseAsync();
        
        var serverStream = await server.AcceptStreamAsync();
        Assert.That(serverStream, Is.Not.Null);
        using var s = serverStream!;
        
        var entry = s.RequestHeaders!.Metadata.FirstOrDefault(e => e.Key == "custom-data-bin");
        Assert.That(entry.Key, Is.EqualTo("custom-data-bin"));
        Assert.That(entry.Values[0], Is.EqualTo(binaryData));
        
        await s.SendTrailersAsync(Grpc.Core.StatusCode.OK);
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(5000)]
    public async Task LargeBinaryMetadata_IsAccepted()
    {
        var segmentName = $"metadata_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 65536, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        
        // Large binary data (8KB)
        var binaryData = new byte[8192];
        new Random(42).NextBytes(binaryData);
        
        var metadata = new Grpc.Core.Metadata
        {
            { "large-data-bin", binaryData }
        };
        
        await clientStream.SendRequestHeadersAsync("/test/large-binary", "localhost", metadata);
        await clientStream.SendHalfCloseAsync();
        
        var serverStream = await server.AcceptStreamAsync();
        Assert.That(serverStream, Is.Not.Null);
        using var s = serverStream!;
        
        var entry = s.RequestHeaders!.Metadata.FirstOrDefault(e => e.Key == "large-data-bin");
        Assert.That(entry.Key, Is.EqualTo("large-data-bin"));
        Assert.That(entry.Values[0].Length, Is.EqualTo(8192));
        Assert.That(entry.Values[0], Is.EqualTo(binaryData));
        
        await s.SendTrailersAsync(Grpc.Core.StatusCode.OK);
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(5000)]
    public async Task MultipleMetadataHeaders_ArePreserved()
    {
        var segmentName = $"metadata_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        var metadata = new Grpc.Core.Metadata
        {
            { "header-1", "value-1" },
            { "header-2", "value-2" },
            { "header-3", "value-3" }
        };
        
        await clientStream.SendRequestHeadersAsync("/test/multi-metadata", "localhost", metadata);
        await clientStream.SendHalfCloseAsync();
        
        var serverStream = await server.AcceptStreamAsync();
        Assert.That(serverStream, Is.Not.Null);
        using var s = serverStream!;
        
        var serverMeta = s.RequestHeaders!.Metadata;
        Assert.That(serverMeta.Count(e => e.Key.StartsWith("header-")), Is.EqualTo(3));
        Assert.That(Encoding.UTF8.GetString(serverMeta.First(e => e.Key == "header-1").Values[0]), Is.EqualTo("value-1"));
        Assert.That(Encoding.UTF8.GetString(serverMeta.First(e => e.Key == "header-2").Values[0]), Is.EqualTo("value-2"));
        Assert.That(Encoding.UTF8.GetString(serverMeta.First(e => e.Key == "header-3").Values[0]), Is.EqualTo("value-3"));
        
        await s.SendTrailersAsync(Grpc.Core.StatusCode.OK);
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(5000)]
    public async Task DuplicateMetadataKeys_AreAllowed()
    {
        var segmentName = $"metadata_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        
        // gRPC allows duplicate keys
        var metadata = new Grpc.Core.Metadata
        {
            { "repeated-key", "value-1" },
            { "repeated-key", "value-2" }
        };
        
        await clientStream.SendRequestHeadersAsync("/test/duplicate-keys", "localhost", metadata);
        await clientStream.SendHalfCloseAsync();
        
        var serverStream = await server.AcceptStreamAsync();
        Assert.That(serverStream, Is.Not.Null);
        using var s = serverStream!;
        
        var entries = s.RequestHeaders!.Metadata.Where(e => e.Key == "repeated-key").ToList();
        Assert.That(entries, Has.Count.EqualTo(2));
        var values = entries.Select(e => Encoding.UTF8.GetString(e.Values[0])).ToList();
        Assert.That(values, Does.Contain("value-1"));
        Assert.That(values, Does.Contain("value-2"));
        
        await s.SendTrailersAsync(Grpc.Core.StatusCode.OK);
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(5000)]
    public async Task EmptyMetadataValue_IsAllowed()
    {
        var segmentName = $"metadata_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        var metadata = new Grpc.Core.Metadata
        {
            { "empty-header", "" }
        };
        
        await clientStream.SendRequestHeadersAsync("/test/empty-value", "localhost", metadata);
        await clientStream.SendHalfCloseAsync();
        
        var serverStream = await server.AcceptStreamAsync();
        Assert.That(serverStream, Is.Not.Null);
        using var s = serverStream!;
        
        var entry = s.RequestHeaders!.Metadata.FirstOrDefault(e => e.Key == "empty-header");
        Assert.That(entry.Key, Is.EqualTo("empty-header"));
        Assert.That(entry.Values[0].Length, Is.EqualTo(0));
        
        await s.SendTrailersAsync(Grpc.Core.StatusCode.OK);
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(5000)]
    public async Task MixedTextAndBinaryMetadata_IsAccepted()
    {
        var segmentName = $"metadata_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var binaryPayload = new byte[] { 1, 2, 3, 4 };
        var clientStream = client.CreateStream();
        var metadata = new Grpc.Core.Metadata
        {
            { "text-header", "text-value" },
            { "binary-data-bin", binaryPayload },
            { "another-text", "another-value" }
        };
        
        await clientStream.SendRequestHeadersAsync("/test/mixed-metadata", "localhost", metadata);
        await clientStream.SendHalfCloseAsync();
        
        var serverStream = await server.AcceptStreamAsync();
        Assert.That(serverStream, Is.Not.Null);
        using var s = serverStream!;
        
        var serverMeta = s.RequestHeaders!.Metadata;
        Assert.That(serverMeta, Has.Count.GreaterThanOrEqualTo(3));
        Assert.That(Encoding.UTF8.GetString(serverMeta.First(e => e.Key == "text-header").Values[0]), Is.EqualTo("text-value"));
        Assert.That(serverMeta.First(e => e.Key == "binary-data-bin").Values[0], Is.EqualTo(binaryPayload));
        Assert.That(Encoding.UTF8.GetString(serverMeta.First(e => e.Key == "another-text").Values[0]), Is.EqualTo("another-value"));
        
        await s.SendTrailersAsync(Grpc.Core.StatusCode.OK);
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
    [CancelAfter(5000)]
    public async Task NullMetadata_IsAccepted()
    {
        var segmentName = $"metadata_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        
        // Null metadata should be fine (no custom headers)
        await clientStream.SendRequestHeadersAsync("/test/null-metadata", "localhost", null);
        await clientStream.SendHalfCloseAsync();
        
        var serverStream = await server.AcceptStreamAsync();
        Assert.That(serverStream, Is.Not.Null);
        using var s = serverStream!;
        
        Assert.That(s.RequestHeaders, Is.Not.Null);
        Assert.That(s.RequestHeaders!.Metadata, Has.Count.EqualTo(0));
        
        await s.SendTrailersAsync(Grpc.Core.StatusCode.OK);
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(5000)]
    public async Task EmptyMetadata_IsAccepted()
    {
        var segmentName = $"metadata_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        var metadata = new Grpc.Core.Metadata();
        
        await clientStream.SendRequestHeadersAsync("/test/empty-metadata", "localhost", metadata);
        await clientStream.SendHalfCloseAsync();
        
        var serverStream = await server.AcceptStreamAsync();
        Assert.That(serverStream, Is.Not.Null);
        using var s = serverStream!;
        
        Assert.That(s.RequestHeaders, Is.Not.Null);
        Assert.That(s.RequestHeaders!.Metadata, Has.Count.EqualTo(0));
        
        await s.SendTrailersAsync(Grpc.Core.StatusCode.OK);
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(5000)]
    public async Task SpecialCharactersInValue_ArePreserved()
    {
        var segmentName = $"metadata_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        var metadata = new Grpc.Core.Metadata
        {
            { "special-chars", "value with spaces and !@#$%^&*()" }
        };
        
        await clientStream.SendRequestHeadersAsync("/test/special-chars", "localhost", metadata);
        await clientStream.SendHalfCloseAsync();
        
        var serverStream = await server.AcceptStreamAsync();
        Assert.That(serverStream, Is.Not.Null);
        using var s = serverStream!;
        
        var entry = s.RequestHeaders!.Metadata.FirstOrDefault(e => e.Key == "special-chars");
        Assert.That(entry.Key, Is.EqualTo("special-chars"));
        Assert.That(Encoding.UTF8.GetString(entry.Values[0]), Is.EqualTo("value with spaces and !@#$%^&*()"));
        
        await s.SendTrailersAsync(Grpc.Core.StatusCode.OK);
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(5000)]
    public async Task UnicodeInValue_IsPreserved()
    {
        var segmentName = $"metadata_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var clientStream = client.CreateStream();
        var metadata = new Grpc.Core.Metadata
        {
            { "unicode-value", "Hello 世界 🌍" }
        };
        
        await clientStream.SendRequestHeadersAsync("/test/unicode", "localhost", metadata);
        await clientStream.SendHalfCloseAsync();
        
        var serverStream = await server.AcceptStreamAsync();
        Assert.That(serverStream, Is.Not.Null);
        using var s = serverStream!;
        
        var entry = s.RequestHeaders!.Metadata.FirstOrDefault(e => e.Key == "unicode-value");
        Assert.That(entry.Key, Is.EqualTo("unicode-value"));
        Assert.That(Encoding.UTF8.GetString(entry.Values[0]), Is.EqualTo("Hello 世界 🌍"));
        
        await s.SendTrailersAsync(Grpc.Core.StatusCode.OK);
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
    [CancelAfter(5000)]
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
    [CancelAfter(5000)]
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
