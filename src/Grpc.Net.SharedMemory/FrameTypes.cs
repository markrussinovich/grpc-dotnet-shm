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

    /// <summary>Handshake acknowledgement frame.</summary>
    HandshakeAck = 0x22,

    /// <summary>Handshake failure frame.</summary>
    HandshakeFail = 0x23
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
    /// <summary>Indicates this is the last message (implicit half-close).</summary>
    public const byte EndStream = 0x02;
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

    /// <summary>
    /// Bench-only "strict fair" mode cap on per-frame payload size.
    /// When env var <c>SHM_FAIR_MAX_FRAME</c> is set to a positive integer
    /// (typically 16384 to match HTTP/2 spec default
    /// SETTINGS_MAX_FRAME_SIZE / Go gRPC's spec default), the SHM
    /// writer caps both the single-frame threshold and multi-frame
    /// chunk size to this value. This forces a large message to be
    /// split into multiple frames the same way TCP/UDS gRPC does,
    /// removing SHM's "single 16 MiB frame" advantage from fair
    /// comparisons.
    ///
    /// Has zero effect when the env var is unset (production default).
    /// Has no effect on messages smaller than the cap (still single
    /// frame).
    /// </summary>
    public static readonly int FairMaxFramePayload =
        int.TryParse(Environment.GetEnvironmentVariable("SHM_FAIR_MAX_FRAME"),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0
            ? v : int.MaxValue;

    /// <summary>Default ring buffer capacity (64 MiB).</summary>
    public const int DefaultRingCapacity = 64 * 1024 * 1024;

    /// <summary>Default maximum concurrent streams.</summary>
    public const uint DefaultMaxStreams = 100;

    /// <summary>
    /// Initial per-stream and per-conn HTTP/2 receive window size.
    /// Default is 32 MiB (SHM-tuned: matches grpc-go-shmem's tuned default;
    /// reduces WU traffic vs the spec 64 KiB for the common large-message
    /// case). Override at process start via env var
    /// <c>SHM_INITIAL_WINDOW</c> — set to <c>65535</c> for "fair" mode
    /// matching the HTTP/2 spec default (RFC 7540 §6.9.2). The drip
    /// threshold (<c>limit/4</c>) inside <see cref="Synchronization.InFlow"/>
    /// and <see cref="Synchronization.TrInFlow"/> follows this value.
    /// </summary>
    public static readonly int InitialWindowSize =
        int.TryParse(Environment.GetEnvironmentVariable("SHM_INITIAL_WINDOW"),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var iw) && iw > 0
            ? iw : 32 * 1024 * 1024;

    /// <summary>Maximum window size.</summary>
    public const int MaxWindowSize = int.MaxValue;

    /// <summary>
    /// Drip threshold for batched <c>WINDOW_UPDATE</c> emission
    /// (matches Go's <c>limit/4</c> cadence). Computed from
    /// <see cref="InitialWindowSize"/> at process start; not used directly
    /// in code (the InFlow/TrInFlow classes recompute the same value
    /// from their <c>_limit</c> field) but exported for observability.
    /// </summary>
    public static readonly uint WindowUpdateBatchThreshold = (uint)(InitialWindowSize / 4);

    /// <summary>Maximum stream ID for client (odd numbers).</summary>
    public const uint MaxStreamId = uint.MaxValue - 1;

    /// <summary>Default spin iterations before falling back to blocking.
    /// Tuned high enough to cover writer response latency in streaming
    /// ping-pong (~40µs at 3000 × ~35ns/spin). The adaptive algorithm
    /// adjusts downward to SpinIterationsMin when data arrives quickly,
    /// so idle CPU burn is minimal in practice.</summary>
    public const int SpinIterationsDefault = 3000;

    /// <summary>Minimum spin iterations for adaptive adjustment.
    /// Must be high enough to cover writer response latency in streaming
    /// ping-pong (~40µs). At ~35ns/spin, 2000 iterations = ~70µs window,
    /// sufficient for the writer to process and commit a response frame.
    /// Lower values cause the adaptive algorithm to drop the cutoff below
    /// the writer's response time, forcing unnecessary OS-level waits
    /// (~80µs penalty per wait).</summary>
    public const int SpinIterationsMin = 2000;

    /// <summary>Maximum spin iterations to prevent excessive CPU use.</summary>
    public const int SpinIterationsMax = 10000;

    /// <summary>
    /// Writer-loop Phase 2 spin budget (iterations). Default 0 = NO SPIN,
    /// matching grpc-go-shmem's <c>shmSpinDefault = 0</c> policy
    /// (see <c>internal/transport/shm_spin_config.go</c>). With spin
    /// disabled the WriterLoop's Phase 2 falls straight through to
    /// Phase 2.5 (Thread.Yield, matches Go's <c>runtime.Gosched()</c>)
    /// and then Phase 3 (kernel <c>ManualResetEventSlim.Wait</c>).
    /// <para>
    /// Operators that want sub-µs RPC latency can opt in by setting
    /// the env var <c>SHM_WRITER_SPIN_ITERATIONS</c> to a positive
    /// value (typically 500-4000). 2000 (~60 µs window) is the legacy
    /// value tuned for single-stream ping-pong. Larger values (up to
    /// <see cref="SpinIterationsMax"/>) burn more idle CPU for tighter
    /// catch of next-batch arrival in the 10-100 stream regime.
    /// </para>
    /// <para>
    /// MUST be opt-in for fair-comparison benches (Doug's request
    /// mirrored from grpc-go-shmem): comparing SHM to UDS/TCP with
    /// SHM idle-spinning skews the result unfairly.
    /// </para>
    /// </summary>
    public static readonly int WriterLoopSpinIterations =
        int.TryParse(Environment.GetEnvironmentVariable("SHM_WRITER_SPIN_ITERATIONS"),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var ws) && ws >= 0
            ? Math.Min(ws, SpinIterationsMax)
            : 0;

    /// <summary>Suffix for control segment names.</summary>
    public const string ControlSegmentSuffix = "_ctl";

    /// <summary>Control wire protocol version.</summary>
    public const byte ControlWireVersion = 1;

    /// <summary>Minimum ring capacity for control segments (4KB).</summary>
    public const ulong MinRingCapacity = 4096;
}
