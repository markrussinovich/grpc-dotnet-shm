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

using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Google.Protobuf;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Batched frame writer inspired by Kestrel's <c>Http2FrameWriter</c>.
/// Uses a lock-free <see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/>
/// for MPSC enqueue (multiple app threads → single writer thread) to avoid
/// <c>Monitor.Enter</c> contention in high-concurrency streaming scenarios.
/// Small payloads are defensively copied into pooled buffers at enqueue time.
/// Large payloads can be enqueued zero-copy; the pooled buffer is returned
/// after the data has been written to the ring buffer.
/// </summary>
internal sealed class ShmFrameWriter : IDisposable
{
    private struct FrameEntry
    {
        public FrameType Type;
        public uint StreamId;
        public byte Flags;
        public int Length;
        public ReadOnlyMemory<byte> Payload;
        public byte[]? ReturnToPool;
        public ManualResetEventSlim? CompletionSignal; // set after ring write; caller waits if non-null
        public StrongBox<bool>? CancelFlag; // shared with caller; true = skip this entry

        /// <summary>
        /// Number of payload bytes already written when this Message
        /// entry was parked in <c>_deferred</c>. 0 = fresh; equal to
        /// <see cref="Length"/> = fully done. Non-zero means a partial
        /// chunked write — resume from this offset. Only used for
        /// <see cref="FrameType.Message"/> entries on the chunked-write
        /// path (Length &gt; window). Mirrors grpc-go-shmem's
        /// <c>deferredMessage{entry, offset}</c> resume state.
        /// </summary>
        public int BytesWritten;
    }

    /// <summary>
    /// Pooled signal+cancelFlag pair for EnqueueZeroCopyAndWait.
    /// Avoids per-message MRES+StrongBox allocation on the large-
    /// streaming hot path. Up to ~maxConcurrentStreams entries cached.
    /// </summary>
    private sealed class WaitToken
    {
        public readonly ManualResetEventSlim Signal = new(false);
        public readonly StrongBox<bool> CancelFlag = new(false);

        public void Reset()
        {
            Signal.Reset();
            Volatile.Write(ref CancelFlag.Value, false);
        }
    }

    private readonly ConcurrentBag<WaitToken> _waitTokenPool = new();

    private WaitToken RentWaitToken()
    {
        if (_waitTokenPool.TryTake(out var token))
        {
            token.Reset();
            return token;
        }
        return new WaitToken();
    }

    private void ReturnWaitToken(WaitToken token)
    {
        _waitTokenPool.Add(token);
    }

    private readonly ShmRing _ring;
    private readonly ConcurrentQueue<FrameEntry> _queue;
    private readonly ConcurrentQueue<FrameEntry> _controlQueue; // WindowUpdate/Ping/Pong bypass Messages
    private readonly ManualResetEventSlim _readySignal;
    // Parallel kernel handle for SAW-WriterLoop (env SHM_SAW_WRITERLOOP=1).
    private readonly EventWaitHandle _kernelReadySignal;
    private readonly IntPtr _kernelReadyHandle;

    // Phase B deferred-on-quota-fail: when a queued Message's per-stream
    // send window is insufficient, FlushBatch parks it here keyed by
    // streamId instead of blocking the writer task on
    // ReserveSendQuotaOrBlock. WU arrival on that stream wakes the
    // writer (via NotifyQuotaUpdated) which then drains _deferred.
    // Matches grpc-go-shmem's writer-side deferred[streamID] map.
    //
    // Per-stream FIFO is preserved by the inner LinkedList: AddLast on
    // park, RemoveFirst when fully drained. LinkedList (rather than
    // Queue) so a partial chunked-write entry at the head can be
    // updated in place via <c>First.Value = updatedEntry</c> when its
    // <see cref="FrameEntry.BytesWritten"/> advances without going to
    // a fresh dequeue cycle. Single-threaded: only the writer task
    // mutates this map; other threads only call NotifyQuotaUpdated
    // which signals _readySignal.
    private readonly Dictionary<uint, LinkedList<FrameEntry>> _deferred = new();
    private int _deferredCount; // fast empty-check; mirrors _deferred values' sum of Count

    // Bench-only: lookup from streamId → ShmGrpcStream, used by WriterLoop to
    // route per-chunk fair-window gating into the right stream when the
    // strict-fair mode is enabled. Constructor-injected by ShmConnection so
    // the field publication is happens-before the writer task starts.
    // Null = no fair-window enforcement (production default).
    internal readonly System.Collections.Concurrent.ConcurrentDictionary<uint, ShmGrpcStream>? StreamMap;
    // SAW-WriterLoop state: set true when FlushBatch wrote data with
    // signal deferred; consumed at Phase 3 wait entry.
    private bool _pendingDeferredSignal;
    internal int _waiting; // 1 if writer thread is blocked in Wait; accessed via Volatile.Read/Write
    private volatile bool _completed;
    private readonly Task _writerTask;
    private readonly CancellationTokenSource _cts;
    private readonly CancellationToken _ct;
    private int _disposed;

    // Inline write: handler submits a write callback to be executed on the
    // WriterLoop thread, avoiding concurrent ring access entirely.
    // _inlineAction is the callback, _inlineSignal signals completion.
    private volatile Action? _inlineAction;
    private readonly ManualResetEventSlim _inlineSignal = new(false);

    // Cooperative pause fields for TryPauseWriterLoop / ResumeWriterLoop.
    // Used by singleStreamMode handlers to get exclusive ring access.
    private volatile bool _paused;
    private int _inlineWriterActive;
    private volatile bool _idleInWait;
    private volatile bool _singleStreamMode;

    // Diagnostic counters (env-gated SHM_DIAG_WRITERLOOP=1) for assessing
    // how often the WriterLoop reaches Phase 3 kernel wait — needed to
    // estimate the potential benefit of SAW signal+wait combining.
    private static readonly bool s_diagWriterLoop =
        string.Equals(Environment.GetEnvironmentVariable("SHM_DIAG_WRITERLOOP"),
            "1", StringComparison.Ordinal);
    private static long s_phase3Waits;
    internal static long GetPhase3Waits() => Volatile.Read(ref s_phase3Waits);

    /// <summary>
    /// Opens an inline "wake-coalescing" batch. While this batch is open
    /// any inline writes will fill the ring without firing an OS-level
    /// data-signal on the peer waker. The batch MUST be paired with
    /// <see cref="EndInlineBatch"/> which fires a single coalesced signal
    /// if any data was written and a parker is waiting.
    ///
    /// This is layered on top of <see cref="ShmRing.BeginBatchWrite"/> so
    /// it correctly nests with the WriterLoop's own batch-flush logic —
    /// but in practice the WriterLoop is paused via
    /// <see cref="TryPauseWriterLoop"/> for the duration of the inline
    /// session, so the two callers never collide.
    /// </summary>
    internal void BeginInlineBatch() => _ring.BeginBatchWrite();

    /// <summary>
    /// Closes an inline batch opened with <see cref="BeginInlineBatch"/>.
    /// Fires a single deferred SignalData if any waiter is parked.
    /// </summary>
    internal void EndInlineBatch() => _ring.EndBatchWrite();

    /// <summary>
    /// Capacity (in bytes) of the underlying TX ring buffer. Exposed
    /// for callers that need to size-gate before opening an inline
    /// batch — prefer <see cref="CanCoalesceInlineMessage"/> which uses
    /// the canonical single-frame threshold and is what the writer
    /// itself uses to decide whether to chunk.
    /// </summary>
    internal int RingCapacity => (int)_ring.Capacity;

    /// <summary>
    /// Canonical single-frame payload threshold used by
    /// <see cref="WriteInlineDirectMultiFrame"/>: a MESSAGE frame whose
    /// payload (5-byte gRPC LPM header + protobuf body) is &lt;= this
    /// value is emitted as ONE H2 DATA frame on the first attempt.
    /// Above this, the writer chunks into <c>cap/8</c>-sized pieces.
    /// The formula caps cap/3 by both the HTTP/2 24-bit hard limit and
    /// the strict-fair <see cref="ShmConstants.FairMaxFramePayload"/>
    /// bench env.
    /// </summary>
    /// <remarks>
    /// NOT the right threshold for wake-coalesce gates — see
    /// <see cref="CanCoalesceInlineMessage"/> / <see cref="ComputeCoalesceSafeThreshold"/>
    /// which also accounts for the wrap-fall-through path's chunk size.
    /// </remarks>
    private static int ComputeSingleFrameThreshold(int ringCapacity)
    {
        var t = Math.Max(1, ringCapacity / 3);
        if (t > Wire.Http2FrameHeader.MaxAllowedPayloadLength)
            t = Wire.Http2FrameHeader.MaxAllowedPayloadLength;
        if (ShmConstants.FairMaxFramePayload < t)
            t = ShmConstants.FairMaxFramePayload;
        return t;
    }

    /// <summary>
    /// Returns the maximum LPM payload size that
    /// <see cref="WriteInlineDirectMultiFrame"/> is GUARANTEED to emit
    /// as a single H2 DATA frame, regardless of whether the ring
    /// reservation hits a wrap-around boundary. Use this for
    /// wake-coalesce gates — single-frame threshold alone is NOT
    /// safe because on wrap fall-through the writer chunks at
    /// <c>cap/8</c> via <see cref="RingFrameStream"/>.
    /// </summary>
    /// <remarks>
    /// Derivation (round-2 review fix): the wrap fall-through path
    /// chunks at <c>chunkSize = min(cap/8, H2max, FairMaxFramePayload)</c>.
    /// Combined with the non-wrap single-frame threshold
    /// <c>min(cap/3, H2max, FairMaxFramePayload)</c>, the threshold
    /// safe for BOTH paths is the more restrictive of the two —
    /// i.e. <c>min(cap/8, H2max, FairMaxFramePayload)</c>. Without
    /// this tightening, a 10 MiB Unary response on a 64 MiB ring
    /// would pass the gate (10 &lt;= cap/3 = 21 MiB), but on wrap
    /// would chunk into 2 frames of cap/8 = 8 MiB each, each chunk's
    /// signal suppressed by the open batch -> deadlock.
    /// </remarks>
    private static int ComputeCoalesceSafeThreshold(int ringCapacity)
    {
        var t = Math.Max(1, ringCapacity / 8);
        if (t > Wire.Http2FrameHeader.MaxAllowedPayloadLength)
            t = Wire.Http2FrameHeader.MaxAllowedPayloadLength;
        if (ShmConstants.FairMaxFramePayload < t)
            t = ShmConstants.FairMaxFramePayload;
        return t;
    }

