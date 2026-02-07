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

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Interface for shared memory security handshakers. Allows custom
/// implementations and mocking in tests.
/// </summary>
public interface IShmSecurityHandshaker
{
    /// <summary>
    /// Gets the local identity token used in the handshake.
    /// </summary>
    string Identity { get; }

    /// <summary>
    /// Performs the client side of the security handshake.
    /// </summary>
    /// <param name="writer">Delegate to write a handshake frame to the transport.</param>
    /// <param name="reader">Delegate to read a handshake frame from the transport.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Authentication info on success.</returns>
    Task<ShmAuthInfo> ClientHandshakeAsync(
        Func<FrameType, ReadOnlyMemory<byte>, CancellationToken, Task> writer,
        Func<CancellationToken, Task<(FrameType Type, ReadOnlyMemory<byte> Payload)>> reader,
        CancellationToken cancellationToken);

    /// <summary>
    /// Performs the server side of the security handshake.
    /// </summary>
    /// <param name="writer">Delegate to write a handshake frame to the transport.</param>
    /// <param name="reader">Delegate to read a handshake frame from the transport.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Authentication info on success.</returns>
    Task<ShmAuthInfo> ServerHandshakeAsync(
        Func<FrameType, ReadOnlyMemory<byte>, CancellationToken, Task> writer,
        Func<CancellationToken, Task<(FrameType Type, ReadOnlyMemory<byte> Payload)>> reader,
        CancellationToken cancellationToken);
}

/// <summary>
/// Default shared memory security handshaker implementing the 3-step
/// Init → Resp → Ack protocol compatible with grpc-go-shmem.
/// </summary>
/// <remarks>
/// <para>Wire format for Init/Resp: version(1) + identityLen(2-LE) + identity(var) + nonce(16)</para>
/// <para>Wire format for Ack: version(1) + status(1)</para>
/// <para>Wire format for Fail: version(1) + code(1) + messageLen(2-LE) + message(var)</para>
/// </remarks>
public sealed class ShmSecurityHandshaker : IShmSecurityHandshaker
{
    /// <summary>
    /// Current handshake protocol version.
    /// </summary>
    public const byte HandshakeVersion = 1;

    /// <summary>
    /// Size of the nonce in bytes.
    /// </summary>
    public const int NonceSize = 16;

    /// <summary>
    /// Maximum identity string size in bytes.
    /// </summary>
    public const int MaxIdentitySize = 256;

    /// <summary>
    /// Default handshake timeout.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;

    /// <summary>
    /// Gets the local identity token.
    /// </summary>
    public string Identity { get; }

    /// <summary>
    /// Gets or sets the optional callback to verify the remote peer's identity.
    /// Return null/no exception if valid, throw if invalid.
    /// </summary>
    public Func<string, Task>? VerifyIdentity { get; set; }

    /// <summary>
    /// Creates a handshaker with the specified identity.
    /// </summary>
    /// <param name="identity">Local identity token. If null, defaults to "pid:{ProcessId}".</param>
    /// <param name="logger">Optional logger.</param>
    public ShmSecurityHandshaker(string? identity = null, ILogger? logger = null)
    {
        Identity = identity ?? $"pid:{Environment.ProcessId}";
        _logger = logger ?? NullLogger.Instance;

        if (Encoding.UTF8.GetByteCount(Identity) > MaxIdentitySize)
        {
            throw new ArgumentException(
                $"Identity exceeds maximum size of {MaxIdentitySize} bytes.",
                nameof(identity));
        }
    }

    /// <summary>
    /// Creates a default handshaker with PID-based identity.
    /// </summary>
    public static ShmSecurityHandshaker CreateDefault(ILogger? logger = null)
        => new(null, logger);

