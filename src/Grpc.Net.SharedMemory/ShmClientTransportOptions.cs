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
/// Options for configuring the shared memory transport on the client side.
/// Mirrors <see cref="System.Net.Http.SocketsHttpHandler"/> naming conventions where applicable.
/// </summary>
/// <example>
/// <code>
/// var handler = new ShmControlHandler("my_segment", new ShmClientTransportOptions
/// {
///     EnableMultipleConnections = true,
///     MaxConnectionCount = 8,
///     RingCapacity = 16 * 1024 * 1024 // 16MB for small-message workloads
/// });
/// </code>
/// </example>
public sealed class ShmClientTransportOptions
{
    /// <summary>
    /// Gets or sets whether the connection pool is enabled with support for multiple
    /// connections. Mirrors <see cref="System.Net.Http.SocketsHttpHandler.EnableMultipleHttp2Connections"/>.
    /// <para>
    /// When <c>true</c> (default), the handler uses <see cref="ShmConnectionPool"/> to
    /// manage connections. The pool automatically creates additional connections when
    /// stream capacity is exhausted, performs idle cleanup, and load-balances across
    /// connections by available stream slots.
    /// </para>
    /// <para>
    /// When <c>false</c>, the handler bypasses the pool entirely and holds a single
    /// <see cref="ShmConnection"/> directly — matching the pre-pool behavior.
    /// This eliminates all pool overhead on the hot path. <see cref="MaxConnectionCount"/>
    /// and <see cref="PooledConnectionIdleTimeout"/> are ignored in this mode.
    /// </para>
    /// Default is <c>true</c>.
    /// </summary>
    public bool EnableMultipleConnections { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of shared memory connections to maintain.
    /// <c>0</c> means no limit (pool grows dynamically and shrinks via idle cleanup).
    /// <para>
    /// Each connection consumes <see cref="RingCapacity"/> × 2 bytes of shared memory
    /// (one ring per direction). Set this to a reasonable limit to control memory usage.
    /// </para>
    /// Default is <c>0</c>.
    /// </summary>
    public int MaxConnectionCount
    {
        get => _maxConnectionCount;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _maxConnectionCount = value;
        }
    }
    private int _maxConnectionCount;

    /// <summary>
    /// Gets or sets the preferred ring buffer capacity in bytes for each connection.
    /// Must be a power of 2.
    /// <para>
    /// The client sends this value in the CONNECT request. The server negotiates the
    /// actual capacity as <c>Min(clientPreferred, serverMax)</c>. A value of <c>0</c>
    /// means the client has no preference and the server will use its default.
    /// </para>
    /// <para>
    /// Smaller rings improve CPU cache locality — a 4 MiB ring fits in most L3 caches,
    /// while a 64 MiB ring causes TLB thrashing and frequent cache evictions. Use larger
    /// rings only for workloads with very large messages (e.g., &gt;1 MiB) or high pipeline depth.
    /// </para>
    /// Default is 64 MiB (67,108,864 bytes).
    /// </summary>
    public ulong RingCapacity { get; set; } = 64 * 1024 * 1024;

    /// <summary>
    /// Gets or sets how long an idle connection can remain in the pool before being closed.
    /// At least one connection is always kept alive regardless of this setting.
    /// Mirrors <see cref="System.Net.Http.SocketsHttpHandler.PooledConnectionIdleTimeout"/>.
    /// <para>
    /// Default is 2 minutes.
    /// </para>
    /// </summary>
    public TimeSpan PooledConnectionIdleTimeout
    {
        get => _pooledConnectionIdleTimeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _pooledConnectionIdleTimeout = value;
        }
    }
    private TimeSpan _pooledConnectionIdleTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets or sets the timeout for establishing a new shared memory connection
    /// via the control segment protocol.
    /// Mirrors <see cref="System.Net.Http.SocketsHttpHandler.ConnectTimeout"/>.
    /// <para>
    /// This timeout also applies when all connections are at capacity and the pool
    /// is waiting for a stream slot to become available.
    /// </para>
    /// Default is 30 seconds.
    /// </summary>
    public TimeSpan ConnectTimeout
    {
        get => _connectTimeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _connectTimeout = value;
        }
    }
    private TimeSpan _connectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets whether to enable single-stream optimizations on the client.
    /// When <c>true</c>, enables zero-copy reads from the ring buffer, eliminating
    /// one memcpy per received message. Only safe when each connection handles at
    /// most one concurrent stream (e.g., serial unary calls or a single streaming call).
    /// <para>
    /// In multi-stream scenarios (concurrent RPCs on the same connection), deferred
    /// ring CommitRead can accumulate in the Channel queue faster than handlers
    /// consume them, causing the remote writer to block on WaitForSpace.
    /// </para>
    /// Default is <c>false</c>.
    /// </summary>
    public bool SingleStreamMode { get; set; }

    /// <summary>
    /// Gets or sets the optional security handshaker. When non-null, the
    /// client performs a process-level identity handshake on the data
    /// segment immediately after the control-segment CONNECT/ACCEPT
    /// completes; the resulting <see cref="ShmAuthInfo"/> is surfaced on
    /// the returned <see cref="ShmConnection.AuthInfo"/>. Mirrors
    /// grpc-go-shmem's <c>credentials/shm</c> <c>Options{Identity,
    /// VerifyIdentity}</c>: the wire-level handshake frames
    /// (HandshakeInit 0x20 / HandshakeResp 0x21 / HandshakeAck 0x22 /
    /// HandshakeFail 0x23) and the identity-token + nonce payload
    /// encoding are bit-identical across the two implementations.
    /// <para>
    /// When <c>null</c> (default) the client skips the handshake and
    /// surfaces no <c>AuthInfo</c> — equivalent to insecure local SHM,
    /// matching the Go default behaviour when no <c>credentials/shm</c>
    /// is supplied. Insecure mode is interop-compatible with Go peers
    /// that also lack a handshaker; mixed modes (one side with a
    /// handshaker, the other without) will hang waiting for the
    /// HandshakeInit frame the silent peer never sends.
    /// </para>
    /// </summary>
    public IShmSecurityHandshaker? Handshaker { get; set; }
}
