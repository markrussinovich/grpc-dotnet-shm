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

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Grpc.Net.SharedMemory.Synchronization;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Exception thrown when the ring buffer is closed.
/// </summary>
public class RingClosedException : Exception
{
    public RingClosedException() : base("Ring buffer is closed") { }
    public RingClosedException(string message) : base(message) { }
}

/// <summary>
/// Represents a write reservation for zero-copy writes to the ring buffer.
/// </summary>
public readonly struct WriteReservation
{
    /// <summary>First contiguous slice (from write position to end of buffer or requested size).</summary>
    public Memory<byte> First { get; init; }

    /// <summary>Second contiguous slice (from start of buffer) - may be empty if First has enough space.</summary>
    public Memory<byte> Second { get; init; }

    /// <summary>Total reserved bytes.</summary>
    public int Length => First.Length + Second.Length;

    internal ShmRing? Ring { get; init; }
    internal ulong WriteIdx { get; init; }
    internal int MaxBytes { get; init; }
}

/// <summary>
/// Represents a read reservation for zero-copy reads from the ring buffer.
/// </summary>
public readonly struct ReadReservation
{
    /// <summary>First contiguous slice.</summary>
    public ReadOnlyMemory<byte> First { get; init; }

    /// <summary>Second contiguous slice (handles wrap-around).</summary>
    public ReadOnlyMemory<byte> Second { get; init; }

    /// <summary>Total bytes available to read.</summary>
    public int Length => First.Length + Second.Length;

    internal ShmRing? Ring { get; init; }
    /// <summary>
    /// For speculative CommitRead: the shared ReadIdx at reservation time.
    /// </summary>
    internal ulong CommitReadIdx { get; init; }
    internal int MaxBytes { get; init; }
}

/// <summary>
/// Single-Producer Single-Consumer (SPSC) ring buffer operating over shared memory
/// with event-driven blocking. This implementation provides high-performance
/// cross-process communication with zero-copy operations and minimal kernel calls
/// through futex-based (Linux) or named event (Windows) synchronization.
///
/// This implementation matches the grpc-go-shmem ring buffer for interoperability.
/// </summary>
public sealed class ShmRing : IDisposable
{
    private readonly Memory<byte> _memory;
    private readonly ulong _capacity;
    private readonly ulong _capMask;
    private readonly int _headerOffset;
    private readonly int _dataOffset;
    private readonly IRingSync? _sync;
    private readonly bool _isOwner;

    private volatile bool _localClosed;
    private ulong _pendingReadIdx;

    // Adaptive spin state
    private int _dataSpinCutoff = ShmConstants.SpinIterationsDefault;
    private int _spaceSpinCutoff = ShmConstants.SpinIterationsDefault;

    // Batch write: suppress OS-level data signals until EndBatchWrite.
    // DataSeq is still incremented per-frame so spin waiters see updates.
    private int _batchWriteDepth;

    // Callback invoked during WaitForSpace before blocking, allowing the
    // WriterLoop to drain control frames (e.g. WindowUpdate) that can
    // free space on the remote side and break bidirectional deadlocks.
    internal Action? WaitForSpaceDrainCallback;
    private int _drainRecursionDepth;

    /// <summary>
    /// Creates a new ShmRing from a memory region.
    /// </summary>
    /// <param name="memory">The memory region containing the ring (header + data).</param>
    /// <param name="headerOffset">Offset to the ring header within the memory.</param>
    /// <param name="capacity">The data area capacity (must be power of 2).</param>
    /// <param name="sync">Optional synchronization primitive for cross-process signaling.</param>
    /// <param name="isOwner">If true, this instance owns the ring and will set the Closed flag in shared memory on dispose.</param>
    public ShmRing(Memory<byte> memory, int headerOffset, ulong capacity, IRingSync? sync = null, bool isOwner = true)
    {
        if (capacity == 0 || !IsPowerOfTwo(capacity))
        {
            throw new ArgumentException("Capacity must be a power of two", nameof(capacity));
        }

        var requiredSize = headerOffset + ShmConstants.RingHeaderSize + (int)capacity;
        if (memory.Length < requiredSize)
        {
            throw new ArgumentException($"Memory region too small. Required: {requiredSize}, Got: {memory.Length}", nameof(memory));
        }

        _memory = memory;
        _headerOffset = headerOffset;
        _dataOffset = headerOffset + ShmConstants.RingHeaderSize;
        _capacity = capacity;
        _capMask = capacity - 1;
        _sync = sync;
        _isOwner = isOwner;

        // Initialize pending read index from current shared read index
        ref var header = ref GetHeader();
        _pendingReadIdx = Volatile.Read(ref header.ReadIdx);
    }

    /// <summary>
    /// Gets the ring buffer capacity.
    /// </summary>
    public ulong Capacity => _capacity;

    /// <summary>
    /// Whether the owning connection negotiated single-stream (ping-pong)
    /// mode. Set once during connection establishment and read by
    /// <see cref="ChainZcBudget"/> to decide how aggressively a multi-frame
    /// chain ZC anchor may consume the ring.
    /// </summary>
    /// <remarks>
    /// In single-stream / ping-pong mode the writer naturally pauses
    /// after each request — the client only sends the next request
    /// after receiving a response — so the chain anchor may safely hold
    /// up to <c>cap - SmallReserve</c> bytes without risking a writer-side
    /// stall. In multi-stream mode the writer can pipeline a follow-up
    /// message before the consumer parses the current one, so the budget
    /// stays at <c>cap/2</c> to leave headroom for the next message's
    /// first frame.
    /// </remarks>
    public bool SingleStreamMode { get; internal set; }

    // Speculative zero-copy: track bytes committed but not yet consumed.
    // Writer deducts this from available space in ReserveWrite, ensuring
    // it can never reach ring memory still referenced by handler code.
    //
    // This is precise: 1MB speculative frame → writer loses 1MB capacity,
    // not the full maxFramePayload. Handler Release restores the space.
    //
    // Thread safety: Interlocked.Add for increment (FrameReaderLoop thread)
    // and decrement (handler thread via Release). ReserveWrite reads with
    // Volatile.Read — stale reads only cause a conservative wait, never
    // a safety violation.
    internal long SpeculativeReservedBytes;

    /// <summary>
    /// Computes the number of bytes the writer can safely reserve right now.
    /// </summary>
    /// <remarks>
    /// Single source of truth for the writer's "available space" formula.
    /// Cross-process zero-copy safety is achieved on the READER side by
    /// deferring <c>header.ReadIdx</c> advancement while a ZC frame is in
    /// flight (see <see cref="BeginZcReservation"/> and
    /// <see cref="EndZcReservation"/>). This means the writer's plain
    /// <c>used = writeIdx - readIdx</c> formula is automatically correct —
    /// no shared-memory ZC field, no protocol change.
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private ulong ComputeAvailableForWrite(ulong writeIdx, ulong readIdx)
    {
        var used = writeIdx - readIdx;
        return used >= _capacity ? 0UL : _capacity - used;
    }

