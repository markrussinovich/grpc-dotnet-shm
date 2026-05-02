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

/// <summary>
/// HTTP/2 framing codec for SHM rings (RFC 7540 §4 + §6).
/// </summary>
/// <remarks>
/// <para>
/// This codec maps the internal <see cref="FrameType"/> model to/from HTTP/2
/// frame types. Only the subset of HTTP/2 needed by gRPC over SHM is
/// implemented:
/// </para>
/// <list type="bullet">
///   <item><description>DATA, HEADERS, RST_STREAM, SETTINGS, PING, GOAWAY, WINDOW_UPDATE</description></item>
///   <item><description>HPACK header compression with a static table only (no dynamic table; <c>SETTINGS_HEADER_TABLE_SIZE = 0</c>)</description></item>
///   <item><description>The peer is expected to negotiate <c>SETTINGS_MAX_FRAME_SIZE = 16 MiB</c> so CONTINUATION is never required for gRPC headers</description></item>
/// </list>
/// <para>
/// The control-plane CONNECT/ACCEPT handshake (a separate SHM segment with
/// <see cref="ControlWire"/>) replaces the HTTP/2 connection preface. Stream
/// flow control is disabled by advertising <c>SETTINGS_INITIAL_WINDOW_SIZE = 2^31-1</c>;
/// the SHM ring's own back-pressure is the sole flow-control mechanism.
/// </para>
/// </remarks>
internal static partial class Http2Codec
{
    // Concrete implementations live in Http2Codec.Read.cs and Http2Codec.Write.cs.
}
