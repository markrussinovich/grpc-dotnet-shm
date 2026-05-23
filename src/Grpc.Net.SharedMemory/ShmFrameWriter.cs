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
    /// Capacity (in bytes) of the underlying TX ring buffer. Used by
    /// callers of <see cref="BeginInlineBatch"/> to decide whether the
    /// message they are about to write is small enough to wake-coalesce
    /// safely. Coalescing a multi-chunk message that exceeds
    /// <c>Capacity / 2</c> deadlocks: the chunks fill the ring before the
    /// batch closes (so no peer wake fires), the peer cannot drain, and
    /// the next <c>ReserveWrite</c> blocks in <c>WaitForSpace</c>.
    /// Matches the <c>willLikelyChunk</c> threshold inside
    /// <see cref="FlushBatch"/>.
    /// </summary>
    internal int RingCapacity => (int)_ring.Capacity;


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

                // Phase 2: spin-wait for data.
                // In streaming ping-pong, the consumer typically enqueues the
                // next frame within ~30-50µs (one full cross-ring round-trip).
                // A brief spin here avoids falling through to the heavier
                // ManualResetEventSlim.Wait (~80µs OS penalty).
                //
                var found = false;
                for (int spin = 0; spin < ShmConstants.SpinIterationsMin; spin++)
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

                    // Skip entries cancelled by the caller (e.g. OperationCanceledException
                    // in EnqueueZeroCopyAndWait). The caller is responsible for
                    // releasing any pooled buffer; writing a cancelled entry
                    // would produce duplicate/out-of-order data on the wire.
                    if (entry.CancelFlag != null && Volatile.Read(ref entry.CancelFlag.Value))
                        continue;

                    var payload = entry.Payload.Span;

                    // Before writing a large message that may block in
                    // WaitForSpace, drain any newly queued priority control
                    // frames (Ping, Pong; WindowUpdate is a no-op slot under
                    // SHM no-WU). These are tiny (20-30 bytes) and always
                    // fit. Keeping keepalive responsive matters when 16+
                    // concurrent streams compete for the WriterLoop and a
                    // large Message would otherwise stall the queue head.
                    //
                    // Also exit batch mode for ANY message that may need to
                    // chunk on the current ring (payload + 9-byte H2 header
                    // bigger than ~half the ring). Otherwise a small ring +
                    // multi-frame message deadlocks: chunk N reserves, fills,
                    // commits silently (no OS signal under batch); the next
                    // ReserveWrite blocks because reader is asleep waiting for
                    // a signal that won't fire until EndBatchWrite — which
                    // never runs because we're stuck in WaitForSpace.
                    //
                    // Strict-fair mode (SHM_FAIR_MAX_FRAME): the cap forces
                    // chunking at 16 KiB, well below the 64 KiB
                    // willLikelyChunk threshold. If we stayed in batch
                    // mode, already-written chunks would not have signalled
                    // the peer reader, stalling progress until EndBatchWrite.
                    // Drop out of batch for ANY multi-frame Message in fair
                    // mode.
                    var ringCap = (int)_ring.Capacity;
                    var hdrSize = Wire.Http2FrameHeader.Size;
                    var fairChunking = entry.Type == FrameType.Message
                        && ShmConstants.FairMaxFramePayload != int.MaxValue
                        && payload.Length > ShmConstants.FairMaxFramePayload;
                    var willLikelyChunk = fairChunking
                        || (entry.Type == FrameType.Message
                            && payload.Length >= 65536)
                        || (entry.Type == FrameType.Message
                            && payload.Length + hdrSize >= ringCap / 2);
                    if (willLikelyChunk)
                    {
                        _ring.EndBatchWrite();
                        DrainControlFrames();
                    }

                    if (entry.Type == FrameType.Message)
                    {
                        var isLast = (entry.Flags & MessageFlags.More) == 0;
                        var extraFlags = (byte)(entry.Flags & ~MessageFlags.More);
                        ShmGrpcStream? fairStream = null;
                        StreamMap?.TryGetValue(entry.StreamId, out fairStream);
                        // Pass DrainControlFrames as preChunkDrain so each
                        // chunk write flushes pending Ping/Pong control
                        // traffic; keeps keepalive responsive during long
                        // chunked sends. (FairAwaitWindow is a no-op under
                        // SHM no-WU alignment — see ShmGrpcStream.)
                        FrameProtocol.WriteMessage(_ring, entry.StreamId, payload, isLast, _ct, extraFlags, fairStream,
                            fairStream != null ? DrainControlFrames : null);
                        // Re-enter batch mode after large messages to coalesce
                        // OS signals for the rest of the batch (previously
                        // removed due to suspected deadlock, but trace confirmed
                        // no deadlock — the "TIMEOUT" was performance regression
                        // from 32× extra signals per batch).
                        if (willLikelyChunk)
                            _ring.BeginBatchWrite();
                    }
                    else
                    {
                        var header = new FrameHeader(entry.Type, entry.StreamId, (uint)entry.Length, entry.Flags);
                        FrameProtocol.WriteFrame(_ring, header, payload, _ct);
                    }
                }
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
        // Single-frame threshold: payload ≤ cap/3 → WriteTo(Span) direct ring write.
        // Kept high to maximize speculative zero-copy on the reader side.
        var singleFrameThreshold = Math.Max(1, cap / 3);
        // Multi-frame chunk size: cap/8 for deeper pipeline (~8 chunks in-flight).
        // More reader/writer overlap reduces WaitForSpace stalls on large messages.
        var chunkSize = Math.Max(1, cap / 8);

        // HTTP/2 hard limit (RFC 7540 §4.2 / §6.5.2): per-frame payload must
        // fit in 24 bits (≤ 2^24 - 1). Cap both thresholds below that so a
        // 16 MiB protobuf (which yields a 16 MiB + 5 B framePayloadSize) is
        // not handed to Http2FrameHeader.Encode where it would throw.
        if (singleFrameThreshold > Wire.Http2FrameHeader.MaxAllowedPayloadLength)
            singleFrameThreshold = Wire.Http2FrameHeader.MaxAllowedPayloadLength;
        if (chunkSize > Wire.Http2FrameHeader.MaxAllowedPayloadLength)
            chunkSize = Wire.Http2FrameHeader.MaxAllowedPayloadLength;

        // Strict-fair bench cap (SHM_FAIR_MAX_FRAME): force same multi-frame
        // splitting as TCP/UDS gRPC. No-op when env var is unset.
        if (ShmConstants.FairMaxFramePayload < singleFrameThreshold)
            singleFrameThreshold = ShmConstants.FairMaxFramePayload;
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
        if (framePayloadSize <= singleFrameThreshold)
        {
            // Flush pending Ping/Pong control frames before the message
            // write so keepalive stays responsive during long batches.
            // FairAwaitWindow is a no-op under SHM no-WU alignment (kept
            // for API stability).
            if (fairStream != null) DrainControlFrames();
            fairStream?.FairAwaitWindow(framePayloadSize, fairStream != null ? DrainControlFrames : null);
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
        // WriteTo(IBufferWriter) through RingFrameStream.
        using var rfs = new RingFrameStream(this, streamId, framePayloadSize, chunkSize, extraFlags, ct, fairStream);
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
            // for keepalive responsiveness. FairAwaitWindow is a no-op
            // under SHM no-WU alignment; retained for API stability.
            if (_fairStream != null) _owner.DrainControlFrames();
            _fairStream?.FairAwaitWindow(chunkPayload, _fairStream != null ? _owner.DrainControlFrames : null);
            var totalSize = _wireHeaderSize + chunkPayload;
            _currentReservation = _ring.ReserveWrite(totalSize, _ct);
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