    // ===== Position-aware speculative ZC protection (no protocol change) =====
    //
    // Problem: SpeculativeReservedBytes is a count, not a position; it cannot
    // tell the writer WHERE the held bytes are. If a non-ZC frame commits AFTER
    // a ZC frame, header.ReadIdx advances past the ZC region's tail, and the
    // writer's available-space formula allows wrapping onto still-held bytes.
    //
    // Worse: SpeculativeReservedBytes is a per-process managed field, so the
    // cross-process writer never even saw it.
    //
    // Solution: don't advance the SHARED header.ReadIdx while a ZC frame is in
    // flight. Reader keeps its progress in a local _deferredReadIdx; on ZC
    // release the deferred value is published to header.ReadIdx in one shot.
    // Cross-process writer reads header.ReadIdx normally and is correct without
    // knowing anything about ZC.
    //
    // Invariants enforced by callers:
    //   - At most ONE ZC in flight per ring (gated by Volatile.Read(spec)==0).
    //   - BeginZcReservation/EndZcReservation are paired exactly once per ZC.
    //   - The reader-side FrameReaderLoop is single-threaded; CommitReadRaw is
    //     only called from that thread or from EndZcReservation (consumer side).

    // Note: NOT marked `volatile` because we access this field via
    // Volatile.Read/Write everywhere — taking a `ref` to a `volatile`
    // field would silently strip the volatile semantics (CS0420) and
    // mixing `volatile` keyword with explicit Volatile.Read/Write is
    // confusing. Volatile.Read provides acquire semantics, Volatile.Write
    // provides release semantics, which is exactly what we need.
    private bool _zcActive;
    private ulong _deferredReadIdxTarget; // furthest absolute idx wanting commit

    // ===== Multi-frame chain ZC state =====
    //
    // Chain modes for a multi-frame logical message:
    //
    //   * Full chain ZC: every frame ZC. The chain anchor is opened on the
    //     first frame; readIdx is frozen for the duration. Eligibility
    //     decided ONCE on the first frame: <c>totalMsg ≤ ChainZcBudget</c>.
    //
    //   * Pure copy: every frame copies; no anchor opens. Used when chain
    //     start eligibility fails (totalMsg too big, wrap, ZC disabled, or
    //     sub-MinZc).
    //
    // Why no tail-ZC? The savings would be at most ChainZcBudget (~cap)
    // worth of memcpy on the codec→pool boundary, but the upper-layer
    // protobuf parser already needs a contiguous buffer for messages that
    // span multiple segments. The deserializer copies the multi-segment
    // ROS into one Rented buffer, paying back the memcpy we saved. Net
    // gain on big messages is small (one fewer Rent, but same byte
    // movement). Keeping the codec simple — single mode decision per
    // message — is worth more than the marginal gain.
    //
    // <c>_chainOpen</c>: codec marks <c>true</c> when the anchor opens
    // (start of full ZC); marks <c>false</c> when emitting the chain's
    // final (More=0) frame. <see cref="FramePayload.Release"/> reads it:
    // <see cref="EndZcReservation"/> may fire only when the chain is closed
    // (<c>!_chainOpen</c>) AND no more ZC frames are held
    // (<c>SpeculativeReservedBytes == 0</c>).
    //
    // <c>_chainCopyMode</c>: set when chain ZC was rejected. Cleared on
    // the message's final frame.

    private bool _chainOpen;
    private bool _chainCopyMode;

    /// <summary>True while a speculative-ZC anchor is held (deferred-publish active).</summary>
    internal bool IsZcChainActive => Volatile.Read(ref _zcActive);

    /// <summary>True between codec's chain-start and chain-end (codec view).</summary>
    internal bool IsChainOpen => Volatile.Read(ref _chainOpen);

    /// <summary>Codec entered a multi-frame chain. Pairs with <see cref="CloseZcChain"/>.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void OpenZcChain() => Volatile.Write(ref _chainOpen, true);

    /// <summary>
    /// Codec emitted a chain's final frame.
    /// </summary>
    /// <remarks>
    /// In the typical (regular ZC) path, the consumer's last
    /// <see cref="FramePayload.Release"/> on a still-held ZC frame fires
    /// <see cref="EndZcReservation"/> via the
    /// <c>(remaining == 0 &amp;&amp; !IsChainOpen)</c> gate inside
    /// <see cref="FramePayload.Release"/>: the regular ZC path's last
    /// frame increments <see cref="SpeculativeReservedBytes"/> just
    /// before this <c>CloseZcChain</c> call, so SpecReserved &gt; 0 at
    /// close time and the consumer's later Release of that frame will
    /// satisfy the gate.
    /// <para>
    /// Wrap-copy chain end (chain opened on a contiguous first frame
    /// but the chain's final frame fell to the copy path due to a
    /// reservation that wraps the ring boundary) breaks this invariant:
    /// the wrap-copy last frame does NOT increment SpecReserved. If a
    /// concurrent consumer happens to have released ALL preceding ZC
    /// frames before the reader gets here, SpecReserved is already 0
    /// and no future Release on a pooled (wrap-copy) FramePayload can
    /// observe the gate — <see cref="_zcActive"/> stays <c>true</c>
    /// forever, <c>header.ReadIdx</c> stays frozen, and ring capacity
    /// permanently shrinks from the writer's perspective.
    /// </para>
    /// <para>
    /// We close that race by firing <see cref="EndZcReservation"/>
    /// defensively here when SpecReserved is observed at 0 immediately
    /// after the close. <c>EndZcReservation</c> is idempotent
    /// (<see cref="PublishTarget"/> is a CAS loop, the
    /// <c>_zcActive=false</c> write is plain), so the rare race where a
    /// concurrent Release also fires it is harmless.
    /// </para>
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void CloseZcChain()
    {
        Volatile.Write(ref _chainOpen, false);
        if (Volatile.Read(ref SpeculativeReservedBytes) == 0
            && Volatile.Read(ref _zcActive))
        {
            EndZcReservation();
        }
    }

    /// <summary>
    /// Codec-local: marks the in-flight multi-frame message as committed to
    /// the copy path. Reset on the message's final frame so the next
    /// message's first frame is re-evaluated.
    /// </summary>
    internal bool ChainCopyMode
    {
        get => Volatile.Read(ref _chainCopyMode);
        set => Volatile.Write(ref _chainCopyMode, value);
    }

    /// <summary>
    /// Maximum bytes a chain ZC anchor may hold.
    /// <para>
    /// <b>Single-stream mode</b>: <c>cap - SmallReserve</c> (where
    /// <c>SmallReserve</c> covers the worst-case wire-frame headers and
    /// LPM prefix the writer needs in flight). The client only sends the
    /// next request after receiving the previous response, so a single
    /// in-flight message holding nearly the whole ring is safe — the
    /// writer naturally pauses until the chain anchor releases.
    /// </para>
    /// <para>
    /// <b>Multi-stream mode (default)</b>: <c>cap / 2</c>. Under multi-
    /// stream pipelining the writer may want to start emitting another
    /// message's first frame before the consumer has finished parsing
    /// the current chain. Holding nearly the whole ring would deadlock:
    /// the next message's first frame can't be reserved, and the chain
    /// anchor only releases when the consumer parses the current message
    /// — which can't happen if the next frame never arrives.
    /// </para>
    /// </summary>
    internal long ChainZcBudget
    {
        get
        {
            if (!SingleStreamMode)
            {
                return (long)(_capacity / 2);
            }
            // Reserve enough for a worst-case 4-frame H2 chain
            // (4 × 9 B header) + the 5-byte gRPC LPM prefix, rounded up
            // to 1 KiB for breathing room.
            const ulong SmallReserve = 1024UL;
            return _capacity > SmallReserve
                ? (long)(_capacity - SmallReserve)
                : (long)_capacity;
        }
    }

