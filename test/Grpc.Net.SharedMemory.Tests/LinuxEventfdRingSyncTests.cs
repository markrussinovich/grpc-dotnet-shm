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

#if LINUX

using Grpc.Net.SharedMemory.Synchronization;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Focused unit tests for <see cref="LinuxDataSegWaker"/>, the v3.4
/// per-data-segment per-direction eventfd primitive. Mirrors the
/// behaviour tests in
/// <c>internal/transport/shm_dataseg_wake_linux_test.go</c>.
/// </summary>
[TestFixture]
[Platform("Linux")]
public partial class LinuxDataSegWakerTests
{
    private static (LinuxDataSegWaker a, LinuxDataSegWaker b) NewPair()
    {
        var fd1 = LinuxDataSegWaker.CreateEventfd();
        int fd2;
        try { fd2 = LinuxDataSegWaker.CreateEventfd(); }
        catch { CloseFd(fd1); throw; }

        // a reads efd1, writes efd2 (= b's recv)
        // b reads efd2, writes efd1 (= a's recv)
        var a = new LinuxDataSegWaker(myReadFd: fd1, peerReadFd: fd2, ownsFds: true);
        var b = new LinuxDataSegWaker(myReadFd: fd2, peerReadFd: fd1, ownsFds: false);
        return (a, b);
    }

