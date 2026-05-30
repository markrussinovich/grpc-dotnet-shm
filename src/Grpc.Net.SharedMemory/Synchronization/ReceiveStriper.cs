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

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Grpc.Net.SharedMemory.Synchronization;

/// <summary>
/// Receive-side dispatcher that distributes inbound frames across a small
/// fixed number of "stripe" workers so the SHM reader thread no longer
/// fans out to N per-stream channels on its own thread of execution.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists. The previous design had the single SHM reader Thread
/// call <c>stream._inboundFrames.Writer.TryWrite(frame)</c> for every
/// inbound frame. For the high-concurrency tiny-payload cell
/// (1000 streams x 64 B ping-pong) that is one
/// <c>ThreadPool.UnsafeQueueUserWorkItem</c> per frame because the
/// per-stream channel's <c>MRVTSCore.SetResult</c> hits a registered
/// awaiter. The accumulated dispatch cost (~0.5-1 us per frame on Linux,
/// ~17 us per frame on Windows where the threadpool worker wake is more
/// expensive) is the ONLY cell where SHM lost to UDS in the apples-to-
/// apples fair-default bench. Kestrel's <c>System.IO.Pipelines.Pipe</c>
/// dispatch path is more tightly optimised by the .NET runtime team and
/// pays ~0.5-1 us less per frame on this exact pattern.
/// </para>
/// <para>
/// The striper changes the pattern. The reader thread now does only
/// frame-header parse plus a single <c>TryWrite</c> into one of N
/// per-stripe SPSC queues, picked by a Knuth multiplicative hash of
/// <c>streamId</c>. Each stripe owns a dedicated Thread that consumes
/// the queue and invokes <c>stream.OnFrameReceived(frame)</c> inline.
/// Because the stripe is the single writer of the per-stream
/// <c>Channel&lt;InboundFrame&gt;</c>, the per-stream channel is created
/// with <c>AllowSynchronousContinuations=true</c>, so the user's
/// awaiter continuation runs inline on the stripe thread — zero
/// ThreadPool dispatch in the steady state.
/// </para>
/// <para>
/// Why 4 stripes. On a 16-core target, 4 stripes is enough to remove
/// the single-receive-dispatcher bottleneck while leaving cores for the
/// reader, writer, ThreadPool, and application continuations. More
/// stripes add cache churn and wake overhead before they help any cell
/// we care about. The stripe count is a power of two so the hash
/// selector can be a single AND mask.
/// </para>
/// <para>
/// Head-of-line blocking. A single stripe serves ~250 streams in the
/// 1000-stream bench. A slow user continuation blocks the other ~249
/// streams in that stripe but not the other 750. The previous fan-out
/// design had 0 % HOL exposure but paid the dispatch tax on every
/// frame; this design has 25 % HOL exposure for grossly misbehaving
/// user code in exchange for the dispatch cost reclaim.
/// </para>
/// <para>
/// Frame ownership. The reader thread transfers ownership of the
/// <see cref="FramePayload"/> to the striper at <see cref="Enqueue"/>.
/// On dispatch, ownership transfers to <c>ShmGrpcStream.OnFrameReceived</c>
/// (which transfers it again to the per-stream Channel on success, or
/// releases on failure). On stripe shutdown, any frame that was
/// queued but never dispatched is released by the stripe shutdown
/// drain loop. On <see cref="ReceiveStriper"/> dispose after the
/// disposed flag has been observed, the calling reader thread releases
/// the payload itself before returning.
/// </para>
/// </remarks>
internal sealed class ReceiveStriper : IDisposable
{
    // Power-of-two stripe count. Chosen empirically: 4 on a 16-core
    // bench host removes the single-receive-dispatcher bottleneck
    // without hitting diminishing returns from cache churn. Encoded as
    // a constant so the hash mask `& StripeMask` and the stripe array
    // size are both compile-time integers — the JIT can fold the
    // single AND instruction inline.
    private const int StripeCount = 4;
    private const int StripeMask = StripeCount - 1;

    private readonly Stripe[] _stripes;
    private readonly ConcurrentDictionary<uint, ShmGrpcStream> _streams;
    private int _disposed;

