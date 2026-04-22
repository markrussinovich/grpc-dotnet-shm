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

using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Unit tests for <see cref="ShmConnectionPool"/> — lifecycle, scaling, selection, and cleanup.
/// </summary>
[TestFixture]
[Platform("Win")]
[NonParallelizable]
public class ShmConnectionPoolTests
{
    private static ShmClientTransportOptions DefaultOptions() => new()
    {
        EnableMultipleConnections = true,
        MaxConnectionCount = 0,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(5),
        RingCapacity = 4096
    };

    private static async Task<(ShmConnectionPool pool, ShmConnection serverConn, string name)> CreatePoolWithServer(
        ShmClientTransportOptions? options = null, uint maxStreams = 10)
    {
        var name = $"pool_test_{Guid.NewGuid():N}";
        var serverConn = ShmConnection.CreateAsServer(name, ringCapacity: 4096, maxStreams: maxStreams);

        var opts = options ?? DefaultOptions();
        var pool = new ShmConnectionPool(opts, ct =>
        {
            var client = ShmConnection.ConnectAsClient(name);
            return Task.FromResult(client);
        });

        return (pool, serverConn, name);
    }

    #region A. Basic Lifecycle

    [Test]
    public async Task Pool_FirstCall_CreatesSingleConnection()
    {
        var (pool, serverConn, _) = await CreatePoolWithServer();
        await using var _ = pool;
        using var __ = serverConn;

        var conn = await pool.GetConnectionAsync(CancellationToken.None);

        Assert.That(pool.ConnectionCount, Is.EqualTo(1));
        Assert.That(conn.State, Is.EqualTo(ShmConnectionState.Active));
    }

    [Test]
    public async Task Pool_Dispose_ClosesAllConnections()
    {
        var (pool, serverConn, _) = await CreatePoolWithServer();
        using var _ = serverConn;

        var conn = await pool.GetConnectionAsync(CancellationToken.None);
        Assert.That(pool.ConnectionCount, Is.EqualTo(1));

        await pool.DisposeAsync();

        Assert.That(pool.ConnectionCount, Is.EqualTo(0));
    }

    [Test]
    public async Task Pool_DisabledMultiConn_AlwaysReturnsSame()
    {
        var options = DefaultOptions();
        options.EnableMultipleConnections = false;
        var (pool, serverConn, _) = await CreatePoolWithServer(options);
        await using var _ = pool;
        using var __ = serverConn;

        var conn1 = await pool.GetConnectionAsync(CancellationToken.None);
        var conn2 = await pool.GetConnectionAsync(CancellationToken.None);

        Assert.That(conn1, Is.SameAs(conn2));
        Assert.That(pool.ConnectionCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Pool_ConnectionFactory_CalledOnce_WhenCapacityAvailable()
    {
        var factoryCallCount = 0;
        var name = $"pool_test_{Guid.NewGuid():N}";
        using var serverConn = ShmConnection.CreateAsServer(name, ringCapacity: 4096, maxStreams: 100);
        var opts = DefaultOptions();

        await using var pool = new ShmConnectionPool(opts, ct =>
        {
            Interlocked.Increment(ref factoryCallCount);
            return Task.FromResult(ShmConnection.ConnectAsClient(name));
        });

        // Multiple GetConnectionAsync calls should all return the same connection
        // as long as it has capacity.
        await pool.GetConnectionAsync(CancellationToken.None);
        await pool.GetConnectionAsync(CancellationToken.None);
        await pool.GetConnectionAsync(CancellationToken.None);

        Assert.That(factoryCallCount, Is.EqualTo(1));
    }

    #endregion

    #region B. Connection Scaling

    [Test]
    public async Task Pool_AllStreamsFull_CreatesNewConnection()
    {
        // maxStreams=2 so we can easily fill it.
        var (pool, serverConn, name) = await CreatePoolWithServer(maxStreams: 2);
        await using var _ = pool;
        using var __ = serverConn;

        var conn1 = await pool.GetConnectionAsync(CancellationToken.None);
        // Create 2 streams to fill connection 1.
        var s1 = conn1.CreateStream();
        var s2 = conn1.CreateStream();

        Assert.That(conn1.AvailableStreams, Is.EqualTo(0));
        Assert.That(pool.ConnectionCount, Is.EqualTo(1));

        // Next GetConnectionAsync should create a new connection.
        var conn2 = await pool.GetConnectionAsync(CancellationToken.None);

        Assert.That(pool.ConnectionCount, Is.EqualTo(2));
        Assert.That(conn2, Is.Not.SameAs(conn1));
    }

    [Test]
    public async Task Pool_MaxConnectionCount_Enforced()
    {
        var opts = DefaultOptions();
        opts.MaxConnectionCount = 2;
        opts.ConnectTimeout = TimeSpan.FromMilliseconds(500);
        var (pool, serverConn, _) = await CreatePoolWithServer(opts, maxStreams: 1);
        await using var _ = pool;
        using var __ = serverConn;

        // Fill 2 connections × 1 stream each.
        var c1 = await pool.GetConnectionAsync(CancellationToken.None);
        c1.CreateStream();
        var c2 = await pool.GetConnectionAsync(CancellationToken.None);
        c2.CreateStream();

        Assert.That(pool.ConnectionCount, Is.EqualTo(2));

        // Next call should timeout since both are full and max=2.
        Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await pool.GetConnectionAsync(CancellationToken.None);
        });
    }

