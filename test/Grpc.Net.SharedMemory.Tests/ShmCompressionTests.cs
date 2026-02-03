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

using Grpc.Net.SharedMemory.Compression;
using NUnit.Framework;
using System.IO.Compression;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Tests for compression support in shared memory transport.
/// SHM equivalent of TCP compression tests in FunctionalTests.
/// </summary>
[TestFixture]
public class ShmCompressionTests
{
    #region Compressor Unit Tests

    [Test]
    public void IdentityCompressor_Compress_ReturnsOriginalData()
    {
        // Arrange
        var compressor = IdentityCompressor.Instance;
        var data = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        var compressed = compressor.Compress(data);

        // Assert
        Assert.That(compressed, Is.EqualTo(data));
    }

    [Test]
    public void IdentityCompressor_Decompress_ReturnsOriginalData()
    {
        // Arrange
        var compressor = IdentityCompressor.Instance;
        var data = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        var decompressed = compressor.Decompress(data);

        // Assert
        Assert.That(decompressed, Is.EqualTo(data));
    }

    [Test]
    public void IdentityCompressor_Properties_AreCorrect()
    {
        // Arrange & Act
        var compressor = IdentityCompressor.Instance;

        // Assert
        Assert.That(compressor.Name, Is.EqualTo("identity"));
        Assert.That(compressor.IsIdentity, Is.True);
    }

    [Test]
    public void GzipCompressor_Properties_AreCorrect()
    {
        // Arrange & Act
        var compressor = GzipCompressor.Default;

        // Assert
        Assert.That(compressor.Name, Is.EqualTo("gzip"));
        Assert.That(compressor.IsIdentity, Is.False);
    }

    [Test]
    public void GzipCompressor_CompressDecompress_RoundTrip()
    {
        // Arrange
        var compressor = GzipCompressor.Default;
        var original = new byte[1000];
        Random.Shared.NextBytes(original);

        // Act
        var compressed = compressor.Compress(original);
        var decompressed = compressor.Decompress(compressed);

        // Assert
        Assert.That(decompressed, Is.EqualTo(original));
    }

    [Test]
    public void GzipCompressor_CompressibleData_ReducesSize()
    {
        // Arrange
        var compressor = GzipCompressor.Default;
        // Highly compressible data (repeated pattern)
        var data = new byte[10000];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 4);
        }

        // Act
        var compressed = compressor.Compress(data);

        // Assert
        Assert.That(compressed.Length, Is.LessThan(data.Length), "Gzip should compress repeating data");
    }

    [Test]
    public void GzipCompressor_EmptyData_RoundTrip()
    {
        // Arrange
        var compressor = GzipCompressor.Default;
        var empty = Array.Empty<byte>();

        // Act
        var compressed = compressor.Compress(empty);
        var decompressed = compressor.Decompress(compressed);

        // Assert
        Assert.That(decompressed, Is.Empty);
    }

    [Test]
    public void GzipCompressor_DifferentCompressionLevels_Work()
    {
        // Arrange
        var data = new byte[1000];
        Random.Shared.NextBytes(data);

        var levels = new[] 
        { 
            CompressionLevel.Fastest, 
            CompressionLevel.Optimal, 
            CompressionLevel.SmallestSize 
        };

        foreach (var level in levels)
        {
            // Act
            var compressor = new GzipCompressor(level);
            var compressed = compressor.Compress(data);
            var decompressed = compressor.Decompress(compressed);

            // Assert
            Assert.That(decompressed, Is.EqualTo(data), $"Round trip failed for level {level}");
        }
    }

    #endregion

    #region Deflate Compressor Tests

    [Test]
    public void DeflateCompressor_Properties_AreCorrect()
    {
        // Arrange & Act
        var compressor = DeflateCompressor.Default;

        // Assert
        Assert.That(compressor.Name, Is.EqualTo("deflate"));
        Assert.That(compressor.IsIdentity, Is.False);
    }

    [Test]
    public void DeflateCompressor_CompressDecompress_RoundTrip()
    {
        // Arrange
        var compressor = DeflateCompressor.Default;
        var original = new byte[1000];
        Random.Shared.NextBytes(original);

        // Act
        var compressed = compressor.Compress(original);
        var decompressed = compressor.Decompress(compressed);

        // Assert
        Assert.That(decompressed, Is.EqualTo(original));
    }

    #endregion

    #region Compressor Registry Tests

    [Test]
    public void CompressorRegistry_Get_ReturnsRegisteredCompressor()
    {
        // Act & Assert - static class, call methods directly
        Assert.That(ShmCompressorRegistry.Get("gzip"), Is.Not.Null);
        Assert.That(ShmCompressorRegistry.Get("identity"), Is.Not.Null);
        Assert.That(ShmCompressorRegistry.Get("deflate"), Is.Not.Null);
    }

    [Test]
    public void CompressorRegistry_Get_UnknownName_ReturnsNull()
    {
        // Act
        var compressor = ShmCompressorRegistry.Get("unknown");

        // Assert
        Assert.That(compressor, Is.Null);
    }

    [Test]
    public void CompressorRegistry_Register_AddsNewCompressor()
    {
        // Arrange
        var customCompressor = new TestCompressor();

        // Act
        ShmCompressorRegistry.Register(customCompressor);
        var retrieved = ShmCompressorRegistry.Get("test");

        // Assert
        Assert.That(retrieved, Is.SameAs(customCompressor));
    }

    [Test]
    public void CompressorRegistry_IsRegistered_ReturnsCorrectValue()
    {
        // Act & Assert
        Assert.That(ShmCompressorRegistry.IsRegistered("gzip"), Is.True);
        Assert.That(ShmCompressorRegistry.IsRegistered("unknown"), Is.False);
    }

    [Test]
    public void CompressorRegistry_GetRegisteredNames_ReturnsNames()
    {
        // Act
        var names = ShmCompressorRegistry.GetRegisteredNames().ToList();

        // Assert
        Assert.That(names, Does.Contain("gzip"));
        Assert.That(names, Does.Contain("identity"));
    }

    #endregion

    #region Compression Options Tests

    [Test]
    public void CompressionOptions_Default_HasCorrectValues()
    {
        // Arrange & Act
        var options = ShmCompressionOptions.Default;

        // Assert
        Assert.That(options.Enabled, Is.True);
        Assert.That(options.AcceptedCompressors, Is.Not.Null);
    }

    [Test]
    public void CompressionOptions_None_DisablesCompression()
    {
        // Arrange & Act
        var options = ShmCompressionOptions.None;

        // Assert
        Assert.That(options.Enabled, Is.False);
    }

    [Test]
    public void CompressionOptions_SendCompressor_CanBeSet()
    {
        // Arrange & Act
        var options = new ShmCompressionOptions
        {
            SendCompressor = GzipCompressor.Default
        };

        // Assert
        Assert.That(options.SendCompressor, Is.SameAs(GzipCompressor.Default));
    }

    #endregion

    #region Helper Classes

    private class TestCompressor : IShmCompressor
    {
        public string Name => "test";
        public bool IsIdentity => false;
        public byte[] Compress(ReadOnlySpan<byte> data) => data.ToArray();
        public byte[] Decompress(ReadOnlySpan<byte> data) => data.ToArray();
    }

    #endregion
}
