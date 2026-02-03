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

using System.IO.Compression;

namespace Grpc.Net.SharedMemory.Compression;

/// <summary>
/// Interface for shared memory transport compressors.
/// Modeled on grpc-go encoding.Compressor.
/// </summary>
public interface IShmCompressor
{
    /// <summary>
    /// Gets the name of the compressor (e.g., "gzip", "deflate", "identity").
    /// This corresponds to the grpc-encoding header value.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Compresses the input data.
    /// </summary>
    /// <param name="data">The data to compress.</param>
    /// <returns>The compressed data.</returns>
    byte[] Compress(ReadOnlySpan<byte> data);

    /// <summary>
    /// Decompresses the input data.
    /// </summary>
    /// <param name="data">The compressed data.</param>
    /// <returns>The decompressed data.</returns>
    byte[] Decompress(ReadOnlySpan<byte> data);

    /// <summary>
    /// Gets whether compression is a no-op (e.g., identity encoding).
    /// </summary>
    bool IsIdentity { get; }
}

/// <summary>
/// Identity compressor that performs no compression.
/// </summary>
public sealed class IdentityCompressor : IShmCompressor
{
    /// <summary>
    /// Singleton instance of the identity compressor.
    /// </summary>
    public static readonly IdentityCompressor Instance = new();

    /// <inheritdoc/>
    public string Name => "identity";

    /// <inheritdoc/>
    public bool IsIdentity => true;

    /// <inheritdoc/>
    public byte[] Compress(ReadOnlySpan<byte> data) => data.ToArray();

    /// <inheritdoc/>
    public byte[] Decompress(ReadOnlySpan<byte> data) => data.ToArray();
}

/// <summary>
/// Gzip compressor for shared memory transport.
/// Modeled on grpc-go encoding/gzip.
/// </summary>
public sealed class GzipCompressor : IShmCompressor
{
    private readonly CompressionLevel _level;

    /// <summary>
    /// Singleton instance with default compression level.
    /// </summary>
    public static readonly GzipCompressor Default = new(CompressionLevel.Fastest);

    /// <summary>
    /// Creates a new gzip compressor with the specified compression level.
    /// </summary>
    public GzipCompressor(CompressionLevel level = CompressionLevel.Fastest)
    {
        _level = level;
    }

    /// <inheritdoc/>
    public string Name => "gzip";

    /// <inheritdoc/>
    public bool IsIdentity => false;

    /// <inheritdoc/>
    public byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, _level, leaveOpen: true))
        {
            gzip.Write(data);
        }
        return output.ToArray();
    }

    /// <inheritdoc/>
    public byte[] Decompress(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray());
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}

/// <summary>
/// Deflate compressor for shared memory transport.
/// </summary>
public sealed class DeflateCompressor : IShmCompressor
{
    private readonly CompressionLevel _level;

    /// <summary>
    /// Singleton instance with default compression level.
    /// </summary>
    public static readonly DeflateCompressor Default = new(CompressionLevel.Fastest);

    /// <summary>
    /// Creates a new deflate compressor with the specified compression level.
    /// </summary>
    public DeflateCompressor(CompressionLevel level = CompressionLevel.Fastest)
    {
        _level = level;
    }

    /// <inheritdoc/>
    public string Name => "deflate";

    /// <inheritdoc/>
    public bool IsIdentity => false;

    /// <inheritdoc/>
    public byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, _level, leaveOpen: true))
        {
            deflate.Write(data);
        }
        return output.ToArray();
    }

    /// <inheritdoc/>
    public byte[] Decompress(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray());
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }
}

/// <summary>
/// Registry for shared memory transport compressors.
/// Modeled on grpc-go encoding.RegisterCompressor.
/// </summary>
public static class ShmCompressorRegistry
{
    private static readonly Dictionary<string, IShmCompressor> _compressors = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    static ShmCompressorRegistry()
    {
        // Register built-in compressors
        Register(IdentityCompressor.Instance);
        Register(GzipCompressor.Default);
        Register(DeflateCompressor.Default);
    }

    /// <summary>
    /// Registers a compressor.
    /// </summary>
    /// <param name="compressor">The compressor to register.</param>
    public static void Register(IShmCompressor compressor)
    {
        ArgumentNullException.ThrowIfNull(compressor);
        lock (_lock)
        {
            _compressors[compressor.Name] = compressor;
        }
    }

    /// <summary>
    /// Gets a compressor by name.
    /// </summary>
    /// <param name="name">The compressor name (e.g., "gzip").</param>
    /// <returns>The compressor, or null if not found.</returns>
    public static IShmCompressor? Get(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Equals("identity", StringComparison.OrdinalIgnoreCase))
        {
            return IdentityCompressor.Instance;
        }

        lock (_lock)
        {
            return _compressors.TryGetValue(name, out var compressor) ? compressor : null;
        }
    }

    /// <summary>
    /// Gets all registered compressor names.
    /// </summary>
    public static IEnumerable<string> GetRegisteredNames()
    {
        lock (_lock)
        {
            return _compressors.Keys.ToArray();
        }
    }

    /// <summary>
    /// Checks if a compressor is registered.
    /// </summary>
    public static bool IsRegistered(string name)
    {
        lock (_lock)
        {
            return _compressors.ContainsKey(name);
        }
    }
}
