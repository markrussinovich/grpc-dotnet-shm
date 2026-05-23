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

using System.Runtime.InteropServices;

namespace Grpc.Net.SharedMemory.Synchronization;

/// <summary>
/// Linux per-data-segment per-direction eventfd wake primitive.
/// Mirrors the Go side's <c>shmDataSegWaker</c>
/// (see <c>internal/transport/shm_dataseg_wake_linux.go</c>).
/// </summary>
/// <remarks>
/// <para>
/// Each data segment has exactly TWO eventfds — one per direction.
/// Each process holds a (<see cref="MyReadFd"/>, <see cref="PeerReadFd"/>)
/// pair: the peer wakes us by writing to <see cref="MyReadFd"/>; we wake
/// the peer by writing to <see cref="PeerReadFd"/>. Both rings on the
/// same side share the same waker — a wake means "something might have
/// changed, check your ring state". The caller's existing seq-recheck
/// loop is the recovery path for spurious wakes.
/// </para>
/// <para>
/// Counter-mode (default, NOT <c>EFD_SEMAPHORE</c>): a single
/// <c>read(fd, 8)</c> drains the entire accumulated counter to zero.
/// This coalesces bursts cheaply, but with N&gt;1 same-side parkers the
/// drainer can swallow wakes intended for other parkers — see
/// <c>RewakeLocal</c> for the cascade primitive.
/// </para>
/// </remarks>
internal sealed partial class LinuxDataSegWaker : IDisposable
{
    /// <summary>
    /// Recv eventfd for THIS side. We block on
    /// <c>read(MyReadFd, 8)</c> here; the peer writes to it via Wake.
    /// </summary>
    public int MyReadFd { get; private set; }

    /// <summary>
    /// Recv eventfd for the PEER side. We write 1 here to wake the
    /// peer.
    /// </summary>
    public int PeerReadFd { get; private set; }

    /// <summary>
    /// When <c>true</c>, <see cref="Dispose"/> closes both descriptors.
    /// Set on the side that allocated the fds (the creator) and on
    /// cross-process openers that received their fds via SCM_RIGHTS.
    /// Same-process openers obtain the creator's fd ints from the
    /// in-proc stash and must NOT close them (creator owns).
    /// </summary>
    private readonly bool _ownsFds;

    /// <summary>
    /// <see cref="MyReadFd"/> boxed once so the per-Wait
    /// <see cref="CancellationToken.Register(Action{object?}, object?)"/>
    /// state argument does not allocate a fresh boxed-int on every
    /// call. Hot-path optimization: a streaming ping-pong does one
    /// <c>Register</c> per direction per RT, so per-call boxing was
    /// ~144 KB GC pressure across a 6000-iter bench.
    /// </summary>
    private readonly object _myReadFdBoxed;

    /// <summary>
    /// Cached single-shot delegate that backs the CT callback. Static
    /// to avoid an instance closure, captures the fd via the state
    /// argument (which is the pre-boxed <see cref="_myReadFdBoxed"/>).
    /// </summary>
    private static readonly Action<object?> s_ctCancelWake = static state =>
    {
        if (state is int fd) WriteOne(fd);
    };

    private int _closed;
    private int _parkers;

    // Diagnostic counters (only updated when SHM_EVENTFD_DIAG=1 env var
    // is set at construction). Used to attribute the eventfd path's
    // contribution to the bench RT without external profilers.
    private static readonly bool s_diagEnabled =
        string.Equals(
            Environment.GetEnvironmentVariable("SHM_EVENTFD_DIAG"),
            "1", StringComparison.Ordinal);
    private static long s_wakeCalls;
    private static long s_wakeSyscalls;
    private static long s_waitCalls;
    private static long s_waitDrainedZero;
    private static long s_waitReadCounterUs;

    public static (long WakeCalls, long WakeSyscalls, long WaitCalls, long WaitDrainedZero, long WaitReadCounterUs) GetDiag()
        => (Volatile.Read(ref s_wakeCalls),
            Volatile.Read(ref s_wakeSyscalls),
            Volatile.Read(ref s_waitCalls),
            Volatile.Read(ref s_waitDrainedZero),
            Volatile.Read(ref s_waitReadCounterUs));

    /// <summary>
    /// Number of goroutines / tasks currently inside <see cref="Wait"/>
    /// on this side. Used by the eventfd ring sync to gate the
    /// "wrong parker" cascade RewakeLocal: when only one parker is
    /// present a self-rewake would just self-spin in the caller's
    /// outer-retry loop.
    /// </summary>
    public int Parkers => Volatile.Read(ref _parkers);

