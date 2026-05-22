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
using System.Text;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Helpers for transport-level tests that exercise <see cref="ShmGrpcStream"/>
/// directly (bypassing <c>ShmGrpcServer</c>'s gRPC framing). Since the SHM
/// transport speaks gRPC-over-HTTP/2, MESSAGE-frame bodies on the wire are
/// gRPC LPM (length-prefixed-message) blobs: <c>[compFlag(1)][len(4 BE)][body]</c>.
/// These helpers wrap/unwrap that envelope so tests can pass plain byte
/// sequences while still producing wire-correct H2 DATA frames.
/// </summary>
internal static class LpmHelpers
{
    /// <summary>
    /// Wraps <paramref name="body"/> with the 5-byte gRPC LPM header.
    /// </summary>
    public static byte[] WrapLpm(ReadOnlySpan<byte> body)
    {
        var buf = new byte[5 + body.Length];
        buf[0] = 0; // no compression
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(1, 4), (uint)body.Length);
        body.CopyTo(buf.AsSpan(5));
        return buf;
    }

    /// <summary>Wraps a UTF-8 string into a gRPC LPM blob.</summary>
    public static byte[] WrapLpmText(string text) => WrapLpm(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Unwraps a gRPC LPM blob into its body bytes. Throws on a malformed
    /// or compressed blob; tests that need either should not use this helper.
    /// </summary>
    public static byte[] UnwrapLpm(ReadOnlySpan<byte> framed)
    {
        if (framed.Length < 5) throw new ArgumentException($"LPM blob too short: {framed.Length} bytes");
        if (framed[0] != 0) throw new InvalidOperationException(
            $"compressed LPM not supported by this helper (first byte=0x{framed[0]:X2}, len={framed.Length})");
        var len = (int)BinaryPrimitives.ReadUInt32BigEndian(framed.Slice(1, 4));
        if (len + 5 != framed.Length) throw new InvalidOperationException(
            $"LPM length mismatch: declared {len}, framed length {framed.Length - 5}");
        return framed.Slice(5, len).ToArray();
    }

    /// <summary>Unwraps and decodes UTF-8.</summary>
    public static string UnwrapLpmText(ReadOnlySpan<byte> framed) => Encoding.UTF8.GetString(UnwrapLpm(framed));

    /// <summary>
    /// Async-yields each LPM-unwrapped message body from a
    /// <see cref="ShmGrpcStream"/>. Replaces direct iteration over
    /// <see cref="ShmGrpcStream.ReceiveMessagesAsync"/> in tests so the
    /// caller sees the application-level body, not the gRPC LPM blob.
    /// </summary>
    public static async IAsyncEnumerable<byte[]> ReceiveLpmMessagesAsync(
        this ShmGrpcStream stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        await foreach (var framed in stream.ReceiveMessagesAsync(cancellationToken))
        {
            yield return UnwrapLpm(framed);
        }
    }
}
