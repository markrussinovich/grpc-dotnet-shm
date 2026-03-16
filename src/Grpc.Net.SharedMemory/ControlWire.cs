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
/// Control wire protocol encoding/decoding for grpc-go-shmem compatibility.
/// Used for connection establishment on the control segment (_ctl).
/// </summary>
public static class ControlWire
{
    /// <summary>
    /// Encodes a CONNECT request.
    /// Format: version(1) + ringA(8) + ringB(8) = 17 bytes
    /// </summary>
    public static byte[] EncodeConnectRequest(ulong ringA = 0, ulong ringB = 0)
    {
        var buffer = new byte[1 + 8 + 8];
        buffer[0] = ShmConstants.ControlWireVersion;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(1, 8), ringA);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(9, 8), ringB);
        return buffer;
    }

    /// <summary>
    /// Decodes a CONNECT request.
    /// </summary>
    public static (ulong ringA, ulong ringB) DecodeConnectRequest(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1)
        {
            throw new InvalidDataException("Connect request too short");
        }

        if (data[0] != ShmConstants.ControlWireVersion)
        {
            throw new InvalidDataException($"Unsupported connect request version {data[0]}");
        }

        // Allow minimal v1 payloads (just version byte)
        if (data.Length == 1)
        {
            return (0, 0);
        }

        if (data.Length != 1 + 8 + 8)
        {
            throw new InvalidDataException("Connect request invalid length");
        }

        var ringA = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(1, 8));
        var ringB = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(9, 8));
        return (ringA, ringB);
    }

    /// <summary>
    /// Encodes an ACCEPT response with the data segment name.
    /// Format: version(1) + nameLen(4) + name(n)
    /// </summary>
    public static byte[] EncodeConnectResponse(string segmentName)
    {
        var nameBytes = Encoding.UTF8.GetBytes(segmentName);
        var buffer = new byte[1 + 4 + nameBytes.Length];
        buffer[0] = ShmConstants.ControlWireVersion;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(1, 4), (uint)nameBytes.Length);
        nameBytes.CopyTo(buffer.AsSpan(5));
        return buffer;
    }

    /// <summary>
    /// Decodes an ACCEPT response.
    /// </summary>
    public static string DecodeConnectResponse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1 + 4)
        {
            throw new InvalidDataException("Connect response too short");
        }

        if (data[0] != ShmConstants.ControlWireVersion)
        {
            throw new InvalidDataException($"Unsupported connect response version {data[0]}");
        }

        var nameLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(1, 4));
        if (nameLen < 0 || data.Length < 5 + nameLen)
        {
            throw new InvalidDataException("Connect response name missing");
        }

        return Encoding.UTF8.GetString(data.Slice(5, nameLen));
    }

    /// <summary>
    /// Encodes a REJECT response with an error message.
    /// Format: version(1) + msgLen(4) + msg(n)
    /// </summary>
    public static byte[] EncodeConnectReject(string message)
    {
        var msgBytes = Encoding.UTF8.GetBytes(message);
        var buffer = new byte[1 + 4 + msgBytes.Length];
        buffer[0] = ShmConstants.ControlWireVersion;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(1, 4), (uint)msgBytes.Length);
        msgBytes.CopyTo(buffer.AsSpan(5));
        return buffer;
    }

    /// <summary>
    /// Decodes a REJECT response.
    /// </summary>
    public static string DecodeConnectReject(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1 + 4)
        {
            throw new InvalidDataException("Connect reject too short");
        }

        if (data[0] != ShmConstants.ControlWireVersion)
        {
            throw new InvalidDataException($"Unsupported connect reject version {data[0]}");
        }

        var msgLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(1, 4));
        if (msgLen < 0 || data.Length < 5 + msgLen)
        {
            throw new InvalidDataException("Connect reject message missing");
        }

        return Encoding.UTF8.GetString(data.Slice(5, msgLen));
    }

    /// <summary>
    /// Negotiates the ring buffer capacity between client preference and server maximum.
    /// Returns <c>Min(clientPreferred, serverMax)</c>, clamped to <see cref="ShmConstants.MinRingCapacity"/>.
    /// If the client sends 0 (no preference) or a non-power-of-2, the server default is used.
    /// </summary>
    /// <param name="clientPreferred">Client's preferred ring capacity from the CONNECT request.</param>
    /// <param name="serverMax">Server's configured maximum ring capacity.</param>
    /// <returns>The negotiated ring capacity (always a power of 2, ≥ MinRingCapacity).</returns>
    public static ulong NegotiateRingCapacity(ulong clientPreferred, ulong serverMax)
    {
        // Client 0 = no preference → use server default
        if (clientPreferred == 0)
        {
            return serverMax;
        }

        // Client must request a power of 2
        if ((clientPreferred & (clientPreferred - 1)) != 0)
        {
            return serverMax;
        }

        // Negotiate: smaller of client preference and server max
        var negotiated = Math.Min(clientPreferred, serverMax);

        // Clamp to minimum
        if (negotiated < ShmConstants.MinRingCapacity)
        {
            negotiated = ShmConstants.MinRingCapacity;
        }

        return negotiated;
    }
}