    /// <summary>
    /// Returns true iff a MESSAGE whose LPM payload (5-byte gRPC
    /// header + protobuf body) is <paramref name="lpmPayloadBytes"/>
    /// is GUARANTEED to be emitted as ONE H2 DATA frame by
    /// <see cref="WriteInlineDirectMultiFrame"/> on BOTH the non-wrap
    /// single-frame path AND the wrap fall-through to
    /// <see cref="RingFrameStream"/> — i.e. safe to wrap in a
    /// wake-coalesce <see cref="BeginInlineBatch"/> /
    /// <see cref="EndInlineBatch"/> pair without risking deadlock.
    /// </summary>
    /// <remarks>
    /// Single source of truth for the coalesce-safety predicate. The
    /// server-side <c>ShmGrpcServer.UnaryHandler</c> and
    /// <c>ClientStreamingHandler</c> gate their
    /// <see cref="BeginInlineBatch"/> on this. The threshold (see
    /// <see cref="ComputeCoalesceSafeThreshold"/>) is tighter than
    /// <see cref="ComputeSingleFrameThreshold"/> because the wrap-
    /// fall-through chunks at <c>cap/8</c>, not <c>cap/3</c>.
    /// </remarks>
    internal bool CanCoalesceInlineMessage(int lpmPayloadBytes)
    {
        return lpmPayloadBytes <= ComputeCoalesceSafeThreshold((int)_ring.Capacity);
    }


