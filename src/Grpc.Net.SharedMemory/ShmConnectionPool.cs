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

using System.Diagnostics;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Manages a pool of <see cref="ShmConnection"/> instances for a single endpoint,
/// analogous to <c>HttpConnectionPool</c> in <c>dotnet/runtime</c>.
/// <para>
/// When <see cref="ShmClientTransportOptions.EnableMultipleConnections"/> is <c>true</c>
/// (the default), the pool automatically creates additional connections when all
/// existing connections have exhausted their stream capacity, mirroring
/// <see cref="System.Net.Http.SocketsHttpHandler.EnableMultipleHttp2Connections"/>.
/// </para>
/// </summary>
internal sealed class ShmConnectionPool : IAsyncDisposable
{
    private readonly ShmClientTransportOptions _options;
    private readonly Func<CancellationToken, Task<ShmConnection>> _connectionFactory;
    private readonly List<ShmPooledConnection> _connections;
    private readonly object _syncObj = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly CancellationTokenSource _factoryCts = new();
    private Timer? _cleanupTimer;
    private int _disposed; // 0 = active, 1 = disposed; atomic via Interlocked

    // Cached reference to the first (and often only) connection for lock-free
    // fast path. Updated under _syncObj when connections are added/removed.
    private volatile ShmPooledConnection? _cachedFirstConnection;

    // Signal for waiters: instead of accumulating SemaphoreSlim permits (which
    // causes spin-loops on false wakeups), we use a TaskCompletionSource that
    // gets replaced on each signal. Waiters observe the current TCS and await
    // it; signalers complete the current TCS and install a fresh one.
    private TaskCompletionSource _streamAvailableTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Tracks how many callers are blocked in GetConnectionSlowAsync waiting for
    // a stream slot. When zero, SignalStreamAvailable can skip the lock+TCS
    // allocation entirely — this is the critical optimization for low-concurrency
    // workloads where stream slots are always available.
    private int _waiterCount;

    // Tracks fire-and-forget dispose tasks so DisposeAsync can await them.
    private readonly List<Task> _pendingDisposeTasks = new();

    // Diagnostics
    private long _connectionsCreated;
    private long _connectionsClosed;