    /// <summary>True after <see cref="Dispose"/> has run.</summary>
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>
    /// Constructs a waker from a pre-allocated fd pair.
    /// </summary>
    /// <param name="myReadFd">This side's recv eventfd.</param>
    /// <param name="peerReadFd">The peer's recv eventfd (we write
    /// here to wake them).</param>
    /// <param name="ownsFds">When <c>true</c>, Dispose closes the fds.
    /// </param>
    public LinuxDataSegWaker(int myReadFd, int peerReadFd, bool ownsFds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(myReadFd);
        ArgumentOutOfRangeException.ThrowIfNegative(peerReadFd);
        MyReadFd = myReadFd;
        PeerReadFd = peerReadFd;
        _ownsFds = ownsFds;
        // Box MyReadFd once so CancellationToken.Register doesn't
        // box-per-call in the hot path.
        _myReadFdBoxed = myReadFd;
    }

    /// <summary>
    /// Wakes the peer by writing a <c>u64=1</c> increment to
    /// <see cref="PeerReadFd"/>. Errors are silently ignored — the
    /// only realistic ones are EBADF after peer close and EAGAIN at
    /// counter saturation (effectively unreachable).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Wake()
    {
        if (Volatile.Read(ref _closed) != 0) return;
        if (s_diagEnabled) Interlocked.Increment(ref s_wakeCalls);
        WriteOne(PeerReadFd);
        if (s_diagEnabled) Interlocked.Increment(ref s_wakeSyscalls);
    }

    /// <summary>
    /// Writes a wake to OUR OWN counter. Used by the eventfd ring
    /// sync to cascade a spurious wake to other same-side parkers
    /// (see file-header comment in
    /// <c>internal/transport/shm_dataseg_wake_linux.go</c>).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void RewakeLocal()
    {
        if (Volatile.Read(ref _closed) != 0) return;
        WriteOne(MyReadFd);
    }

