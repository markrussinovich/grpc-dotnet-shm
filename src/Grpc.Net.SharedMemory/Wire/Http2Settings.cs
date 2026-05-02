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

using System.Buffers.Binary;

namespace Grpc.Net.SharedMemory.Wire;

/// <summary>
/// HTTP/2 SETTINGS frame helpers (RFC 7540 §6.5).
/// </summary>
/// <remarks>
/// The SHM transport uses fixed values for all settings; we still emit/echo
/// SETTINGS for spec compliance, but we don't dynamically adjust behaviour
/// based on a peer's advertised values. Both peers are this implementation.
/// </remarks>
internal static class Http2Settings
{
    /// <summary>
    /// Settings advertised by this implementation. Each pair is
    /// (16-bit identifier, 32-bit value).
    /// </summary>
    public static readonly (Http2SettingsParameter Id, uint Value)[] Defaults = new (Http2SettingsParameter, uint)[]
    {
        // Disable HPACK dynamic table — we never reference dynamic entries.
        (Http2SettingsParameter.HeaderTableSize, 0),
        // Server push not used.
        (Http2SettingsParameter.EnablePush, 0),
        // Maximum frame payload size (RFC 7540 max). Allows large single-frame
        // messages, which preserves our zero-copy path for messages up to ~16 MiB.
        (Http2SettingsParameter.MaxFrameSize, (1u << 24) - 1),
        // Effectively disable HTTP/2 stream-level flow control. The SHM ring's
        // own back-pressure is the sole flow-control mechanism.
        (Http2SettingsParameter.InitialWindowSize, int.MaxValue),
        // Allow large header lists (1 MiB).
        (Http2SettingsParameter.MaxHeaderListSize, 1u << 20),
    };

    /// <summary>Size of a single setting on the wire (id + value).</summary>
    public const int EntrySize = 6;

    /// <summary>
    /// Encodes the default settings into <paramref name="destination"/>.
    /// Returns the number of bytes written.
    /// </summary>
    public static int EncodeDefaults(Span<byte> destination)
    {
        var offset = 0;
        for (var i = 0; i < Defaults.Length; i++)
        {
            var (id, value) = Defaults[i];
            BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(offset, 2), (ushort)id);
            BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(offset + 2, 4), value);
            offset += EntrySize;
        }
        return offset;
    }

    /// <summary>Total size of the default settings payload.</summary>
    public static int DefaultsLength => Defaults.Length * EntrySize;
}