    /// <summary>
    /// Schedules an async dispose and tracks the resulting task so
    /// <see cref="DisposeAsync"/> can await it. Centralises the fire-and-forget
    /// + error-observation pattern used in cleanup, purge, and connection-closed paths.
    /// </summary>
    private Task TrackDisposeAsync(ShmPooledConnection connection, string caller)
    {
        var task = connection.DisposeAsync().AsTask().ContinueWith(
            static (t, state) => Debug.WriteLineIf(t.IsFaulted,
                $"ShmConnectionPool [{state}]: dispose error: {t.Exception?.Message}"),
            caller,
            TaskScheduler.Default);

        lock (_syncObj)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _pendingDisposeTasks.Add(task);
            }
        }

        return task;
    }

    /// <summary>
    /// Creates a new connection pool.
    /// </summary>
    /// <param name="options">Transport options controlling pool behavior.</param>
    /// <param name="connectionFactory">
    /// Async factory that establishes a new <see cref="ShmConnection"/>.
    /// For <see cref="ShmControlHandler"/> this performs the control-segment handshake.
    /// The factory is always called under <c>_connectLock</c> to ensure SPSC ring safety.
    /// </param>
    public ShmConnectionPool(
        ShmClientTransportOptions options,
        Func<CancellationToken, Task<ShmConnection>> connectionFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _connections = new List<ShmPooledConnection>();

        // Start idle cleanup timer (every 30 seconds)
        _cleanupTimer = new Timer(
            _ => CleanupIdleConnections(),
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Gets the number of connections currently in the pool.
    /// </summary>
    public int ConnectionCount
    {
        get { lock (_syncObj) { return _connections.Count; } }
    }

    /// <summary>
    /// Gets the total number of connections created during the pool's lifetime.
    /// </summary>
    public long TotalConnectionsCreated => Interlocked.Read(ref _connectionsCreated);

    /// <summary>
    /// Gets the total number of connections closed during the pool's lifetime.
    /// </summary>
    public long TotalConnectionsClosed => Interlocked.Read(ref _connectionsClosed);

    /// <summary>
    /// Synchronous fast path: tries to return a connection with available capacity
    /// without any async machinery. Returns <c>true</c> on success (~95% of calls
    /// at low/medium concurrency).
    /// <para>
    /// This eliminates the <c>ValueTask</c> → <c>await</c> bridge cost in the caller's
    /// <c>async Task&lt;T&gt;</c> method, which otherwise allocates an async state machine
    /// even when the <c>ValueTask</c> completes synchronously.
    /// </para>
    /// </summary>
    /// <param name="connection">The available connection, or <c>null</c> if none found.</param>
    /// <returns><c>true</c> if a connection with available stream capacity was found.</returns>
    public bool TryGetConnection([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ShmPooledConnection? connection)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            connection = null;
            return false;
        }

        // Lock-free fast path: cached first connection.
        var cached = _cachedFirstConnection;
        if (cached != null && cached.IsAvailable)
        {
            connection = cached;
            return true;
        }

        // Medium path: scan under lock.
        connection = TryGetAvailableConnection();
        return connection != null;
    }

    /// <summary>
    /// Gets a connection with available stream capacity, creating a new one if needed.
    /// Analogous to <c>HttpConnectionPool.GetHttp2ConnectionAsync()</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A pooled connection with at least one available stream slot.</returns>
    /// <exception cref="TimeoutException">
    /// Thrown when all connections are at capacity, <see cref="ShmClientTransportOptions.MaxConnectionCount"/>
    /// is reached, and no stream slot becomes available within <see cref="ShmClientTransportOptions.ConnectTimeout"/>.
    /// </exception>
    public ValueTask<ShmPooledConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // Lock-free fast path: if the cached first connection has capacity,
        // return it immediately. No lock, no list scan, no allocation.
        // This covers the common case of a single connection with available streams.
        var cached = _cachedFirstConnection;
        if (cached != null && cached.IsAvailable)
        {
            return new ValueTask<ShmPooledConnection>(cached);
        }

        // Medium path: scan the connection list under lock.
        var conn = TryGetAvailableConnection();
        if (conn != null)
        {
            return new ValueTask<ShmPooledConnection>(conn);
        }

        // Slow path: need to create a new connection or wait for capacity.
        return new ValueTask<ShmPooledConnection>(GetConnectionSlowAsync(cancellationToken));
    }

    /// <summary>
    /// Scans the connection list for one with available stream capacity.
    /// Selects the least-loaded connection (most <see cref="ShmPooledConnection.AvailableStreams"/>).
    /// </summary>
    private ShmPooledConnection? TryGetAvailableConnection()
    {
        lock (_syncObj)
        {
            return TryGetAvailableConnectionLocked();
        }
    }

    /// <summary>
    /// Same as <see cref="TryGetAvailableConnection"/> but caller must already hold <c>_syncObj</c>.
    /// </summary>
    private ShmPooledConnection? TryGetAvailableConnectionLocked()
    {
        ShmPooledConnection? best = null;
        var bestAvailable = 0;

        foreach (var conn in _connections)
        {
            if (!conn.IsAvailable)
            {
                continue;
            }

            var available = conn.AvailableStreams;
            if (available > bestAvailable)
            {
                best = conn;
                bestAvailable = available;
            }
        }

        return best;
    }

    /// <summary>
    /// Updates the cached first-connection reference after the connection list changes.
    /// Must be called under <c>lock (_syncObj)</c>.
    /// </summary>
    private void UpdateCachedConnectionLocked()
    {
        _cachedFirstConnection = _connections.Count > 0 ? _connections[0] : null;
    }

    /// <summary>
    /// Slow path: either creates a new connection or waits for a stream slot.
    /// </summary>
    private async Task<ShmPooledConnection> GetConnectionSlowAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(_options.ConnectTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var ct = linkedCts.Token;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            // Re-check after potential wait.
            var conn = TryGetAvailableConnection();
            if (conn != null)
            {
                return conn;
            }

            // Determine if we can create a new connection.
            bool canCreate;
            lock (_syncObj)
            {
                // Remove any closed connections while we're here.
                PurgeClosedConnectionsLocked();

                var activeCount = _connections.Count;

                if (_options.MaxConnectionCount > 0 && activeCount >= _options.MaxConnectionCount)
                {
                    // At capacity limit.
                    canCreate = false;
                }
                else
                {
                    canCreate = true;
                }
            }

            if (canCreate)
            {
                var newConn = await CreateConnectionAsync(ct).ConfigureAwait(false);
                if (newConn != null)
                {
                    return newConn;
                }

                // Another thread may have created a connection; retry selection.
                continue;
            }

            // All connections are full and we can't create more — wait for a stream slot.
            // Capture the current TCS *before* re-checking, then await it.
            // This avoids the SemaphoreSlim permit-accumulation problem: we only
            // wait once per signal, and each signal wakes all current waiters.
            //
            // IMPORTANT: increment _waiterCount BEFORE capturing the TCS task so
            // that SignalStreamAvailable sees the waiter even if a stream completes
            // between the increment and the lock acquisition.
            Interlocked.Increment(ref _waiterCount);
            try
            {
                Task waitTask;
                lock (_syncObj)
                {
                    // Re-check disposed under lock after registering as a waiter.
                    // This closes the race where DisposeAsync sets _disposed=1 and
                    // calls SignalStreamAvailable (which sees _waiterCount==0 and
                    // returns) between our disposed check at the top of the loop
                    // and the Increment above.
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

                    // Final check under lock to avoid missed-wakeup race.
                    var lastChance = TryGetAvailableConnectionLocked();
                    if (lastChance != null)
                    {
                        return lastChance;
                    }
                    waitTask = _streamAvailableTcs.Task;
                }

                await waitTask.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                throw new ObjectDisposedException(nameof(ShmConnectionPool));
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Timed out after {_options.ConnectTimeout.TotalSeconds:F0}s waiting for an available " +
                    $"shared memory connection stream slot. All {ConnectionCount} connection(s) are at capacity.");
            }
            finally
            {
                Interlocked.Decrement(ref _waiterCount);
            }
        }
    }

    /// <summary>
    /// Creates a new connection via the connection factory.
    /// Serialized by <c>_connectLock</c> to:
    /// 1. Prevent N threads from creating N connections simultaneously.
    /// 2. Protect SPSC ring semantics on the control segment.
    /// Returns <c>null</c> if another thread already created a suitable connection.
    /// </summary>
    private async Task<ShmPooledConnection?> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Abort if the pool was disposed while we waited for the lock.
            // Without this check, a connection created during shutdown would be
            // added to _connections after DisposeAsync has already drained the
            // list, causing a resource leak.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            // Double-check: another thread may have created a connection while we waited.
            var existing = TryGetAvailableConnection();
            if (existing != null)
            {
                return existing;
            }

            // Re-check capacity under lock.
            lock (_syncObj)
            {
                if (_options.MaxConnectionCount > 0 && _connections.Count >= _options.MaxConnectionCount)
                {
                    return null; // Caller will wait on _streamAvailableTcs.
                }
            }

            // Link the caller's token with _factoryCts so DisposeAsync can
            // immediately cancel in-flight factory calls. This prevents the
            // SPSC ring violation where overlapping factory calls (from an
            // old pool being disposed and a new pool starting) both write to
            // the same control segment ring buffer concurrently.
            using var linkedFactoryCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _factoryCts.Token);
            var connection = await _connectionFactory(linkedFactoryCts.Token).ConfigureAwait(false);

            // Re-check disposed after the potentially long factory call.
            // If disposed, clean up the just-created connection immediately.
            // Cannot use ObjectDisposedException.ThrowIf here because we must
            // dispose the connection before throwing.
            if (Volatile.Read(ref _disposed) != 0)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
