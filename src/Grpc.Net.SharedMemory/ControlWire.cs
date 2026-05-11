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
    /// Format (v1 baseline, 18 bytes):
    ///     version(1) + ringA(8) + ringB(8) + flags(1)
    /// Optional v1 extension (advertised wire formats, backward compatible):
    ///     wireFormatCount(1) + wireFormats(N)
    /// flags bit 0: singleStreamMode requested
    /// </summary>
    /// <param name="ringA">Client preferred capacity for ring A.</param>
    /// <param name="ringB">Client preferred capacity for ring B.</param>
    /// <param name="singleStreamMode">Whether single-stream optimisations are requested.</param>
    /// <param name="supportedWireFormats">
    /// Optional ordered list of wire formats the client supports (preference order).
    /// If <c>null</c> or empty, the request is bit-identical to a legacy v1 CONNECT.
    /// </param>
    public static byte[] EncodeConnectRequest(
        ulong ringA = 0,
        ulong ringB = 0,
        bool singleStreamMode = false,
        IReadOnlyList<Wire.WireFormat>? supportedWireFormats = null)
    {
        var extensionLen = 0;
        if (supportedWireFormats is { Count: > 0 })
        {
            extensionLen = 1 + supportedWireFormats.Count;
        }
        var buffer = new byte[1 + 8 + 8 + 1 + extensionLen];
        buffer[0] = ShmConstants.ControlWireVersion;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(1, 8), ringA);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(9, 8), ringB);
        buffer[17] = (byte)(singleStreamMode ? 1 : 0);
        if (extensionLen > 0)
        {
            buffer[18] = (byte)supportedWireFormats!.Count;
            for (var i = 0; i < supportedWireFormats.Count; i++)
            {
                buffer[19 + i] = (byte)supportedWireFormats[i];
            }
        }
        return buffer;
    }

    /// <summary>
    /// Decodes a CONNECT request. Returns the legacy fields plus the optional
    /// wire-format advertisement (empty array if the peer didn't advertise).
    /// </summary>
    public static (ulong ringA, ulong ringB, bool singleStreamMode, Wire.WireFormat[] supportedWireFormats) DecodeConnectRequest(ReadOnlySpan<byte> data)
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
            return (0, 0, false, Array.Empty<Wire.WireFormat>());
        }

        if (data.Length < 1 + 8 + 8)
        {
            throw new InvalidDataException("Connect request invalid length");
        }

        var ringA = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(1, 8));
        var ringB = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(9, 8));
        // flags byte is optional for backward compatibility (old clients send 17 bytes)
        var singleStream = data.Length > 17 && (data[17] & 1) != 0;

        // Optional wire-format extension at offset 18+.
        Wire.WireFormat[] formats = Array.Empty<Wire.WireFormat>();
        if (data.Length > 18)
        {
            int count = data[18];
            // Strict validation: a peer that advertises N formats but doesn't
            // include their bytes is malformed and the connection should fail
            // rather than silently being treated as "no advertisement"
            // (which would default to Custom16). Be loud about protocol errors.
            if (data.Length < 19 + count)
            {
                throw new InvalidDataException(
                    $"Connect request truncated: declared {count} wire formats but only {data.Length - 19} byte(s) of advertisement available");
            }
            formats = new Wire.WireFormat[count];
            for (var i = 0; i < count; i++)
            {
                var raw = data[19 + i];
                // Reject unknown enum values at the protocol boundary so we
                // never propagate garbage into ring.Wire.
                if (raw != (byte)Wire.WireFormat.Custom16 && raw != (byte)Wire.WireFormat.Http2)
                {
                    throw new InvalidDataException(
                        $"Connect request advertises unknown wire format 0x{raw:X2} at index {i}");
                }
                formats[i] = (Wire.WireFormat)raw;
            }
        }

        return (ringA, ringB, singleStream, formats);
    }

    /// <summary>
    /// Encodes an ACCEPT response with the data segment name.
    /// Format (v1 baseline): version(1) + nameLen(4) + name(n)
    /// Optional v1 extension (backward compatible): selectedWireFormat(1)
    /// </summary>
    /// <param name="segmentName">The data segment name advertised to the client.</param>
    /// <param name="selectedWireFormat">
    /// Selected wire format. <c>null</c> = legacy response (no extension), implies Custom16.
    /// </param>
    public static byte[] EncodeConnectResponse(string segmentName, Wire.WireFormat? selectedWireFormat = null)
    {
        var nameBytes = Encoding.UTF8.GetBytes(segmentName);
        var extensionLen = selectedWireFormat.HasValue ? 1 : 0;
        var buffer = new byte[1 + 4 + nameBytes.Length + extensionLen];
        buffer[0] = ShmConstants.ControlWireVersion;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(1, 4), (uint)nameBytes.Length);
        nameBytes.CopyTo(buffer.AsSpan(5));
        if (selectedWireFormat.HasValue)
        {
            buffer[5 + nameBytes.Length] = (byte)selectedWireFormat.Value;
        }
        return buffer;
    }

    /// <summary>
    /// Decodes an ACCEPT response. Returns the segment name and the optional
    /// selected wire format (Custom16 if absent for backward compat).
    /// </summary>
    public static (string SegmentName, Wire.WireFormat WireFormat) DecodeConnectResponse(ReadOnlySpan<byte> data)
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
        var wf = Wire.WireFormat.Custom16;
        if (data.Length > 5 + nameLen)
        {
            var raw = data[5 + nameLen];
            // Reject unknown enum values; treating an unrecognised byte as
            // "default to Custom16" would silently downgrade clients that
            // think they negotiated H2 with a misbehaving peer.
            if (raw != (byte)Wire.WireFormat.Custom16 && raw != (byte)Wire.WireFormat.Http2)
            {
                throw new InvalidDataException(
                    $"Connect response advertises unknown wire format 0x{raw:X2}");
            }
            wf = (Wire.WireFormat)raw;
        }
        return (name, wf);
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