    public ShmFrameWriter(ShmRing ring, CancellationTokenSource cts,
        System.Collections.Concurrent.ConcurrentDictionary<uint, ShmGrpcStream>? streamMap = null)
    {
        _ring = ring;
        _cts = cts;
        _ct = cts.Token;
        StreamMap = streamMap;
        _queue = new ConcurrentQueue<FrameEntry>();
        _controlQueue = new ConcurrentQueue<FrameEntry>();
        _readySignal = new ManualResetEventSlim(false);

        // Parallel kernel handle used only on the SAW-WriterLoop path
        // (env SHM_SAW_WRITERLOOP=1). Lets WriterLoop's Phase 3 wait
        // be combined with the deferred peer SignalData via Windows
        // SignalObjectAndWait — 1 kernel transition instead of 2.
        // Enqueue Set's both _readySignal AND _kernelReadySignal so the
        // legacy MRES path remains correct when SAW is off.
        _kernelReadySignal = new EventWaitHandle(false, EventResetMode.AutoReset);
        _kernelReadyHandle = _kernelReadySignal.SafeWaitHandle.DangerousGetHandle();

        // Register drain callback so WaitForSpace can flush control frames
        // (Ping/Pong keepalive) before blocking when the ring fills, keeping
        // keepalive responsive even under sustained back-pressure.
        _ring.WaitForSpaceDrainCallback = DrainControlFrames;

        _writerTask = Task.Factory.StartNew(
            WriterLoop, _ct,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Opt-in SAW-WriterLoop optimization: when set, the writer-loop
    /// defers <see cref="ShmRing.SignalData"/> from <c>FlushBatch</c>
    /// to the next Phase 3 wait point, then issues both via Windows
    /// <c>SignalObjectAndWait</c> for 1 kernel transition instead of 2.
    /// Saves ~5-10 µs per server-side RT under no-spin operation.
    /// No effect on Linux eventfd path (no equivalent atomic primitive).
    /// </summary>
    private static readonly bool s_sawWriterLoop =
        string.Equals(Environment.GetEnvironmentVariable("SHM_SAW_WRITERLOOP"),
            "1", StringComparison.Ordinal);

    /// <summary>
    /// Enables singleStreamMode: ResumeWriterLoop won't wake WriterLoop,
    /// letting it stay in Phase 3 idle. Next TryPause succeeds instantly
    /// without the kernel wake + Phase 2 spin overhead (~120µs saved).
    /// Control frames (Ping/Pong) still wake WriterLoop via Enqueue's
    /// _readySignal.Set() check.
    /// </summary>
    internal void EnableSingleStreamMode() => _singleStreamMode = true;

    /// <summary>
    /// Enqueues a frame by defensively copying the payload into a pooled buffer.
    /// The copy is written to the ring and the buffer returned to the pool by
    /// the dedicated writer thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">The writer has been completed (disposed).</exception>
    public void Enqueue(FrameType type, uint streamId, byte flags, ReadOnlySpan<byte> payload)
    {
        var len = payload.Length;
        byte[]? buf = null;
        ReadOnlyMemory<byte> mem = default;
        if (len > 0)
        {
            // Small payloads (control frames like Ping/Pong ≤ 64B):
            // allocate a small byte[] directly — cheaper than ArrayPool
            // rent/return overhead. ReturnToPool stays null so FlushBatch
            // won't call ArrayPool.Return for these tiny arrays.
            if (len <= 64)
            {
                var small = new byte[len];
                payload.CopyTo(small);
                mem = small.AsMemory(0, len);
            }
            else
            {
                buf = ArrayPool<byte>.Shared.Rent(len);
                payload.CopyTo(buf);
                mem = buf.AsMemory(0, len);
            }
        }

        if (_completed)
        {
            if (buf != null)
                ArrayPool<byte>.Shared.Return(buf);
            throw new InvalidOperationException("Frame writer has been disposed.");
        }

        var entry = new FrameEntry
        {
            Type = type, StreamId = streamId, Flags = flags,
            Length = len, Payload = mem, ReturnToPool = buf
        };

        // Control frames (WindowUpdate, Ping, Pong) go to a priority queue
        // so DrainControlFrames can always reach them without scanning past
        // queued Messages. WindowUpdate is included for completeness (the
        // enum value is still routed) even though SHM is no-WU and the
        // production writer never enqueues it; Ping/Pong keepalive must
        // remain responsive when the WriterLoop is busy draining large
        // Messages.
        if (type == FrameType.WindowUpdate || type == FrameType.Ping || type == FrameType.Pong)
            _controlQueue.Enqueue(entry);
        else
            _queue.Enqueue(entry);

        // Wake the writer thread if it is blocked waiting for data.
        // Late enqueues (after _completed is set) are handled by the three
        // drain layers in WriterLoop + Dispose — no dequeue here to avoid
        // accidentally consuming another thread's frame from the queue head.
        if (Volatile.Read(ref _waiting) != 0 && _disposed == 0)
        {
            try { _readySignal.Set(); _kernelReadySignal.Set(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Enqueues a frame without copying the payload. The caller's
    /// <paramref name="pooledBuffer"/> is returned to <see cref="ArrayPool{T}"/>
    /// after the data has been written to the ring buffer.
    /// Pass <c>null</c> if the payload does not need to be returned.
    /// </summary>
    /// <remarks>
    /// On failure the caller retains ownership of <paramref name="pooledBuffer"/>;
    /// this method does NOT return it to the pool.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The writer has been completed (disposed).</exception>
    public void EnqueueZeroCopy(FrameType type, uint streamId, byte flags,
        ReadOnlyMemory<byte> payload, byte[]? pooledBuffer)
    {
        if (_completed)
        {
            throw new InvalidOperationException("Frame writer has been disposed.");
        }

        _queue.Enqueue(new FrameEntry
        {
            Type = type, StreamId = streamId, Flags = flags,
            Length = payload.Length, Payload = payload,
            ReturnToPool = pooledBuffer
        });

        if (Volatile.Read(ref _waiting) != 0 && _disposed == 0)
        {
            try { _readySignal.Set(); _kernelReadySignal.Set(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Enqueues a frame without copying and waits for the WriterLoop to finish
    /// writing it to the ring. This is safe for callers that may reuse the
    /// payload buffer immediately after this method returns (e.g., streaming RPCs
    /// where grpc-dotnet reuses serialization buffers across WriteAsync calls).
    /// </summary>
    public void EnqueueZeroCopyAndWait(FrameType type, uint streamId, byte flags,
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (_completed)
        {
            throw new InvalidOperationException("Frame writer has been disposed.");
        }

        var token = RentWaitToken();
        _queue.Enqueue(new FrameEntry
        {
            Type = type, StreamId = streamId, Flags = flags,
            Length = payload.Length, Payload = payload,
            ReturnToPool = null,
            CompletionSignal = token.Signal,
            CancelFlag = token.CancelFlag
        });

        if (Volatile.Read(ref _waiting) != 0 && _disposed == 0)
        {
            try { _readySignal.Set(); _kernelReadySignal.Set(); } catch (ObjectDisposedException) { }
        }

        // Block until WriterLoop has written the data to the ring.
        try
        {
            token.Signal.Wait(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Volatile.Write(ref token.CancelFlag.Value, true);
#pragma warning disable CA2016
            try { token.Signal.Wait(); }
            catch (ObjectDisposedException) { /* writer already done */ }
#pragma warning restore CA2016
            ReturnWaitToken(token);
            throw;
        }
        ReturnWaitToken(token);
    }

    private void WriterLoop()
    {
        const int maxBatch = 512;
        var batch = new FrameEntry[maxBatch];

        try
        {
            while (!_ct.IsCancellationRequested && !_completed)
            {
                // Inline write request: handler submitted a callback to execute
                // on this thread. Execute it immediately (with ring exclusivity)
                // and signal completion. This is the primary singleStreamMode
                // optimization path — no pause/resume needed.
                var inlineAction = _inlineAction;
                if (inlineAction != null)
                {
                    _inlineAction = null;
                    // Drain any pending control frames before the inline write
                    // (e.g., WindowUpdate that arrived during handler setup).
                    if (!_controlQueue.IsEmpty)
                        DrainControlFrames();
                    try
                    {
                        inlineAction();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"WriterLoop inline action failed: {ex.Message}");
                    }
                    _inlineSignal.Set();
                    continue;
                }

                // Cooperative pause: skip to Phase 3 when TryPauseWriterLoop
                // needs exclusive ring access for inline writes.
                if (_paused)
                    goto phase3;

                // Phase 1: immediate dequeue
                if (!_controlQueue.IsEmpty)
                    DrainControlFrames();

                if (_queue.TryDequeue(out batch[0]))
                {
                    // Drain control frames within FlushBatch (before large
                    // messages) rather than here — saves ~15ns per iteration.
                    var count = 1;
                    while (count < maxBatch && _queue.TryDequeue(out batch[count]))
                        count++;
                    FlushBatch(batch, count);
                    continue;
                }

                // Phase 1.5: nothing in _queue, but Phase B might have
                // deferred entries waiting on WU credit. Try to drain
                // them — cheap when _deferredCount == 0, else wraps
                // BeginBatch/EndBatch around a TryDrainDeferredLocked.
                //
                // Continue only when FlushDeferred returns true (made
                // progress, i.e. at least one entry fully drained).
                // Otherwise fall through to Phase 2/3 wait — spinning
                // the FlushDeferred loop with no quota would burn CPU
                // until the next WU arrives.
                if (_deferredCount > 0)
                {
                    if (FlushDeferred())
                        continue;
                }

                // Phase 2: spin-wait for data.
                // Default is NO SPIN (matches grpc-go-shmem's
                // shmSpinDefault = 0 policy — see Doug's "no
                // lock-spinning" requirement for fair UDS/TCP
                // comparison). Operators can opt in via env var
                // SHM_WRITER_SPIN_ITERATIONS for sub-µs latency at
                // the cost of idle CPU. See FrameTypes.cs.
                var found = false;
                var spinBudget = ShmConstants.WriterLoopSpinIterations;
                for (int spin = 0; spin < spinBudget; spin++)
                {
                    // Check for inline write request (singleStreamMode).
                    if (_inlineAction != null || _paused)
                    {
                        found = false;
                        // Loop back to top where _inlineAction/_paused is handled.
                        break;
                    }

                    Thread.SpinWait(1);
                    if (_queue.TryDequeue(out batch[0]))
                    {
                        var count = 1;
                        while (count < maxBatch && _queue.TryDequeue(out batch[count]))
                            count++;
                        FlushBatch(batch, count);
                        found = true;
                        break;
                    }
                }

                if (found) continue;
                // If broke due to _inlineAction/_paused, loop back to top.
                if (_inlineAction != null || _paused) continue;

                // Phase 2.5: yield before blocking (singleStreamMode only).
                // Thread.Yield() is much cheaper than a kernel wait (~1us
                // vs ~80us). In ping-pong the response often arrives during
                // this window. Skip in multi-stream mode where the queue
                // refills quickly and Yield wastes a scheduler quantum.
                if (_singleStreamMode)
                {
                    Thread.Yield();
                    if (_inlineAction != null || _paused) continue;
                    if (_queue.TryDequeue(out batch[0]))
                    {
                        var count = 1;
                        while (count < maxBatch && _queue.TryDequeue(out batch[count]))
                            count++;
                        FlushBatch(batch, count);
                        continue;
                    }
                }

                // Phase 3: blocking wait (lost-wake-safe pattern)
                // Set _waiting BEFORE Reset to ensure writers see it and call Set().
                phase3:
                Volatile.Write(ref _waiting, 1);
                _readySignal.Reset();

                // SAW-WriterLoop safety: if we have a pending deferred
                // signal and we're about to enter a path that does NOT
                // go through the SAW combine point (inline action / pause
                // branches), fire the signal eagerly so peer waiters
                // aren't stranded waiting on our suppressed signal.
                if (s_sawWriterLoop && _pendingDeferredSignal
                    && (_inlineAction != null || _paused))
                {
                    _pendingDeferredSignal = false;
                    _ring.FireDeferredSignalIfWaiters();
                }

                // Re-check inline action: handler may have set _inlineAction
                // between Phase 2 exit and here. If so, execute it now.
                if (_inlineAction != null)
                {
                    Volatile.Write(ref _waiting, 0);
                    continue; // back to top → execute inline
                }

                // Re-check _paused: the direct writer may have set _paused
                // between Phase 2 and here. If so, stay in _waiting state
                // so TryPauseWriterLoop sees _idleInWait and succeeds.
                if (_paused)
                {
                    _idleInWait = true;
                    try { _readySignal.Wait(_ct); }
                    finally { _idleInWait = false; Volatile.Write(ref _waiting, 0); }
                    continue;
                }

                // Drain control frames before checking _queue — WindowUpdate
                // frames in _controlQueue won't wake WriterLoop via Set()
                // if they arrive during Phase 2 spin (_waiting=0).
                DrainControlFrames();

                if (_queue.TryDequeue(out batch[0]))
                {
                    // Data arrived between Phase 2 and Reset — no need to wait.
                    Volatile.Write(ref _waiting, 0);
                    var count2 = 1;
                    while (count2 < maxBatch && _queue.TryDequeue(out batch[count2]))
                        count2++;
                    FlushBatch(batch, count2);
                    continue;
                }

                // Lost-wake guard for the partial-write deferred path:
                // a WU may have landed (via NotifyQuotaUpdated) between
                // the previous FlushDeferred returning empty and Reset
                // above. The sticky wake design lets _readySignal stay
                // Set across our Reset only if NotifyQuotaUpdated fires
                // AFTER the Reset; one that fired BEFORE Reset (in the
                // narrow window between the Phase 1.5 FlushDeferred and
                // Reset) leaves the wake lost. Drain deferred here so
                // such a stale WU has a chance to advance an entry
                // before we commit to the kernel wait. Keep _waiting=1
                // during the drain so any concurrent
                // NotifyQuotaUpdated re-fires the sticky Set — racy
                // but harmless (one extra wake cycle at worst).
                if (_deferredCount > 0)
                {
                    if (FlushDeferred())
                    {
                        Volatile.Write(ref _waiting, 0);
                        continue;
                    }
                    // No progress drained; fall through to wait. The
                    // sticky NotifyQuotaUpdated Set is guaranteed to
                    // fire on the next WU (its early-return is gated
                    // only on _deferredCount==0 which is non-zero
                    // here), so Wait will return promptly.
                }

                _idleInWait = true;
                try
                {
                    if (s_diagWriterLoop) Interlocked.Increment(ref s_phase3Waits);

                    // SAW-WriterLoop path: if we have a deferred SignalData,
                    // combine it with the wait via SignalObjectAndWait — 1
                    // kernel transition instead of 2 (saves ~5-10 µs per RT
                    // on Windows no-spin server side). The kernel-handle
                    // event was Set() by every Enqueue path that touched
                    // _readySignal, so it's race-safe with respect to wakes
                    // arriving between Reset() above and the wait here.
                    if (s_sawWriterLoop && _pendingDeferredSignal)
                    {
                        _pendingDeferredSignal = false;
                        // _kernelReadySignal is AutoReset: if a producer
                        // already Set() it (a wake between Phase 2 and here)
                        // SAW returns immediately, we loop, see queue data,
                        // and process — one harmless extra iteration. We do
                        // NOT explicitly Reset here because Reset() is a
                        // kernel call (~3-5 µs) that would offset most of
                        // the SAW savings we're trying to gain.
                        var saw = _ring.TryFireDeferredSignalAndWaitForLocal(
                            _kernelReadyHandle, timeout: null, _ct);
                        if (!saw)
                        {
                            // SAW unsupported or no peer waiter: fire the
                            // deferred signal manually (if waiters appeared),
                            // then fall back to the legacy MRES wait.
                            _ring.FireDeferredSignalIfWaiters();
                            _readySignal.Wait(_ct);
                        }
                    }
                    else
                    {
                        _readySignal.Wait(_ct);
                    }
                }
                finally
                {
                    _idleInWait = false;
                    Volatile.Write(ref _waiting, 0);
                }
            }

            // Drain remaining entries after _completed is set.
            DrainControlFrames();
            while (_queue.TryDequeue(out batch[0]))
            {
                var count = 1;
                while (count < maxBatch && _queue.TryDequeue(out batch[count]))
                    count++;
                FlushBatch(batch, count);
            }
            DrainControlFrames();

            // SAW-WriterLoop shutdown safety: ensure any signals
            // deferred by the final FlushBatch are actually delivered
            // so peer readers see the last bytes before we exit.
            if (s_sawWriterLoop && _pendingDeferredSignal)
            {
                _pendingDeferredSignal = false;
                _ring.FireDeferredSignalIfWaiters();
            }
        }
        catch (OperationCanceledException) { }
        catch (RingClosedException) { }
    }

    private void FlushBatch(FrameEntry[] batch, int count)
    {
        try
        {
            // WriterLoop is the sole ring writer (SPSC).
            _ring.BeginBatchWrite();
            try
            {
                for (var i = 0; i < count; i++)
                {
                    ref var entry = ref batch[i];

                    // Skip slots cleared to default by the partial-write
                    // defer path (parked entries set batch[i]=default to
                    // hand ownership of the pooled buffer + completion
                    // signal off to _deferred). The original cancel-flag
                    // check below would NOT catch these because their
                    // CancelFlag is null. Real frames enqueued to _queue
                    // always carry StreamId != 0 (only WindowUpdate /
                    // Ping / Pong are streamId==0 and they go to
                    // _controlQueue, not _queue), so this is a safe
                    // sentinel for "default-initialised slot, do not
                    // process".
                    if (entry.StreamId == 0 && entry.Payload.IsEmpty && entry.Length == 0 && entry.ReturnToPool == null && entry.CompletionSignal == null)
                        continue;

                    // Skip entries cancelled by the caller (e.g. OperationCanceledException
                    // in EnqueueZeroCopyAndWait). The caller is responsible for
                    // releasing any pooled buffer; writing a cancelled entry
                    // would produce duplicate/out-of-order data on the wire.
                    if (entry.CancelFlag != null && Volatile.Read(ref entry.CancelFlag.Value))
                        continue;

                    var payload = entry.Payload.Span;

                    // Phase B (writer-task FC defer): for Message entries
                    // whose total length fits in the H/2 window AND whose
                    // stream's current send-quota is insufficient, park the
                    // entry in _deferred instead of doing the willLikelyChunk
                    // batch-mode dance and blocking inside WriteMessage.
                    // This keeps the writer task unblocked so other
                    // streams' messages in the batch can still be flushed.
                    //
                    // Messages larger than the window (e.g. a 256 MB unary
                    // through a 32 MiB window) can NEVER pass the "quota >=
                    // length" drain check, so they MUST fall through to
                    // the chunked WriteMessage path which blocks per-chunk
                    // and rides the WU drip cadence. This is a Phase B
                    // simplification vs grpc-go-shmem's partial-write
                    // deferred (which tracks bytes already written and
                    // resumes from offset); supporting that here would
                    // require richer DeferredEntry state.
                    if (entry.Type == FrameType.Message
                        && entry.Length <= ShmConstants.InitialWindowSize)
                    {
                        ShmGrpcStream? fairStream = null;
                        StreamMap?.TryGetValue(entry.StreamId, out fairStream);
                        if (fairStream != null)
                        {
                            var hasDeferred = _deferred.TryGetValue(entry.StreamId, out var dq)
                                && dq.Count > 0;
                            if (hasDeferred || fairStream.SendQuota < entry.Length)
                            {
                                if (dq == null)
                                {
                                    dq = new LinkedList<FrameEntry>();
                                    _deferred[entry.StreamId] = dq;
                                }
                                dq.AddLast(entry);
                                _deferredCount++;
                                // Ownership of pooled buffer + completion
                                // signal transfers to _deferred. Clear the
                                // batch slot so the finally block does NOT
                                // release the buffer or set the signal.
                                batch[i] = default;
                                // Stream-ordering preservation: also park
                                // forward same-stream entries (HalfClose,
                                // Trailers, follow-up Messages) so the
                                // wire order behind our parked Message is
                                // never re-ordered through the LARGE-
                                // message TryDrain resume.
                                ParkForwardSameStreamLocked(batch, i + 1, count, entry.StreamId, dq);
                                continue;
                            }
                        }

                        WriteMessageEntryInBatch(in entry, fairStream);
                    }
                    else if (entry.Type == FrameType.Message)
                    {
                        // Large message (> window): chunked-write path.
                        // Partial-write deferred (2026-05-28): if the
                        // first reserve fails, park the entry in
                        // <see cref="_deferred"/> with its current
                        // <see cref="FrameEntry.BytesWritten"/> offset
                        // and move to the next batch entry. This is
                        // the fix for the fair_conc 10×64KB hang where
                        // the blocking <c>WriteMessageEntryInBatch</c>
                        // path serialised all 10 streams' 64 KiB
                        // messages through a single per-chunk wait,
                        // exceeding the bench's 120 s stepTimeout per
                        // <c>WriteAsync</c> once contention pushed WU
                        // RT past a few ms. Mirrors grpc-go-shmem's
                        // <c>writerLoop</c> partial-write defer.
                        //
                        // STREAM ORDERING NOTE: when we park, ALL
                        // subsequent batch entries for the same stream
                        // MUST also be parked behind us so the wire
                        // order stays {chunk-1..N, HalfClose, Trailers}
                        // and the peer's LPM accumulator doesn't see
                        // an out-of-order END_STREAM mid-LPM (the H2
                        // codec rejects that with
                        // "stream ended mid-LPM" — see
                        // Http2Codec.Read.cs ~line 695).
                        ShmGrpcStream? fairStream = null;
                        StreamMap?.TryGetValue(entry.StreamId, out fairStream);

                        // Partial-write defer path: applies to BOTH
                        // signal-bearing (EnqueueZeroCopyAndWait) and
                        // fire-and-forget (SendFrameZeroCopy) entries.
                        // For fire-and-forget callers (Unary server-side
                        // response), the stream may be disposed before
                        // the writer drains the queue; the "stream
                        // gone" branch in TryDrainDeferredLocked
                        // handles that by calling
                        // WriteRemainingChunksNoQuota so the peer's
                        // LPM accumulator gets all bytes.
                        if (fairStream != null)
                        {
                            var hasDeferred = _deferred.TryGetValue(entry.StreamId, out var dq)
                                && dq.Count > 0;
                            if (hasDeferred)
                            {
                                // Pre-existing entry on this stream is
                                // ahead of us in FIFO; just park behind
                                // it and let TryDrainDeferredLocked
                                // process us when it gets here.
                                if (dq == null)
                                {
                                    dq = new LinkedList<FrameEntry>();
                                    _deferred[entry.StreamId] = dq;
                                }
                                dq.AddLast(entry);
                                _deferredCount++;
                                batch[i] = default;
                                continue;
                            }

                            // Exit batch mode for chunked writes — peer
                            // reader must be signaled per chunk to drive
                            // the WU drip cadence.
                            _ring.EndBatchWrite();
                            DrainControlFrames();
                            int writtenOffset;
                            try
                            {
                                writtenOffset = TryWriteChunkedMessageNonBlocking(in entry, fairStream, 0);
                            }
                            finally
                            {
                                _ring.BeginBatchWrite();
                            }

                            if (writtenOffset < entry.Length)
                            {
                                // Partial write — park the rest, AND scan
                                // forward in batch[] to also park any
                                // subsequent entries for this stream
                                // (HalfClose, Trailers, more Messages)
                                // so wire ordering is preserved.
                                var parked = entry;
                                parked.BytesWritten = writtenOffset;
                                if (dq == null)
                                {
                                    dq = new LinkedList<FrameEntry>();
                                    _deferred[entry.StreamId] = dq;
                                }
                                dq.AddLast(parked);
                                _deferredCount++;
                                batch[i] = default;
                                ParkForwardSameStreamLocked(batch, i + 1, count, entry.StreamId, dq);
                                continue;
                            }
                            // else: fully written — fall through to the
                            // finally-block ownership release at end of
                            // loop iter.
                        }
                        else
                        {
                            // No fair stream (e.g. test shim or
                            // singleton-stream-mode path that doesn't
                            // register in StreamMap): use the legacy
                            // blocking chunked path. This still serialises
                            // but is rare in fair-mode benchmarks.
                            WriteMessageEntryInBatch(in entry, fairStream);
                        }
                    }
                    else
                    {
                        // Non-Message frames (Headers, Trailers, HalfClose,
                        // Ping, Pong, GoAway, WindowUpdate, RstStream):
                        // small enough to stay inside the current batch.
                        //
                        // STREAM-ORDERING GUARD: if this stream has a
                        // parked Message in <see cref="_deferred"/>
                        // (typically a partial-write deferred chunked
                        // body whose tail is still waiting on WU
                        // credit), this Trailers / HalfClose / etc
                        // MUST stay behind those chunks so the peer's
                        // LPM accumulator sees {chunk-1..N, END_STREAM}
                        // in order. Without this guard, fire-and-forget
                        // Unary server-side path emits the response
                        // Message, parks chunks 4-N, then the gRPC
                        // framework synchronously enqueues Trailers in
                        // a SEPARATE batch — and Trailers would hit
                        // this branch and go straight to the ring,
                        // arriving at the peer BEFORE the parked
                        // chunks. Client codec sees Trailers mid-LPM,
                        // marks the stream done, discards the late
                        // chunks → "Failed to deserialise response
                        // message".
                        if (entry.StreamId != 0
                            && _deferred.TryGetValue(entry.StreamId, out var dqNon)
                            && dqNon.Count > 0)
                        {
                            dqNon.AddLast(entry);
                            _deferredCount++;
                            batch[i] = default;
                            continue;
                        }
                        var header = new FrameHeader(entry.Type, entry.StreamId, (uint)entry.Length, entry.Flags);
                        FrameProtocol.WriteFrame(_ring, header, payload, _ct);
                    }
                }

                // Phase B piggyback: drain any control frames (WindowUpdate,
                // Ping, Pong) that arrived during the batch's writes — these
                // are emitted by the codec reader thread when it parses DATA
                // we just wrote on the peer side, so under multi-stream
                // streaming load there's typically at least one WU pending
                // by the end of FlushBatch. Draining here, INSIDE the active
                // BeginBatchWrite scope, coalesces those control frames into
                // the same ring batch signal as the messages, avoiding a
                // second wakeup of the peer reader. Saves ~250ns of ring
                // signal cost per WU under sustained streaming. Matches
                // grpc-go-shmem's piggybackWUFn pattern.
                if (!_controlQueue.IsEmpty)
                    DrainControlFrames();

                // Phase B: try to drain any deferred entries whose stream's
                // quota has been replenished while this batch was writing
                // (the peer's WU emission for our just-written DATA arrives
                // asynchronously; some deferred entries may now be sendable).
                if (_deferredCount > 0)
                    TryDrainDeferredLocked();
            }
            finally
            {
                if (s_sawWriterLoop)
                {
                    // SAW-WriterLoop: defer the SignalData so Phase 3 wait
                    // can combine it with the wait via SignalObjectAndWait.
                    // _pendingDeferredSignal flag is set so Phase 3 knows
                    // to fire (or fall back to immediate signal on
                    // shutdown / pause / continue-with-queue-nonempty).
                    _ring.EndBatchWriteSuppressSignal();
                    _pendingDeferredSignal = true;
                }
                else
                {
                    _ring.EndBatchWrite();
                }
            }
        }
        finally
        {
            for (var i = 0; i < count; i++)
            {
                if (batch[i].ReturnToPool != null)
                    ArrayPool<byte>.Shared.Return(batch[i].ReturnToPool!);
                var sig = batch[i].CompletionSignal;
                if (sig != null)
                {
                    // Signal the waiting caller. Do NOT dispose — the signal
                    // belongs to a pooled WaitToken, returned by the caller.
                    sig.Set();
                }
                batch[i] = default;
            }
        }
    }

    /// <summary>
    /// Writes a single Message FrameEntry inside the writer-loop's
    /// active <see cref="ShmRing.BeginBatchWrite"/> scope. Handles the
    /// "willLikelyChunk" batch-mode dance: for large or fair-mode
    /// chunked messages we MUST exit the batch (so each chunk
    /// individually signals the peer reader to make progress) and
    /// re-enter afterwards to coalesce the remaining batch entries'
    /// signals.
    /// </summary>
    private void WriteMessageEntryInBatch(in FrameEntry entry, ShmGrpcStream? fairStream)
    {
        var payload = entry.Payload.Span;
        var isLast = (entry.Flags & MessageFlags.More) == 0;
        var extraFlags = (byte)(entry.Flags & ~MessageFlags.More);

        // willLikelyChunk: see FlushBatch's original comment block —
        // multi-frame Messages must NOT stay inside a single batch
        // because per-chunk ReserveWrite waits for ring space, which
        // requires the peer reader to be signaled.
        var ringCap = (int)_ring.Capacity;
        var hdrSize = Wire.Http2FrameHeader.Size;
        var fairChunking = ShmConstants.FairMaxFramePayload != int.MaxValue
            && payload.Length > ShmConstants.FairMaxFramePayload;
        var willLikelyChunk = fairChunking
            || payload.Length >= 65536
            || payload.Length + hdrSize >= ringCap / 2;

        if (willLikelyChunk)
        {
            _ring.EndBatchWrite();
            DrainControlFrames();
        }

        FrameProtocol.WriteMessage(_ring, entry.StreamId, payload, isLast, _ct, extraFlags, fairStream,
            fairStream != null ? DrainControlFrames : null);

        if (willLikelyChunk)
            _ring.BeginBatchWrite();
    }

    /// <summary>
    /// Non-blocking chunked-write helper used by the partial-write
    /// deferred path (commit ad029a17 / 2026-05-28). Starts at
    /// <paramref name="startOffset"/> bytes into <paramref name="entry"/>'s
    /// payload and writes as many H/2 DATA frames as
    /// <see cref="ShmGrpcStream.TryReserveSendQuota"/> allows without
    /// blocking. Returns the new offset (==
    /// <see cref="FrameEntry.Length"/> if fully drained, otherwise the
    /// offset to resume from when the next
    /// <c>WINDOW_UPDATE</c> arrives).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Caller MUST have exited batch mode (<see cref="ShmRing.EndBatchWrite"/>)
    /// before calling so peer reader is signaled per chunk — the WU
    /// drip cadence depends on that.
    /// </para>
    /// <para>
    /// The chunk-size computation mirrors
    /// <see cref="FrameProtocol.WriteMessage"/> exactly so that the
    /// blocking and non-blocking paths produce bit-identical wire
    /// output up to interleaving across streams. End-of-message flag
    /// handling: only the chunk that consumes the final byte sets
    /// <c>!MessageFlags.More</c>; preceding chunks always set
    /// <c>MessageFlags.More</c>.
    /// </para>
    /// </remarks>
    private int TryWriteChunkedMessageNonBlocking(in FrameEntry entry, ShmGrpcStream fairStream, int startOffset)
    {
        var fullPayload = entry.Payload.Span;
        var totalLen = fullPayload.Length;
        if (startOffset >= totalLen) return totalLen;

        var isLast = (entry.Flags & MessageFlags.More) == 0;
        var extraFlags = (byte)(entry.Flags & ~MessageFlags.More);

        var ringCap = (int)_ring.Capacity;
        var maxFramePayload = Math.Max(1, ringCap / 3);
        if (maxFramePayload > Wire.Http2FrameHeader.MaxAllowedPayloadLength)
            maxFramePayload = Wire.Http2FrameHeader.MaxAllowedPayloadLength;
        if (ShmConstants.FairMaxFramePayload < maxFramePayload)
            maxFramePayload = ShmConstants.FairMaxFramePayload;

        var offset = startOffset;
        while (offset < totalLen)
        {
            var remainingLen = totalLen - offset;
            var chunkSize = Math.Min(maxFramePayload, remainingLen);
            if (!fairStream.TryReserveSendQuota(chunkSize))
            {
                return offset;
            }

            var isLastChunk = (offset + chunkSize == totalLen);
            byte chunkFlags;
            if (isLastChunk)
                chunkFlags = (byte)(isLast ? extraFlags : (MessageFlags.More | extraFlags));
            else
                chunkFlags = MessageFlags.More;

            var header = new FrameHeader(FrameType.Message, entry.StreamId, (uint)chunkSize, chunkFlags);
            FrameProtocol.WriteFrame(_ring, header, fullPayload.Slice(offset, chunkSize), _ct);
            offset += chunkSize;
        }
        return offset;
    }

    /// <summary>
    /// Stream-gone resume: write all remaining chunks of a partial
    /// Message without any per-stream FC reservation. Called from
    /// <see cref="TryDrainDeferredLocked"/>'s
    /// <c>fairStream == null</c> branch when the local stream object
    /// has been disposed (typically the Unary server-side path where
    /// the handler returned before the response was fully written to
    /// the ring). The peer is still reading the LPM and needs these
    /// bytes; the credit was already committed when chunks 1..N were
    /// reserved, so writing N+1..end is wire-consistent.
    /// </summary>
    private void WriteRemainingChunksNoQuota(in FrameEntry entry, int startOffset)
    {
        var fullPayload = entry.Payload.Span;
        var totalLen = fullPayload.Length;
        if (startOffset >= totalLen) return;

        var isLast = (entry.Flags & MessageFlags.More) == 0;
        var extraFlags = (byte)(entry.Flags & ~MessageFlags.More);

        var ringCap = (int)_ring.Capacity;
        var maxFramePayload = Math.Max(1, ringCap / 3);
        if (maxFramePayload > Wire.Http2FrameHeader.MaxAllowedPayloadLength)
            maxFramePayload = Wire.Http2FrameHeader.MaxAllowedPayloadLength;
        if (ShmConstants.FairMaxFramePayload < maxFramePayload)
            maxFramePayload = ShmConstants.FairMaxFramePayload;

        var offset = startOffset;
        while (offset < totalLen)
        {
            var remainingLen = totalLen - offset;
            var chunkSize = Math.Min(maxFramePayload, remainingLen);
            var isLastChunk = (offset + chunkSize == totalLen);
            byte chunkFlags;
            if (isLastChunk)
                chunkFlags = (byte)(isLast ? extraFlags : (MessageFlags.More | extraFlags));
            else
                chunkFlags = MessageFlags.More;

            var header = new FrameHeader(FrameType.Message, entry.StreamId, (uint)chunkSize, chunkFlags);
            FrameProtocol.WriteFrame(_ring, header, fullPayload.Slice(offset, chunkSize), _ct);
            offset += chunkSize;
        }
    }

    /// <summary>
    /// Stream-ordering preservation helper: scans the remaining slots
    /// of <paramref name="batch"/> starting at <paramref name="startIdx"/>
    /// and parks any entry whose <see cref="FrameEntry.StreamId"/>
    /// matches <paramref name="streamId"/> into the supplied per-stream
    /// deferred queue. Called when a Message for <paramref name="streamId"/>
    /// was just parked (either as a partial chunked write or because
    /// its full quota wasn't available): any later entry on the same
    /// stream — HalfClose, Trailers, follow-up Messages — MUST stay
    /// behind it on the wire, otherwise the peer's H2 codec sees an
    /// END_STREAM mid-LPM or trailers without prior message body and
    /// rejects the stream.
    /// </summary>
    /// <remarks>
    /// Subsequent entries for OTHER streams keep their batch slot —
    /// they can still be flushed in this batch pass, which is the
    /// whole point of the partial-write defer (multiplex across
    /// streams while one stream is parked on credit).
    /// </remarks>
    private void ParkForwardSameStreamLocked(FrameEntry[] batch, int startIdx, int count, uint streamId, LinkedList<FrameEntry> dq)
    {
        for (var j = startIdx; j < count; j++)
        {
            ref var later = ref batch[j];
            if (later.StreamId != streamId) continue;
            if (later.CancelFlag != null && Volatile.Read(ref later.CancelFlag.Value)) continue;
            dq.AddLast(later);
            _deferredCount++;
            batch[j] = default;
        }
    }

    /// <summary>
    /// Phase B: drain <see cref="_deferred"/> entries whose stream's send
    /// quota has been replenished since they were parked. Called by
    /// <see cref="FlushBatch"/> after writing the live batch (so any WU
    /// arrived since we entered FlushBatch is picked up) and from
    /// <see cref="WriterLoop"/> after wake from kernel wait.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Single-threaded: only invoked by the writer task. The
    /// <see cref="_deferred"/> dictionary is therefore not concurrent.
    /// </para>
    /// <para>
    /// Per-stream FIFO is preserved by the per-stream <see cref="Queue{T}"/>.
    /// Across streams the drain order is unspecified — H/2 has no
    /// cross-stream ordering requirement.
    /// </para>
    /// <para>
    /// MUST be called inside an active <see cref="ShmRing.BeginBatchWrite"/>
    /// scope (the same scope as the parent <see cref="FlushBatch"/>) so
    /// drained entries piggyback the same ring signal.
    /// </para>
    /// </remarks>
    private bool TryDrainDeferredLocked()
    {
        if (_deferredCount == 0) return false;

        bool anyProgress = false;
        List<uint>? toRemove = null;
        foreach (var kvp in _deferred)
        {
            var streamId = kvp.Key;
            var queue = kvp.Value;
            if (queue.Count == 0)
            {
                (toRemove ??= new List<uint>()).Add(streamId);
                continue;
            }

            ShmGrpcStream? fairStream = null;
            StreamMap?.TryGetValue(streamId, out fairStream);
            if (fairStream == null)
            {
                // Stream was removed from StreamMap (typically because
                // <see cref="ShmGrpcStream.Dispose"/> ran after the
                // caller used the fire-and-forget
                // <c>SendFrameZeroCopy</c> path — common for Unary
                // server-side: handler returns, stream is disposed,
                // but our partial-write defer still has the response
                // body's last chunks parked here). The PEER is still
                // reading and expecting the full LPM — silently
                // dropping the rest would surface as a deserialise
                // failure on the client. Write any remaining bytes
                // best-effort (no FC reservation — the bytes already
                // fit in the peer-advertised window since the FIRST
                // chunks were reserved on this stream's quota and the
                // peer's drip-on-receive is matching them with WUs
                // back), then drain the queue.
                while (queue.First != null)
                {
                    var ent = queue.First.Value;
                    queue.RemoveFirst();
                    _deferredCount--;
                    if (ent.Type == FrameType.Message && ent.BytesWritten > 0 && ent.BytesWritten < ent.Length)
                    {
                        // Resume the partial chunked write without FC
                        // (the stream is gone so there's no
                        // <see cref="ShmGrpcStream.TryReserveSendQuota"/>
                        // to call). The peer's accumulator is mid-LPM
                        // and needs these bytes to deliver the
                        // message to the application.
                        WriteRemainingChunksNoQuota(in ent, ent.BytesWritten);
                    }
                    else if (ent.Type == FrameType.Message && ent.BytesWritten == 0)
                    {
                        // Fresh chunked Message that hadn't begun
                        // sending — peer never saw an LPM header for
                        // this one, no obligation to deliver. Just
                        // drop. (Stream is gone anyway.)
                    }
                    else if (ent.Type != FrameType.Message)
                    {
                        // HalfClose / Trailers / etc parked behind the
                        // Message. Write them out so the peer sees
                        // proper stream termination.
                        var hdr = new FrameHeader(ent.Type, ent.StreamId, (uint)ent.Length, ent.Flags);
                        FrameProtocol.WriteFrame(_ring, hdr, ent.Payload.Span, _ct);
                    }
                    if (ent.ReturnToPool != null)
                        ArrayPool<byte>.Shared.Return(ent.ReturnToPool);
                    ent.CompletionSignal?.Set();
                    anyProgress = true;
                }
                (toRemove ??= new List<uint>()).Add(streamId);
                continue;
            }

            // Drain entries whose stream now has enough quota. Per-stream
            // FIFO is preserved: we always inspect the head first and bail
            // at the first entry that can't make progress.
            //
            // Three paths share this loop:
            //   * Chunked-write resume (Message with Length > window
            //     OR BytesWritten > 0): partial-write via
            //     TryWriteChunkedMessageNonBlocking; update offset in
            //     place if still partial, drain fully if complete.
            //   * Small Message (<= window): wait for full quota then
            //     dispatch through WriteMessageEntryInBatch (legacy
            //     behaviour).
            //   * Non-Message entry parked behind a stream-ordering
            //     hold (HalfClose, Trailers, etc): no FC concerns —
            //     just write the single frame. Reached only after the
            //     preceding Message ahead of it has fully drained, so
            //     wire order {Message…, HalfClose, Trailers} is
            //     guaranteed.
            while (queue.First != null)
            {
                var ent = queue.First.Value;

                if (ent.Type != FrameType.Message)
                {
                    // HalfClose / Trailers / etc parked behind a Message
                    // on this stream by ParkForwardSameStreamLocked.
                    // No FC required for these frame types — write
                    // immediately.
                    queue.RemoveFirst();
                    _deferredCount--;
                    var header = new FrameHeader(ent.Type, ent.StreamId, (uint)ent.Length, ent.Flags);
                    FrameProtocol.WriteFrame(_ring, header, ent.Payload.Span, _ct);
                    if (ent.ReturnToPool != null)
                        ArrayPool<byte>.Shared.Return(ent.ReturnToPool);
                    ent.CompletionSignal?.Set();
                    anyProgress = true;
                    continue;
                }

                if (ent.Length > ShmConstants.InitialWindowSize || ent.BytesWritten > 0)
                {
                    // Chunked-write entry. Use non-blocking helper so the
                    // writer task can hop to other streams between WU
                    // round-trips instead of blocking per chunk.
                    _ring.EndBatchWrite();
                    DrainControlFrames();
                    int writtenOffset;
                    try
                    {
                        writtenOffset = TryWriteChunkedMessageNonBlocking(in ent, fairStream, ent.BytesWritten);
                    }
                    finally
                    {
                        _ring.BeginBatchWrite();
                    }

                    if (writtenOffset == ent.Length)
                    {
                        // Fully done.
                        queue.RemoveFirst();
                        _deferredCount--;
                        if (ent.ReturnToPool != null)
                            ArrayPool<byte>.Shared.Return(ent.ReturnToPool);
                        ent.CompletionSignal?.Set();
                        anyProgress = true;
                        continue;
                    }

                    if (writtenOffset > ent.BytesWritten)
                    {
                        // Made some progress but still parked. Update the
                        // head's offset in place; LinkedList<T> supports
                        // mutating LinkedListNode.Value directly.
                        ent.BytesWritten = writtenOffset;
                        queue.First!.Value = ent;
                        anyProgress = true;
                    }
                    // Whether we made progress or not, the next chunk's
                    // reserve failed — give up on this stream until WU
                    // arrives again.
                    break;
                }

                // Small message (<= window) legacy path: defer until full
                // quota is available, then dispatch in one go.
                if (fairStream.SendQuota < ent.Length) break;
                queue.RemoveFirst();
                _deferredCount--;
                WriteMessageEntryInBatch(in ent, fairStream);
                if (ent.ReturnToPool != null)
                    ArrayPool<byte>.Shared.Return(ent.ReturnToPool);
                ent.CompletionSignal?.Set();
                anyProgress = true;
            }

            if (queue.Count == 0)
                (toRemove ??= new List<uint>()).Add(streamId);
        }

        if (toRemove != null)
        {
            foreach (var sid in toRemove)
                _deferred.Remove(sid);
        }
        return anyProgress;
    }

    /// <summary>
    /// Phase B: invoked by <see cref="ShmConnection.AddSendQuota"/>
    /// when a per-stream <c>WINDOW_UPDATE</c> arrives. Wakes the
    /// writer task so it can re-evaluate <see cref="_deferred"/> for
    /// entries that the new quota now admits. Cheap — just a signal
    /// set; the actual drain is deferred to the writer task to keep
    /// the codec reader hot path lock-free.
    /// </summary>
    internal void NotifyQuotaUpdated(uint streamId)
    {
        if (_disposed != 0) return;
        // Sticky wake (unconditional): always Set the ready signal on
        // every per-stream WU arrival. The earlier `_deferredCount == 0`
        // gate had a lost-wake race window:
        //   T1 (writer task)              T2 (codec reader)
        //   -----------------             ------------------
        //   FlushBatch enters
        //   processing entry,
        //   about to park to _deferred
        //                                 WU arrives, AddSendQuota,
        //                                 NotifyQuotaUpdated:
        //                                   _deferredCount==0 → return
        //   parks entry, _deferredCount=1
        //   continues processing batch
        //   Phase 1.5 FlushDeferred
        //   (no progress, quota not
        //    yet seen because the WU's
        //    AddSendQuota happened before
        //    we parked)
        //   Phase 3 Reset+Wait → parks forever
        //
        // Sticky semantics of MRES means Set on a non-waiting writer
        // costs a few ns and persists until the next Reset+Wait, which
        // will see the wake immediately. Set on a fully-idle writer
        // (no deferred, no queue) wakes it for one harmless iteration
        // — net cost is on the order of per-WU Channel<T> hop noise.
        // NotifyQuotaUpdated is only called from the codec reader hot
        // path when an inbound WINDOW_UPDATE actually grants per-stream
        // credit, so the rate is per WU RT, not per byte.
        try { _readySignal.Set(); _kernelReadySignal.Set(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Standalone drain pass for <see cref="_deferred"/>, used when
    /// <see cref="_queue"/> is empty but deferred entries exist (e.g.
    /// the writer just woke from a kernel wait triggered by
    /// <see cref="NotifyQuotaUpdated"/>). Wraps its own
    /// <see cref="ShmRing.BeginBatchWrite"/> /
    /// <see cref="ShmRing.EndBatchWrite"/> pair so drained entries
    /// share a single ring signal.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if at least one entry was fully drained
    /// (so <see cref="_deferredCount"/> decreased), <see langword="false"/>
    /// otherwise. The caller uses this to decide whether to continue
    /// the main loop (progress made — may have more work in
    /// <see cref="_queue"/>) or fall through to the wait phase (no
    /// progress — wait for the next <c>WINDOW_UPDATE</c>). Returning
    /// false from a no-progress drain prevents the WriterLoop from
    /// spinning when a partial-write deferred entry can't advance
    /// without more credit.
    /// </returns>
    private bool FlushDeferred()
    {
        if (_deferredCount == 0) return false;
        int before = _deferredCount;
        bool partialProgress = false;
        try
        {
            _ring.BeginBatchWrite();
            try
            {
                partialProgress = TryDrainDeferredLocked();
            }
            finally
            {
                if (s_sawWriterLoop)
                {
                    _ring.EndBatchWriteSuppressSignal();
                    _pendingDeferredSignal = true;
                }
                else
                {
                    _ring.EndBatchWrite();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (RingClosedException) { }
        // "Progress" = either an entry fully drained (count dropped) OR
        // at least one partial-write entry advanced its BytesWritten
        // offset by one or more chunks. The latter matters for the
        // partial-write resume path: the chunked Message stays in
        // <see cref="_deferred"/> after a partial advance, so
        // <c>_deferredCount</c> alone misses that bytes went out on
        // the wire and a follow-up peer drip is imminent.
        return _deferredCount < before || partialProgress;
    }

    /// <summary>
    /// Submits a write action to be executed with exclusive ring access.
    ///
    /// When WriterLoop is active (Phase 2 spin): submits the action as a
    /// callback — WriterLoop detects _inlineAction within ~1 spin iteration
    /// (~30ns) and executes it. The caller blocks on _inlineSignal until done.
    ///
    /// When WriterLoop is idle (Phase 3 kernel Wait): executes the action
    /// directly on the caller's thread via TryPause, avoiding two kernel
    /// wakeups (_readySignal.Set + _inlineSignal.Wait) that on Linux VMs
    /// can each take 0.5–15ms, causing 40–80× throughput regression for
    /// callers with async dispatch layers (Task.Yield → Channel).
    /// </summary>
    internal void ExecuteInline(Action action)
    {
        // Fast path: if WriterLoop is idle in Phase 3, execute directly
        // on the caller's thread. TryPause succeeds instantly when
        // _idleInWait=true (no spin needed).
        if (_idleInWait && TryPauseWriterLoop())
        {
            try
            {
                action();
            }
            finally
            {
                ResumeWriterLoop();
            }
            return;
        }

        // WriterLoop is active (Phase 2 spin) — standard inline execution.
        // Phase 2 checks _inlineAction every iteration, so detection is
        // near-instant.
        _inlineSignal.Reset();
        _inlineAction = action;

        // Always signal _readySignal regardless of _waiting state.
        // On Linux, checking _waiting before Set() creates a lost-wake window:
        //   WriterLoop exits Phase 2 spin (_waiting=0) → context switch →
        //   handler sets _inlineAction, sees _waiting==0, skips Set() →
        //   WriterLoop enters Phase 3, _readySignal.Reset(), re-checks
        //   _inlineAction (should see it), but on Linux/ARM64 the volatile
        //   read may not yet observe the store due to cross-core propagation
        //   delay → Wait() blocks indefinitely.
        // Unconditional Set() adds a spurious wakeup (~50ns) but eliminates
        // the lost-wake entirely. Phase 2 spin + FlushBatch ignore Set()
        // (ManualResetEventSlim stays set until Reset in Phase 3, which
        // re-checks _inlineAction before Wait).
        if (_disposed == 0)
        {
            try { _readySignal.Set(); _kernelReadySignal.Set(); } catch (ObjectDisposedException) { }
        }

        // Wait for WriterLoop to pick up and execute the action.
        _inlineSignal.Wait(_ct);
    }

    /// <summary>
    /// Tries to pause the WriterLoop within a bounded spin.
    /// Returns true if paused successfully (exclusive ring access).
    /// Returns false if WriterLoop is busy — caller should use fallback.
    /// </summary>
    internal bool TryPauseWriterLoop()
    {
        // CAS guard: only one inline writer at a time.
        if (Interlocked.CompareExchange(ref _inlineWriterActive, 1, 0) != 0)
            return false;

        // Fast path: WriterLoop is already idle in Phase 3 wait.
        // In ping-pong benchmarks this is the common case — the queue
        // is empty and WriterLoop is sleeping. Skip the 2000-spin.
        if (_idleInWait)
        {
            _paused = true;
            return true;
        }

        _paused = true;
        // Spin until WriterLoop is truly idle (_idleInWait = true).
        // Phase 2's _paused check (every iteration) ensures WriterLoop
        // exits spin quickly and enters Phase 3 → _idleInWait = true.
        for (int i = 0; i < 2000; i++)
        {
            if (_idleInWait)
                return true;
            Thread.SpinWait(1);
        }
        _paused = false;
        Volatile.Write(ref _inlineWriterActive, 0);
        return false;
    }

    /// <summary>Resumes the WriterLoop after a pause.</summary>
    internal void ResumeWriterLoop()
    {
        _paused = false;
        Volatile.Write(ref _inlineWriterActive, 0);

        // In singleStreamMode with an empty queue, skip the wake —
        // WriterLoop stays in Phase 3 idle for instant next TryPause.
        // But if frames were enqueued while paused (other streams or
        // control frames), we MUST wake WriterLoop to process them.
        // Without this, s=16 concurrent streams regress 56% because
        // queue consumers find WriterLoop asleep with no signal coming.
        if (_singleStreamMode && _queue.IsEmpty && _controlQueue.IsEmpty)
            return;

        try { _readySignal.Set(); _kernelReadySignal.Set(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Writes a message frame inline on the caller's thread.
    /// Caller MUST have called PauseWriterLoop first.
    /// Drains ALL queued frames first (including Headers) to preserve ordering.
    /// </summary>
    internal void WriteInline(uint streamId, ReadOnlySpan<byte> payload, byte extraFlags, CancellationToken ct, ShmGrpcStream? fairStream = null)
    {
        DrainAllQueued();
        var isLast = (extraFlags & MessageFlags.More) == 0;
        // Pass DrainControlFrames as preChunkDrain so each chunk write
        // keeps Ping/Pong keepalive responsive. (FairAwaitWindow is a
        // no-op under SHM no-WU alignment.)
        FrameProtocol.WriteMessage(_ring, streamId, payload, isLast, ct, extraFlags, fairStream,
            fairStream != null ? DrainControlFrames : null);
    }

    /// <summary>
    /// Writes an arbitrary frame inline on the caller's thread.
    /// Caller MUST have called PauseWriterLoop first.
    /// Does NOT drain the queue — caller is responsible for ordering.
    /// </summary>
    internal void WriteInlineFrame(FrameType type, uint streamId, byte flags, ReadOnlySpan<byte> payload, CancellationToken ct)
    {
        var header = new FrameHeader(type, streamId, (uint)payload.Length, flags);
        FrameProtocol.WriteFrame(_ring, header, payload, ct);
    }

    /// <summary>
    /// Inline-write fallback for wire formats where the hand-crafted SHM
    /// header path doesn't apply (e.g. HTTP/2 — its codec needs to own
    /// the on-wire header layout). Serialises the protobuf message into
    /// a temporary pooled buffer, prepends the 5-byte gRPC LPM header,
    /// and emits a single MESSAGE frame via <see cref="FrameProtocol.WriteFrame"/>.
    /// Still benefits from the inline lock: bypasses the WriterLoop queue
    /// and signals overhead.
    /// </summary>
    private void WriteInlineDirectMultiFrameViaCodec(uint streamId, int payloadSize, IMessage message, byte extraFlags, CancellationToken ct)
    {
        const int GrpcHeaderSize = 5;
        var totalPayload = GrpcHeaderSize + payloadSize;
        var buffer = ArrayPool<byte>.Shared.Rent(totalPayload);
        try
        {
            buffer[0] = 0; // no compression
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(1, 4), (uint)payloadSize);
            if (payloadSize > 0)
            {
                message.WriteTo(buffer.AsSpan(GrpcHeaderSize, payloadSize));
            }

            var isLast = (extraFlags & MessageFlags.More) == 0;
            var flags = (byte)((isLast ? 0 : MessageFlags.More) | extraFlags);
            var header = new FrameHeader(FrameType.Message, streamId, (uint)totalPayload, flags);
            FrameProtocol.WriteFrame(_ring, header, buffer.AsSpan(0, totalPayload), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// On-wire frame header size for the H2 wire format used on every ring.
    /// </summary>
    private const int WireHeaderSize = Wire.Http2FrameHeader.Size;

    /// <summary>
    /// Encodes a MESSAGE/DATA wire-format frame header into <paramref name="dest"/>.
    /// <paramref name="internalFlags"/> uses the SHM-internal convention
    /// (<see cref="MessageFlags"/>): the H2 path translates to <c>END_STREAM</c>.
    /// </summary>
    private static void EncodeMessageWireHeader(Span<byte> dest, uint streamId, int payloadLen, byte internalFlags)
    {
        // Defensive: callers (WriteInlineDirectMultiFrame, RingFrameStream)
        // are expected to chunk so each call's payload fits in a 24-bit
        // length field. Bail out loudly rather than silently truncating
        // or corrupting the on-wire frame.
        if ((uint)payloadLen > Wire.Http2FrameHeader.MaxAllowedPayloadLength)
        {
            throw new InvalidOperationException(
                $"H2 wire frame payload {payloadLen} exceeds 24-bit limit. " +
                "Caller must apply Http2FrameHeader.MaxAllowedPayloadLength chunk cap.");
        }
        // Mirror Http2Codec.WriteH2Data: END_STREAM only on a final non-More chunk.
        var isMore = (internalFlags & MessageFlags.More) != 0;
        var isEndStream = (internalFlags & MessageFlags.EndStream) != 0 && !isMore;
        Wire.Http2FrameHeader.Encode(
            dest,
            Wire.Http2FrameType.Data,
            (byte)(isEndStream ? Wire.Http2Flags.EndStream : 0),
            streamId,
            payloadLen);
    }

    /// <summary>
    /// Serializes a protobuf message directly into the ring buffer as one or
    /// more frames, bypassing any intermediate byte[] buffer. A custom
    /// <see cref="RingFrameStream"/> feeds <see cref="CodedOutputStream"/>
    /// writes into per-frame ring reservations. Each frame is committed as
    /// it fills, allowing the reader to start processing early and freeing
    /// ring space for subsequent frames. Works for all message sizes —
    /// single-frame and multi-frame are handled uniformly.
    /// Caller MUST have called TryPauseWriterLoop first.
    /// </summary>
    internal void WriteInlineDirectMultiFrame(uint streamId, int payloadSize, IMessage message, byte extraFlags, CancellationToken ct, ShmGrpcStream? fairStream = null)
    {
        DrainAllQueued();

        var wireHdrSize = WireHeaderSize;
        var cap = (int)_ring.Capacity;
        // Single-frame threshold: payload <= cap/3 -> WriteTo(Span) direct
        // ring write, capped further by H2 24-bit limit and the
        // strict-fair SHM_FAIR_MAX_FRAME bench env. See
        // ComputeSingleFrameThreshold for the canonical formula —
        // server / client coalesce gates also use it to stay aligned
        // with what this method actually treats as single-frame.
        var singleFrameThreshold = ComputeSingleFrameThreshold(cap);
        // Multi-frame chunk size: cap/8 for deeper pipeline (~8 chunks in-flight).
        // More reader/writer overlap reduces WaitForSpace stalls on large messages.
        var chunkSize = Math.Max(1, cap / 8);
        if (chunkSize > Wire.Http2FrameHeader.MaxAllowedPayloadLength)
            chunkSize = Wire.Http2FrameHeader.MaxAllowedPayloadLength;
        if (ShmConstants.FairMaxFramePayload < chunkSize)
            chunkSize = ShmConstants.FairMaxFramePayload;

        // The MESSAGE frame payload includes a 5-byte gRPC length-prefix
        // header (compression flag + big-endian uint32 length) followed by
        // the protobuf bytes. This is required for cross-language interop.
        const int GrpcHeaderSize = 5;
        var framePayloadSize = GrpcHeaderSize + payloadSize;

        // Single-frame + contiguous: use WriteTo(Span<byte>) to serialize
        // protobuf directly into the ring reservation. No CodedOutputStream,
        // no intermediate buffer, no Stream abstraction — one copy from
        // protobuf fields to ring memory.
        // Track whether the single-frame path already reserved H2 send
        // quota for the full message. If we fall through to the RFS path
        // because the ring reservation wrapped, RFS MUST NOT charge the
        // quota again — outer already paid for framePayloadSize bytes
        // (gRFC SHM v3.4 FC double-charge fix).
        bool quotaAlreadyReserved = false;
        if (framePayloadSize <= singleFrameThreshold)
        {
            // Flush pending Ping/Pong control frames before the message
            // write so keepalive stays responsive during long batches,
            // then reserve per-stream H2 send quota for the chunk
            // (blocks until peer grants enough WINDOW_UPDATE credit).
            if (fairStream != null) DrainControlFrames();
            fairStream?.ReserveSendQuotaOrBlock(framePayloadSize, fairStream != null ? DrainControlFrames : null, ct);
            quotaAlreadyReserved = fairStream != null;
            var totalSize = wireHdrSize + framePayloadSize;
            var reservation = _ring.ReserveWrite(totalSize, ct);
            if (reservation.Second.IsEmpty)
            {
                // 9-byte HTTP/2 frame header.
                var isLast = (extraFlags & MessageFlags.More) == 0;
                var flags = (byte)((isLast ? 0 : MessageFlags.More) | extraFlags);
                EncodeMessageWireHeader(reservation.First.Span, streamId, framePayloadSize, flags);

                // 5-byte gRPC LPM header.
                var grpcHdr = reservation.First.Span.Slice(wireHdrSize, GrpcHeaderSize);
                grpcHdr[0] = 0; // no compression
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(grpcHdr.Slice(1), (uint)payloadSize);

                // Serialize directly into ring span — zero intermediate buffer.
                if (payloadSize > 0)
                {
                    var payloadSpan = reservation.First.Span.Slice(wireHdrSize + GrpcHeaderSize, payloadSize);
                    message.WriteTo(payloadSpan);
                }

                _ring.CommitWrite(reservation, totalSize);
                return;
            }
            // Wrap-around: fall through to RingFrameStream
        }

        // Multi-frame or wrap-around: prepend 5-byte gRPC header, then
        // WriteTo(IBufferWriter) through RingFrameStream. Pass
        // fairStream=null when wrap fall-through already reserved quota
        // — RFS must skip its own ReserveSendQuotaOrBlock calls to
        // avoid double-charging the H2 send window (gRFC SHM v3.4).
        var rfsFairStream = quotaAlreadyReserved ? null : fairStream;
        using var rfs = new RingFrameStream(this, streamId, framePayloadSize, chunkSize, extraFlags, ct, rfsFairStream);
        // Write gRPC header as first 5 bytes
        Span<byte> grpcHeader = stackalloc byte[GrpcHeaderSize];
        grpcHeader[0] = 0; // no compression
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(grpcHeader.Slice(1), (uint)payloadSize);
        rfs.Write(grpcHeader);
        // Serialize protobuf directly into ring spans
        message.WriteTo((IBufferWriter<byte>)rfs);
        rfs.CommitFinalFrame();
    }

    /// <summary>
    /// A write-only <see cref="Stream"/> that feeds <see cref="CodedOutputStream"/>
    /// data directly into the ring buffer, splitting across frame boundaries
    /// automatically. Each frame is reserved, header-stamped, and committed
    /// independently so the reader can pipeline consumption.
    /// </summary>
    private sealed class RingFrameStream : Stream, IBufferWriter<byte>
    {
        private readonly ShmFrameWriter _owner;
        private readonly ShmRing _ring;
        private readonly uint _streamId;
        private readonly int _maxFramePayload;
        private readonly byte _extraFlags;
        private readonly int _wireHeaderSize;
        private readonly CancellationToken _ct;
        private readonly ShmGrpcStream? _fairStream;
        private int _remainingPayload;     // total protobuf bytes left to write
        private int _currentFrameCapacity; // payload capacity of current frame
        private int _currentFrameWritten;  // bytes written into current frame payload
        private WriteReservation _currentReservation;
        private bool _reservationActive;

        public RingFrameStream(ShmFrameWriter owner, uint streamId, int totalPayload,
            int maxFramePayload, byte extraFlags, CancellationToken ct, ShmGrpcStream? fairStream = null)
        {
            _owner = owner;
            _ring = owner._ring;
            _streamId = streamId;
            _maxFramePayload = maxFramePayload;
            _extraFlags = extraFlags;
            _wireHeaderSize = ShmFrameWriter.WireHeaderSize;
            _ct = ct;
            _fairStream = fairStream;
            _remainingPayload = totalPayload;
            ReserveNextFrame();
        }

        private void ReserveNextFrame()
        {
            var chunkPayload = Math.Min(_maxFramePayload, _remainingPayload);
            // Flush pending Ping/Pong control traffic before each chunk
            // for keepalive responsiveness, then reserve per-stream H2
            // send quota for this chunk (blocks until peer grants enough
            // WINDOW_UPDATE credit). gRFC SHM v3.4+ FC.
            if (_fairStream != null) _owner.DrainControlFrames();
            _fairStream?.ReserveSendQuotaOrBlock(chunkPayload, _fairStream != null ? _owner.DrainControlFrames : null, _ct);
            // Refund send quota if ring reservation throws (e.g., RingClosed,
            // OperationCanceled): we have debited chunkPayload bytes but
            // never put them on the wire, so peer will never emit WU to
            // refund. Without this, future sends on this stream block
            // forever waiting for credit that the peer doesn't owe.
            WriteReservation reservation;
            try
            {
                reservation = _ring.ReserveWrite(_wireHeaderSize + chunkPayload, _ct);
            }
            catch
            {
                _fairStream?.RefundSendQuota(chunkPayload);
                throw;
            }
            _currentReservation = reservation;
            _currentFrameCapacity = chunkPayload;
            _currentFrameWritten = 0;
            _reservationActive = true;

            // 9-byte HTTP/2 frame header.
            var isLastFrame = (chunkPayload >= _remainingPayload);
            var isLast = isLastFrame && (_extraFlags & MessageFlags.More) == 0;
            byte flags;
            if (isLast)
                flags = _extraFlags;
            else
                flags = (byte)(MessageFlags.More | _extraFlags);

            Span<byte> headerBytes = stackalloc byte[Wire.Http2FrameHeader.Size];
            EncodeMessageWireHeader(headerBytes, _streamId, chunkPayload, flags);

            WriteToReservation(_currentReservation, 0, headerBytes);
        }

        /// <summary>Commits the current frame after all payload bytes have been written.</summary>
        internal void CommitFinalFrame()
        {
            if (_reservationActive)
            {
                var totalSize = _wireHeaderSize + _currentFrameWritten;
                _ring.CommitWrite(_currentReservation, totalSize);
                _reservationActive = false;
            }
        }

        // IBufferWriter<byte>: protobuf WriteTo(IBufferWriter) calls GetSpan →
        // writes directly into ring span → Advance. No COS, no intermediate
        // buffer. Frame boundaries are handled automatically in Advance.

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            var spaceInFrame = _currentFrameCapacity - _currentFrameWritten;
            if (spaceInFrame <= 0)
            {
                var totalSize = _wireHeaderSize + _currentFrameWritten;
                _ring.CommitWrite(_currentReservation, totalSize);
                _remainingPayload -= _currentFrameWritten;
                _reservationActive = false;
                ReserveNextFrame();
                spaceInFrame = _currentFrameCapacity;
            }

            var writeOffset = _wireHeaderSize + _currentFrameWritten;
            // Return contiguous span within current reservation's First slice.
            // If reservation wraps (Second non-empty), limit to First's remaining.
            var firstLen = _currentReservation.First.Length;
            if (writeOffset < firstLen)
            {
                var available = Math.Min(spaceInFrame, firstLen - writeOffset);
                return _currentReservation.First.Span.Slice(writeOffset, available);
            }
            else
            {
                var secondOffset = writeOffset - firstLen;
                var available = Math.Min(spaceInFrame, _currentReservation.Second.Length - secondOffset);
                return _currentReservation.Second.Span.Slice(secondOffset, available);
            }
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            var spaceInFrame = _currentFrameCapacity - _currentFrameWritten;
            if (spaceInFrame <= 0)
            {
                var totalSize = _wireHeaderSize + _currentFrameWritten;
                _ring.CommitWrite(_currentReservation, totalSize);
                _remainingPayload -= _currentFrameWritten;
                _reservationActive = false;
                ReserveNextFrame();
                spaceInFrame = _currentFrameCapacity;
            }

            var writeOffset = _wireHeaderSize + _currentFrameWritten;
            var firstLen = _currentReservation.First.Length;
            if (writeOffset < firstLen)
            {
                var available = Math.Min(spaceInFrame, firstLen - writeOffset);
                return _currentReservation.First.Slice(writeOffset, available);
            }
            else
            {
                var secondOffset = writeOffset - firstLen;
                var available = Math.Min(spaceInFrame, _currentReservation.Second.Length - secondOffset);
                return _currentReservation.Second.Slice(secondOffset, available);
            }
        }

        public void Advance(int count)
        {
            if ((uint)count > (uint)(_currentFrameCapacity - _currentFrameWritten))
                throw new ArgumentOutOfRangeException(nameof(count));
            _currentFrameWritten += count;
        }

        public override void Write(byte[] buffer, int offset, int count)
            => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (buffer.Length > 0)
            {
                var spaceInFrame = _currentFrameCapacity - _currentFrameWritten;
                if (spaceInFrame <= 0)
                {
                    // Current frame full — commit and reserve next.
                    // Do NOT use BeginBatchWrite here: the next ReserveWrite
                    // may WaitForSpace, which needs the reader to consume
                    // data. If the OS signal is deferred, the reader may be
                    // blocked in a kernel wait and never see the data →
                    // deadlock. Each per-frame signal costs ~10µs (futex
                    // wake), negligible for multi-frame messages (≥16MB).
                    var totalSize = _wireHeaderSize + _currentFrameWritten;
                    _ring.CommitWrite(_currentReservation, totalSize);
                    _remainingPayload -= _currentFrameWritten;
                    _reservationActive = false;
                    ReserveNextFrame();
                    spaceInFrame = _currentFrameCapacity;
                }

                var toCopy = Math.Min(buffer.Length, spaceInFrame);
                var writeOffset = _wireHeaderSize + _currentFrameWritten;
                WriteToReservation(_currentReservation, writeOffset, buffer[..toCopy]);
                _currentFrameWritten += toCopy;
                buffer = buffer[toCopy..];
            }
        }

        /// <summary>
        /// Writes data into a reservation at a given byte offset, handling
        /// the First/Second wrap-around split.
        /// </summary>
        private static void WriteToReservation(WriteReservation reservation, int offset, ReadOnlySpan<byte> data)
        {
            var firstLen = reservation.First.Length;
            if (offset < firstLen)
            {
                var available = firstLen - offset;
                if (data.Length <= available)
                {
                    data.CopyTo(reservation.First.Span.Slice(offset));
                }
                else
                {
                    data[..available].CopyTo(reservation.First.Span.Slice(offset));
                    data[available..].CopyTo(reservation.Second.Span);
                }
            }
            else
            {
                var secondOffset = offset - firstLen;
                data.CopyTo(reservation.Second.Span.Slice(secondOffset));
            }
        }

        protected override void Dispose(bool disposing)
        {
            // If an exception interrupted WriteTo/Flush, we have an
            // uncommitted reservation with a header stamped for the
            // full planned payload but only partial data written.
            // We must NOT commit it as-is: the reader reads by header
            // length, so it would block waiting for bytes that will
            // never arrive, or interpret following data as this frame's
            // tail — corrupting the connection.
            //
            // Instead, rewrite the header with the actual bytes written
            // and commit only that. The frame contains truncated protobuf
            // (will fail deserialization), but the ring stays consistent
            // and the reader can skip/error the frame cleanly.
            if (disposing && _reservationActive)
            {
                try
                {
                    // Rewrite header with actual payload length.
                    Span<byte> headerBytes = stackalloc byte[Wire.Http2FrameHeader.Size];
                    EncodeMessageWireHeader(headerBytes, _streamId, _currentFrameWritten, _extraFlags);
                    WriteToReservation(_currentReservation, 0, headerBytes);

                    var written = _wireHeaderSize + _currentFrameWritten;
                    _ring.CommitWrite(_currentReservation, written);
                }
                catch { /* ring may be closed */ }
                _reservationActive = false;
                // Refund the unused portion of H2 send quota: we reserved
                // _currentFrameCapacity bytes but only put _currentFrameWritten
                // bytes on the wire, so the peer will only emit WU for the
                // bytes it received. Without this refund we permanently
                // over-debit by (capacity - written) per partial frame,
                // causing future sends on the stream to stall.
                var unused = _currentFrameCapacity - _currentFrameWritten;
                if (unused > 0)
                {
                    _fairStream?.RefundSendQuota(unused);
                }
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// Drain priority-queue control frames (Ping, Pong; WindowUpdate
    /// retained as a legacy enum value but never emitted under SHM no-WU).
    /// Control frames are routed to <c>_controlQueue</c> at enqueue time so
    /// they are reachable regardless of how many Message frames are queued
    /// in <c>_queue</c>. Called before message writes to keep Ping/Pong
    /// keepalive responsive during long batches.
    /// </summary>
    private void DrainControlFrames()
    {
        while (_controlQueue.TryDequeue(out var entry))
        {
            var header = new FrameHeader(entry.Type, entry.StreamId, (uint)entry.Length, entry.Flags);
            FrameProtocol.WriteFrame(_ring, header, entry.Payload.Span, _ct);
            if (entry.ReturnToPool != null)
                ArrayPool<byte>.Shared.Return(entry.ReturnToPool);
            entry.CompletionSignal?.Set();
        }
    }

    /// <summary>
    /// Drain ALL queued frames and write them to the ring.
    /// Called by WriteInline to ensure frames enqueued before the pause
    /// (e.g., response Headers from EnsureResponseHeadersSentAsync) are
    /// written before the inline message, preserving frame ordering.
    /// </summary>
    private void DrainAllQueued()
    {
        // Drain control frames first (WindowUpdate, Ping, Pong)
        DrainControlFrames();

        // Then drain all message/stream frames
        while (_queue.TryDequeue(out var entry))
        {
            if (entry.CancelFlag != null && Volatile.Read(ref entry.CancelFlag.Value))
            {
                // Signal the waiting caller even though we skipped the write.
                // Without this, EnqueueZeroCopyAndWait blocks indefinitely.
                if (entry.ReturnToPool != null)
                    ArrayPool<byte>.Shared.Return(entry.ReturnToPool);
                entry.CompletionSignal?.Set();
                continue;
            }

            if (entry.Type == FrameType.Message)
            {
                var isLast = (entry.Flags & MessageFlags.More) == 0;
                var extraFlags = (byte)(entry.Flags & ~MessageFlags.More);
                ShmGrpcStream? fairStream = null;
                StreamMap?.TryGetValue(entry.StreamId, out fairStream);
                // preChunkDrain not threaded through DrainAllQueued: this path
                // is only taken from WriteInline's pre-pause drain where
                // _controlQueue is empty by construction (caller paused
                // WriterLoop after observing both queues drained).
                FrameProtocol.WriteMessage(_ring, entry.StreamId, entry.Payload.Span, isLast, _ct, extraFlags, fairStream);
            }
            else
            {
                var header = new FrameHeader(entry.Type, entry.StreamId, (uint)entry.Length, entry.Flags);
                FrameProtocol.WriteFrame(_ring, header, entry.Payload.Span, _ct);
            }

            if (entry.ReturnToPool != null)
                ArrayPool<byte>.Shared.Return(entry.ReturnToPool);
            entry.CompletionSignal?.Set();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

            // 1. Stop accepting new entries and wake the writer thread.
            _completed = true;
            _readySignal.Set();
            _kernelReadySignal.Set();

            // 2. Give the writer thread a chance to flush remaining entries.
            var writerDone = false;
            try
            {
                writerDone = _writerTask.Wait(TimeSpan.FromMilliseconds(500));
            }
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException or RingClosedException)
            {
                writerDone = true;
            }
            catch (AggregateException ex)
            {
                System.Diagnostics.Debug.WriteLine($"ShmFrameWriter.Dispose: writer task faulted: {ex.InnerException?.Message}");
                writerDone = true; // task faulted — it's done
            }

            // 3. If the writer is still blocked (e.g. ring full), cancel to
            //    unblock it, then wait again for it to actually exit.
            if (!writerDone)
            {
                _cts.Cancel();
                _readySignal.Set(); // unblock if waiting again
                _kernelReadySignal.Set();
                try
                {
                    writerDone = _writerTask.Wait(TimeSpan.FromMilliseconds(500));
                }
                catch (AggregateException ex2) when (ex2.InnerException is OperationCanceledException or RingClosedException)
                {
                    writerDone = true;
                }
                catch (AggregateException ex2)
                {
                    System.Diagnostics.Debug.WriteLine($"ShmFrameWriter.Dispose: writer task faulted after cancel: {ex2.InnerException?.Message}");
                    writerDone = true;
                }
            }

            // 4. Drain remaining entries.
            if (writerDone)
            {
                while (_queue.TryDequeue(out var entry))
                {
                    if (entry.ReturnToPool != null)
                        ArrayPool<byte>.Shared.Return(entry.ReturnToPool);
                    entry.CompletionSignal?.Set();
                }
                while (_controlQueue.TryDequeue(out var ctlEntry))
                {
                    if (ctlEntry.ReturnToPool != null)
                        ArrayPool<byte>.Shared.Return(ctlEntry.ReturnToPool);
                    ctlEntry.CompletionSignal?.Set();
                }
            }

            _readySignal.Dispose();
            // _kernelReadySignal is allocated alongside _readySignal in the
            // ctor (used by the SAW-WriterLoop opt-in path via Win32
            // SignalObjectAndWait); dispose it here too to avoid leaking the
            // EventWaitHandle when the WriterLoop is torn down.
            _kernelReadySignal.Dispose();

            // 5. Final drain: catch any frames enqueued between step 4 and
            //    _readySignal.Dispose(). Concurrent Enqueue calls that passed
            //    the _completed check before it was set may still be in-flight.
            while (_queue.TryDequeue(out var lateEntry))
            {
                if (lateEntry.ReturnToPool != null)
                    ArrayPool<byte>.Shared.Return(lateEntry.ReturnToPool);
                lateEntry.CompletionSignal?.Set();
            }
            while (_controlQueue.TryDequeue(out var lateCtl))
            {
                if (lateCtl.ReturnToPool != null)
                    ArrayPool<byte>.Shared.Return(lateCtl.ReturnToPool);
                lateCtl.CompletionSignal?.Set();
            }

            // 6. Dispose pooled wait tokens.
            while (_waitTokenPool.TryTake(out var token))
            {
                token.Signal.Dispose();
            }
    }
}
