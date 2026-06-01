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
using System.Security.Cryptography;
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
    /// Generates a fresh 8-byte per-CONNECT correlation nonce using a
    /// cryptographic RNG. Required to be unpredictable because the
    /// control segment is shared memory; any local process able to read
    /// it could otherwise forge an ACCEPT for a guessable counter
    /// nonce. Mirrors grpc-go-shmem's <c>newConnectNonce()</c> (uses
    /// <c>crypto/rand</c>).
    /// </summary>
    public static ulong NewConnectNonce()
    {
        Span<byte> buf = stackalloc byte[8];
        RandomNumberGenerator.Fill(buf);
        return BinaryPrimitives.ReadUInt64LittleEndian(buf);
    }

    /// <summary>
    /// Encodes a CONNECT request.
    /// Format (28 bytes):
    ///     version(1) + ringA(8) + ringB(8) + flags(1)
    ///   + wireFormatCount(1)=1 + wireFormat(1)=Http2
    ///   + nonce(8 LE)
    /// flags bit 0: singleStreamMode requested.
    /// </summary>
    /// <remarks>
    /// Always advertises HTTP/2 (and only HTTP/2). Pre-H2 peers that
    /// expected the optional extension to be absent will see a 28-byte
    /// payload instead of 18 and reject (or default-to-Custom16 and then
    /// fail at the codec). The server side validates the extension and
    /// rejects connections that do not advertise H2.
    /// <para>
    /// The trailing 8-byte nonce is echoed by the server in the matching
    /// ACCEPT / REJECT so the dialer can confirm the response answers
    /// its own in-flight CONNECT (closes the stale-response misbinding
    /// race documented in gRFC A-shared-memory-transport — Stale Response
    /// Correlation).
    /// </para>
    /// </remarks>
    /// <param name="ringA">Client preferred capacity for ring A.</param>
    /// <param name="ringB">Client preferred capacity for ring B.</param>
    /// <param name="singleStreamMode">Whether single-stream optimisations are requested.</param>
    /// <param name="nonce">Per-CONNECT correlation nonce (from <see cref="NewConnectNonce"/>).</param>
    public static byte[] EncodeConnectRequest(
        ulong ringA,
        ulong ringB,
        bool singleStreamMode,
        ulong nonce)
    {
        var buffer = new byte[1 + 8 + 8 + 1 + 1 + 1 + 8];
        buffer[0] = ShmConstants.ControlWireVersion;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(1, 8), ringA);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(9, 8), ringB);
        buffer[17] = (byte)(singleStreamMode ? 1 : 0);
        buffer[18] = 1;                  // wireFormatCount
        buffer[19] = ProtocolWireHttp2;  // only Http2
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(20, 8), nonce);
        return buffer;
    }

    /// <summary>
    /// Decodes a CONNECT request. Validates that the peer advertises HTTP/2
    /// and rejects everything else (legacy Custom16-only peers or unknown
    /// formats are not accepted). Returns the 8-byte correlation nonce
    /// that the server MUST echo verbatim in its ACCEPT/REJECT.
    /// </summary>
    public static (ulong ringA, ulong ringB, bool singleStreamMode, ulong nonce) DecodeConnectRequest(ReadOnlySpan<byte> data)
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

        // Per-CONNECT correlation nonce: mandatory 8 bytes after the
        // wire-format advertisement. Matches grpc-go-shmem's
        // decodeConnectRequest layout (see control_wire.go).
        int nonceOff = 19 + count;
        if (data.Length < nonceOff + 8)
        {
            throw new InvalidDataException("Connect request missing correlation nonce");
        }
        ulong nonce = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(nonceOff, 8));

        return (ringA, ringB, singleStream, nonce);
    }

    /// <summary>
    /// Encodes an ACCEPT response with the data segment name. Always emits
    /// the HTTP/2 wire-format byte, a reserved flags byte (zero), and the
    /// 8-byte correlation nonce echoed back to the dialer.
    /// Format: version(1) + nameLen(4) + name(n) + wireFormat(1)=Http2
    ///       + flags(1) + nonce(8 LE).
    /// </summary>
    /// <param name="segmentName">The data segment name advertised to the client.</param>
    /// <param name="nonce">The correlation nonce from the matching CONNECT, echoed verbatim.</param>
    public static byte[] EncodeConnectResponse(string segmentName, ulong nonce)
    {
        var nameBytes = Encoding.UTF8.GetBytes(segmentName);
        var buffer = new byte[1 + 4 + nameBytes.Length + 1 + 1 + 8];
        buffer[0] = ShmConstants.ControlWireVersion;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(1, 4), (uint)nameBytes.Length);
        nameBytes.CopyTo(buffer.AsSpan(5));
        buffer[5 + nameBytes.Length] = ProtocolWireHttp2;
        buffer[5 + nameBytes.Length + 1] = 0; // reserved flags
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(5 + nameBytes.Length + 2, 8), nonce);
        return buffer;
    }

    /// <summary>
    /// Decodes an ACCEPT response. Validates that the server selected HTTP/2,
    /// includes the reserved flags byte, and the 8-byte correlation nonce.
    /// Returns the segment name and the nonce so the dialer can correlate
    /// this ACCEPT to its own in-flight CONNECT.
    /// </summary>
    public static (string segmentName, ulong nonce) DecodeConnectResponse(ReadOnlySpan<byte> data)
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

        // Reserved flags byte (currently always zero).
        if (data.Length <= 5 + nameLen + 1)
        {
            throw new InvalidDataException(
                "Connect response missing flags byte; server MUST include the reserved flags byte");
        }

        // Per-CONNECT correlation nonce: mandatory 8 bytes after the
        // flags byte. Matches grpc-go-shmem's decodeConnectResponse.
        int nonceOff = 5 + nameLen + 2;
        if (data.Length < nonceOff + 8)
        {
            throw new InvalidDataException("Connect response missing correlation nonce");
        }
        ulong nonce = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(nonceOff, 8));

        return (name, nonce);
    }

    /// <summary>
    /// Encodes a REJECT response with an error message and the 8-byte
    /// correlation nonce echoed back to the dialer (or zero when the
    /// server could not decode the CONNECT and therefore has no nonce
    /// to echo).
    /// Format: version(1) + msgLen(4) + msg(n) + nonce(8 LE).
    /// </summary>
    public static byte[] EncodeConnectReject(string message, ulong nonce)
    {
        var msgBytes = Encoding.UTF8.GetBytes(message);
        var buffer = new byte[1 + 4 + msgBytes.Length + 8];
        buffer[0] = ShmConstants.ControlWireVersion;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(1, 4), (uint)msgBytes.Length);
        msgBytes.CopyTo(buffer.AsSpan(5));
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(5 + msgBytes.Length, 8), nonce);
        return buffer;
    }

    /// <summary>
    /// Decodes a REJECT response. Returns the message and the 8-byte
    /// correlation nonce. A zero nonce means the server could not decode
    /// the CONNECT (so could not extract our nonce) — the dialer treats
    /// these as stale per the bounded-skip protocol.
    /// </summary>
    public static (string message, ulong nonce) DecodeConnectReject(ReadOnlySpan<byte> data)
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

        // Per-CONNECT correlation nonce: mandatory 8 bytes after the message.
        int nonceOff = 5 + msgLen;
        if (data.Length < nonceOff + 8)
        {
            throw new InvalidDataException("Connect reject missing correlation nonce");
        }
        ulong nonce = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(nonceOff, 8));

        return (Encoding.UTF8.GetString(data.Slice(5, msgLen)), nonce);
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
