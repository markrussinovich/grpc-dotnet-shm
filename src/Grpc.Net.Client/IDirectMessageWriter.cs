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

using Grpc.Core;

namespace Grpc.Net.Client;

/// <summary>
/// Allows a gRPC client stream writer to bypass the standard gRPC framing
/// (5-byte header + SerializationContext buffer + Stream.WriteAsync) when
/// the transport can accept raw protobuf payloads directly.
/// <para>
/// When the write stream implements this interface,
/// <c>StreamExtensions.WriteMessageAsync</c> calls
/// <see cref="WriteSerializedMessageAsync{TMessage}"/> which lets the
/// transport control the serialization buffer lifetime, enabling
/// zero-copy fire-and-forget sends.
/// </para>
/// </summary>
public interface IDirectMessageWriter
{
    /// <summary>
    /// Serializes and sends a protobuf message directly, bypassing gRPC
    /// framing. The transport owns the serialization buffer and controls
    /// its lifetime (typically returned to ArrayPool after ring write).
    /// </summary>
    Task WriteSerializedMessageAsync<TMessage>(
        TMessage message,
        Action<TMessage, SerializationContext> serializer,
        CancellationToken cancellationToken);
}
