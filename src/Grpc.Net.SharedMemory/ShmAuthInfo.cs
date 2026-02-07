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
/// Authentication information produced by the shared memory security handshake.
/// Mirrors Go's <c>ShmAuthInfo</c>.
/// </summary>
public sealed class ShmAuthInfo
{
    /// <summary>
    /// Gets the authentication type. Always "shm".
    /// </summary>
    public string AuthType => "shm";

    /// <summary>
    /// Gets the local identity token used in the handshake.
    /// </summary>
    public required string LocalIdentity { get; init; }

    /// <summary>
    /// Gets the remote peer's identity token received during the handshake.
    /// </summary>
    public required string RemoteIdentity { get; init; }

    /// <summary>
    /// Gets the 16-byte nonce exchanged during the handshake.
    /// </summary>
    public required byte[] Nonce { get; init; }
}

/// <summary>
/// Error codes for the shared memory security handshake.
/// Matches grpc-go-shmem <c>HandshakeErrorCode</c>.
/// </summary>
public enum HandshakeErrorCode : byte
{
    /// <summary>No error.</summary>
    None = 0,

    /// <summary>Protocol version mismatch.</summary>
    VersionMismatch = 1,

    /// <summary>Remote identity validation failed.</summary>
    IdentityInvalid = 2,

    /// <summary>Nonce mismatch.</summary>
    NonceMismatch = 3,

    /// <summary>Handshake timed out.</summary>
    Timeout = 4,

    /// <summary>Internal handshake error.</summary>
    Internal = 5
}

/// <summary>
/// Exception thrown when the shared memory security handshake fails.
/// </summary>
public sealed class ShmHandshakeException : Exception
{
    /// <summary>
    /// Gets the handshake error code.
    /// </summary>
    public HandshakeErrorCode Code { get; }

    /// <summary>
    /// Creates a new <see cref="ShmHandshakeException"/>.
    /// </summary>
    public ShmHandshakeException(HandshakeErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>
    /// Creates a new <see cref="ShmHandshakeException"/> with an inner exception.
    /// </summary>
    public ShmHandshakeException(HandshakeErrorCode code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }
}
