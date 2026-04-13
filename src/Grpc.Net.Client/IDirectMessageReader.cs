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

using System.Buffers;

namespace Grpc.Net.Client;

/// <summary>
/// Allows a gRPC client stream reader to bypass the standard Stream.ReadAsync
/// loop and gRPC framing overhead when the transport already has complete
/// message payloads in memory (e.g., shared-memory ring buffer).
/// <para>
/// When the HTTP response content implements this interface, the client
/// reader calls <see cref="ReadNextMessageAsync"/> to get the raw
/// protobuf payload directly, skipping the 5-byte gRPC header parsing,
/// intermediate buffer allocation, and the async ReadMessageContentAsync
/// loop that normally accounts for ~70% of large-message read latency.
/// </para>
/// </summary>
public interface IDirectMessageReader
{
    /// <summary>
    /// Returns the next complete gRPC message payload (without the 5-byte
    /// gRPC frame header) as a <see cref="ReadOnlySequence{T}"/>.
    /// Multi-frame messages are returned as a linked chain of segments,
    /// avoiding the need to flatten into a single contiguous buffer.
    /// The returned sequence is valid until the next call to
    /// <see cref="ReadNextMessageAsync"/> or <see cref="ReleaseCurrentMessage"/>.
    /// </summary>
    ValueTask<(ReadOnlySequence<byte> Payload, bool EndOfStream)> ReadNextMessageAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Releases the buffer backing the current message payload.
    /// Called after deserialization is complete.
    /// </summary>
    void ReleaseCurrentMessage();
}

#if SHM_TRACE
/// <summary>Profiling counters for IDirectMessageReader path — compile with /d:SHM_TRACE to enable.</summary>
public static class DirectReaderProf
{
    public static long Count, ReadTicks, DeserTicks;
}
#endif