    /// <summary>
    /// Blocks until our counter is non-zero, the waker is disposed,
    /// or the cancellation token fires. Returns the drained counter
    /// value (0 on close / cancellation; &gt;=1 on real wake).
    /// </summary>
    /// <param name="timeout">Optional timeout. <c>null</c> means
    /// block indefinitely.</param>
    /// <param name="cancellationToken">Cancellation token. When the
    /// token fires we write 1 to MyReadFd to unblock the read.</param>
    /// <returns>Drained counter value, or 0 on close / cancellation /
    /// timeout / read error.</returns>
    public ulong Wait(TimeSpan? timeout, CancellationToken cancellationToken)
    {
        // Hot-path order matters: the fast "already cancelled / closed"
        // checks come first to avoid the Interlocked-increment / CT
        // registration cost when we know we're going to return 0 anyway.
        if (Volatile.Read(ref _closed) != 0) return 0;
        if (cancellationToken.IsCancellationRequested) return 0;

        if (s_diagEnabled) Interlocked.Increment(ref s_waitCalls);
        Interlocked.Increment(ref _parkers);
        CancellationTokenRegistration ctReg = default;
        try
        {
            // CT cancellation: writing 1 to OUR OWN fd unblocks the
            // blocking read; the post-read close / CT check converts the
            // wake into a 0 return. We pass the pre-boxed MyReadFd to
            // avoid the per-call object allocation a fresh boxed int
            // would incur.
            if (cancellationToken.CanBeCanceled)
            {
                ctReg = cancellationToken.UnsafeRegister(s_ctCancelWake, _myReadFdBoxed);
            }

            if (timeout.HasValue && timeout.Value < TimeSpan.MaxValue)
            {
                var ms = (int)Math.Min(int.MaxValue, Math.Max(0, timeout.Value.TotalMilliseconds));
                var pollResult = Poll(MyReadFd, ms);
                if (pollResult <= 0)
                {
                    // 0 = timeout; -1 = error (closed, EBADF).
                    return 0;
                }
            }

            Span<byte> buf = stackalloc byte[8];
            long readStart = 0;
            if (s_diagEnabled) readStart = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!ReadCounter(MyReadFd, buf))
            {
                if (s_diagEnabled) Interlocked.Increment(ref s_waitDrainedZero);
                return 0;
            }
            if (s_diagEnabled)
            {
                long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - readStart;
                long us = (long)(ticks * 1_000_000L / System.Diagnostics.Stopwatch.Frequency);
                Interlocked.Add(ref s_waitReadCounterUs, us);
            }

            // Post-read recheck: turn shutdown / CT writes into a 0
            // return so the caller's ring loop bails out promptly.
            if (Volatile.Read(ref _closed) != 0) return 0;
            if (cancellationToken.IsCancellationRequested) return 0;

            // Drained LE uint64 == accumulated wake count since last
            // read. With counter-mode eventfd, multiple peer Wakes
            // coalesce into one read.
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(buf);
        }
        finally
        {
            ctReg.Dispose();
            Interlocked.Decrement(ref _parkers);
        }
    }

    /// <summary>
    /// Marks the waker as closed, wakes any parked Wait, and (if owner)
    /// closes both descriptors. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;

        // Wake any parked reader so they observe the closed flag and
        // return 0. Multiple in-flight Wakes / Disposes only ever
        // write the constant 1, so the kernel counter cannot overflow
        // (would require ~2^64 writes).
        WriteOne(MyReadFd);

        if (_ownsFds)
        {
            // Brief grace period so an in-flight reader finishes the
            // read before we close the fd from under it (close-during-
            // read race returns EBADF either way, but a clean drain is
            // friendlier in logs).
            Thread.Sleep(1);
            var myFd = MyReadFd;
            var peerFd = PeerReadFd;
            MyReadFd = -1;
            PeerReadFd = -1;
            if (myFd >= 0) close(myFd);
            if (peerFd >= 0) close(peerFd);
        }
    }

    // ----- Helpers -----

    private static void WriteOne(int fd)
    {
        if (fd < 0) return;
        Span<byte> buf = stackalloc byte[8];
        // LE 1 (kernel atomic-adds u64 counter += 1).
        buf[0] = 1; buf[1] = 0; buf[2] = 0; buf[3] = 0;
        buf[4] = 0; buf[5] = 0; buf[6] = 0; buf[7] = 0;
        unsafe
        {
            fixed (byte* p = buf)
            {
                long n;
                do { n = write(fd, p, (UIntPtr)8); }
                while (n == -1 && Marshal.GetLastWin32Error() == EINTR);
                // Ignore other errors: EBADF (peer closed) or EAGAIN
                // (overflow at u64-1, unreachable in practice) — the
                // wake is best-effort.
            }
        }
    }

    private static bool ReadCounter(int fd, Span<byte> buf)
    {
        unsafe
        {
            fixed (byte* p = buf)
            {
                long n;
                do { n = read(fd, p, (UIntPtr)8); }
                while (n == -1 && Marshal.GetLastWin32Error() == EINTR);
                return n == 8;
            }
        }
    }

    private static int Poll(int fd, int timeoutMs)
    {
        unsafe
        {
            var pfd = new Pollfd { fd = fd, events = POLLIN, revents = 0 };
            int n;
            do { n = poll(&pfd, 1, timeoutMs); }
            while (n == -1 && Marshal.GetLastWin32Error() == EINTR);
            return n;
        }
    }

    /// <summary>
    /// Allocates a fresh blocking eventfd. Used by
    /// <see cref="EventfdRegistry"/> when the creator builds its
    /// (creator, opener) pair.
    /// </summary>
    internal static int CreateEventfd()
    {
        // EFD_CLOEXEC (0x80000 / O_CLOEXEC on Linux): prevent the wake fd
        // from leaking into any child process spawned by the host (e.g.
        // gRPC server processes that fork worker bins).
        var fd = eventfd(0, EFD_CLOEXEC);
        if (fd < 0)
        {
            var err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"eventfd() failed (errno={err})");
        }
        return fd;
    }

    // ----- P/Invoke -----

    private const int EINTR = 4;
    private const short POLLIN = 0x001;
    // O_CLOEXEC value on Linux (same numeric as on all glibc / musl
    // platforms grpc-dotnet-shm currently supports). Matches the
    // EFD_CLOEXEC constant defined in <sys/eventfd.h>.
    private const int EFD_CLOEXEC = 0x80000;

    [StructLayout(LayoutKind.Sequential)]
    private struct Pollfd
    {
        public int fd;
        public short events;
        public short revents;
    }

    [LibraryImport("libc", EntryPoint = "eventfd", SetLastError = true)]
    private static partial int eventfd(uint initval, int flags);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static unsafe partial long write(int fd, byte* buf, UIntPtr count);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static unsafe partial long read(int fd, byte* buf, UIntPtr count);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static unsafe partial int poll(Pollfd* fds, ulong nfds, int timeout);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int close(int fd);
}

#endif