    /// <summary>
    /// Begins a speculative-zero-copy reservation. Subsequent
    /// <see cref="CommitReadRaw"/> calls are deferred (do not touch shared
    /// <c>header.ReadIdx</c>) until <see cref="EndZcReservation"/> is called.
    /// </summary>
    /// <remarks>
    /// Caller must guarantee no other ZC is in flight on this ring (the
    /// consumer is single-threaded; the existing
    /// <c>Volatile.Read(SpeculativeReservedBytes) == 0</c> gate enforces this).
    /// Pair every call with <see cref="EndZcReservation"/> via
    /// <see cref="FramePayload.Release"/>.
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void BeginZcReservation(ulong baseIdx)
    {
        // Initialise the deferred target to the ZC frame's start so that
        // even if no frames follow, EndZc has something coherent to publish.
        // The ZC's own CommitReadRaw call (made right after BeginZc) will
        // bump it to baseIdx + totalBytes.
        //
        // Ordering: write the target FIRST, then set _zcActive. Volatile.Write
        // on _zcActive provides a release barrier so any reader-thread
        // CommitReadRaw that observes _zcActive=true (volatile-read, acquire)
        // will see the initialised target value, never a leftover stale value
        // from the previous ZC cycle.
        Volatile.Write(ref _deferredReadIdxTarget, baseIdx);
        Volatile.Write(ref _zcActive, true);
    }

    /// <summary>
    /// Single-frame speculative-ZC fast path: fuses
    /// <see cref="BeginZcReservation"/> + the frame's own
    /// <see cref="CommitReadRaw"/>-deferred bump into one atomic
    /// sequence. The reader thread is single-threaded so no other
    /// <see cref="CommitReadRaw"/> can race between Begin and the
    /// frame's own deferred bump; we therefore set
    /// <c>_deferredReadIdxTarget</c> directly to its post-frame value
    /// instead of doing the standard
    /// <c>(write base) → (read base) → (write base+totalBytes)</c>
    /// triple-step. Saves 1 Volatile.Read and 1 Volatile.Write per
    /// single-frame ZC compared to the two-call sequence.
    /// </summary>
    /// <remarks>
    /// Only safe when:
    ///   - This is a SINGLE-frame ZC anchor (no chain follows). Multi-
    ///     frame chains must use the separate
    ///     <see cref="BeginZcReservation"/> + per-frame
    ///     <see cref="CommitReadRaw"/> sequence so that intervening
    ///     non-chain frames committed during the chain hold are also
    ///     captured into <c>_deferredReadIdxTarget</c>.
    ///   - At-most-one-ZC FIFO invariant holds (caller already verified
    ///     <see cref="SpeculativeReservedBytes"/> == 0).
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void BeginSingleFrameZcCommit(ulong baseIdx, int totalBytes)
    {
        // Set the deferred target to its FINAL value right away. Same
        // ordering invariant as BeginZcReservation: target FIRST, then
        // _zcActive. The Volatile.Write on _zcActive provides the release
        // barrier so any subsequent reader observing _zcActive=true sees
        // the post-frame target value, not stale.
        Volatile.Write(ref _deferredReadIdxTarget, baseIdx + (ulong)totalBytes);
        Volatile.Write(ref _zcActive, true);
    }