    private static void CloseFd(int fd)
    {
        // Best-effort: cleanup-only path used during test setup failures.
        try { LibcClose(fd); } catch { }
    }

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "close")]
    private static partial int LibcClose(int fd);

    [Test]
    public void Wake_Then_Wait_Returns_DrainedCount()
    {
        var (a, b) = NewPair();
        try
        {
            // b wakes a → a's counter += 1.
            b.Wake();

            var n = a.Wait(timeout: TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.That(n, Is.EqualTo(1u),
                "Single Wake should drain to counter value 1.");
        }
        finally
        {
            a.Dispose();
        }
    }

    [Test]
    public void Multiple_Wakes_Coalesce_Into_DrainedCount()
    {
        var (a, b) = NewPair();
        try
        {
            // Three peer wakes before a Reads → counter should be 3.
            b.Wake();
            b.Wake();
            b.Wake();

            var n = a.Wait(timeout: TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.That(n, Is.EqualTo(3u),
                "Counter-mode eventfd coalesces multiple wakes into one Read.");
        }
        finally
        {
            a.Dispose();
        }
    }

    [Test]
    public void Wait_Without_Wake_Returns_OnTimeout()
    {
        var (a, _) = NewPair();
        try
        {
            var n = a.Wait(timeout: TimeSpan.FromMilliseconds(50), CancellationToken.None);
            Assert.That(n, Is.EqualTo(0u),
                "Timeout path returns 0 (no drained wake).");
        }
        finally
        {
            a.Dispose();
        }
    }

    [Test]
    public void Wait_Returns_OnCancellation()
    {
        var (a, _) = NewPair();
        using var cts = new CancellationTokenSource();
        try
        {
            var task = System.Threading.Tasks.Task.Run(() =>
                a.Wait(timeout: null, cancellationToken: cts.Token));

            Thread.Sleep(50);
            cts.Cancel();

            Assert.That(task.Wait(TimeSpan.FromSeconds(2)), Is.True,
                "Cancellation must unblock Wait promptly.");
            Assert.That(task.Result, Is.EqualTo(0u),
                "Cancellation path returns 0.");
        }
        finally
        {
            a.Dispose();
        }
    }

    [Test]
    public void Dispose_Wakes_ParkedReader()
    {
        var (a, _) = NewPair();

        var done = new ManualResetEventSlim(false);
        ulong drained = ulong.MaxValue;
        var t = new Thread(() =>
        {
            drained = a.Wait(timeout: TimeSpan.FromSeconds(5), CancellationToken.None);
            done.Set();
        }) { IsBackground = true };
        t.Start();

        Thread.Sleep(50); // let the reader park
        a.Dispose();

        Assert.That(done.Wait(TimeSpan.FromSeconds(2)), Is.True,
            "Dispose must wake a parked Wait.");
        Assert.That(drained, Is.EqualTo(0u),
            "Dispose path returns 0 (not a real wake).");
    }

    [Test]
    public void RewakeLocal_FiresOwnSide()
    {
        var (a, _) = NewPair();
        try
        {
            a.RewakeLocal();
            var n = a.Wait(timeout: TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.That(n, Is.EqualTo(1u),
                "RewakeLocal puts a +1 into our own counter so we wake ourselves.");
        }
        finally
        {
            a.Dispose();
        }
    }

    [Test]
    public void Parkers_Counter_TracksWaiters()
    {
        var (a, _) = NewPair();
        try
        {
            Assert.That(a.Parkers, Is.EqualTo(0));
            var t = new Thread(() => a.Wait(timeout: TimeSpan.FromSeconds(2), CancellationToken.None))
                { IsBackground = true };
            t.Start();
            Thread.Sleep(50);
            Assert.That(a.Parkers, Is.EqualTo(1), "Parker count rises while Wait is blocked.");
            a.Dispose();
            t.Join(TimeSpan.FromSeconds(2));
            Assert.That(a.Parkers, Is.EqualTo(0), "Parker count returns to 0 after Wait exits.");
        }
        finally
        {
            // a already disposed; tolerate double-dispose
        }
    }
}

[TestFixture]
[Platform("Linux")]
public class EventfdRegistryTests
{
    [Test]
    public void AllocateAndStash_Then_ClaimOpener_Returns_PeerWaker()
    {
        var name = "shm_efd_test_" + System.Guid.NewGuid().ToString("N");
        var (creator, _) = EventfdRegistry.AllocateAndStash(name);
        try
        {
            var opener = EventfdRegistry.TryClaimOpener(name);
            Assert.That(opener, Is.Not.Null);
            // Same-process opener does NOT own the fds (creator does).
            // creatorWaker.PeerReadFd == opener.MyReadFd (same kernel fd).
            Assert.That(opener!.MyReadFd, Is.EqualTo(creator.PeerReadFd));
            Assert.That(opener.PeerReadFd, Is.EqualTo(creator.MyReadFd));
            opener.Dispose();
        }
        finally
        {
            creator.Dispose();
            EventfdRegistry.Drop(name);
        }
    }

    [Test]
    public void Second_ClaimOpener_Returns_Null()
    {
        var name = "shm_efd_test_" + System.Guid.NewGuid().ToString("N");
        var (creator, _) = EventfdRegistry.AllocateAndStash(name);
        try
        {
            var first = EventfdRegistry.TryClaimOpener(name);
            Assert.That(first, Is.Not.Null);
            first!.Dispose();
            var second = EventfdRegistry.TryClaimOpener(name);
            Assert.That(second, Is.Null, "Claim is single-shot.");
        }
        finally
        {
            creator.Dispose();
            EventfdRegistry.Drop(name);
        }
    }

    [Test]
    public void AllocateAndStash_Twice_Throws()
    {
        var name = "shm_efd_test_" + System.Guid.NewGuid().ToString("N");
        var (first, _) = EventfdRegistry.AllocateAndStash(name);
        try
        {
            Assert.Throws<System.InvalidOperationException>(() =>
                EventfdRegistry.AllocateAndStash(name));
        }
        finally
        {
            first.Dispose();
            EventfdRegistry.Drop(name);
        }
    }
}

[TestFixture]
[Platform("Linux")]
public class LinuxFdPassCrossProcessTests
{
    [Test]
    public void FdPass_SameProcess_Roundtrip_TransfersWorkingWaker()
    {
        // End-to-end check: spin up an FD-pass server using a real
        // creator-side waker, dial it from the same process, build an
        // opener-side waker over the returned descriptors, and verify
        // Wake/Wait flows in both directions over the new fds.
        var name = "shm_fdpass_" + System.Guid.NewGuid().ToString("N");
        var path = "/dev/shm/" + name;
        // Touch the segment file so the fd-pass server's chmod target exists.
        System.IO.File.WriteAllBytes(path, new byte[16]);
        try
        {
            var (creator, stash) = EventfdRegistry.AllocateAndStash(name);
            using var srv = LinuxFdPassServer.Start(path, stash);
            try
            {
                Assert.That(srv, Is.Not.Null, "fd-pass server must start.");

                var fds = LinuxFdPassClient.TryReceive(path);
                Assert.That(fds, Is.Not.Null, "Cross-process recv must succeed.");
                Assert.That(fds!.Length, Is.EqualTo(2));

                // fds[0] = creator's recv (=peer for opener); fds[1] = opener's recv.
                using var opener = new LinuxDataSegWaker(
                    myReadFd: fds[1], peerReadFd: fds[0], ownsFds: true);

                // opener → creator: opener.Wake writes 1 to creator's read.
                opener.Wake();
                var nCreator = creator.Wait(TimeSpan.FromSeconds(2), CancellationToken.None);
                Assert.That(nCreator, Is.EqualTo(1u), "creator must receive opener's wake.");

                // creator → opener: creator.Wake writes 1 to opener's read.
                creator.Wake();
                var nOpener = opener.Wait(TimeSpan.FromSeconds(2), CancellationToken.None);
                Assert.That(nOpener, Is.EqualTo(1u), "opener must receive creator's wake.");
            }
            finally
            {
                creator.Dispose();
                EventfdRegistry.Drop(name);
            }
        }
        finally
        {
            try { System.IO.File.Delete(path); } catch { }
            try { System.IO.File.Delete(path + ".fds.sock"); } catch { }
        }
    }

    [Test]
    public void FdPass_MissingSocket_FastFails()
    {
        var name = "shm_fdpass_miss_" + System.Guid.NewGuid().ToString("N");
        var fakeSegPath = "/dev/shm/" + name;
        var t0 = System.Diagnostics.Stopwatch.StartNew();
        var fds = LinuxFdPassClient.TryReceive(fakeSegPath);
        t0.Stop();
        Assert.That(fds, Is.Null);
        Assert.That(t0.ElapsedMilliseconds, Is.LessThan(500),
            "Missing socket should fast-fail; recvTimeout (5s) must not apply.");
    }
}

#endif