    /// <inheritdoc/>
    public async Task<ShmAuthInfo> ClientHandshakeAsync(
        Func<FrameType, ReadOnlyMemory<byte>, CancellationToken, Task> writer,
        Func<CancellationToken, Task<(FrameType Type, ReadOnlyMemory<byte> Payload)>> reader,
        CancellationToken cancellationToken)
    {
        // Step 1: Send HandshakeInit
        var clientNonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(clientNonce);

        var initPayload = EncodeIdentityMessage(Identity, clientNonce);
        await writer(FrameType.HandshakeInit, initPayload, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Client sent HandshakeInit with identity '{Identity}'", Identity);

        // Step 2: Read HandshakeResp (or HandshakeFail)
        var (respType, respPayload) = await reader(cancellationToken).ConfigureAwait(false);

        if (respType == FrameType.HandshakeFail)
        {
            var (code, message) = DecodeFailMessage(respPayload);
            throw new ShmHandshakeException(code, $"Server rejected handshake: {message}");
        }

        if (respType != FrameType.HandshakeResp)
        {
            throw new ShmHandshakeException(
                HandshakeErrorCode.Internal,
                $"Expected HandshakeResp (0x21), got 0x{(byte)respType:X2}");
        }

        var (serverIdentity, serverNonce, serverVersion) = DecodeIdentityMessage(respPayload);
        if (serverVersion != HandshakeVersion)
        {
            var failPayload = EncodeFailMessage(HandshakeErrorCode.VersionMismatch,
                $"Expected version {HandshakeVersion}, got {serverVersion}");
            await writer(FrameType.HandshakeFail, failPayload, cancellationToken).ConfigureAwait(false);
            throw new ShmHandshakeException(HandshakeErrorCode.VersionMismatch,
                $"Server version {serverVersion} does not match client version {HandshakeVersion}");
        }

        _logger.LogDebug("Client received HandshakeResp from '{ServerIdentity}'", serverIdentity);

        // Step 2b: Verify server identity
        if (VerifyIdentity != null)
        {
            try
            {
                await VerifyIdentity(serverIdentity).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var failPayload = EncodeFailMessage(HandshakeErrorCode.IdentityInvalid, ex.Message);
                await writer(FrameType.HandshakeFail, failPayload, cancellationToken).ConfigureAwait(false);
                throw new ShmHandshakeException(HandshakeErrorCode.IdentityInvalid,
                    $"Server identity verification failed: {ex.Message}", ex);
            }
        }

        // Step 3: Send HandshakeAck
        var ackPayload = new byte[] { HandshakeVersion, 0x00 }; // version + status=success
        await writer(FrameType.HandshakeAck, ackPayload, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Client sent HandshakeAck — handshake complete");

        return new ShmAuthInfo
        {
            LocalIdentity = Identity,
            RemoteIdentity = serverIdentity,
            Nonce = serverNonce
        };
    }

    /// <inheritdoc/>
    public async Task<ShmAuthInfo> ServerHandshakeAsync(
        Func<FrameType, ReadOnlyMemory<byte>, CancellationToken, Task> writer,
        Func<CancellationToken, Task<(FrameType Type, ReadOnlyMemory<byte> Payload)>> reader,
        CancellationToken cancellationToken)
    {
        // Step 1: Read HandshakeInit
        var (initType, initPayload) = await reader(cancellationToken).ConfigureAwait(false);

        if (initType != FrameType.HandshakeInit)
        {
            throw new ShmHandshakeException(
                HandshakeErrorCode.Internal,
                $"Expected HandshakeInit (0x20), got 0x{(byte)initType:X2}");
        }

        var (clientIdentity, clientNonce, clientVersion) = DecodeIdentityMessage(initPayload);
        if (clientVersion != HandshakeVersion)
        {
            var failPayload = EncodeFailMessage(HandshakeErrorCode.VersionMismatch,
                $"Expected version {HandshakeVersion}, got {clientVersion}");
            await writer(FrameType.HandshakeFail, failPayload, cancellationToken).ConfigureAwait(false);
            throw new ShmHandshakeException(HandshakeErrorCode.VersionMismatch,
                $"Client version {clientVersion} does not match server version {HandshakeVersion}");
        }

        _logger.LogDebug("Server received HandshakeInit from '{ClientIdentity}'", clientIdentity);

        // Step 1b: Verify client identity
        if (VerifyIdentity != null)
        {
            try
            {
                await VerifyIdentity(clientIdentity).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var failPayload = EncodeFailMessage(HandshakeErrorCode.IdentityInvalid, ex.Message);
                await writer(FrameType.HandshakeFail, failPayload, cancellationToken).ConfigureAwait(false);
                throw new ShmHandshakeException(HandshakeErrorCode.IdentityInvalid,
                    $"Client identity verification failed: {ex.Message}", ex);
            }
        }

        // Step 2: Send HandshakeResp
        var serverNonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(serverNonce);

        var respPayload = EncodeIdentityMessage(Identity, serverNonce);
        await writer(FrameType.HandshakeResp, respPayload, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Server sent HandshakeResp with identity '{Identity}'", Identity);

        // Step 3: Read HandshakeAck (or HandshakeFail)
        var (ackType, ackPayload) = await reader(cancellationToken).ConfigureAwait(false);

        if (ackType == FrameType.HandshakeFail)
        {
            var (code, message) = DecodeFailMessage(ackPayload);
            throw new ShmHandshakeException(code, $"Client rejected handshake: {message}");
        }

        if (ackType != FrameType.HandshakeAck)
        {
            throw new ShmHandshakeException(
                HandshakeErrorCode.Internal,
                $"Expected HandshakeAck (0x22), got 0x{(byte)ackType:X2}");
        }

        _logger.LogDebug("Server received HandshakeAck — handshake complete");

        return new ShmAuthInfo
        {
            LocalIdentity = Identity,
            RemoteIdentity = clientIdentity,
            Nonce = clientNonce
        };
    }

    // ================================================================
    // Wire encoding/decoding — matches grpc-go-shmem binary format
    // ================================================================

    /// <summary>
    /// Encodes an identity message (Init or Resp): version(1) + identityLen(2-LE) + identity(var) + nonce(16).
    /// </summary>
    public static byte[] EncodeIdentityMessage(string identity, byte[] nonce)
    {
        var identityBytes = Encoding.UTF8.GetBytes(identity);
        var buffer = new byte[1 + 2 + identityBytes.Length + NonceSize];
        buffer[0] = HandshakeVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(1), (ushort)identityBytes.Length);
        identityBytes.CopyTo(buffer.AsSpan(3));
        nonce.AsSpan(0, NonceSize).CopyTo(buffer.AsSpan(3 + identityBytes.Length));
        return buffer;
    }

    /// <summary>
    /// Decodes an identity message (Init or Resp).
    /// </summary>
    public static (string Identity, byte[] Nonce, byte Version) DecodeIdentityMessage(ReadOnlyMemory<byte> payload)
    {
        var span = payload.Span;
        if (span.Length < 3)
        {
            throw new ShmHandshakeException(HandshakeErrorCode.Internal, "Handshake message too short");
        }

        var version = span[0];
        var identityLen = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(1));

        if (identityLen > MaxIdentitySize)
        {
            throw new ShmHandshakeException(HandshakeErrorCode.Internal,
                $"Identity length {identityLen} exceeds maximum {MaxIdentitySize}");
        }

        var expectedLen = 3 + identityLen + NonceSize;
        if (span.Length < expectedLen)
        {
            throw new ShmHandshakeException(HandshakeErrorCode.Internal,
                $"Handshake message too short: expected {expectedLen}, got {span.Length}");
        }

        var identity = Encoding.UTF8.GetString(span.Slice(3, identityLen));
        var nonce = span.Slice(3 + identityLen, NonceSize).ToArray();

        return (identity, nonce, version);
    }

    /// <summary>
    /// Encodes a failure message: version(1) + code(1) + messageLen(2-LE) + message(var).
    /// </summary>
    public static byte[] EncodeFailMessage(HandshakeErrorCode code, string message)
    {
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var buffer = new byte[1 + 1 + 2 + messageBytes.Length];
        buffer[0] = HandshakeVersion;
        buffer[1] = (byte)code;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2), (ushort)messageBytes.Length);
        messageBytes.CopyTo(buffer.AsSpan(4));
        return buffer;
    }

    /// <summary>
    /// Decodes a failure message.
    /// </summary>
    public static (HandshakeErrorCode Code, string Message) DecodeFailMessage(ReadOnlyMemory<byte> payload)
    {
        var span = payload.Span;
        if (span.Length < 4)
        {
            return (HandshakeErrorCode.Internal, "Malformed failure message");
        }

        // span[0] = version (ignored for fail decoding)
        var code = (HandshakeErrorCode)span[1];
        var messageLen = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(2));
        var message = span.Length >= 4 + messageLen
            ? Encoding.UTF8.GetString(span.Slice(4, messageLen))
            : "Unknown error";

        return (code, message);
    }
}
