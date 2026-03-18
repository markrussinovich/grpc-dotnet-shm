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
/// Connection state within the pool, mirroring HTTP/2 connection lifecycle.
/// </summary>
internal enum ShmConnectionState
{
    /// <summary>Connection is healthy and available for new streams.</summary>
    Active,

    /// <summary>
    /// Connection received GoAway — no new streams will be allocated,
    /// but existing streams can complete.
    /// </summary>
    Draining,

    /// <summary>Connection is closed and will be removed from the pool.</summary>
    Closed
}

/// <summary>
/// A pooled wrapper around <see cref="ShmConnection"/> that tracks pool-relevant
/// metadata (state, last-used time). Analogous to <c>Http2Connection</c> inside
/// <c>HttpConnectionPool</c> in dotnet/runtime.
/// </summary>
internal sealed class ShmPooledConnection
{
    private readonly ShmConnection _connection;
    private readonly ShmConnectionPool _pool;
    private int _stateValue; // Backing field for atomic CAS; use State property
    private long _lastUsedTick; // Environment.TickCount64 for cheap monotonic timestamp
    private int _disposed;

    // Cached availability flag: set to false only on GoAway / Close transitions.
    // In normal operation, the connection is almost always active and not closed,
    // so _active short-circuits the more expensive State + IsClosed volatile reads
    // on every TryGetConnection call. The AvailableStreams check is still needed.
    private volatile bool _active = true;

    public ShmPooledConnection(ShmConnection connection, ShmConnectionPool pool)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _stateValue = (int)ShmConnectionState.Active;
        _lastUsedTick = Environment.TickCount64;

        // Subscribe to GoAway so the pool can react.
        _connection.GoAwayReceived += OnGoAwayReceived;

        // Subscribe to stream removal so the pool can wake waiters.
        _connection.StreamRemoved += OnStreamRemoved;
    }

    /// <summary>Gets the underlying <see cref="ShmConnection"/>.</summary>
    public ShmConnection Connection => _connection;

    /// <summary>Gets the number of currently active streams.</summary>
    public int ActiveStreamCount => _connection.ActiveStreamCount;

    /// <summary>
    /// Gets the maximum number of concurrent streams allowed.
    /// Mirrors <c>KestrelServerLimits.Http2.MaxStreamsPerConnection</c>.
    /// </summary>
    public uint MaxConcurrentStreams => _connection.MaxConcurrentStreams;

    /// <summary>Gets the number of additional streams that can be created.</summary>
    public int AvailableStreams => _connection.AvailableStreams;

    /// <summary>Gets whether the underlying connection has been closed.</summary>
    public bool IsClosed => _connection.IsClosed || State == ShmConnectionState.Closed;

    /// <summary>Gets whether the connection is draining (GoAway received).</summary>
    public bool IsDraining => _connection.IsDraining || State == ShmConnectionState.Draining;

    /// <summary>Gets or sets the connection state (thread-safe via Interlocked).</summary>
    public ShmConnectionState State
    {
        get => (ShmConnectionState)Volatile.Read(ref _stateValue);
        set
        {
            Volatile.Write(ref _stateValue, (int)value);
            if (value != ShmConnectionState.Active)
            {
                _active = false;
            }
        }
    }

    /// <summary>
    /// Gets the monotonic tick (from <see cref="Environment.TickCount64"/>) when
    /// this connection last created a stream. Used by the idle cleanup timer.
    /// </summary>
    public long LastUsedTick => Volatile.Read(ref _lastUsedTick);

    /// <summary>
    /// Creates a new stream on the underlying connection.
    /// Updates the last-used timestamp only when activating an idle connection
    /// (transitioning from 0 to 1 active streams). The 30-second idle cleanup
    /// timer does not need per-RPC precision.
    /// </summary>
    /// <returns>A new <see cref="ShmGrpcStream"/>.</returns>
    /// <exception cref="ShmStreamCapacityExceededException">
    /// Thrown if the connection was closed by the idle cleanup timer between
    /// <see cref="ShmConnectionPool.TryGetConnection"/> and this call.
    /// </exception>
    public ShmGrpcStream CreateStream()
    {
        // Guard against the race where idle cleanup marks this connection
        // as Closed (setting _active = false) between TryGetConnection
        // returning it and the caller invoking CreateStream.
        if (!_active)
        {
            throw new ShmStreamCapacityExceededException(
                "Connection was closed by idle cleanup before stream could be created");
        }

        // Only update tick when going from idle → active. This avoids a
        // Volatile.Write + Environment.TickCount64 syscall on every RPC.
        if (_connection.ActiveStreamCount == 0)
        {
            Volatile.Write(ref _lastUsedTick, Environment.TickCount64);
        }

        return _connection.CreateStream();
    }

    /// <summary>
    /// Returns <c>true</c> if this connection can accept at least one more stream
    /// and is in <see cref="ShmConnectionState.Active"/> state.
    /// <para>
    /// Optimized: <c>_active</c> caches the State + IsClosed check, avoiding
    /// multiple volatile reads on the hot path. It is set to <c>false</c> on
    /// GoAway/Close/Dispose transitions. <see cref="ShmConnection.IsClosed"/>
    /// is still checked as a safety net for unexpected connection failures.
    /// </para>
    /// </summary>
    public bool IsAvailable => _active
        && !_connection.IsClosed
        && _connection.AvailableStreams > 0;

    private void OnGoAwayReceived(object? sender, GoAwayEventArgs e)
    {
        // Transition to Draining — pool will no longer allocate new streams here.
        // CAS first, then update _active cache; both paths converge on _active = false
        // because State setter handles it.
        if (Interlocked.CompareExchange(ref _stateValue, (int)ShmConnectionState.Draining, (int)ShmConnectionState.Active) == (int)ShmConnectionState.Active)
        {
            _active = false;
        }

        _pool.OnConnectionDraining(this);
    }

    private void OnStreamRemoved(uint streamId)
    {
        // Notify the pool so it can wake any waiters blocked in GetConnectionSlowAsync.
        // SignalStreamAvailable internally checks _waiterCount == 0 and short-circuits,
        // so there is no need for a redundant check here.
        _pool.OnStreamCompleted();

        // Draining check: only when connection is draining (GoAway received),
        // which is extremely rare during normal operation.
        if (!_active && _connection.ActiveStreamCount == 0)
        {
            if (Interlocked.CompareExchange(ref _stateValue, (int)ShmConnectionState.Closed, (int)ShmConnectionState.Draining) == (int)ShmConnectionState.Draining)
            {
                _pool.OnConnectionClosed(this);
            }
        }
    }

    /// <summary>
    /// Unsubscribes from connection events and disposes the underlying connection.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // State setter already sets _active = false for non-Active values.
        State = ShmConnectionState.Closed;
        _connection.GoAwayReceived -= OnGoAwayReceived;
        _connection.StreamRemoved -= OnStreamRemoved;
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
