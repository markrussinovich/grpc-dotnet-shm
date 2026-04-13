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

    private readonly ShmRing _ring;
    private readonly ConcurrentQueue<FrameEntry> _queue;
    private readonly ConcurrentQueue<FrameEntry> _controlQueue; // WindowUpdate/Ping/Pong bypass Messages
    private readonly ManualResetEventSlim _readySignal;
    internal int _waiting; // 1 if writer thread is blocked in Wait; accessed via Volatile.Read/Write
    private volatile bool _completed;
    private readonly Task _writerTask;
    private readonly CancellationTokenSource _cts;
    private readonly CancellationToken _ct;
    private bool _disposed;

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

    public ShmFrameWriter(ShmRing ring, CancellationTokenSource cts)
    {
        _ring = ring;
        _cts = cts;
        _ct = cts.Token;
        _queue = new ConcurrentQueue<FrameEntry>();
        _controlQueue = new ConcurrentQueue<FrameEntry>();
        _readySignal = new ManualResetEventSlim(false);

        // Register drain callback so WaitForSpace can flush control frames
        // (WindowUpdate) before blocking, preventing bidirectional deadlock.
        _ring.WaitForSpaceDrainCallback = DrainControlFrames;

        _writerTask = Task.Factory.StartNew(
            WriterLoop, _ct,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

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
            buf = ArrayPool<byte>.Shared.Rent(len);
            payload.CopyTo(buf);
            mem = buf.AsMemory(0, len);
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
        // queued Messages. This prevents deadlock when 32+ concurrent streams
        // fill the WriterLoop's batch with large Messages that block on
        // WaitForSpace — WindowUpdate must still be deliverable.
        if (type == FrameType.WindowUpdate || type == FrameType.Ping || type == FrameType.Pong)
            _controlQueue.Enqueue(entry);
        else
            _queue.Enqueue(entry);

        // Wake the writer thread if it is blocked waiting for data.
        // Late enqueues (after _completed is set) are handled by the three
        // drain layers in WriterLoop + Dispose — no dequeue here to avoid
        // accidentally consuming another thread's frame from the queue head.
        if (Volatile.Read(ref _waiting) != 0 && !_disposed)
        {
            try { _readySignal.Set(); } catch (ObjectDisposedException) { }
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

        if (Volatile.Read(ref _waiting) != 0 && !_disposed)
        {
            try { _readySignal.Set(); } catch (ObjectDisposedException) { }
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

        var signal = new ManualResetEventSlim(false);
        var cancelFlag = new StrongBox<bool>(false);
        _queue.Enqueue(new FrameEntry
        {
            Type = type, StreamId = streamId, Flags = flags,
            Length = payload.Length, Payload = payload,
            ReturnToPool = null,
            CompletionSignal = signal,
            CancelFlag = cancelFlag
        });

        if (Volatile.Read(ref _waiting) != 0 && !_disposed)
        {
            try { _readySignal.Set(); } catch (ObjectDisposedException) { }
        }

        // Block until WriterLoop has written the data to the ring.
        try
        {
            if (!signal.Wait(5000, cancellationToken))
            {
                signal.Wait(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Mark the queued entry so the writer thread skips it.
            // This prevents a cancelled message from hitting the wire
            // and avoids flow-control skew (caller restores _sendWindow).
            Volatile.Write(ref cancelFlag.Value, true);
            throw;
        }
        finally
        {
            signal.Dispose();
        }
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
                    inlineAction();
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

                // Phase 3: blocking wait (lost-wake-safe pattern)
                // Set _waiting BEFORE Reset to ensure writers see it and call Set().
                phase3:
                Volatile.Write(ref _waiting, 1);
                _readySignal.Reset();

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
                    _readySignal.Wait(_ct);
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
                    // in EnqueueZeroCopyAndWait). Writing a cancelled entry would cause
                    // flow-control skew since the caller already restored _sendWindow.
                    if (entry.CancelFlag != null && Volatile.Read(ref entry.CancelFlag.Value))
                        continue;

                    var payload = entry.Payload.Span;

                    // Before writing a large message that may block in
                    // WaitForSpace, drain any newly queued control frames
                    // (WindowUpdate, Ping, Pong). These are tiny (20-30 bytes)
                    // and always fit. WindowUpdate is critical: it tells the
                    // remote side to advance its ReadIdx, freeing ring space
                    // that this write needs. Without this drain, 16+ concurrent
                    // streams can deadlock: both sides' WriterLoops block on
                    // WaitForSpace while WindowUpdates sit in the queue behind
                    // the large message being written.
                    if (entry.Type == FrameType.Message && payload.Length >= 65536)
                    {
                        _ring.EndBatchWrite();
                        DrainControlFrames();
                    }

                    if (entry.Type == FrameType.Message)
                    {
                        var isLast = (entry.Flags & MessageFlags.More) == 0;
                        var extraFlags = (byte)(entry.Flags & ~MessageFlags.More);
                        FrameProtocol.WriteMessage(_ring, entry.StreamId, payload, isLast, _ct, extraFlags);
                        // Re-enter batch mode after large messages to coalesce
                        // OS signals for the rest of the batch (previously
                        // removed due to suspected deadlock, but trace confirmed
                        // no deadlock — the "TIMEOUT" was performance regression
                        // from 32× extra signals per batch).
                        if (payload.Length >= 65536)
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
                _ring.EndBatchWrite();
            }
        }
        finally
        {
            for (var i = 0; i < count; i++)
            {
                if (batch[i].ReturnToPool != null)
                    ArrayPool<byte>.Shared.Return(batch[i].ReturnToPool!);
                batch[i].CompletionSignal?.Set();
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
        if (!_disposed)
        {
            try { _readySignal.Set(); } catch (ObjectDisposedException) { }
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
        try { _readySignal.Set(); } catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Writes a message frame inline on the caller's thread.
    /// Caller MUST have called PauseWriterLoop first.
    /// Drains ALL queued frames first (including Headers) to preserve ordering.
    /// </summary>
    internal void WriteInline(uint streamId, ReadOnlySpan<byte> payload, byte extraFlags, CancellationToken ct)
    {
        DrainAllQueued();
        var isLast = (extraFlags & MessageFlags.More) == 0;
        FrameProtocol.WriteMessage(_ring, streamId, payload, isLast, ct, extraFlags);
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
    /// Tries to serialize a protobuf message directly into the ring buffer,
    /// avoiding the intermediate ArrayPool buffer and memcpy. Pre-checks that
    /// enough contiguous ring space exists so ReserveWrite won't wrap around.
    /// If the ring would wrap, returns false immediately (no PAD frame, no
    /// side effects) — the caller falls back to WriteInline.
    /// Caller MUST have called PauseWriterLoop first (sole writer guarantee).
    /// </summary>
    internal bool TryWriteInlineDirect(uint streamId, int payloadSize, IMessage message, byte extraFlags, CancellationToken ct)
    {
        var cap = (int)_ring.Capacity;
        var maxFramePayload = Math.Max(1, cap / 4 - ShmConstants.FrameHeaderSize);

        if (payloadSize > maxFramePayload)
            return false;

        var totalSize = ShmConstants.FrameHeaderSize + payloadSize;

        // Drain queued frames first (response Headers etc.) so that
        // space consumed by pending queue entries is freed before the
        // contiguity check. Without this, the precheck may reject a
        // direct-to-ring write that would succeed after draining.
        DrainAllQueued();

        // Check contiguity after drain.
        if (!_ring.HasContiguousWriteSpace(totalSize))
            return false;

        var reservation = _ring.ReserveWrite(totalSize, ct);
        if (!reservation.Second.IsEmpty)
            return false; // lost contiguity — very rare

        // Write frame header.
        var isLast = (extraFlags & MessageFlags.More) == 0;
        var flags = (byte)((isLast ? 0 : MessageFlags.More) | extraFlags);
        var header = new FrameHeader(FrameType.Message, streamId, (uint)payloadSize, flags);
        Span<byte> headerBytes = stackalloc byte[ShmConstants.FrameHeaderSize];
        header.EncodeTo(headerBytes);
        headerBytes.CopyTo(reservation.First.Span);

        // Zero-copy: serialize directly into ring memory via CodedOutputStream
        // backed by an UnmanagedMemoryStream over the ring's mapped pointer.
        var payloadSlice = reservation.First.Slice(ShmConstants.FrameHeaderSize, payloadSize);
        using (var pin = payloadSlice.Pin())
        {
            unsafe
            {
                using var ums = new System.IO.UnmanagedMemoryStream(
                    (byte*)pin.Pointer, 0, payloadSize, System.IO.FileAccess.Write);
                using var cos = new CodedOutputStream(ums);
                message.WriteTo(cos);
                cos.Flush();
            }
        }

        _ring.CommitWrite(reservation, totalSize);
        return true;
    }

    /// <summary>
    /// Drain control frames (WindowUpdate, Ping, Pong) from the priority queue.
    /// These are routed to _controlQueue at enqueue time so they are always
    /// reachable regardless of how many Message frames are queued in _queue.
    /// Critical for preventing deadlock when WriterLoop's WaitForSpace needs
    /// the remote side to advance ReadIdx via WindowUpdate.
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
                continue;

            if (entry.Type == FrameType.Message)
            {
                var isLast = (entry.Flags & MessageFlags.More) == 0;
                var extraFlags = (byte)(entry.Flags & ~MessageFlags.More);
                FrameProtocol.WriteMessage(_ring, entry.StreamId, entry.Payload.Span, isLast, _ct, extraFlags);
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
        if (!_disposed)
        {
            _disposed = true;

            // 1. Stop accepting new entries and wake the writer thread.
            _completed = true;
            _readySignal.Set();

            // 2. Give the writer thread a chance to flush remaining entries.
            var writerDone = false;
            try
            {
                writerDone = _writerTask.Wait(TimeSpan.FromMilliseconds(500));
            }
            catch (AggregateException)
            {
                writerDone = true; // task faulted — it's done
            }

            // 3. If the writer is still blocked (e.g. ring full), cancel to
            //    unblock it, then wait again for it to actually exit.
            if (!writerDone)
            {
                _cts.Cancel();
                _readySignal.Set(); // unblock if waiting again
                try
                {
                    writerDone = _writerTask.Wait(TimeSpan.FromMilliseconds(500));
                }
                catch (AggregateException)
                {
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
        }
    }
}