    /// <summary>
    /// Per-stripe diagnostic counters (env-gated by SHM_DIAG_STRIPER=1).
    /// Read via <see cref="GetDiagCounters"/> for bench reporting.
    /// </summary>
    private static readonly bool s_diag =
        string.Equals(Environment.GetEnvironmentVariable("SHM_DIAG_STRIPER"),
            "1", StringComparison.Ordinal);
    private static readonly long[] s_stripeEnqueues = new long[StripeCount];
    private static readonly long[] s_stripeDispatches = new long[StripeCount];

    internal static (long[] Enqueues, long[] Dispatches) GetDiagCounters()
    {
        var enq = new long[StripeCount];
        var disp = new long[StripeCount];
        for (var i = 0; i < StripeCount; i++)
        {
            enq[i] = Volatile.Read(ref s_stripeEnqueues[i]);
            disp[i] = Volatile.Read(ref s_stripeDispatches[i]);
        }
        return (enq, disp);
    }

    public ReceiveStriper(ConcurrentDictionary<uint, ShmGrpcStream> streams)
    {
        _streams = streams ?? throw new ArgumentNullException(nameof(streams));
        _stripes = new Stripe[StripeCount];
        for (var i = 0; i < _stripes.Length; i++)
        {
            _stripes[i] = new Stripe(this, i);
        }
    }

