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
/// Concurrency and thread-safety tests for <see cref="ShmConnectionPool"/>.
/// </summary>
[TestFixture]
[Platform("Win")]
[NonParallelizable]
public class ShmConnectionPoolConcurrencyTests
{
    private static ShmClientTransportOptions DefaultOptions() => new()
    {
        EnableMultipleConnections = true,
        MaxConnectionCount = 0,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        RingCapacity = 4096
    };

    #region CA. GetConnectionAsync Concurrency

    [Test]
    [Timeout(15000)]
    public async Task GetConnection_100ConcurrentCalls_AllSucceed()
    {
        var name = $"conc_test_{Guid.NewGuid():N}";
        using var serverConn = ShmConnection.CreateAsServer(name, ringCapacity: 4096, maxStreams: 200);
        await using var pool = new ShmConnectionPool(DefaultOptions(), ct =>
            Task.FromResult(ShmConnection.ConnectAsClient(name)));

        var tasks = new Task<ShmPooledConnection>[100];
        for (int i = 0; i < 100; i++)
        {
            tasks[i] = pool.GetConnectionAsync(CancellationToken.None).AsTask();
        }

        var results = await Task.WhenAll(tasks);

        Assert.That(results, Has.All.Not.Null);
        Assert.That(pool.ConnectionCount, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    [Timeout(15000)]
    public async Task GetConnection_RaceWithDispose_NoDeadlock()
    {
        var name = $"conc_test_{Guid.NewGuid():N}";
        using var serverConn = ShmConnection.CreateAsServer(name, ringCapacity: 4096, maxStreams: 100);
        var pool = new ShmConnectionPool(DefaultOptions(), ct =>
            Task.FromResult(ShmConnection.ConnectAsClient(name)));

        // Warm up with one connection.
        await pool.GetConnectionAsync(CancellationToken.None);

        // Start a few concurrent getters.
        using var cts = new CancellationTokenSource();
        var completed = new System.Collections.Concurrent.ConcurrentBag<bool>();
        var getTasks = Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await pool.GetConnectionAsync(cts.Token);
                    completed.Add(true);
                }
                catch (ObjectDisposedException) { return; }
                catch (OperationCanceledException) { return; }
            }
        })).ToArray();

        // Allow some calls to succeed.
        await Task.Delay(100);

        // Dispose pool while getters are running.
        await pool.DisposeAsync();
        cts.Cancel();

        // Should not deadlock — tasks should complete (with exceptions or normally).
        var allDone = Task.WhenAll(getTasks);
        var result = await Task.WhenAny(allDone, Task.Delay(10000));
        Assert.That(result, Is.SameAs(allDone), "Tasks deadlocked after pool dispose");
    }

    [Test]
    [Timeout(10000)]
    public async Task GetConnection_DisposeWhileFactoryBlocked_FailsFastAndNoLeak()
    {
        // This test verifies that when DisposeAsync() races with a slow
        // connection factory that respects cancellation, the caller gets
        // ObjectDisposedException promptly and no connection is leaked.
        // The pool's shutdown is best-effort: it cancels _factoryCts and
        // waits up to 3 seconds for the factory to exit. Factories that
        // ignore cancellation may still be running when DisposeAsync returns.

        var name = $"conc_test_{Guid.NewGuid():N}";
        using var serverConn = ShmConnection.CreateAsServer(name, ringCapacity: 4096, maxStreams: 100);

        // Gate that lets us block the factory until we're ready.
        var factoryGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var pool = new ShmConnectionPool(DefaultOptions(), async ct =>
        {
            // Signal that the factory has been entered.
            factoryEntered.TrySetResult();
            // Block until the test releases us OR cancellation is requested.
            // A well-behaved factory must observe the token.
            await factoryGate.Task.WaitAsync(ct);
            return ShmConnection.ConnectAsClient(name);
        });
        await using var __ = pool;

        // Start GetConnectionAsync — it will enter the factory and block.
        var getTask = pool.GetConnectionAsync(CancellationToken.None).AsTask();

        // Wait for the factory to be entered.
        await factoryEntered.Task;

        // Dispose the pool while the factory is blocked.
        await pool.DisposeAsync();

        // Release the factory gate (in case cancellation arrived before the
        // wait — prevents the factory Task from leaking).
        factoryGate.TrySetResult();

        // The GetConnectionAsync call should fail — either with
        // ObjectDisposedException (pool detected disposed state) or
        // OperationCanceledException (factory's WaitAsync observed _factoryCts).
        var ex = Assert.CatchAsync(async () => await getTask);
        Assert.That(ex, Is.InstanceOf<ObjectDisposedException>()
            .Or.InstanceOf<OperationCanceledException>());

        // Pool should have no residual connections.
        Assert.That(pool.ConnectionCount, Is.EqualTo(0));
    }

    #endregion

    #region CB. Connect Lock Serialization

    [Test]
    [Timeout(30000)]
    public async Task ConnectLock_ConcurrentThreads_FactoryCalledSerially()
    {
        var name = $"conc_test_{Guid.NewGuid():N}";
        using var serverConn = ShmConnection.CreateAsServer(name, ringCapacity: 4096, maxStreams: 2);

        var concurrentCount = 0;
        var maxConcurrent = 0;
        var factoryCalls = 0;

        await using var pool = new ShmConnectionPool(DefaultOptions(), async ct =>
        {
            var current = Interlocked.Increment(ref concurrentCount);
            try
            {
                var maxSeen = Volatile.Read(ref maxConcurrent);
                while (current > maxSeen)
                {
                    Interlocked.CompareExchange(ref maxConcurrent, current, maxSeen);
                    maxSeen = Volatile.Read(ref maxConcurrent);
                }

                Interlocked.Increment(ref factoryCalls);
                await Task.Delay(20, ct); // Simulate slow connect
                return ShmConnection.ConnectAsClient(name);
            }
            finally
            {
                Interlocked.Decrement(ref concurrentCount);
            }
        });

        // 10 threads compete for streams. Some may hit maxStreams race — that's expected.
        // The test goal is factory serialization, not that every task gets a stream.
        var tasks = new Task[10];
        var streamSuccesses = 0;
        for (int i = 0; i < 10; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                var conn = await pool.GetConnectionAsync(CancellationToken.None);
                try
                {
                    conn.CreateStream();
                    Interlocked.Increment(ref streamSuccesses);
                }
                catch (InvalidOperationException)
                {
                    // maxStreams race — another thread took the last slot. Expected.
                }
            });
        }

        await Task.WhenAll(tasks);

        // At least some streams should have succeeded.
        Assert.That(streamSuccesses, Is.GreaterThan(0));

        // Factory should never have been called concurrently.
        Assert.That(maxConcurrent, Is.LessThanOrEqualTo(1),
            "Connection factory was called concurrently — _connectLock is broken");
    }

    [Test]
    [Timeout(10000)]
    public async Task ConnectLock_FactoryThrows_WaitersGetException()
    {
        var callCount = 0;
        var opts = DefaultOptions();
        opts.EnableMultipleConnections = false;
        opts.ConnectTimeout = TimeSpan.FromSeconds(5);

        await using var pool = new ShmConnectionPool(opts, ct =>
        {
            Interlocked.Increment(ref callCount);
            throw new InvalidOperationException("Connection failed");
        });

        // Should propagate the factory exception.
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await pool.GetConnectionAsync(CancellationToken.None);
        });
    }

    #endregion

    #region CC. StreamRemoved Concurrency

    [Test]
    [Timeout(10000)]
    public async Task StreamRemoved_50StreamsDisposeConcurrently_CountConsistent()
    {
        var name = $"conc_test_{Guid.NewGuid():N}";
        using var serverConn = ShmConnection.CreateAsServer(name, ringCapacity: 4096, maxStreams: 200);
        await using var pool = new ShmConnectionPool(DefaultOptions(), ct =>
            Task.FromResult(ShmConnection.ConnectAsClient(name)));

        var conn = await pool.GetConnectionAsync(CancellationToken.None);
        var streams = new ShmGrpcStream[50];
        for (int i = 0; i < 50; i++)
        {
            streams[i] = conn.CreateStream();
        }

        Assert.That(conn.ActiveStreamCount, Is.EqualTo(50));

        // Dispose all concurrently.
        var disposeTasks = streams.Select(s => Task.Run(() => s.Dispose())).ToArray();
        await Task.WhenAll(disposeTasks);

        Assert.That(conn.ActiveStreamCount, Is.EqualTo(0));
    }

    #endregion

    #region CF. Stress Tests

    [Test]
    [Timeout(15000)]
    public async Task StressTest_200Tasks_RandomCreateDispose_2Seconds()
    {
        var name = $"stress_test_{Guid.NewGuid():N}";
        using var serverConn = ShmConnection.CreateAsServer(name, ringCapacity: 4096, maxStreams: 500);
        await using var pool = new ShmConnectionPool(DefaultOptions(), ct =>
            Task.FromResult(ShmConnection.ConnectAsClient(name)));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, 200).Select(_ => Task.Run(async () =>
        {
            var rng = Random.Shared;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var conn = await pool.GetConnectionAsync(cts.Token);
                    var stream = conn.CreateStream();
                    await Task.Delay(rng.Next(1, 5), cts.Token);
                    stream.Dispose();
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { } // maxStreams race — expected
                catch (Exception ex) { exceptions.Add(ex); }
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.That(exceptions, Is.Empty,
            $"Unexpected exceptions: {string.Join("; ", exceptions.Take(5).Select(e => e.Message))}");

        // Final state should be consistent.
        Assert.That(pool.ConnectionCount, Is.GreaterThanOrEqualTo(1));
    }

    #endregion

    #region CE. Direct-Mode (No Pool) Dispose Race

    [Test]
    [Timeout(10000)]
    public async Task DirectMode_DisposeWhileConnecting_NoLeakOrCrash()
    {
        // Verifies that when ShmControlHandler is in direct-connection mode
        // (EnableMultipleConnections=false) and Dispose() races with
        // EnsureDirectConnectionAsync(), the handler doesn't leak a connection
        // or throw from a disposed semaphore.

        var name = $"conc_test_{Guid.NewGuid():N}";

        // Use an ShmControlListener to accept data connections from the handler.
        using var listener = new ShmControlListener(name, ringCapacity: 4096, maxStreams: 100);
        var listenCts = new CancellationTokenSource();
        var listenTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var conn in listener.AcceptConnectionsAsync(listenCts.Token))
                {
                    conn.Dispose();
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        });

        var options = new ShmClientTransportOptions { EnableMultipleConnections = false };
        var handler = new ShmControlHandler(name, options);

        // Start a request that will trigger EnsureDirectConnectionAsync.
        var requestTask = Task.Run(async () =>
        {
            try
            {
                using var invoker = new System.Net.Http.HttpMessageInvoker(handler, disposeHandler: false);
                var request = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Post, "http://localhost/test.Service/Method")
                {
                    Version = new Version(2, 0),
                    Content = new System.Net.Http.ByteArrayContent(new byte[] { 0, 0, 0, 0, 0 })
                };
                await invoker.SendAsync(request, CancellationToken.None);
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            catch (OperationCanceledException) { }
            catch (RingClosedException) { }
        });

        // Give the request a moment to start connecting.
        await Task.Delay(50);

        // Dispose handler while connection may be in progress.
        handler.Dispose();

        // Request task should complete without deadlock or unobserved exceptions.
        var completed = await Task.WhenAny(requestTask, Task.Delay(8000));
        Assert.That(completed, Is.SameAs(requestTask), "Request task deadlocked after handler dispose");

        listenCts.Cancel();
        listener.Dispose();
        await Task.WhenAny(listenTask, Task.Delay(2000));
    }

    #endregion
}