    /// <summary>
    /// Ends the in-flight ZC reservation: publishes the deferred read index
    /// to the shared <c>header.ReadIdx</c>, releasing all bytes consumed
    /// during the ZC hold (the ZC frame itself plus any non-ZC frames that
    /// were committed-deferred while ZC was active).
    /// </summary>
    /// <remarks>
    /// CRITICAL ordering: PUBLISH header.ReadIdx FIRST, then clear
    /// <see cref="_zcActive"/>. The reverse order has a window where a
    /// concurrent reader-thread <see cref="CommitReadRaw"/> observes
    /// <c>_zcActive=false</c> but <c>header.ReadIdx</c> is still at the ZC
    /// start (we haven't CAS'd yet); the reader then takes the
    /// immediate-publish branch with a STALE
    /// <c>baseCommitReadIdx</c> (= ZC start) and CASes <c>header.ReadIdx</c>
    /// to a value INSIDE the still-held ZC region. Cross-process writer
    /// then sees those bytes as free, wraps onto them, and corrupts the
    /// in-flight payload — observed in stress as
    /// <c>SHM_LPM_ASSERT declared=0, payload all-zero</c>.
    /// <para>
    /// After clearing <c>_zcActive</c>, a small race window remains where
    /// the reader bumped <c>_deferredReadIdxTarget</c> with
    /// <c>_zcActive=true</c> still observed but our publish happened with
    /// the older snapshot. We catch that by re-reading and re-publishing
    /// in a loop until <c>_deferredReadIdxTarget</c> is fully reflected
    /// in <c>header.ReadIdx</c>. Once <c>_zcActive</c> is false, no further
    /// bumps can occur (reader's <see cref="CommitReadRaw"/> goes to the
    /// CAS path), so the loop terminates after at most one extra iteration.
    /// </para>
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void EndZcReservation()
    {
        ref var header = ref GetHeader();
        var target = Volatile.Read(ref _deferredReadIdxTarget);

        // Phase 1: publish target while _zcActive is still true. Any concurrent
        // reader-thread CommitReadRaw still sees _zcActive=true and stays on
        // the deferred path, which only bumps _deferredReadIdxTarget — never
        // touches header.ReadIdx. So our CAS here is uncontended on the
        // reader-thread side; only cross-process EndZcReservation-on-the-other-
        // side could compete (impossible: ZC is per-direction).
        PublishTarget(ref header, target);

        // Phase 2: drop the active flag. From this point the reader thread
        // will route future CommitReadRaw calls through the CAS path. A frame
        // whose baseCommitReadIdx was captured before our publish has
        // newReadIdx <= target and is a no-op (CAS sees current >= newReadIdx).
        Volatile.Write(ref _zcActive, false);

        // Phase 3: catch the small window where the reader bumped
        // _deferredReadIdxTarget after our Volatile.Read(target) above but
        // before Volatile.Write(_zcActive=false). Such a bump would have been
        // routed to the deferred path (reader saw _zcActive=true) and is
        // therefore NOT reflected in header.ReadIdx yet. Publish it now.
        var refreshed = Volatile.Read(ref _deferredReadIdxTarget);
        if (refreshed > target)
        {
            PublishTarget(ref header, refreshed);
        }

        SignalSpaceAvailability(ref header);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void PublishTarget(ref RingHeader header, ulong target)
    {
        while (true)
        {
            var current = Volatile.Read(ref header.ReadIdx);
            if (target <= current) return;
            if (Interlocked.CompareExchange(ref header.ReadIdx, target, current) == current)
                return;
        }
    }

    /// <summary>Reads the current WriteIdx (for speculative safety checks).</summary>
    internal ulong PeekWriteIdx()
    {
        ref var header = ref GetHeader();
        return Volatile.Read(ref header.WriteIdx);
    }

    /// <summary>
    /// Returns the current pending read index (bytes reserved but not yet committed).
    /// Used by speculative CommitRead to commit all bytes up to the current position,
    /// including any deferred frames that precede the speculative frame.
    /// </summary>
    internal ulong PeekPendingReadIdx() => Volatile.Read(ref _pendingReadIdx);

    /// <summary>
    /// Approximate "ring used bytes from the writer's perspective" —
    /// <c>writeIdx - readIdx</c>. Used by the ZC fast path as a back-pressure
    /// hint: if the ring is already heavily occupied, deferring more reads
    /// (which is what ZC effectively does until the consumer releases) would
    /// risk stalling the writer. In that case it is better to take the copy
    /// path so the reader can publish ReadIdx promptly.
    /// </summary>
    /// <remarks>
    /// Approximate because we read writeIdx and readIdx without a fence
    /// between them; the value can momentarily underestimate or overestimate.
    /// That is acceptable: this is just a heuristic, not a correctness gate.
    /// </remarks>
    internal ulong UsedBytesApprox()
    {
        ref var header = ref GetHeader();
        var writeIdx = Volatile.Read(ref header.WriteIdx);
        var readIdx = Volatile.Read(ref header.ReadIdx);
        return writeIdx - readIdx;
    }

    /// <summary>
    /// Centralised speculative-ZC eligibility check used by the HTTP/2
    /// wire-format reader.
    /// </summary>
    /// <param name="payloadLength">Byte length of the candidate frame payload (excluding wire headers).</param>
    /// <param name="contiguous"><c>true</c> if the payload reservation is contiguous (no ring wrap).</param>
    /// <returns><c>true</c> if speculative-ZC is allowed; <c>false</c> if the caller should take the copy path.</returns>
    /// <remarks>
    /// <para><b>Adaptive minimum payload threshold</b>: 64 KiB on rings ≥ 1 MiB
    /// (large enough that a 64 KiB ZC hold leaves >> 90% of the ring free for
    /// the writer); progressively smaller on smaller rings so ZC stays useful
    /// for the dominant message size in those scenarios. Below 4 KiB ZC never
    /// pays off (memcpy of <4 KiB is ~250 ns; speculative-ZC bookkeeping
    /// alone costs ~50 ns plus a CAS for cross-process publish on Release).
    /// </para>
    /// <para><b>Why the ring-size gate matters</b>: the ZC's deferred-publish
    /// window holds <c>header.ReadIdx</c> at the ZC frame's start until the
    /// consumer releases. On a 256 KiB ring even a single 64 KiB ZC frame
    /// freezes 25% of capacity for the consumer's parse duration; pipelined
    /// writes then stall the writer. We disable ZC entirely below 1 MiB so
    /// the heuristic only kicks in where ring headroom is plentiful.</para>
    /// <para><b>Back-pressure self-disable</b>: if the ring is already
    /// &gt; 75% full (used×4 &gt; cap×3), taking ZC would risk stalling the
    /// writer. Fall through to copy so <c>header.ReadIdx</c> keeps advancing
    /// per-frame. This makes ZC effectively a low-to-medium-concurrency
    /// optimisation that self-disables under sustained pressure.</para>
    /// <para><b>At-most-one-ZC</b>: <see cref="SpeculativeReservedBytes"/> ==
    /// 0 enforces a single ZC payload in flight per ring. The deferred-publish
    /// protocol assumes a single producer of bumps to
    /// <see cref="_deferredReadIdxTarget"/>; multiple concurrent ZC frames
    /// would require multi-producer ordering not yet implemented.</para>
    /// </remarks>
    internal bool IsSpeculativeZcEligible(int payloadLength, bool contiguous)
    {
        if (!contiguous) return false;

        // Disable ZC entirely on rings below 1 MiB — see remarks.
        const ulong MinRingForZeroCopy = 1024UL * 1024UL;
        if (_capacity < MinRingForZeroCopy) return false;

        // Adaptive minimum payload: scales down on smaller rings so ZC
        // stays useful, but never below 4 KiB where memcpy is faster than
        // ZC bookkeeping. cap/16 means a single ZC frame never holds more
        // than ~6.25% of the ring (very conservative; lots of headroom for
        // the writer to keep producing while the consumer parses).
        var adaptiveMin = (int)Math.Min(64UL * 1024UL, _capacity / 16);
        if (adaptiveMin < 4 * 1024) adaptiveMin = 4 * 1024;
        if (payloadLength < adaptiveMin) return false;

        // At-most-one-ZC.
        if (Volatile.Read(ref SpeculativeReservedBytes) != 0) return false;

        // Back-pressure auto-degrade — see remarks.
        if (UsedBytesApprox() * 4 > _capacity * 3) return false;

        return true;
    }


    /// <summary>
    /// Checks whether a contiguous write of <paramref name="size"/> bytes is
    /// possible without wrap-around. Safe to call from the sole writer thread
    /// while no other writer is active (e.g., WriterLoop is paused).
    /// </summary>
    internal bool HasContiguousWriteSpace(int size)
    {
        ref var header = ref GetHeader();
        var writeIdx = Volatile.Read(ref header.WriteIdx);
        var readIdx = Volatile.Read(ref header.ReadIdx);
        var used = writeIdx - readIdx;
        if ((ulong)size > _capacity - used)
            return false;
        var writePos = writeIdx & _capMask;
        return writePos + (ulong)size <= _capacity;
    }

    /// <summary>
    /// Returns the number of contiguous bytes available at the current write
    /// position before the ring wraps. Returns 0 if the ring is full.
    /// </summary>
    internal ulong ContiguousWriteSpace()
    {
        ref var header = ref GetHeader();
        var writeIdx = Volatile.Read(ref header.WriteIdx);
        var readIdx = Volatile.Read(ref header.ReadIdx);
        var used = writeIdx - readIdx;
        var free = _capacity - used;
        if (free == 0) return 0;
        var writePos = writeIdx & _capMask;
        var tailSpace = _capacity - writePos;
        return tailSpace < free ? tailSpace : free;
    }

    /// <summary>
    /// Gets whether the ring is closed.
    /// </summary>
    public bool IsClosed => _localClosed || GetHeader().Closed != 0;

    /// <summary>
    /// Gets a snapshot of the current ring state for debugging.
    /// </summary>
    public RingState GetState()
    {
        if (_localClosed)
        {
            return new RingState { Capacity = _capacity, Closed = true };
        }

        ref var header = ref GetHeader();
        return new RingState
        {
            Capacity = _capacity,
            WriteIdx = Volatile.Read(ref header.WriteIdx),
            ReadIdx = Volatile.Read(ref header.ReadIdx),
            DataSeq = Volatile.Read(ref header.DataSeq),
            SpaceSeq = Volatile.Read(ref header.SpaceSeq),
            ContigSeq = Volatile.Read(ref header.ContigSeq),
            Closed = header.Closed != 0,
            DataWaiters = Volatile.Read(ref header.DataWaiters),
            SpaceWaiters = Volatile.Read(ref header.SpaceWaiters)
        };
    }

    /// <summary>
    /// Writes data to the ring buffer, blocking until space is available.
    /// </summary>
    /// <param name="data">The data to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="RingClosedException">Thrown if the ring is closed.</exception>
    /// <exception cref="OperationCanceledException">Thrown if cancelled.</exception>
    public void Write(ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty)
        {
            return;
        }

        if ((ulong)data.Length > _capacity)
        {
            throw new ArgumentException($"Data ({data.Length} bytes) exceeds ring capacity ({_capacity} bytes)", nameof(data));
        }

        ref var header = ref GetHeader();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_localClosed || header.Closed != 0)
            {
                throw new RingClosedException();
            }

            var writeIdx = Volatile.Read(ref header.WriteIdx);
            var readIdx = Volatile.Read(ref header.ReadIdx);
            var available = ComputeAvailableForWrite(writeIdx, readIdx);

