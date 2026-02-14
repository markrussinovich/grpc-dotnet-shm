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

namespace Grpc.Net.SharedMemory.Compression;

/// <summary>
/// Compression options for a shared memory stream.
/// Modeled on grpc-go compression configuration.
/// </summary>
public sealed class ShmCompressionOptions
{
    /// <summary>
    /// Gets or sets the compression algorithm to use for outbound messages.
    /// Default is null (no compression).
    /// </summary>
    public string? SendCompress { get; set; }

    /// <summary>
    /// Gets or sets the accepted compression algorithms for inbound messages.
    /// Default includes all registered compressors.
    /// </summary>
    public IList<string>? AcceptedCompressors { get; set; }

    /// <summary>
    /// Gets or sets the minimum message size for compression (in bytes).
    /// Messages smaller than this are not compressed. Default is 0 (always compress).
    /// </summary>
    public int MinSizeForCompression { get; set; }

    /// <summary>
    /// Gets or sets whether compression is enabled. Default is true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the compressor to use for sending.
    /// Takes precedence over SendCompress name if both are set.
    /// </summary>
    public IShmCompressor? SendCompressor { get; set; }

    /// <summary>
    /// Creates default compression options (gzip enabled).
    /// </summary>
    public static ShmCompressionOptions Default => new()
    {
        Enabled = true,
        AcceptedCompressors = new List<string> { "gzip", "deflate", "identity" }
    };

    /// <summary>
    /// Creates compression options with no compression.
    /// </summary>
    public static ShmCompressionOptions None => new()
    {
        Enabled = false
    };

    /// <summary>
    /// Gets the compressor to use for sending messages.
    /// </summary>
    internal IShmCompressor GetSendCompressor()
    {
        if (!Enabled)
        {
            return IdentityCompressor.Instance;
        }

        if (SendCompressor != null)
        {
            return SendCompressor;
        }

        if (!string.IsNullOrEmpty(SendCompress))
        {
            var compressor = ShmCompressorRegistry.Get(SendCompress);
            if (compressor != null)
            {
                return compressor;
            }
        }

        return IdentityCompressor.Instance;
    }

    /// <summary>
    /// Gets the grpc-accept-encoding header value.
    /// </summary>
    internal string GetAcceptEncoding()
    {
        if (!Enabled)
        {
            return "identity";
        }

        if (AcceptedCompressors != null && AcceptedCompressors.Count > 0)
        {
            return string.Join(",", AcceptedCompressors);
        }

        return string.Join(",", ShmCompressorRegistry.GetRegisteredNames());
    }

    /// <summary>
    /// Gets a decompressor for the given encoding.
    /// </summary>
    internal IShmCompressor? GetDecompressor(string? encoding)
    {
        if (string.IsNullOrEmpty(encoding) || encoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
        {
            return IdentityCompressor.Instance;
        }

        // Check if accepted
        if (AcceptedCompressors != null && !AcceptedCompressors.Contains(encoding, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        return ShmCompressorRegistry.Get(encoding);
    }

    /// <summary>
    /// Determines whether a message should be compressed based on size.
    /// </summary>
    internal bool ShouldCompress(int messageSize)
    {
        return Enabled && messageSize >= MinSizeForCompression;
    }
}
