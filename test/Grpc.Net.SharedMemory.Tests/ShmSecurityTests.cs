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
using System.Text;
using System.Threading.Channels;
using Grpc.Net.SharedMemory;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Tests for <see cref="ShmSecurityHandshaker"/>, <see cref="ShmAuthInfo"/>,
/// <see cref="HandshakeErrorCode"/>, and the wire encoding.
/// </summary>
[TestFixture]
public class ShmSecurityTests
{
    // ============================================================
    // ShmAuthInfo tests
    // ============================================================

    [Test]
    public void ShmAuthInfo_AuthType_IsShm()
    {
        var info = new ShmAuthInfo
        {
            LocalIdentity = "pid:123",
            RemoteIdentity = "pid:456",
            Nonce = new byte[16]
        };
        Assert.That(info.AuthType, Is.EqualTo("shm"));
    }

    // ============================================================
    // ShmSecurityHandshaker construction
    // ============================================================

    [Test]
    public void ShmSecurityHandshaker_DefaultIdentity_IsPidBased()
    {
        var hs = new ShmSecurityHandshaker();
        Assert.That(hs.Identity, Does.StartWith("pid:"));
        Assert.That(hs.Identity, Is.EqualTo($"pid:{Environment.ProcessId}"));
    }

    [Test]
    public void ShmSecurityHandshaker_CustomIdentity_IsUsed()
    {
        var hs = new ShmSecurityHandshaker("my-service");
        Assert.That(hs.Identity, Is.EqualTo("my-service"));
    }

    [Test]
    public void ShmSecurityHandshaker_TooLongIdentity_Throws()
    {
        var longIdentity = new string('x', 300);
        Assert.Throws<ArgumentException>(() => new ShmSecurityHandshaker(longIdentity));
    }

    [Test]
    public void ShmSecurityHandshaker_CreateDefault_ReturnsPidBased()
    {
        var hs = ShmSecurityHandshaker.CreateDefault();
        Assert.That(hs.Identity, Does.StartWith("pid:"));
    }

    // ============================================================
    // Wire encoding round-trips
    // ============================================================

    [Test]
    public void EncodeDecodeIdentityMessage_RoundTrips()
    {
        var identity = "test-identity";
        var nonce = new byte[16];
        for (int i = 0; i < 16; i++) nonce[i] = (byte)(i + 1);

        var encoded = ShmSecurityHandshaker.EncodeIdentityMessage(identity, nonce);
        var (decodedIdentity, decodedNonce, version) = ShmSecurityHandshaker.DecodeIdentityMessage(encoded);

        Assert.That(version, Is.EqualTo(ShmSecurityHandshaker.HandshakeVersion));
        Assert.That(decodedIdentity, Is.EqualTo(identity));
        Assert.That(decodedNonce, Is.EqualTo(nonce));
    }

    [Test]
    public void EncodeDecodeIdentityMessage_EmptyIdentity_RoundTrips()
    {
        var nonce = new byte[16];
        var encoded = ShmSecurityHandshaker.EncodeIdentityMessage("", nonce);
        var (decodedIdentity, _, _) = ShmSecurityHandshaker.DecodeIdentityMessage(encoded);
        Assert.That(decodedIdentity, Is.EqualTo(""));
    }

    [Test]
    public void EncodeDecodeIdentityMessage_UnicodeIdentity_RoundTrips()
    {
        var identity = "服务-αβγ";
        var nonce = new byte[16];
        var encoded = ShmSecurityHandshaker.EncodeIdentityMessage(identity, nonce);
        var (decodedIdentity, _, _) = ShmSecurityHandshaker.DecodeIdentityMessage(encoded);
        Assert.That(decodedIdentity, Is.EqualTo(identity));
    }

    [Test]
    public void DecodeIdentityMessage_TruncatedPayload_Throws()
    {
        Assert.Throws<ShmHandshakeException>(() =>
            ShmSecurityHandshaker.DecodeIdentityMessage(new byte[] { 1, 0 }));
    }

    [Test]
    public void EncodeDecodeFailMessage_RoundTrips()
    {
        var encoded = ShmSecurityHandshaker.EncodeFailMessage(
            HandshakeErrorCode.IdentityInvalid, "bad identity");
        var (code, message) = ShmSecurityHandshaker.DecodeFailMessage(encoded);

        Assert.That(code, Is.EqualTo(HandshakeErrorCode.IdentityInvalid));
        Assert.That(message, Is.EqualTo("bad identity"));
    }

    [Test]
    public void DecodeFailMessage_TruncatedPayload_ReturnsInternalError()
    {
        var (code, _) = ShmSecurityHandshaker.DecodeFailMessage(new byte[] { 1, 2 });
        Assert.That(code, Is.EqualTo(HandshakeErrorCode.Internal));
    }