#pragma warning disable CA1513 // ThrowIf cannot be used: must dispose connection first
                throw new ObjectDisposedException(nameof(ShmConnectionPool));
#pragma warning restore CA1513
            }

            var pooled = new ShmPooledConnection(connection, this);

            bool addedToPool;
            lock (_syncObj)
            {
                // Final disposed check inside the lock that guards _connections.
                // Without this, DisposeAsync can complete _connections.Clear()
                // between our lock-free check above and this Add, causing the
                // new connection to be added after the dispose snapshot was taken
                // — a resource leak.
                if (Volatile.Read(ref _disposed) != 0)
                {
                    addedToPool = false;
                }
                else
                {
                    _connections.Add(pooled);
                    UpdateCachedConnectionLocked();
                    addedToPool = true;
                }
            }

            if (!addedToPool)
            {
                await pooled.DisposeAsync().ConfigureAwait(false);
#pragma warning disable CA1513
                throw new ObjectDisposedException(nameof(ShmConnectionPool));
#pragma warning restore CA1513
            }

            Interlocked.Increment(ref _connectionsCreated);
            return pooled;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// Gets whether any callers are blocked waiting for a stream slot.
    /// Exposed for diagnostics; the <see cref="SignalStreamAvailable"/> method
    /// already checks <c>_waiterCount</c> internally.
    /// </summary>
    internal bool HasWaiters => Volatile.Read(ref _waiterCount) > 0;

    /// <summary>
    /// Synchronously cancels any in-flight connection factory calls.
    /// Called by <see cref="ShmControlHandler.Dispose(bool)"/> to immediately
    /// unblock ring reads/writes on the control segment before the handler
    /// goes out of scope. This prevents SPSC violations when a new handler
    /// is created for the same segment name while the old one is still
    /// finishing factory work.
    /// </summary>
    internal void CancelPendingConnections()
    {
        // Guard: _factoryCts may already be disposed if DisposeAsync completed
        // before this call arrives from ShmControlHandler.Dispose. Cancel() on
        // a disposed CTS throws ObjectDisposedException.
        try { _factoryCts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Called by <see cref="ShmPooledConnection"/> when a stream completes.
    /// Wakes up any <see cref="GetConnectionAsync"/> callers waiting for capacity.
    /// </summary>
    internal void OnStreamCompleted()
    {
        SignalStreamAvailable();
    }

    /// <summary>
    /// Called by <see cref="ShmPooledConnection"/> when a GoAway is received.
    /// Ensures waiting callers can proceed (they'll see the draining connection
    /// and create a new one).
    /// </summary>
    internal void OnConnectionDraining(ShmPooledConnection connection)
    {
        SignalStreamAvailable();
    }

    /// <summary>
    /// Called by <see cref="ShmPooledConnection"/> when a draining connection
    /// has zero remaining streams and transitions to Closed.
    /// Synchronously removes from the list; async dispose is tracked for clean shutdown.
    /// </summary>
    internal void OnConnectionClosed(ShmPooledConnection connection)
    {
        lock (_syncObj)
        {
            _connections.Remove(connection);
            UpdateCachedConnectionLocked();
        }

        Interlocked.Increment(ref _connectionsClosed);
        SignalStreamAvailable();

        // Track the dispose task so DisposeAsync can await it.
        TrackDisposeAsync(connection, nameof(OnConnectionClosed));
    }

    /// <summary>
    /// Completes the current <see cref="_streamAvailableTcs"/> (waking all current waiters)
    /// and installs a fresh one for future waiters.
    /// <para>
    /// Optimized: when no callers are blocked in <see cref="GetConnectionSlowAsync"/>
    /// (i.e. <c>_waiterCount == 0</c>), the lock, TCS allocation, and completion
    /// are skipped entirely. This eliminates the main per-request overhead at low
    /// concurrency where stream slots are always available.
    /// </para>
    /// </summary>
    private void SignalStreamAvailable()
    {
        // Fast exit: nobody is waiting — skip lock + TCS allocation.
        if (Volatile.Read(ref _waiterCount) == 0)
        {
            return;
        }

        TaskCompletionSource oldTcs;
        lock (_syncObj)
        {
            oldTcs = _streamAvailableTcs;
            _streamAvailableTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        oldTcs.TrySetResult();
    }

    /// <summary>
    /// Removes idle connections that have exceeded <see cref="ShmClientTransportOptions.PooledConnectionIdleTimeout"/>.
    /// Always keeps at least one connection alive.
    /// </summary>
    private void CleanupIdleConnections()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        List<ShmPooledConnection>? toDispose = null;

        lock (_syncObj)
        {
            if (_connections.Count <= 1)
            {
                return; // Always keep at least one connection.
            }

            var nowTick = Environment.TickCount64;
            var idleThresholdMs = (long)_options.PooledConnectionIdleTimeout.TotalMilliseconds;

            for (var i = _connections.Count - 1; i >= 0; i--)
            {
                // Always keep at least one connection.
                if (_connections.Count <= 1)
                {
                    break;
                }

                var conn = _connections[i];
                if (conn.ActiveStreamCount == 0
                    && conn.State == ShmConnectionState.Active
                    && (nowTick - conn.LastUsedTick) > idleThresholdMs)
                {
                    conn.State = ShmConnectionState.Closed;
                    _connections.RemoveAt(i);
                    toDispose ??= new List<ShmPooledConnection>();
                    toDispose.Add(conn);
                }
            }

            if (toDispose != null)
            {
                UpdateCachedConnectionLocked();
            }
        }

        if (toDispose != null)
        {
            foreach (var conn in toDispose)
            {
                Interlocked.Increment(ref _connectionsClosed);
                TrackDisposeAsync(conn, nameof(CleanupIdleConnections));
            }
        }

        // Prune completed dispose tasks to prevent unbounded list growth.
        lock (_syncObj)
        {
            _pendingDisposeTasks.RemoveAll(static t => t.IsCompleted);
        }
    }

    /// <summary>
    /// Removes connections in <see cref="ShmConnectionState.Closed"/> state from the list.
    /// Must be called under <c>lock (_syncObj)</c>.
    /// </summary>
    private void PurgeClosedConnectionsLocked()
    {
        for (var i = _connections.Count - 1; i >= 0; i--)
        {
            var conn = _connections[i];
            if (conn.IsClosed || conn.State == ShmConnectionState.Closed)
            {
                _connections.RemoveAt(i);
                Interlocked.Increment(ref _connectionsClosed);
                TrackDisposeAsync(conn, nameof(PurgeClosedConnectionsLocked));
            }
        }

        UpdateCachedConnectionLocked();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Cancel in-flight connection factory calls immediately.
        // This unblocks any ring read/write blocked in ConnectViaControlSegmentAsync,
        // preventing the SPSC ring violation where overlapping factory calls from
        // old/new pools write to the same control segment concurrently.
        _factoryCts.Cancel();

        // Signal all waiters so they see _disposed and exit.
        SignalStreamAvailable();

        // Stop cleanup timer.
        if (_cleanupTimer != null)
        {
            await _cleanupTimer.DisposeAsync().ConfigureAwait(false);
            _cleanupTimer = null;
        }

        // Dispose all connections.
        List<ShmPooledConnection> snapshot;
        List<Task> pendingDisposes;
        lock (_syncObj)
        {
            snapshot = new List<ShmPooledConnection>(_connections);
            _connections.Clear();
            _cachedFirstConnection = null;
            pendingDisposes = new List<Task>(_pendingDisposeTasks);
            _pendingDisposeTasks.Clear();
        }

        foreach (var conn in snapshot)
        {
            try
            {
                await conn.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ShmConnectionPool: Error disposing connection during pool shutdown: {ex.Message}");
            }
        }

        // Await any in-flight dispose tasks from OnConnectionClosed.
        if (pendingDisposes.Count > 0)
        {
            try
            {
                await Task.WhenAll(pendingDisposes).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ShmConnectionPool: Error awaiting pending disposes: {ex.Message}");
            }
        }

        // Wait for any in-flight CreateConnectionAsync to exit before returning.
        // _factoryCts was already cancelled above, so a well-behaved factory
        // (one that observes its CancellationToken) will unblock promptly.
        // The 3-second timeout is a best-effort safeguard: if the factory
        // ignores cancellation, DisposeAsync will still return rather than
        // hang indefinitely. Callers that need a hard guarantee should ensure
        // their factory respects the token.
        var lockAcquired = false;
        try
        {
            lockAcquired = await _connectLock.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch { /* Semaphore may be disposed or faulted — proceed with cleanup. */ }
        finally
        {
            if (lockAcquired)
            {
                try { _connectLock.Release(); } catch { }
            }
        }

        _factoryCts.Dispose();

        // _connectLock is not disposed here. SemaphoreSlim.Dispose on a semaphore
        // that may still have waiters (from CreateConnectionAsync's finally block)
        // leads to undefined behavior. The semaphore holds no unmanaged resources,
        // so letting the GC collect it is safe and avoids the "dispose before Release"
        // race between CreateConnectionAsync's finally and DisposeAsync.
    }
}
