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

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Frame types for the shared memory transport protocol.
/// These match the grpc-go-shmem frame types for interoperability.
/// </summary>
public enum FrameType : byte
{
    /// <summary>Padding frame for alignment.</summary>
    Pad = 0x00,

    /// <summary>Request/response headers frame.</summary>
    Headers = 0x01,

    /// <summary>Data payload message frame.</summary>
    Message = 0x02,

    /// <summary>Trailing metadata and status frame.</summary>
    Trailers = 0x03,

    /// <summary>Stream cancellation frame.</summary>
    Cancel = 0x04,

    /// <summary>Connection shutdown frame.</summary>
    GoAway = 0x05,

    /// <summary>Health check request frame.</summary>
    Ping = 0x06,

    /// <summary>Health check response frame.</summary>
    Pong = 0x07,

    /// <summary>Half-close stream frame.</summary>
    HalfClose = 0x08,

    /// <summary>Flow control window update frame.</summary>
    WindowUpdate = 0x09,

    // Control-plane frame types (used only on the control segment)
    // These match grpc-go-shmem for interoperability

    /// <summary>Connection request frame (control segment only).</summary>
    Connect = 0x10,

    /// <summary>Connection accepted frame (control segment only).</summary>
    Accept = 0x11,

    /// <summary>Connection rejected frame (control segment only).</summary>
    Reject = 0x12,

    // Security handshake frame types

    /// <summary>Handshake initiation frame.</summary>
    HandshakeInit = 0x20,

    /// <summary>Handshake response frame.</summary>
    HandshakeResp = 0x21,

    /// <summary>Handshake failure frame.</summary>
    HandshakeFail = 0x22,

    /// <summary>Handshake acknowledgement frame.</summary>
    HandshakeAck = 0x23
}

/// <summary>
/// Flags for HEADERS frames.
/// </summary>
public static class HeadersFlags
{
    /// <summary>Indicates initial headers (start of stream).</summary>
    public const byte Initial = 0x01;
}

/// <summary>
/// Flags for MESSAGE frames.
/// </summary>
public static class MessageFlags
{
    /// <summary>Indicates more data follows (chunked message).</summary>
    public const byte More = 0x01;
}

/// <summary>
/// Flags for TRAILERS frames.
/// </summary>
public static class TrailersFlags
{
    /// <summary>Indicates end of stream.</summary>
    public const byte EndStream = 0x01;
}

/// <summary>
/// Flags for GOAWAY frames.
/// </summary>
public static class GoAwayFlags
{
    /// <summary>Indicates graceful draining.</summary>
    public const byte Draining = 0x01;

    /// <summary>Indicates immediate shutdown.</summary>
    public const byte Immediate = 0x02;
}

/// <summary>
/// Flags for PING frames.
/// </summary>
public static class PingFlags
{
    /// <summary>Indicates this is a BDP estimation ping.</summary>
    public const byte Bdp = 0x01;

    /// <summary>Indicates this is a ping acknowledgment.</summary>
    public const byte Ack = 0x02;
}

/// <summary>
/// Constants for the shared memory transport protocol.
/// </summary>
public static class ShmConstants
{
    /// <summary>Size of the frame header in bytes.</summary>
    public const int FrameHeaderSize = 16;

    /// <summary>Size of the ring header in bytes.</summary>
    public const int RingHeaderSize = 64;

    /// <summary>Size of the segment header in bytes (matches grpc-go-shmem).</summary>
    public const int SegmentHeaderSize = 128;

    /// <summary>Magic string for segment identification ("GRPCSHM\0").</summary>
    public static ReadOnlySpan<byte> SegmentMagicBytes => "GRPCSHM\0"u8;

    /// <summary>Legacy magic number for backward compatibility.</summary>
    public const uint SegmentMagicLegacy = 0x53484D31;

    /// <summary>Current protocol version.</summary>
    public const uint ProtocolVersion = 1;

    /// <summary>Default ring buffer capacity (64 MiB).</summary>
    public const int DefaultRingCapacity = 64 * 1024 * 1024;

    /// <summary>Default maximum concurrent streams.</summary>
    public const uint DefaultMaxStreams = 100;

    /// <summary>
    /// Initial flow-control window size.
    /// HTTP/2 over TCP uses 65 535, but shared memory is a local, high-bandwidth
    /// transport so we use 16 MiB (¼ of the default 64 MiB ring capacity).
    /// This must be large enough that a single gRPC message can be sent without
    /// requiring a WINDOW_UPDATE first.
    /// </summary>
    public const int InitialWindowSize = 16 * 1024 * 1024;

    /// <summary>Maximum window size.</summary>
    public const int MaxWindowSize = int.MaxValue;

    /// <summary>Maximum stream ID for client (odd numbers).</summary>
    public const uint MaxStreamId = uint.MaxValue - 1;

    /// <summary>Default spin iterations before falling back to blocking.</summary>
    public const int SpinIterationsDefault = 300;

    /// <summary>Minimum spin iterations for adaptive adjustment.</summary>
    public const int SpinIterationsMin = 50;

    /// <summary>Maximum spin iterations to prevent excessive CPU use.</summary>
    public const int SpinIterationsMax = 2000;

    /// <summary>Suffix for control segment names.</summary>
    public const string ControlSegmentSuffix = "_ctl";

    /// <summary>Control wire protocol version.</summary>
    public const byte ControlWireVersion = 1;

    /// <summary>Minimum ring capacity for control segments (4KB).</summary>
    public const ulong MinRingCapacity = 4096;
}