    // ============================================================
    // IdentityMessage wire format verification
    // ============================================================

    [Test]
    public void EncodeIdentityMessage_WireFormat_MatchesGoSpec()
    {
        var identity = "pid:42";
        var nonce = new byte[16];
        for (int i = 0; i < 16; i++) nonce[i] = (byte)i;

        var encoded = ShmSecurityHandshaker.EncodeIdentityMessage(identity, nonce);
        var span = encoded.AsSpan();

        // version(1) + identityLen(2-LE) + identity(var) + nonce(16)
        Assert.That(span[0], Is.EqualTo(ShmSecurityHandshaker.HandshakeVersion), "version");
        var identityLen = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(1));
        Assert.That(identityLen, Is.EqualTo(6), "identityLen for 'pid:42'");
        Assert.That(Encoding.UTF8.GetString(span.Slice(3, 6)), Is.EqualTo("pid:42"));
        Assert.That(span.Slice(9, 16).ToArray(), Is.EqualTo(nonce));
        Assert.That(encoded.Length, Is.EqualTo(1 + 2 + 6 + 16));
    }

    [Test]
    public void EncodeFailMessage_WireFormat_MatchesGoSpec()
    {
        var encoded = ShmSecurityHandshaker.EncodeFailMessage(
            HandshakeErrorCode.Timeout, "timed out");
        var span = encoded.AsSpan();

        // version(1) + code(1) + messageLen(2-LE) + message(var)
        Assert.That(span[0], Is.EqualTo(ShmSecurityHandshaker.HandshakeVersion));
        Assert.That(span[1], Is.EqualTo((byte)HandshakeErrorCode.Timeout));
        var msgLen = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(2));
        Assert.That(msgLen, Is.EqualTo(9));
        Assert.That(Encoding.UTF8.GetString(span.Slice(4, 9)), Is.EqualTo("timed out"));
    }

    // ============================================================
    // Full handshake protocol — in-memory simulation
    // ============================================================

    [Test]
    public async Task Handshake_SuccessfulRoundTrip_ProducesAuthInfo()
    {
        var clientHs = new ShmSecurityHandshaker("client-svc");
        var serverHs = new ShmSecurityHandshaker("server-svc");

        // Channels simulate bidirectional transport
        var clientToServer = Channel.CreateUnbounded<(FrameType, byte[])>();
        var serverToClient = Channel.CreateUnbounded<(FrameType, byte[])>();

        var clientTask = clientHs.ClientHandshakeAsync(
            writer: (type, payload, ct) =>
            {
                clientToServer.Writer.TryWrite((type, payload.ToArray()));
                return Task.CompletedTask;
            },
            reader: async ct =>
            {
                var (t, p) = await serverToClient.Reader.ReadAsync(ct);
                return (t, (ReadOnlyMemory<byte>)p);
            },
            CancellationToken.None);

        var serverTask = serverHs.ServerHandshakeAsync(
            writer: (type, payload, ct) =>
            {
                serverToClient.Writer.TryWrite((type, payload.ToArray()));
                return Task.CompletedTask;
            },
            reader: async ct =>
            {
                var (t, p) = await clientToServer.Reader.ReadAsync(ct);
                return (t, (ReadOnlyMemory<byte>)p);
            },
            CancellationToken.None);

        // Both should complete
        var results = await Task.WhenAll(clientTask, serverTask);
        var clientAuth = results[0];
        var serverAuth = results[1];

        // Client sees server identity and vice versa
        Assert.That(clientAuth.LocalIdentity, Is.EqualTo("client-svc"));
        Assert.That(clientAuth.RemoteIdentity, Is.EqualTo("server-svc"));
        Assert.That(clientAuth.AuthType, Is.EqualTo("shm"));
        Assert.That(clientAuth.Nonce, Has.Length.EqualTo(16));

        Assert.That(serverAuth.LocalIdentity, Is.EqualTo("server-svc"));
        Assert.That(serverAuth.RemoteIdentity, Is.EqualTo("client-svc"));
        Assert.That(serverAuth.Nonce, Has.Length.EqualTo(16));
    }

    // ============================================================
    // Server rejects client identity
    // ============================================================

    [Test]
    public async Task Handshake_ServerRejectsClient_ThrowsOnBothSides()
    {
        var clientHs = new ShmSecurityHandshaker("untrusted-client");
        var serverHs = new ShmSecurityHandshaker("server-svc")
        {
            VerifyIdentity = identity =>
            {
                if (!identity.StartsWith("trusted-"))
                    throw new InvalidOperationException("Identity not trusted");
                return Task.CompletedTask;
            }
        };

        var clientToServer = Channel.CreateUnbounded<(FrameType, byte[])>();
        var serverToClient = Channel.CreateUnbounded<(FrameType, byte[])>();

        var clientTask = clientHs.ClientHandshakeAsync(
            writer: (type, payload, ct) => { clientToServer.Writer.TryWrite((type, payload.ToArray())); return Task.CompletedTask; },
            reader: async ct => { var (t, p) = await serverToClient.Reader.ReadAsync(ct); return (t, (ReadOnlyMemory<byte>)p); },
            CancellationToken.None);

        var serverTask = serverHs.ServerHandshakeAsync(
            writer: (type, payload, ct) => { serverToClient.Writer.TryWrite((type, payload.ToArray())); return Task.CompletedTask; },
            reader: async ct => { var (t, p) = await clientToServer.Reader.ReadAsync(ct); return (t, (ReadOnlyMemory<byte>)p); },
            CancellationToken.None);

        // Server should throw ShmHandshakeException(IdentityInvalid)
        var serverEx = Assert.ThrowsAsync<ShmHandshakeException>(async () => await serverTask);
        Assert.That(serverEx!.Code, Is.EqualTo(HandshakeErrorCode.IdentityInvalid));

        // Client should receive the HandshakeFail and throw too
        var clientEx = Assert.ThrowsAsync<ShmHandshakeException>(async () => await clientTask);
        Assert.That(clientEx!.Code, Is.EqualTo(HandshakeErrorCode.IdentityInvalid));
    }

    // ============================================================
    // Client rejects server identity
    // ============================================================

    [Test]
    public async Task Handshake_ClientRejectsServer_ThrowsOnBothSides()
    {
        var clientHs = new ShmSecurityHandshaker("client-svc")
        {
            VerifyIdentity = identity =>
            {
                if (identity != "expected-server")
                    throw new InvalidOperationException("Wrong server");
                return Task.CompletedTask;
            }
        };
        var serverHs = new ShmSecurityHandshaker("wrong-server");

        var clientToServer = Channel.CreateUnbounded<(FrameType, byte[])>();
        var serverToClient = Channel.CreateUnbounded<(FrameType, byte[])>();

        var clientTask = clientHs.ClientHandshakeAsync(
            writer: (type, payload, ct) => { clientToServer.Writer.TryWrite((type, payload.ToArray())); return Task.CompletedTask; },
            reader: async ct => { var (t, p) = await serverToClient.Reader.ReadAsync(ct); return (t, (ReadOnlyMemory<byte>)p); },
            CancellationToken.None);

        var serverTask = serverHs.ServerHandshakeAsync(
            writer: (type, payload, ct) => { serverToClient.Writer.TryWrite((type, payload.ToArray())); return Task.CompletedTask; },
            reader: async ct => { var (t, p) = await clientToServer.Reader.ReadAsync(ct); return (t, (ReadOnlyMemory<byte>)p); },
            CancellationToken.None);

        // Client should throw (identity rejection)
        var clientEx = Assert.ThrowsAsync<ShmHandshakeException>(async () => await clientTask);
        Assert.That(clientEx!.Code, Is.EqualTo(HandshakeErrorCode.IdentityInvalid));

        // Server receives HandshakeFail instead of HandshakeAck
        var serverEx = Assert.ThrowsAsync<ShmHandshakeException>(async () => await serverTask);
        Assert.That(serverEx!.Code, Is.EqualTo(HandshakeErrorCode.IdentityInvalid));
    }

    // ============================================================
    // Cancellation
    // ============================================================

    [Test]
    public void Handshake_Cancellation_ThrowsOperationCanceled()
    {
        var hs = new ShmSecurityHandshaker("test");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Reader immediately throws because token is cancelled
        Assert.That(async () => await hs.ClientHandshakeAsync(
            writer: (_, _, ct) => Task.CompletedTask,
            reader: (ct) => { ct.ThrowIfCancellationRequested(); return default; },
            cts.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    // ============================================================
    // HandshakeErrorCode enum values match Go
    // ============================================================

    [Test]
    public void HandshakeErrorCode_ValuesMatchGo()
    {
        Assert.That((byte)HandshakeErrorCode.None, Is.EqualTo(0));
        Assert.That((byte)HandshakeErrorCode.VersionMismatch, Is.EqualTo(1));
        Assert.That((byte)HandshakeErrorCode.IdentityInvalid, Is.EqualTo(2));
        Assert.That((byte)HandshakeErrorCode.NonceMismatch, Is.EqualTo(3));
        Assert.That((byte)HandshakeErrorCode.Timeout, Is.EqualTo(4));
        Assert.That((byte)HandshakeErrorCode.Internal, Is.EqualTo(5));
    }

    // ============================================================
    // FrameType handshake values
    // ============================================================

    [Test]
    public void FrameType_HandshakeValues_MatchGo()
    {
        Assert.That((byte)FrameType.HandshakeInit, Is.EqualTo(0x20));
        Assert.That((byte)FrameType.HandshakeResp, Is.EqualTo(0x21));
        Assert.That((byte)FrameType.HandshakeAck, Is.EqualTo(0x22));
        Assert.That((byte)FrameType.HandshakeFail, Is.EqualTo(0x23));
    }

    // ============================================================
    // ShmHandshakeException
    // ============================================================

    [Test]
    public void ShmHandshakeException_PropertiesAreSet()
    {
        var ex = new ShmHandshakeException(HandshakeErrorCode.Timeout, "timed out");
        Assert.That(ex.Code, Is.EqualTo(HandshakeErrorCode.Timeout));
        Assert.That(ex.Message, Is.EqualTo("timed out"));
    }

    [Test]
    public void ShmHandshakeException_WithInnerException()
    {
        var inner = new IOException("pipe broken");
        var ex = new ShmHandshakeException(HandshakeErrorCode.Internal, "failed", inner);
        Assert.That(ex.InnerException, Is.SameAs(inner));
    }

    // ============================================================
    // Mutual verification passes
    // ============================================================

    [Test]
    public async Task Handshake_MutualVerification_BothAccept()
    {
        var clientHs = new ShmSecurityHandshaker("trusted-client")
        {
            VerifyIdentity = identity =>
            {
                if (!identity.StartsWith("trusted-"))
                    throw new InvalidOperationException("Not trusted");
                return Task.CompletedTask;
            }
        };
        var serverHs = new ShmSecurityHandshaker("trusted-server")
        {
            VerifyIdentity = identity =>
            {
                if (!identity.StartsWith("trusted-"))
                    throw new InvalidOperationException("Not trusted");
                return Task.CompletedTask;
            }
        };

        var clientToServer = Channel.CreateUnbounded<(FrameType, byte[])>();
        var serverToClient = Channel.CreateUnbounded<(FrameType, byte[])>();

        var clientTask = clientHs.ClientHandshakeAsync(
            writer: (type, payload, ct) => { clientToServer.Writer.TryWrite((type, payload.ToArray())); return Task.CompletedTask; },
            reader: async ct => { var (t, p) = await serverToClient.Reader.ReadAsync(ct); return (t, (ReadOnlyMemory<byte>)p); },
            CancellationToken.None);

        var serverTask = serverHs.ServerHandshakeAsync(
            writer: (type, payload, ct) => { serverToClient.Writer.TryWrite((type, payload.ToArray())); return Task.CompletedTask; },
            reader: async ct => { var (t, p) = await clientToServer.Reader.ReadAsync(ct); return (t, (ReadOnlyMemory<byte>)p); },
            CancellationToken.None);

        var results = await Task.WhenAll(clientTask, serverTask);

        Assert.That(results[0].RemoteIdentity, Is.EqualTo("trusted-server"));
        Assert.That(results[1].RemoteIdentity, Is.EqualTo("trusted-client"));
    }

    // ============================================================
    // Unexpected frame type handling
    // ============================================================

    [Test]
    public void ServerHandshake_UnexpectedInitFrame_Throws()
    {
        var hs = new ShmSecurityHandshaker("server");

        Assert.That(async () => await hs.ServerHandshakeAsync(
            writer: (_, _, _) => Task.CompletedTask,
            reader: _ => Task.FromResult<(FrameType, ReadOnlyMemory<byte>)>(
                (FrameType.Message, ReadOnlyMemory<byte>.Empty)),
            CancellationToken.None),
            Throws.InstanceOf<ShmHandshakeException>()
                .With.Property("Code").EqualTo(HandshakeErrorCode.Internal));
    }

    [Test]
    public void ClientHandshake_UnexpectedRespFrame_Throws()
    {
        var hs = new ShmSecurityHandshaker("client");

        Assert.That(async () => await hs.ClientHandshakeAsync(
            writer: (_, _, _) => Task.CompletedTask,
            reader: _ => Task.FromResult<(FrameType, ReadOnlyMemory<byte>)>(
                (FrameType.Message, ReadOnlyMemory<byte>.Empty)),
            CancellationToken.None),
            Throws.InstanceOf<ShmHandshakeException>()
                .With.Property("Code").EqualTo(HandshakeErrorCode.Internal));
    }
}