            if ((ulong)data.Length <= available)
            {
                // Space available - perform the write
                var writePos = writeIdx & _capMask;
                var dataSpan = GetDataSpan();

                if (writePos + (ulong)data.Length <= _capacity)
                {
                    // Simple case: no wrap
                    data.CopyTo(dataSpan.Slice((int)writePos, data.Length));
                }
                else
                {
                    // Wrap case: split the write
                    var firstChunk = (int)(_capacity - writePos);
                    data[..firstChunk].CopyTo(dataSpan.Slice((int)writePos, firstChunk));
                    data[firstChunk..].CopyTo(dataSpan[..(data.Length - firstChunk)]);
                }

                // Publish new write index (release semantics)
                Volatile.Write(ref header.WriteIdx, writeIdx + (ulong)data.Length);

                // Signal waiters
                if (data.Length > 0)
                {
                    Interlocked.Increment(ref header.DataSeq);
                    if (_batchWriteDepth == 0 && Volatile.Read(ref header.DataWaiters) > 0)
                    {
                        _sync?.SignalData();
                    }
                }

                return;
            }

            // Not enough space - wait for it
            WaitForSpace(ref header, (ulong)data.Length, cancellationToken);
        }
    }

    /// <summary>
    /// Reads data from the ring buffer, blocking until data is available.
    /// </summary>
    /// <param name="buffer">The buffer to read into.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of bytes read.</returns>
    /// <exception cref="RingClosedException">Thrown if the ring is closed and empty.</exception>
    /// <exception cref="OperationCanceledException">Thrown if cancelled.</exception>
    public int Read(Span<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        ref var header = ref GetHeader();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var writeIdx = Volatile.Read(ref header.WriteIdx);
            var readIdx = Volatile.Read(ref header.ReadIdx);
            var used = writeIdx - readIdx;

            if (used > 0)
            {
                // Data available - perform the read
                var toRead = Math.Min((ulong)buffer.Length, used);
                var readPos = readIdx & _capMask;
                var dataSpan = GetDataSpan();
                var bytesRead = 0;

                if (readPos + toRead <= _capacity)
                {
                    // Simple case: no wrap
                    dataSpan.Slice((int)readPos, (int)toRead).CopyTo(buffer);
                    bytesRead = (int)toRead;
                }
                else
                {
                    // Wrap case: split the read
                    var firstChunk = (int)(_capacity - readPos);
                    dataSpan.Slice((int)readPos, firstChunk).CopyTo(buffer);
                    var secondChunk = (int)toRead - firstChunk;
                    dataSpan[..secondChunk].CopyTo(buffer[firstChunk..]);
                    bytesRead = (int)toRead;
                }

                // Publish new read index (release semantics)
                Volatile.Write(ref header.ReadIdx, readIdx + (ulong)bytesRead);

                // Signal space availability
                if (bytesRead > 0)
                {
                    if (Volatile.Read(ref header.ContigWaiters) > 0)
                    {
                        Interlocked.Increment(ref header.ContigSeq);
                        _sync?.SignalContig();
                    }

                    if (Volatile.Read(ref header.SpaceWaiters) > 0)
                    {
                        Interlocked.Increment(ref header.SpaceSeq);
                        _sync?.SignalSpace();
                    }
                }

                return bytesRead;
            }

            // Check if closed with no data
            if (_localClosed || header.Closed != 0)
            {
                throw new RingClosedException();
            }

            // No data - wait for it
            WaitForData(ref header, cancellationToken);
        }
    }

    /// <summary>
    /// Reserves space for writing, returning slices for zero-copy writes.
    /// The reservation must be committed via CommitWrite.
    /// </summary>
    /// <param name="size">The number of bytes to reserve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A write reservation containing memory slices.</returns>
    public WriteReservation ReserveWrite(int size, CancellationToken cancellationToken = default)
    {
        if (size <= 0)
        {
            throw new ArgumentException("Size must be positive", nameof(size));
        }

        if ((ulong)size > _capacity)
        {
            throw new ArgumentException($"Size ({size} bytes) exceeds ring capacity ({_capacity} bytes)", nameof(size));
        }

        ref var header = ref GetHeader();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_localClosed || header.Closed != 0)
            {
                throw new RingClosedException();
            }

            var writeIdx = Volatile.Read(ref header.WriteIdx);
            var readIdx = Volatile.Read(ref header.ReadIdx);
            var available = ComputeAvailableForWrite(writeIdx, readIdx);

            if ((ulong)size <= available)
            {
                var writePos = writeIdx & _capMask;

                Memory<byte> first, second;

                if (writePos + (ulong)size <= _capacity)
                {
                    // No wrap needed
                    first = _memory.Slice(_dataOffset + (int)writePos, size);
                    second = Memory<byte>.Empty;
                }
                else
                {
                    // Wrap case
                    var firstLen = (int)(_capacity - writePos);
                    first = _memory.Slice(_dataOffset + (int)writePos, firstLen);
                    second = _memory.Slice(_dataOffset, size - firstLen);
                }

                return new WriteReservation
                {
                    First = first,
                    Second = second,
                    Ring = this,
                    WriteIdx = writeIdx,
                    MaxBytes = size
                };
            }

            WaitForSpace(ref header, (ulong)size, cancellationToken);
        }
    }

    /// <summary>
    /// Commits a write reservation, publishing the written bytes.
    /// </summary>
    /// <param name="reservation">The reservation to commit.</param>
    /// <param name="bytesWritten">The number of bytes actually written (must not exceed reservation size).</param>
    public void CommitWrite(WriteReservation reservation, int bytesWritten)
    {
        if (reservation.Ring != this)
        {
            throw new ArgumentException("Reservation is not for this ring", nameof(reservation));
        }

        if (bytesWritten < 0 || bytesWritten > reservation.MaxBytes)
        {
            throw new ArgumentException($"Invalid bytes written: {bytesWritten}. Must be 0-{reservation.MaxBytes}", nameof(bytesWritten));
        }

        if (_localClosed)
        {
            return;
        }

        ref var header = ref GetHeader();

        // Publish new write index
        Volatile.Write(ref header.WriteIdx, reservation.WriteIdx + (ulong)bytesWritten);

        // Signal waiters
        if (bytesWritten > 0)
        {
            Interlocked.Increment(ref header.DataSeq);
            if (_batchWriteDepth == 0 && Volatile.Read(ref header.DataWaiters) > 0)
            {
                _sync?.SignalData();
            }
        }
    }

    /// <summary>
    /// Begins a batch write. OS-level data signals are deferred until
    /// <see cref="EndBatchWrite"/>. DataSeq is still incremented per commit
    /// so spin waiters see updates immediately.
    /// </summary>
    internal void BeginBatchWrite() => _batchWriteDepth++;

    /// <summary>
    /// Ends a batch write and fires the deferred OS-level data signal
    /// if any waiter is blocking.
    /// </summary>
    internal void EndBatchWrite()
    {
        if (--_batchWriteDepth <= 0)
        {
            _batchWriteDepth = 0;
            ref var header = ref GetHeader();
            if (Volatile.Read(ref header.DataWaiters) > 0)
            {
                _sync?.SignalData();
            }
        }
    }

    /// <summary>
    /// Reserves bytes for reading, returning slices for zero-copy reads.
    /// The reservation must be committed via CommitRead.
    /// </summary>
    /// <param name="size">The number of bytes to reserve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A read reservation containing memory slices.</returns>
    public ReadReservation ReserveRead(int size, CancellationToken cancellationToken = default)
    {
        if (size <= 0)
        {
            throw new ArgumentException("Size must be positive", nameof(size));
        }

        ref var header = ref GetHeader();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var writeIdx = Volatile.Read(ref header.WriteIdx);
            var pendingIdx = Volatile.Read(ref _pendingReadIdx);
            var available = writeIdx - pendingIdx;

            // If closed, allow draining remaining data
            if ((_localClosed || header.Closed != 0) && available == 0)
            {
                throw new RingClosedException();
            }

            if (available >= (ulong)size)
            {
                var readPos = pendingIdx & _capMask;
                ReadOnlyMemory<byte> first, second;

                if (readPos + (ulong)size <= _capacity)
                {
                    // No wrap needed
                    first = _memory.Slice(_dataOffset + (int)readPos, size);
                    second = ReadOnlyMemory<byte>.Empty;
                }
                else
                {
                    // Wrap case
                    var firstLen = (int)(_capacity - readPos);
                    first = _memory.Slice(_dataOffset + (int)readPos, firstLen);
                    second = _memory.Slice(_dataOffset, size - firstLen);
                }

                // Advance pending read index
                Volatile.Write(ref _pendingReadIdx, pendingIdx + (ulong)size);

                return new ReadReservation
                {
                    First = first,
                    Second = second,
                    Ring = this,
                    CommitReadIdx = Volatile.Read(ref header.ReadIdx),
                    MaxBytes = size
                };
            }

            // If closed with insufficient data
            if (_localClosed || header.Closed != 0)
            {
                throw new RingClosedException();
            }

            // Wait for the writer to advance PAST our local pending index
            // (the byte boundary up to which the reader has already
            // committed reservations to itself). Using <c>_pendingReadIdx</c>
            // here — instead of the shared <c>header.ReadIdx</c> — is what
            // makes the wait composable with speculative-ZC: while a ZC
            // anchor is held the shared index is intentionally frozen, so
            // a watermark of <c>header.ReadIdx</c> would always satisfy
            // <c>writeIdx &gt; watermark</c> and cause this loop to spin at
            // 100% CPU until the consumer's Release advances the shared
            // index. <c>_pendingReadIdx</c> is the right semantic anchor:
            // the reader has already "claimed" everything up to it, and we
            // genuinely want to wait until NEW bytes arrive past that point.
            WaitForDataAfter(ref header, pendingIdx, cancellationToken);
        }
    }

    /// <summary>
    /// Commits a read reservation, freeing space for the writer.
    /// </summary>
    public void CommitRead(ReadReservation reservation, int bytesConsumed)
    {
        if (reservation.Ring != this)
        {
            throw new ArgumentException("Reservation is not for this ring", nameof(reservation));
        }

        if (bytesConsumed < 0 || bytesConsumed > reservation.MaxBytes)
        {
            throw new ArgumentException($"Invalid bytes consumed: {bytesConsumed}. Must be 0-{reservation.MaxBytes}", nameof(bytesConsumed));
        }

        if (_localClosed)
        {
            return;
        }

        if (bytesConsumed == 0)
        {
            return;
        }

        ref var header = ref GetHeader();

        var newReadIdx = reservation.CommitReadIdx + (ulong)bytesConsumed;
        while (true)
        {
            var current = Volatile.Read(ref header.ReadIdx);
            if (newReadIdx <= current)
            {
                return;
            }
            if (Interlocked.CompareExchange(ref header.ReadIdx, newReadIdx, current) == current)
            {
                break;
            }
        }
        SignalSpaceAvailability(ref header);
    }

    /// <summary>
    /// Batched commit: advances ReadIdx by <paramref name="totalBytesConsumed"/>
    /// from a saved base index. Used by ReadFramePayload to commit both header
    /// and payload reads in a single shared-memory write, halving the per-frame
    /// write traffic on the read path.
    /// </summary>
    /// <remarks>
    /// While a speculative ZC frame is in flight (<see cref="_zcActive"/>),
    /// this DEFERS the actual <c>header.ReadIdx</c> advance: it just bumps
    /// the local <see cref="_deferredReadIdxTarget"/>. The cross-process
    /// writer keeps seeing the OLD <c>header.ReadIdx</c> (= the start of
    /// the held ZC region), so its <c>used = writeIdx - readIdx</c> formula
    /// correctly accounts for the held bytes without any shared-memory ZC
    /// field. <see cref="EndZcReservation"/> publishes the deferred target
    /// in one shot when the ZC payload is released.
    /// </remarks>
    internal void CommitReadRaw(ulong baseCommitReadIdx, int totalBytesConsumed)
    {
        if (_localClosed || totalBytesConsumed == 0)
            return;

        var newReadIdx = baseCommitReadIdx + (ulong)totalBytesConsumed;

        // ZC-active path: defer the shared-memory write; advance
        // _deferredReadIdxTarget by the bytes the caller is committing.
        //
        // CRITICAL: we use ADDITIVE accumulation here (target += bytes), not
        // the absolute (baseCommitReadIdx + bytes) formula used in the
        // non-deferred branch. Reason: while ZC is active, header.ReadIdx is
        // FROZEN at the ZC frame's start (= baseZc). Every subsequent
        // ReserveRead call captures the STALE header.ReadIdx as its
        // baseCommitReadIdx (= baseZc). A naive (staleBase + perFrameBytes)
        // formula yields a value at most ~one frame past baseZc — far behind
        // the actual cumulative consumed position once several frames have
        // been parsed. The previous max-tracking workaround
        // (`if (newReadIdx > target)`) silently dropped these updates,
        // causing two problems:
        //   (1) RACE: EndZcReservation publishes a target that omits all
        //       deferred frames after the ZC frame; if a CommitReadRaw on
        //       the reader thread then sees _zcActive=false and races on the
        //       CAS path with a stale base, header.ReadIdx can be set to a
        //       value INSIDE the still-held ZC region (writer wraps onto
        //       it → SHM_LPM_ASSERT declared=0, payload all-zero observed
        //       under H2+SingleStream stress).
        //   (2) LEAK: even without the race, ring space for deferred-and-
        //       not-published frames is permanently lost from the writer's
        //       view, accumulating with each ZC episode.
        // Reader is single-threaded, so additive accumulation is race-free
        // on the local field. The Volatile.Write/Read pair with EndZc's
        // _zcActive flag transition guarantees EndZc sees the up-to-date
        // value. After EndZc publishes and clears _zcActive, the next
        // CommitReadRaw observes _zcActive=false, captures a fresh
        // baseCommitReadIdx (= published target) on its NEXT ReserveRead,
        // and resumes correct absolute-formula commits.
        if (Volatile.Read(ref _zcActive))
        {
            var bumped = Volatile.Read(ref _deferredReadIdxTarget) + (ulong)totalBytesConsumed;
            Volatile.Write(ref _deferredReadIdxTarget, bumped);
            return;
        }

        ref var header = ref GetHeader();

        // When ZeroCopyRead is active, multiple frames may share the same
        // baseCommitReadIdx (the shared ReadIdx at reservation time) but
        // their deferred Release calls arrive in arbitrary order.
        // Use a CAS loop to ensure ReadIdx only moves forward — a later
        // Release with an earlier baseCommitReadIdx must not regress it.
        while (true)
        {
            var current = Volatile.Read(ref header.ReadIdx);
            if (newReadIdx <= current)
                return; // Already past this point — nothing to do.
            if (Interlocked.CompareExchange(ref header.ReadIdx, newReadIdx, current) == current)
                break;
        }
        SignalSpaceAvailability(ref header);
    }

    /// <summary>
    /// Signals the writer that space has become available.
    /// </summary>
    private void SignalSpaceAvailability(ref RingHeader header)
    {
        if (Volatile.Read(ref header.ContigWaiters) > 0)
        {
            Interlocked.Increment(ref header.ContigSeq);
            _sync?.SignalContig();
        }

        if (Volatile.Read(ref header.SpaceWaiters) > 0)
        {
            Interlocked.Increment(ref header.SpaceSeq);
            _sync?.SignalSpace();
        }
    }

    /// <summary>
    /// Closes the ring buffer. Readers can still drain remaining data.
    /// Only the owner (server) sets the Closed flag in shared memory.
    /// </summary>
    public void Close()
    {
        if (_localClosed)
        {
            return;
        }

        _localClosed = true;

        // Only the owner (server) should set the Closed flag in shared memory
        // Clients just close locally to stop their own read/write operations
        if (_isOwner)
        {
            ref var header = ref GetHeader();
            Volatile.Write(ref header.Closed, 1);

            // Wake all waiters
            Interlocked.Increment(ref header.DataSeq);
            Interlocked.Increment(ref header.SpaceSeq);
            Interlocked.Increment(ref header.ContigSeq);
        }

        // Signal sync waiters for both owner and non-owner.
        // Non-owner threads may be blocked in futex/WaitOnAddress;
        // without a wake, they'd remain blocked until the memory is unmapped,
        // causing an access violation in the finally block.
        _sync?.SignalData();
        _sync?.SignalSpace();
        _sync?.SignalContig();
    }

    public void Dispose()
    {
        Close();
        _sync?.Dispose();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref RingHeader GetHeader()
    {
        return ref MemoryMarshal.AsRef<RingHeader>(_memory.Span.Slice(_headerOffset, ShmConstants.RingHeaderSize));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Span<byte> GetDataSpan()
    {
        return _memory.Span.Slice(_dataOffset, (int)_capacity);
    }

    private void WaitForSpace(ref RingHeader header, ulong needed, CancellationToken cancellationToken)
    {
        // Adaptive spin before blocking
        var spinLimit = Volatile.Read(ref _spaceSpinCutoff);
        for (var i = 0; i < spinLimit; i++)
        {
            var writeIdx = Volatile.Read(ref header.WriteIdx);
            var readIdx = Volatile.Read(ref header.ReadIdx);
            if (_capacity - (writeIdx - readIdx) >= needed)
            {
                // Success - adapt spin limit: maintain or raise when we
                // waited longer than 75% of the limit.
                if (i > 0)
                {
                    var newCutoff = i > (spinLimit * 3 / 4)
                        ? Math.Min(ShmConstants.SpinIterationsMax, spinLimit + spinLimit / 8)
                        : spinLimit;
                    Volatile.Write(ref _spaceSpinCutoff, Math.Max(ShmConstants.SpinIterationsMin, newCutoff));
                }
                return;
            }

            if (header.Closed != 0 || _localClosed)
            {
                throw new RingClosedException();
            }

            Thread.SpinWait(1);
        }

        // Spin failed - adapt downward and fall back to blocking
        var reducedCutoff = (7 * spinLimit + ShmConstants.SpinIterationsMin) / 8;
        Volatile.Write(ref _spaceSpinCutoff, Math.Max(ShmConstants.SpinIterationsMin, reducedCutoff));

        // Distinguish full vs partial: if ring is completely full, wait on spaceSeq
        // (bumped only when transitioning from full to not-full). If ring has some
        // space but not enough, wait on contigSeq (bumped on every read commit),
        // matching grpc-go-shmem's ReserveWrite behavior.
        var writeIdx2 = Volatile.Read(ref header.WriteIdx);
        var readIdx2 = Volatile.Read(ref header.ReadIdx);
        var free = _capacity - (writeIdx2 - readIdx2);

        // Before blocking, drain any pending control frames (WindowUpdate)
        // so the remote side can free ring space. Without this, both sides'
        // WriterLoops can block waiting for space while WindowUpdates sit
        // unreachable in the queue.
        // Guard against recursion: DrainControlFrames → WriteFrame → WaitForSpace → drain.
        // Depth 1 is enough — control frames are tiny and always fit if any space exists.
        if (_drainRecursionDepth == 0)
        {
            _drainRecursionDepth++;
            try { WaitForSpaceDrainCallback?.Invoke(); }
            finally { _drainRecursionDepth--; }
        }

        // Re-check after drain — the remote may have freed space
        writeIdx2 = Volatile.Read(ref header.WriteIdx);
        readIdx2 = Volatile.Read(ref header.ReadIdx);
        free = _capacity - (writeIdx2 - readIdx2);
        if (free >= needed) return;

        // Wait for total space to become available. We always wait on
        // SpaceSeq regardless of whether the ring is completely full
        // (free==0) or partially free (free>0 but <needed). The old
        // code used ContigSeq for the partial case, but ReserveWrite
        // accepts wrap-around reservations so contiguity is not required.
        // Using ContigSeq caused deadlocks when deferred CommitRead
        // held ReadIdx back: the writer waited for ContigSeq signals
        // that never came because no further reads occurred.
        Interlocked.Increment(ref header.SpaceWaiters);
        try
        {
            var seq = Volatile.Read(ref header.SpaceSeq);

            // Re-check before blocking
            var wi = Volatile.Read(ref header.WriteIdx);
            var ri = Volatile.Read(ref header.ReadIdx);
            if (_capacity - (wi - ri) >= needed)
            {
                return;
            }

            _sync?.WaitForSpace(seq, timeout: null, cancellationToken);
        }
        finally
        {
            if (!_localClosed)
            {
                Interlocked.Decrement(ref header.SpaceWaiters);
            }
        }
    }

    private void WaitForData(ref RingHeader header, CancellationToken cancellationToken)
    {
        // Wait until any unconsumed data is visible at <c>header.ReadIdx</c>.
        // Used by the byte-stream <see cref="Read(byte[], int, int, CancellationToken)"/>
        // path which advances <c>header.ReadIdx</c> in lock-step with each
        // copy-out. <see cref="ReserveRead"/> uses
        // <see cref="WaitForDataAfter"/> instead because it advances a local
        // <c>_pendingReadIdx</c> while leaving the shared index frozen
        // during a speculative-ZC hold (see remarks on
        // <see cref="WaitForDataAfter"/>).
        WaitForDataAfter(ref header, Volatile.Read(ref header.ReadIdx), cancellationToken);
    }

    /// <summary>
    /// Spins/blocks until <c>header.WriteIdx</c> advances past
    /// <paramref name="watermark"/>.
    /// </summary>
    /// <remarks>
    /// Crucial for <see cref="ReserveRead"/>'s wait path: that method tracks
    /// reader progress in <see cref="_pendingReadIdx"/> rather than
    /// <c>header.ReadIdx</c>, because the shared index is intentionally
    /// frozen while a speculative-ZC reservation is held (cross-process
    /// writers must not wrap onto bytes the reader still holds — see
    /// <see cref="BeginZcReservation"/>). If <c>ReserveRead</c> instead
    /// blocked on the shared <c>header.ReadIdx</c>, the wait would observe
    /// <c>writeIdx &gt; ReadIdx</c> (the as-yet-unreleased ZC bytes) and
    /// return immediately, even when no NEW frames had arrived. The reader
    /// thread would spin at 100% CPU pulling <c>ReserveRead → WaitForData →
    /// (immediate return) → loop</c> until the consumer's
    /// <see cref="FramePayload.Release"/> advanced <c>header.ReadIdx</c> —
    /// degrading the SingleStreamMode/ZC perf path this PR aims to optimise.
    /// </remarks>
    private void WaitForDataAfter(ref RingHeader header, ulong watermark, CancellationToken cancellationToken)
    {
        var spinLimit = Volatile.Read(ref _dataSpinCutoff);
        for (var i = 0; i < spinLimit; i++)
        {
            var writeIdx = Volatile.Read(ref header.WriteIdx);
            if (writeIdx > watermark)
            {
                // Success - adapt spin limit: if we found data within the
                // spin window, keep the cutoff at least at the current level.
                // Previous formula (7*limit + i*2)/8 would reduce the cutoff
                // when i < 3*limit/8, causing unnecessary kernel waits.
                if (i > 0)
                {
                    // Maintain current cutoff or raise slightly if we waited
                    // longer than 75% of the limit.
                    var newCutoff = i > (spinLimit * 3 / 4)
                        ? Math.Min(ShmConstants.SpinIterationsMax, spinLimit + spinLimit / 8)
                        : spinLimit;
                    Volatile.Write(ref _dataSpinCutoff, Math.Max(ShmConstants.SpinIterationsMin, newCutoff));
                }
                return;
            }

            if (header.Closed != 0 || _localClosed)
            {
                throw new RingClosedException();
            }

            Thread.SpinWait(1);
        }

        // Spin failed - adapt downward and fall back to blocking
        var reducedCutoff = (7 * _dataSpinCutoff + ShmConstants.SpinIterationsMin) / 8;
        Volatile.Write(ref _dataSpinCutoff, Math.Max(ShmConstants.SpinIterationsMin, reducedCutoff));

        // Block on sync primitive
        Interlocked.Increment(ref header.DataWaiters);
        try
        {
            var seq = Volatile.Read(ref header.DataSeq);

            // Re-check before blocking
            var writeIdx = Volatile.Read(ref header.WriteIdx);
            if (writeIdx > watermark)
            {
                return;
            }

            // Also check if closed to avoid missing close that happened between checks
            if (header.Closed != 0 || _localClosed)
            {
                throw new RingClosedException();
            }

            _sync?.WaitForData(seq, timeout: null, cancellationToken);
        }
        finally
        {
            if (!_localClosed)
            {
                Interlocked.Decrement(ref header.DataWaiters);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPowerOfTwo(ulong value)
    {
        return value > 0 && (value & (value - 1)) == 0;
    }

    /// <summary>
    /// Gets the number of bytes available to read.
    /// </summary>
    public ulong ReadableBytes
    {
        get
        {
            ref var header = ref GetHeader();
            var writeIdx = Volatile.Read(ref header.WriteIdx);
            var readIdx = Volatile.Read(ref header.ReadIdx);
            return writeIdx - readIdx;
        }
    }

    /// <summary>
    /// Gets the number of bytes available to write.
    /// </summary>
    public ulong WritableBytes
    {
        get
        {
            ref var header = ref GetHeader();
            var writeIdx = Volatile.Read(ref header.WriteIdx);
            var readIdx = Volatile.Read(ref header.ReadIdx);
            var used = writeIdx - readIdx;
            return _capacity - used;
        }
    }

    /// <summary>
    /// Checks if at least the specified number of bytes can be read.
    /// </summary>
    public bool TryPeek(int minBytes, out int available)
    {
        if (_localClosed)
        {
            available = 0;
            return false;
        }

        ref var header = ref GetHeader();
        var writeIdx = Volatile.Read(ref header.WriteIdx);
        var readIdx = Volatile.Read(ref header.ReadIdx);
        var used = (int)(writeIdx - readIdx);
        available = used;
        return used >= minBytes;
    }

    /// <summary>
    /// Checks if at least the specified number of bytes can be written.
    /// </summary>
    public bool CanWrite(int size)
    {
        if (_localClosed)
        {
            return false;
        }

        ref var header = ref GetHeader();
        var writeIdx = Volatile.Read(ref header.WriteIdx);
        var readIdx = Volatile.Read(ref header.ReadIdx);
        var available = ComputeAvailableForWrite(writeIdx, readIdx);
        return (ulong)size <= available;
    }

    /// <summary>
    /// Tries to read data without blocking.
    /// </summary>
    /// <param name="buffer">The buffer to read into.</param>
    /// <returns>True if data was read, false if no data available.</returns>
    public bool TryRead(Span<byte> buffer)
    {
        if (buffer.IsEmpty || _localClosed)
        {
            return false;
        }

        ref var header = ref GetHeader();
        var writeIdx = Volatile.Read(ref header.WriteIdx);
        var readIdx = Volatile.Read(ref header.ReadIdx);
        var used = writeIdx - readIdx;

        if (used < (ulong)buffer.Length)
        {
            return false; // Not enough data
        }

        // Data available - perform the read
        var readPos = readIdx & _capMask;
        var dataSpan = GetDataSpan();

        if (readPos + (ulong)buffer.Length <= _capacity)
        {
            // Simple case: no wrap
            dataSpan.Slice((int)readPos, buffer.Length).CopyTo(buffer);
        }
        else
        {
            // Wrap case: split the read
            var firstChunk = (int)(_capacity - readPos);
            dataSpan.Slice((int)readPos, firstChunk).CopyTo(buffer);
            var secondChunk = buffer.Length - firstChunk;
            dataSpan[..secondChunk].CopyTo(buffer[firstChunk..]);
        }

        // Publish new read index (release semantics)
        Volatile.Write(ref header.ReadIdx, readIdx + (ulong)buffer.Length);

        // Signal space availability
        if (buffer.Length > 0)
        {
            Interlocked.Increment(ref header.ContigSeq);
            if (Volatile.Read(ref header.ContigWaiters) > 0)
            {
                _sync?.SignalContig();
            }
            if (Volatile.Read(ref header.SpaceWaiters) > 0)
            {
                Interlocked.Increment(ref header.SpaceSeq);
                _sync?.SignalSpace();
            }
        }

        return true;
    }

    /// <summary>
    /// Tries to write data without blocking.
    /// </summary>
    /// <param name="data">The data to write.</param>
    /// <returns>True if data was written, false if not enough space.</returns>
    public bool TryWrite(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return true;
        }

        if (_localClosed || (ulong)data.Length > _capacity)
        {
            return false;
        }

        ref var header = ref GetHeader();

        if (header.Closed != 0)
        {
            return false;
        }

        var writeIdx = Volatile.Read(ref header.WriteIdx);
        var readIdx = Volatile.Read(ref header.ReadIdx);
        var available = ComputeAvailableForWrite(writeIdx, readIdx);

        if ((ulong)data.Length > available)
        {
            return false; // Not enough space
        }

        // Space available - perform the write
        var writePos = writeIdx & _capMask;
        var dataSpan = GetDataSpan();

        if (writePos + (ulong)data.Length <= _capacity)
        {
            // Simple case: no wrap
            data.CopyTo(dataSpan.Slice((int)writePos, data.Length));
        }
        else
        {
            // Wrap case: split the write
            var firstChunk = (int)(_capacity - writePos);
            data[..firstChunk].CopyTo(dataSpan.Slice((int)writePos, firstChunk));
            data[firstChunk..].CopyTo(dataSpan[..(data.Length - firstChunk)]);
        }

        // Publish new write index (release semantics)
        Volatile.Write(ref header.WriteIdx, writeIdx + (ulong)data.Length);

        // Signal waiters
        if (data.Length > 0)
        {
            Interlocked.Increment(ref header.DataSeq);
            if (Volatile.Read(ref header.DataWaiters) > 0)
            {
                _sync?.SignalData();
            }
        }

        return true;
    }
}
