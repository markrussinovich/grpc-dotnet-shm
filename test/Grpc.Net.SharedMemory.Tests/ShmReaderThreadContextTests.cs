#region Copyright notice and license

// Copyright 2026 The gRPC Authors
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
/// Unit tests for <see cref="ShmReaderThreadContext"/>.
///
/// The hangfix invariant (see <see cref="ShmReaderThreadContext"/> XML
/// docs) requires that <c>IsOnReaderThread</c> reads <see langword="true"/>
/// only while a balanced <c>Enter()</c>/<c>Scope.Dispose()</c> pair is in
/// effect on the current thread. These tests guard:
///
/// (1) baseline Enter/Dispose balance,
///
/// (2) nested Enter scopes compose,
///
/// (3) the strict <c>&gt; 0</c> semantics chosen 2026-06-01 (Opus 4.8
/// PR review): a future bug producing a negative counter must NOT flip
/// the predicate to <see langword="true"/> on innocent ThreadPool
/// workers — that would cause spurious Task.Yield hops on every send
/// from those threads.
/// </summary>
[TestFixture]
public class ShmReaderThreadContextTests
{
    [Test]
    public void IsOnReaderThread_DefaultsFalse()
    {
        // Run on a fresh ThreadPool worker so we don't inherit state
        // from a prior test.
        var observed = Task.Run(() => ShmReaderThreadContext.IsOnReaderThread).Result;
        Assert.That(observed, Is.False);
    }

    [Test]
    public void Enter_AndDispose_BalancesCounter()
    {
        Task.Run(() =>
        {
            Assert.That(ShmReaderThreadContext.IsOnReaderThread, Is.False);
            using (ShmReaderThreadContext.Enter())
            {
                Assert.That(ShmReaderThreadContext.IsOnReaderThread, Is.True);
            }
            Assert.That(ShmReaderThreadContext.IsOnReaderThread, Is.False);
        }).Wait();
    }

    [Test]
    public void Enter_Nested_StaysTrueUntilAllDisposed()
    {
        Task.Run(() =>
        {
            using (ShmReaderThreadContext.Enter())
            {
                Assert.That(ShmReaderThreadContext.IsOnReaderThread, Is.True);
                using (ShmReaderThreadContext.Enter())
                {
                    Assert.That(ShmReaderThreadContext.IsOnReaderThread, Is.True);
                }
                // Inner scope disposed; outer still active.
                Assert.That(ShmReaderThreadContext.IsOnReaderThread, Is.True);
            }
            Assert.That(ShmReaderThreadContext.IsOnReaderThread, Is.False);
        }).Wait();
    }

    /// <summary>
    /// Strict <c>&gt; 0</c> robustness: if a future bug ever Disposes
    /// a <see cref="ShmReaderThreadContext.Scope"/> on a thread that
    /// never called <see cref="ShmReaderThreadContext.Enter"/>, the
    /// counter goes negative. The OLD <c>!= 0</c> predicate would
    /// have reported the thread as "on reader thread" forever,
    /// causing every send on that pool worker to take a spurious
    /// <c>Task.Yield()</c> hop. The strict <c>&gt; 0</c> predicate
    /// correctly returns <see langword="false"/>.
    /// </summary>
    [Test]
    public void IsOnReaderThread_NegativeCounter_ReadsFalse()
    {
        // Run on a dedicated Thread (NOT the ThreadPool) so the
        // intentionally-corrupted t_depth state cannot leak into
        // another test running on the same pool worker. The thread
        // dies when the lambda returns, taking its [ThreadStatic]
        // state with it. (Round-2 review fix: the previous Task.Run
        // version polluted pool TLS even with a "restore" pass,
        // because Enter then Dispose moves -1 -> 0 -> -1.)
        bool assertResult = false;
        var thread = new Thread(() =>
        {
            Assert.That(ShmReaderThreadContext.IsOnReaderThread, Is.False);

            // Simulate the foot-gun: Dispose without matching Enter.
            // Scope is `default` so we can construct one ourselves.
            new ShmReaderThreadContext.Scope().Dispose();

            // Counter is now -1; predicate must NOT report "on reader thread".
            assertResult = !ShmReaderThreadContext.IsOnReaderThread;
        }) { IsBackground = true };
        thread.Start();
        Assert.That(thread.Join(TimeSpan.FromSeconds(5)), Is.True,
            "Test thread did not complete in time.");

        Assert.That(assertResult, Is.True,
            "Strict (>0) predicate guards against negative-counter " +
            "false-positives. If this assertion fails, a regression " +
            "to (!= 0) semantics has reintroduced the foot-gun.");
    }
}
