#region Copyright notice and license

// Copyright 2026 The gRPC Authors
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

namespace Grpc.Net.SharedMemory.Wire;

internal static partial class Http2Codec
{
    /// <summary>
    /// Maximum HTTP/2 frame payload accepted by this implementation.
    /// Set equal to the H2 spec maximum (2^24 - 1) so a peer that advertises
    /// the maximum permissible <c>SETTINGS_MAX_FRAME_SIZE</c> will not see
    /// our reads reject anything legal.
    /// </summary>
    internal const int MaxH2FramePayloadSize = (1 << 24) - 1;

    /// <summary>
    /// Codec-level sanity cap on a single LPM body length. Protects against
    /// a malicious or corrupted peer declaring a giant <c>Message-Length</c>
    /// in the LPM header and forcing us to <see cref="System.Buffers.ArrayPool{T}.Rent"/>
    /// gigabytes from a header-only frame. The application-level receive
    /// policy is enforced separately by the gRPC parser using
    /// <c>GrpcChannelOptions.MaxReceiveMessageSize</c>; this codec cap exists
    /// only to keep a peer from triggering huge allocations BEFORE the body
    /// has even arrived. Sized at 1 GiB — comfortably above the largest
    /// realistic per-message payload (256 MiB matches our bench upper bound)
    /// and well below the 2 GiB <see cref="int.MaxValue"/> ceiling that
    /// would let a malicious peer near-OOM the receiver.
    /// </summary>
    internal const int MaxLpmBodyLength = 1024 * 1024 * 1024;

    /// <summary>
    /// Cumulative cap on a HEADERS + CONTINUATION sequence payload.
    /// </summary>
    /// <remarks>
    /// RFC 7540 §6.10 allows a HEADERS or PUSH_PROMISE to span multiple
    /// CONTINUATION frames; the spec does not impose a per-block size
    /// limit (peers are expected to advertise <c>SETTINGS_MAX_HEADER_LIST_SIZE</c>
    /// to bound this). Without a cap a malicious peer could stream
    /// gigabytes of HEADERS payload, exhausting memory before any
    /// upper-layer rate-limiting kicks in.
    /// <para>
    /// 8 MiB is generous (grpc-dotnet's default
    /// <c>SETTINGS_MAX_HEADER_LIST_SIZE</c> is 16 KiB; nginx caps at
    /// 8 KiB; envoy at 60 KiB) but matches the order-of-magnitude of
    /// the existing <c>MaxLpmBodyLength</c> 1 GiB-class limits without
    /// being a viable attack vector. Real gRPC traffic stays well
    /// under 4 KiB of headers.
    /// </para>
    /// </remarks>
    internal const int MaxHeaderListSize = 8 * 1024 * 1024;

    /// <summary>
    /// Reads a single logical frame off the ring in HTTP/2 wire format and
    /// translates it to the internal <see cref="FrameHeader"/> /
    /// <see cref="FramePayload"/> model used by the upper layers.
    /// </summary>
    public static (FrameHeader Header, FramePayload Payload) ReadFramePayload(
        ShmRing ring,
        CancellationToken cancellationToken,
        bool zeroCopy)
    {
        // Implementation lives in Http2Codec.Read.cs.
        return ReadFramePayloadInternal(ring, cancellationToken, zeroCopy);
    }

    /// <summary>
    /// Writes a single logical frame to the ring in HTTP/2 wire format,
    /// translating from the internal <see cref="FrameHeader"/> model.
    /// </summary>
    public static void WriteFrame(
        ShmRing ring,
        FrameHeader header,
        ReadOnlySpan<byte> payload1,
        ReadOnlySpan<byte> payload2,
        CancellationToken cancellationToken)
    {
        // Implementation lives in Http2Codec.Write.cs.
        WriteFrameInternal(ring, header, payload1, payload2, cancellationToken);
    }
}
