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

using System.Buffers.Binary;
using System.Text;
using Grpc.Core;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Represents the TRAILERS frame payload (version 1).
/// This matches the grpc-go-shmem TrailersV1 format.
/// </summary>
public sealed class TrailersV1
{
    /// <summary>Protocol version (must be 1).</summary>
    public byte Version { get; init; } = 1;

    /// <summary>gRPC status code.</summary>
    public StatusCode GrpcStatusCode { get; init; }

    /// <summary>gRPC status message.</summary>
    public string? GrpcStatusMessage { get; init; }

    /// <summary>Trailing metadata key-value pairs.</summary>
    public IReadOnlyList<MetadataKV> Metadata { get; init; } = Array.Empty<MetadataKV>();

    /// <summary>
    /// Encodes this trailers payload to a byte array.
    /// </summary>
    public byte[] Encode()
    {
        var statusMsgLength = GrpcStatusMessage != null ? Encoding.UTF8.GetByteCount(GrpcStatusMessage) : 0;

        var size = 1 + 4 + 4 + statusMsgLength + 2; // version + statusCode + msgLen + msg + metadataCount

        foreach (var kv in Metadata)
        {
            var keyLength = Encoding.UTF8.GetByteCount(kv.Key);
            size += 2 + keyLength; // keyLen + key
            size += 2; // valueCount
            foreach (var v in kv.Values)
            {
                size += 4 + v.Length; // valueLen + value
            }
        }

        var buffer = new byte[size];
        var offset = 0;

        // Version
        buffer[offset++] = Version;

        // gRPC status code
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), (uint)GrpcStatusCode);
        offset += 4;

        // Status message length and bytes
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), (uint)statusMsgLength);
        offset += 4;
        if (statusMsgLength > 0)
        {
            Encoding.UTF8.GetBytes(GrpcStatusMessage!, buffer.AsSpan(offset, statusMsgLength));
            offset += statusMsgLength;
        }

        // Metadata count
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)Metadata.Count);
        offset += 2;

        // Metadata entries
        foreach (var kv in Metadata)
        {
            var keyLength = Encoding.UTF8.GetByteCount(kv.Key);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)keyLength);
            offset += 2;
            if (keyLength > 0)
            {
                Encoding.UTF8.GetBytes(kv.Key, buffer.AsSpan(offset, keyLength));
                offset += keyLength;
            }

            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)kv.Values.Count);
            offset += 2;

            foreach (var v in kv.Values)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), (uint)v.Length);
                offset += 4;
                v.CopyTo(buffer.AsSpan(offset));
                offset += v.Length;
            }
        }

        return buffer;
    }

    /// <summary>
    /// Decodes a TRAILERS payload from a byte array.
    /// </summary>
    public static TrailersV1 Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1 + 4 + 4)
        {
            throw new InvalidDataException("Trailers payload too short");
        }

        var offset = 0;
        var version = data[offset++];
        if (version != 1)
        {
            throw new InvalidDataException($"Unsupported trailers version: {version}");
        }

        // gRPC status code
        var statusCode = (StatusCode)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
        offset += 4;

        // Status message
        var msgLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
        offset += 4;

        string? statusMsg = null;
        if (msgLen > 0)
        {
            if (data.Length < offset + msgLen)
            {
                throw new InvalidDataException("Trailers missing status message bytes");
            }
            statusMsg = Encoding.UTF8.GetString(data.Slice(offset, msgLen));
        }
        offset += msgLen;

        // Metadata count
        if (data.Length < offset + 2)
        {
            throw new InvalidDataException("Trailers missing metadata count");
        }
        var metadataCount = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
        offset += 2;

        var metadata = new List<MetadataKV>(metadataCount);
        for (var i = 0; i < metadataCount; i++)
        {
            // Key
            if (data.Length < offset + 2)
            {
                throw new InvalidDataException("Trailers missing metadata key length");
            }
            var keyLen = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
            offset += 2;

            if (data.Length < offset + keyLen)
            {
                throw new InvalidDataException("Trailers missing metadata key bytes");
            }
            var key = Encoding.UTF8.GetString(data.Slice(offset, keyLen));
            offset += keyLen;

            // Value count
            if (data.Length < offset + 2)
            {
                throw new InvalidDataException("Trailers missing metadata value count");
            }
            var valueCount = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
            offset += 2;

            var values = new byte[valueCount][];
            for (var j = 0; j < valueCount; j++)
            {
                if (data.Length < offset + 4)
                {
                    throw new InvalidDataException("Trailers missing metadata value length");
                }
                var valueLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
                offset += 4;

                if (data.Length < offset + valueLen)
                {
                    throw new InvalidDataException("Trailers missing metadata value bytes");
                }
                values[j] = data.Slice(offset, valueLen).ToArray();
                offset += valueLen;
            }

            metadata.Add(new MetadataKV { Key = key, Values = values });
        }

        return new TrailersV1
        {
            Version = version,
            GrpcStatusCode = statusCode,
            GrpcStatusMessage = statusMsg,
            Metadata = metadata
        };
    }
}
