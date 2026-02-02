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

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Represents a key-value pair for metadata in shared memory transport.
/// </summary>
public readonly struct MetadataKV
{
    /// <summary>The metadata key.</summary>
    public string Key { get; init; }

    /// <summary>The metadata values (can be multiple for the same key).</summary>
    public IReadOnlyList<byte[]> Values { get; init; }

    public MetadataKV(string key, params byte[][] values)
    {
        Key = key;
        Values = values;
    }

    public MetadataKV(string key, params string[] values)
    {
        Key = key;
        Values = values.Select(v => Encoding.UTF8.GetBytes(v)).ToArray();
    }
}

/// <summary>
/// Represents the HEADERS frame payload (version 1).
/// This matches the grpc-go-shmem HeadersV1 format.
/// </summary>
public sealed class HeadersV1
{
    /// <summary>Protocol version (must be 1).</summary>
    public byte Version { get; init; } = 1;

    /// <summary>Header type: 0=client-initial, 1=server-initial.</summary>
    public byte HeaderType { get; init; }

    /// <summary>RPC method path (only present for client-initial headers).</summary>
    public string? Method { get; init; }

    /// <summary>Authority/host.</summary>
    public string? Authority { get; init; }

    /// <summary>Deadline as Unix nanoseconds (0 if none).</summary>
    public ulong DeadlineUnixNano { get; init; }

    /// <summary>Metadata key-value pairs.</summary>
    public IReadOnlyList<MetadataKV> Metadata { get; init; } = Array.Empty<MetadataKV>();

    /// <summary>
    /// Encodes this headers payload to a byte array.
    /// </summary>
    public byte[] Encode()
    {
        // Calculate size
        var methodBytes = HeaderType == 0 && Method != null ? Encoding.UTF8.GetBytes(Method) : Array.Empty<byte>();
        var authorityBytes = Authority != null ? Encoding.UTF8.GetBytes(Authority) : Array.Empty<byte>();

        var size = 1 + 1 + 4; // version + hdrType + methodLen
        size += methodBytes.Length;
        size += 4 + authorityBytes.Length; // authorityLen + authority
        size += 8; // deadline
        size += 2; // metadata count

        foreach (var kv in Metadata)
        {
            var keyBytes = Encoding.UTF8.GetBytes(kv.Key);
            size += 2 + keyBytes.Length; // keyLen + key
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

        // HeaderType
        buffer[offset++] = HeaderType;

        // Method length and bytes (only for client-initial)
        if (HeaderType == 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), (uint)methodBytes.Length);
            offset += 4;
            methodBytes.CopyTo(buffer.AsSpan(offset));
            offset += methodBytes.Length;
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), 0);
            offset += 4;
        }

        // Authority
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), (uint)authorityBytes.Length);
        offset += 4;
        authorityBytes.CopyTo(buffer.AsSpan(offset));
        offset += authorityBytes.Length;

        // Deadline
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset, 8), DeadlineUnixNano);
        offset += 8;

        // Metadata count
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)Metadata.Count);
        offset += 2;

        // Metadata entries
        foreach (var kv in Metadata)
        {
            var keyBytes = Encoding.UTF8.GetBytes(kv.Key);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)keyBytes.Length);
            offset += 2;
            keyBytes.CopyTo(buffer.AsSpan(offset));
            offset += keyBytes.Length;

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
    /// Decodes a HEADERS payload from a byte array.
    /// </summary>
    public static HeadersV1 Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            throw new InvalidDataException("Headers payload too short");
        }

        var offset = 0;
        var version = data[offset++];
        if (version != 1)
        {
            throw new InvalidDataException($"Unsupported headers version: {version}");
        }

        var headerType = data[offset++];

        // Method
        if (data.Length < offset + 4)
        {
            throw new InvalidDataException("Headers missing method length");
        }
        var methodLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
        offset += 4;

        string? method = null;
        if (headerType == 0 && methodLen > 0)
        {
            if (data.Length < offset + methodLen)
            {
                throw new InvalidDataException("Headers missing method bytes");
            }
            method = Encoding.UTF8.GetString(data.Slice(offset, methodLen));
        }
        offset += methodLen;

        // Authority
        if (data.Length < offset + 4)
        {
            throw new InvalidDataException("Headers missing authority length");
        }
        var authorityLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
        offset += 4;

        string? authority = null;
        if (authorityLen > 0)
        {
            if (data.Length < offset + authorityLen)
            {
                throw new InvalidDataException("Headers missing authority bytes");
            }
            authority = Encoding.UTF8.GetString(data.Slice(offset, authorityLen));
        }
        offset += authorityLen;

        // Deadline
        if (data.Length < offset + 8)
        {
            throw new InvalidDataException("Headers missing deadline");
        }
        var deadline = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
        offset += 8;

        // Metadata count
        if (data.Length < offset + 2)
        {
            throw new InvalidDataException("Headers missing metadata count");
        }
        var metadataCount = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
        offset += 2;

        var metadata = new List<MetadataKV>(metadataCount);
        for (var i = 0; i < metadataCount; i++)
        {
            // Key
            if (data.Length < offset + 2)
            {
                throw new InvalidDataException("Headers missing metadata key length");
            }
            var keyLen = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
            offset += 2;

            if (data.Length < offset + keyLen)
            {
                throw new InvalidDataException("Headers missing metadata key bytes");
            }
            var key = Encoding.UTF8.GetString(data.Slice(offset, keyLen));
            offset += keyLen;

            // Value count
            if (data.Length < offset + 2)
            {
                throw new InvalidDataException("Headers missing metadata value count");
            }
            var valueCount = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
            offset += 2;

            var values = new byte[valueCount][];
            for (var j = 0; j < valueCount; j++)
            {
                if (data.Length < offset + 4)
                {
                    throw new InvalidDataException("Headers missing metadata value length");
                }
                var valueLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
                offset += 4;

                if (data.Length < offset + valueLen)
                {
                    throw new InvalidDataException("Headers missing metadata value bytes");
                }
                values[j] = data.Slice(offset, valueLen).ToArray();
                offset += valueLen;
            }

            metadata.Add(new MetadataKV { Key = key, Values = values });
        }

        return new HeadersV1
        {
            Version = version,
            HeaderType = headerType,
            Method = method,
            Authority = authority,
            DeadlineUnixNano = deadline,
            Metadata = metadata
        };
    }
}