    /// <summary>
    /// Enqueues a frame for dispatch on the stripe that owns the given
    /// stream. Transfers ownership of <paramref name="frame"/>. Returns
    /// <c>true</c> on success; <c>false</c> if the striper is disposed
    /// or the stripe queue refused the frame (in which case
    /// <see cref="InboundFrame.ReturnToPool"/> has already been called).
    /// MUST be called only from the SHM reader Thread (single-producer
    /// invariant on each stripe queue).
    /// </summary>
    public bool Enqueue(uint streamId, InboundFrame frame)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            frame.ReturnToPool();
            return false;
        }

        var idx = StripeIndex(streamId);
        if (s_diag) Interlocked.Increment(ref s_stripeEnqueues[idx]);
        return _stripes[idx].Enqueue(streamId, frame);
    }

    /// <summary>
    /// Stripe dispatch callback. Called from the stripe Thread.
    /// Performs the same routing logic the reader thread used to do:
    /// look up the stream and forward to <c>OnFrameReceived</c>. If the
    /// stream has been removed in the meantime, release the payload.
    /// </summary>
    private void Dispatch(int stripeIdx, uint streamId, InboundFrame frame)
    {
        if (s_diag) Interlocked.Increment(ref s_stripeDispatches[stripeIdx]);

        if (_streams.TryGetValue(streamId, out var stream))
        {
            stream.OnFrameReceived(frame);
        }
        else
        {
            frame.ReturnToPool();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int StripeIndex(uint streamId)
    {
        // Knuth multiplicative hash (golden-ratio fractional bits).
        // The low bits of `streamId * 0x9E3779B9u` are NOT well
        // distributed because the multiplier's low 2 bits are `01`,
        // so `(streamId * 0x9E3779B9u) & 3` collapses to
        // `streamId & 3` \u2014 which puts every client-initiated (odd)
        // stream on stripes {1, 3} only. Standard fix: shift right
        // by (32 - log2(StripeCount)) to pick up the well-mixed
        // high bits, which is what Knuth's hash is actually designed
        // for. For StripeCount=4 (log2=2), shift by 30.
        const int Shift = 32 - 2; // 32 - log2(StripeCount)
        var hash = streamId * 0x9E3779B9u;
        return (int)(hash >> Shift) & StripeMask;
    }

    /// <summary>Test-only accessor for <see cref="StripeIndex"/>.</summary>
    internal static int StripeIndexForTesting(uint streamId) => StripeIndex(streamId);

    /// <summary>Test-only accessor for <see cref="StripeCount"/>.</summary>
    internal static int StripeCountForTesting => StripeCount;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Shutdown each stripe: TryComplete its queue and Join its
        // worker thread. The Stripe's run loop observes TryComplete
        // returning false on the next read, drains any pending frames
        // by releasing their payload, and exits. We Join in order, not
        // in parallel — there are only 4 of them and the join cost is
        // negligible vs the safety of clean teardown ordering.
        foreach (var stripe in _stripes)
        {
            stripe.Dispose();
        }
    }

    // =========================================================================
    // Stripe
    // =========================================================================

    private sealed class Stripe : IDisposable
    {
        private readonly ReceiveStriper _owner;
        private readonly int _index;
        private readonly Channel<QueuedFrame> _queue;
        private readonly Thread _thread;
        private int _stopping;

        public Stripe(ReceiveStriper owner, int index)
        {
            _owner = owner;
            _index = index;

            // Per-stripe SPSC channel. SingleWriter=true because the
            // SHM reader Thread is the sole producer for every stripe;
            // SingleReader=true because this stripe's Thread is the
            // sole consumer. AllowSynchronousContinuations is OFF on
            // THIS channel because the stripe Thread is a dedicated
            // long-running consumer and we don't want a producer-side
            // Enqueue to ever inline-run dispatch logic on the reader
            // Thread. The synchronous-continuations win lives on the
            // PER-STREAM channel one hop downstream (see ShmGrpcStream
            // ctor).
            _queue = Channel.CreateUnbounded<QueuedFrame>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });

            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = $"grpc-shm-rx-stripe-{index}",
            };
            _thread.Start();
        }

        public bool Enqueue(uint streamId, InboundFrame frame)
        {
            if (Volatile.Read(ref _stopping) != 0)
            {
                frame.ReturnToPool();
                return false;
            }

            if (!_queue.Writer.TryWrite(new QueuedFrame(streamId, frame)))
            {
                // Unbounded channel never returns false in practice
                // except after TryComplete (which we only call from
                // Dispose). Defensive release.
                frame.ReturnToPool();
                return false;
            }

            return true;
        }

        private void Run()
        {
            try
            {
                // Sync blocking loop. Using WaitToReadAsync().GetAwaiter()
                // .GetResult() rather than `await` so the stripe Thread
                // does not become an async state machine — every dispatch
                // would otherwise allocate a continuation closure.
                while (WaitForRead())
                {
                    while (_queue.Reader.TryRead(out var item))
                    {
                        try
                        {
                            _owner.Dispatch(_index, item.StreamId, item.Frame);
                        }
                        catch (Exception ex)
                        {
                            // Dispatch exceptions are swallowed at the
                            // stripe boundary — the same contract the
                            // legacy reader Thread used to provide via
                            // ProcessFrame's catch. The individual
                            // stream may end up broken, but the stripe
                            // must keep flowing so the other ~250
                            // streams it serves are not collateral
                            // damage.
                            System.Diagnostics.Debug.WriteLine(
                                $"Stripe {_index} dispatch error: {ex.Message}");
                        }
                    }
                }
            }
            finally
            {
                // Shutdown drain. Any frames still queued at this
                // point were enqueued by the reader Thread and the
                // stream owner never consumed them. Release ownership.
                while (_queue.Reader.TryRead(out var item))
                {
                    item.Frame.ReturnToPool();
                }
            }
        }

        private bool WaitForRead()
        {
            try
            {
                return _queue.Reader.WaitToReadAsync()
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }
            catch (ChannelClosedException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _stopping, 1) != 0)
            {
                return;
            }

            _queue.Writer.TryComplete();

            // Self-join guard. If Dispose was reached from THIS stripe's
            // Thread (the user opted into AllowSynchronousContinuations
            // on the per-stream Channel and the user's awaiter
            // continuation ran inline on the stripe Thread, then that
            // continuation called connection.Dispose), Thread.Join()
            // would deadlock against ourselves. Skip the Join and let
            // the stripe Thread observe the completed queue on its
            // next iteration — the run loop's outer
            // `while (WaitForRead())` will see WaitToReadAsync return
            // false and exit. The shutdown drain runs in the finally
            // block. Caller has guaranteed (via the documented
            // InlineReceiveContinuations contract) that no further
            // frames are in flight for the connection being disposed,
            // so the loose end of the Thread not yet returning is not
            // observable to the user.
            if (Thread.CurrentThread != _thread)
            {
                _thread.Join();
            }
        }
    }

    private readonly record struct QueuedFrame(uint StreamId, InboundFrame Frame);
}