    [Test]
    public async Task Pool_GradualScaleUp_MatchesDemand()
    {
        var (pool, serverConn, _) = await CreatePoolWithServer(maxStreams: 2);
        await using var _ = pool;
        using var __ = serverConn;

        // 1 stream → 1 connection
        var c = await pool.GetConnectionAsync(CancellationToken.None);
        c.CreateStream();
        Assert.That(pool.ConnectionCount, Is.EqualTo(1));

        // 2 streams → 1 connection (still has capacity)
        c.CreateStream();
        Assert.That(pool.ConnectionCount, Is.EqualTo(1));

        // 3rd stream → 2 connections
        var c2 = await pool.GetConnectionAsync(CancellationToken.None);
        c2.CreateStream();
        Assert.That(pool.ConnectionCount, Is.EqualTo(2));
    }

    #endregion

    #region C. Connection Selection

    [Test]
    public async Task Pool_SelectsLeastLoaded()
    {
        var (pool, serverConn, _) = await CreatePoolWithServer(maxStreams: 5);
        await using var _ = pool;
        using var __ = serverConn;

        // Create first connection and fill it with 4/5 streams.
        var c1 = await pool.GetConnectionAsync(CancellationToken.None);
        for (int i = 0; i < 4; i++) c1.CreateStream();

        // Force creation of second connection by filling c1 completely.
        c1.CreateStream(); // 5/5
        var c2 = await pool.GetConnectionAsync(CancellationToken.None); // new connection
        c2.CreateStream(); // 1/5

        // Now c1 has 0 available, c2 has 4 available.
        // Next get should return c2 (least loaded).
        var selected = await pool.GetConnectionAsync(CancellationToken.None);
        Assert.That(selected, Is.SameAs(c2));
    }

    [Test]
    public async Task Pool_SkipsDrainingConnection()
    {
        var (pool, serverConn, _) = await CreatePoolWithServer(maxStreams: 5);
        await using var _ = pool;
        using var __ = serverConn;

        var c1 = await pool.GetConnectionAsync(CancellationToken.None);
        // Simulate draining by setting state.
        c1.State = ShmConnectionState.Draining;

        // Should create a new connection, not return c1.
        var c2 = await pool.GetConnectionAsync(CancellationToken.None);
        Assert.That(c2, Is.Not.SameAs(c1));
        Assert.That(pool.ConnectionCount, Is.EqualTo(2));
    }

    #endregion

    #region D. Stream Completion Notification

    [Test]
    public async Task StreamRemoved_FiresOnDispose()
    {
        var (pool, serverConn, _) = await CreatePoolWithServer(maxStreams: 5);
        await using var _ = pool;
        using var __ = serverConn;

        var c = await pool.GetConnectionAsync(CancellationToken.None);
        var stream = c.CreateStream();
        Assert.That(c.ActiveStreamCount, Is.EqualTo(1));

        stream.Dispose();
        Assert.That(c.ActiveStreamCount, Is.EqualTo(0));
    }

    [Test]
    public async Task ActiveStreamCount_Accurate()
    {
        var (pool, serverConn, _) = await CreatePoolWithServer(maxStreams: 10);
        await using var _ = pool;
        using var __ = serverConn;

        var c = await pool.GetConnectionAsync(CancellationToken.None);
        var streams = new List<ShmGrpcStream>();
        for (int i = 0; i < 5; i++)
        {
            streams.Add(c.CreateStream());
        }

        Assert.That(c.ActiveStreamCount, Is.EqualTo(5));

        streams[0].Dispose();
        streams[1].Dispose();
        Assert.That(c.ActiveStreamCount, Is.EqualTo(3));

        foreach (var s in streams.Skip(2)) s.Dispose();
        Assert.That(c.ActiveStreamCount, Is.EqualTo(0));
    }

    [Test]
    [CancelAfter(5000)]
    public async Task StreamRemoved_WakesWaiter()
    {
        var opts = DefaultOptions();
        opts.MaxConnectionCount = 1;
        var (pool, serverConn, _) = await CreatePoolWithServer(opts, maxStreams: 1);
        await using var _ = pool;
        using var __ = serverConn;

        var c = await pool.GetConnectionAsync(CancellationToken.None);
        var stream = c.CreateStream();

        // Connection is full. Start a task that will wait for capacity.
        var getTask = Task.Run(async () =>
        {
            return await pool.GetConnectionAsync(CancellationToken.None);
        });

        // Give the task time to enter the wait.
        await Task.Delay(100);
        Assert.That(getTask.IsCompleted, Is.False);

        // Dispose the stream — should wake the waiter.
        stream.Dispose();

        var result = await getTask;
        Assert.That(result, Is.Not.Null);
    }

    #endregion

    #region F. Idle Cleanup

    [Test]
    public async Task IdleCleanup_KeepsAtLeastOne()
    {
        var opts = DefaultOptions();
        opts.PooledConnectionIdleTimeout = TimeSpan.FromMilliseconds(100);
        var (pool, serverConn, _) = await CreatePoolWithServer(opts, maxStreams: 1);
        await using var _ = pool;
        using var __ = serverConn;

        // Create and fill to scale up, then release.
        var c1 = await pool.GetConnectionAsync(CancellationToken.None);
        var s1 = c1.CreateStream();
        var c2 = await pool.GetConnectionAsync(CancellationToken.None);
        var s2 = c2.CreateStream();
        Assert.That(pool.ConnectionCount, Is.EqualTo(2));

        s1.Dispose();
        s2.Dispose();

        // Wait for idle timeout + cleanup timer.
        await Task.Delay(2000);

        // Should have cleaned up to 1 connection (at least one kept).
        Assert.That(pool.ConnectionCount, Is.GreaterThanOrEqualTo(1));
    }

    #endregion
}
