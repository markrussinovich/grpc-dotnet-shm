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

namespace Grpc.Net.SharedMemory.Synchronization;

/// <summary>
/// <see cref="IRingSync"/> implementation that delegates every
/// WaitFor*/Signal* call to a shared <see cref="LinuxDataSegWaker"/>.
/// </summary>
/// <remarks>
/// <para>
/// In the v3.4 protocol every data segment has exactly ONE
/// <see cref="LinuxDataSegWaker"/> per side, and BOTH rings (A and B)
/// on that side share it: any wake from the peer just means "your ring
/// state may have changed, recheck". The
/// caller's existing seq-recheck loop is the recovery path for spurious
/// or condition-wrong wakes.
/// </para>
/// <para>
/// Fan-out (N=2 same-side parkers, e.g. H2 reader on dataSeq plus H2
/// writer on spaceSeq under back-pressure): when
/// <see cref="LinuxDataSegWaker.Wait"/> returns a drained count &gt; 1
/// we issue ONE <see cref="LinuxDataSegWaker.RewakeLocal"/> so the
/// other parker observes its edge. Capped at one rewake because
/// production parker count is bounded by 2 (matches Go's
/// <c>maxSameSideParkers</c>).
/// </para>
/// <para>
/// Ownership: the waker is owned by <see cref="Segment"/>; this sync
/// merely holds a reference. <see cref="Dispose"/> here is a no-op
/// because the waker outlives the ring sync and is closed by
/// <see cref="Segment.Dispose"/> at segment teardown.
/// </para>
/// </remarks>
internal sealed class LinuxEventfdRingSync : IRingSync
{
    private readonly LinuxDataSegWaker _waker;

    /// <summary>
    /// Hard requirement from the gRFC: the eventfd path performs NO
    /// pre-block spin. ShmRing's adaptive spin loop is bypassed and
    /// every <c>WaitFor*</c> falls straight into <c>read(eventfd)</c>.
    /// </summary>
    /// <remarks>
    /// Matches Go-side <c>internal/transport/shm_dataseg_wake_linux.go</c>
    /// (counter-mode eventfd + netpoll-integrated park, no spin).
    /// All wake-path optimizations have to live INSIDE the
    /// <c>read</c>/<c>write</c> round-trip on this primitive.
    /// </remarks>
    public bool SkipSpinWait => true;

    /// <summary>
    /// Constructs a ring sync over a shared waker. Multiple
    /// <see cref="LinuxEventfdRingSync"/> instances on the same side
    /// reference the same waker.
    /// </summary>
    public LinuxEventfdRingSync(LinuxDataSegWaker waker)
    {
        ArgumentNullException.ThrowIfNull(waker);
        _waker = waker;
    }

    public bool WaitForData(uint expectedSeq, TimeSpan? timeout, CancellationToken cancellationToken)
        => WaitGeneric(timeout, cancellationToken);

    public bool WaitForSpace(uint expectedSeq, TimeSpan? timeout, CancellationToken cancellationToken)
        => WaitGeneric(timeout, cancellationToken);

    public bool WaitForContig(uint expectedSeq, TimeSpan? timeout, CancellationToken cancellationToken)
        => WaitGeneric(timeout, cancellationToken);

    public void SignalData() { if (s_diag) Interlocked.Increment(ref s_sigData); _waker.Wake(); }
    public void SignalSpace() { if (s_diag) Interlocked.Increment(ref s_sigSpace); _waker.Wake(); }
    public void SignalContig() { if (s_diag) Interlocked.Increment(ref s_sigContig); _waker.Wake(); }

    private static readonly bool s_diag =
        string.Equals(Environment.GetEnvironmentVariable("SHM_EVENTFD_DIAG"), "1", StringComparison.Ordinal);
    private static long s_sigData;
    private static long s_sigSpace;
    private static long s_sigContig;

    public static (long Data, long Space, long Contig) GetSignalDiag()
        => (Volatile.Read(ref s_sigData), Volatile.Read(ref s_sigSpace), Volatile.Read(ref s_sigContig));

    /// <summary>
    /// Layer-3 cascade: invoked by <see cref="ShmRing"/> after a Wait
    /// returns and the caller's seq-recheck finds the condition still
    /// unmet — implying the kernel woke us instead of the parker the
    /// peer signal was meant for. Writes a wake to OUR OWN fd; the
    /// other parker then reads it and gets a chance to check ITS
    /// condition. Gated by the caller checking that another parker
    /// is actually present, so a solo parker never self-rewakes in a
    /// tight spin.
    /// </summary>
    public void RewakeLocal()
    {
        if (_waker.Parkers > 0)
        {
            _waker.RewakeLocal();
        }
    }

    public void Dispose()
    {
        // No-op: the waker is owned by Segment and outlives the
        // IRingSync instances that reference it.
    }

    private bool WaitGeneric(TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (_waker.IsClosed) return false;

        var drained = _waker.Wait(timeout, cancellationToken);
        if (drained == 0)
        {
            return false; // close / timeout / cancellation / EBADF
        }

        // Layer-2 cascade: counter-mode eventfd coalesces multiple peer
        // wakes into one drained read on our side. When n > 1, write
        // ONE wake back to our own counter so the next same-side parker
        // observes its edge. Capped at one rewake because production
        // parker count is bounded by 2 (Go: maxSameSideParkers).
        //
        // The Layer-3 ("wrong parker") case — kernel woke us but our
        // condition is unmet — is handled by ShmRing's outer
        // seq-recheck loop calling <see cref="RewakeLocal"/> explicitly
        // after its condition check. Doing it unconditionally here
        // would burn a syscall on EVERY wake even when only one parker
        // is present and the wake was correctly delivered.
        if (drained > 1 && _waker.Parkers > 0)
        {
            _waker.RewakeLocal();
        }

        return true;
    }
}

#endif
