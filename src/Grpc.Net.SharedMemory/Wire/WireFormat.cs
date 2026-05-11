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
/// Identifies the wire-level frame encoding used on a <see cref="ShmRing"/>.
/// Both encodings share the same in-memory frame model
/// (<see cref="FrameHeader"/> + <see cref="FramePayload"/>); the codec
/// implementations differ only in how those frames are laid out on the ring.
/// </summary>
/// <remarks>
/// The wire format is negotiated once during the control-plane CONNECT/ACCEPT
/// handshake (see <see cref="ControlWire"/>) and never changes for the lifetime
/// of the data segment.
/// </remarks>
public enum WireFormat : byte
{
    /// <summary>
    /// The legacy 16-byte custom frame header. Original encoding used by
    /// grpc-go-shmem and the .NET implementation prior to gRFC alignment.
    /// </summary>
    Custom16 = 0,

    /// <summary>
    /// HTTP/2 frame format (RFC 7540) with a 9-byte header and HPACK header
    /// compression. Used to align the SHM transport with the gRPC over HTTP/2
    /// protocol so that a single gRFC describes the wire (with SHM only
    /// substituting the connection layer).
    /// </summary>
    Http2 = 1,
}
