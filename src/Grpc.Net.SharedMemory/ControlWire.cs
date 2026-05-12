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
    /// Wire-format byte for HTTP/2 in the CONNECT/ACCEPT extension.
    /// The legacy <c>0</c> byte (Custom16) is rejected by current peers.
    /// </summary>
    private const byte ProtocolWireHttp2 = 1;

    /// <summary>
    /// Encodes a CONNECT request.
    /// Format (20 bytes):
    ///     version(1) + ringA(8) + ringB(8) + flags(1)
    ///   + wireFormatCount(1)=1 + wireFormat(1)=Http2
    /// flags bit 0: singleStreamMode requested.
    /// </summary>
    /// <remarks>
    /// Always advertises HTTP/2 (and only HTTP/2). Pre-H2 peers that
    /// expected the optional extension to be absent will see a 20-byte
    /// payload instead of 18 and reject (or default-to-Custom16 and then
    /// fail at the codec). The server side validates the extension and
    /// rejects connections that do not advertise H2.
    /// </remarks>
    /// <param name="ringA">Client preferred capacity for ring A.</param>
    /// <param name="ringB">Client preferred capacity for ring B.</param>
    /// <param name="singleStreamMode">Whether single-stream optimisations are requested.</param>
    public static byte[] EncodeConnectRequest(
        ulong ringA = 0,
        ulong ringB = 0,
        bool singleStreamMode = false)
    {
        var buffer = new byte[1 + 8 + 8 + 1 + 1 + 1];
        buffer[0] = ShmConstants.ControlWireVersion;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(1, 8), ringA);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(9, 8), ringB);
        buffer[17] = (byte)(singleStreamMode ? 1 : 0);
        buffer[18] = 1;                  // wireFormatCount
        buffer[19] = ProtocolWireHttp2;  // only Http2
        return buffer;
    }

    /// <summary>
    /// Decodes a CONNECT request. Validates that the peer advertises HTTP/2
    /// and rejects everything else (legacy Custom16-only peers or unknown
    /// formats are not accepted).
    /// </summary>
    public static (ulong ringA, ulong ringB, bool singleStreamMode) DecodeConnectRequest(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1)
        {
            throw new InvalidDataException("Connect request too short");
        }

        if (data[0] != ShmConstants.ControlWireVersion)
        {
            throw new InvalidDataException($"Unsupported connect request version {data[0]}");
        }

        if (data.Length < 1 + 8 + 8)
        {
            throw new InvalidDataException("Connect request invalid length");
        }

        var ringA = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(1, 8));
        var ringB = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(9, 8));
        var singleStream = data.Length > 17 && (data[17] & 1) != 0;

        // Wire-format extension is mandatory: peer must advertise Http2.
        // A legacy peer that omits the extension or only advertises
        // Custom16 is rejected at the protocol boundary.
        if (data.Length <= 18)
        {
            throw new InvalidDataException(
                "Connect request missing wire-format advertisement; peer must support HTTP/2");
        }
        int count = data[18];
        if (count == 0)
        {
            throw new InvalidDataException(
                "Connect request advertises zero wire formats; peer must support HTTP/2");
        }
        if (data.Length < 19 + count)
        {
            throw new InvalidDataException(
                $"Connect request truncated: declared {count} wire formats but only {data.Length - 19} byte(s) of advertisement available");
        }
        var sawHttp2 = false;
        for (var i = 0; i < count; i++)
        {
            if (data[19 + i] == ProtocolWireHttp2)
            {
                sawHttp2 = true;
                break;
            }
        }
        if (!sawHttp2)
        {
            throw new InvalidDataException(
                "Connect request does not advertise HTTP/2; legacy Custom16-only peers are not supported");
        }

        return (ringA, ringB, singleStream);
    }

    /// <summary>
    /// Encodes an ACCEPT response with the data segment name. Always emits
    /// the HTTP/2 wire-format byte at the end of the payload.
    /// Format: version(1) + nameLen(4) + name(n) + wireFormat(1)=Http2.
    /// </summary>
    /// <param name="segmentName">The data segment name advertised to the client.</param>
    public static byte[] EncodeConnectResponse(string segmentName)
    {
        var nameBytes = Encoding.UTF8.GetBytes(segmentName);
        var buffer = new byte[1 + 4 + nameBytes.Length + 1];
        buffer[0] = ShmConstants.ControlWireVersion;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(1, 4), (uint)nameBytes.Length);
        nameBytes.CopyTo(buffer.AsSpan(5));
        buffer[5 + nameBytes.Length] = ProtocolWireHttp2;
        return buffer;
    }

    /// <summary>
    /// Decodes an ACCEPT response. Validates that the server selected HTTP/2;
    /// rejects legacy responses (no extension byte) and Custom16 selection.
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

        var name = Encoding.UTF8.GetString(data.Slice(5, nameLen));

        // Wire-format byte is mandatory; legacy responses (no extension)
        // are rejected so we never silently downgrade to Custom16.
        if (data.Length <= 5 + nameLen)
        {
            throw new InvalidDataException(
                "Connect response missing wire-format byte; peer must select HTTP/2");
        }
        var raw = data[5 + nameLen];
        if (raw != ProtocolWireHttp2)
        {
            throw new InvalidDataException(
                $"Connect response selects wire format 0x{raw:X2}, expected HTTP/2 (0x{ProtocolWireHttp2:X2})");
        }

        return name;
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
