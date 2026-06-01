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

using System.Buffers;
using System.Collections.Generic;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Helpers that parse uncompressed multi-frame LPM ("logical protobuf
/// message") payloads from <see cref="ShmGrpcStream"/> without inviting
/// the inline-receive-continuation self-deadlock.
/// </summary>
/// <remarks>
/// <para>
/// <b>Problem solved.</b> Before this helper, every multi-frame LPM
/// parse went through <see cref="LazyChainRos"/>, which calls a
/// synchronous puller (<see cref="ShmGrpcStream.ReceiveFrameSync"/>)
/// from inside the parser's <c>GetSpan</c> trampoline. When inline
/// continuations are enabled on the inbound channel
/// (<c>AllowSynchronousContinuations=true</c>), the parser's
/// <c>MoveNext</c> runs INLINE on whichever Thread produced the chunk
/// that resumed it — typically the SHM frame-reader Thread (under
/// <c>_bypassStriper</c>) or the stripe Thread. If that producer
/// Thread is also the only Thread that can deliver the NEXT chunk
/// (which it is), the sync pull blocks forever on a frame nobody can
/// produce: HARD DEADLOCK. Confirmed by dotnet-dump on the
/// 16 MiB max-profile ping-pong cell (2026-06-01).
/// </para>
/// <para>
/// <b>Solution: hybrid dispatch by LPM size.</b>
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Eager pre-fetch path</b> (<c>lpmBodyLen &lt;= ChainZcBudget</c>):
///     async-await <see cref="ShmGrpcStream.ReceiveFrameAsync"/> for every
///     subsequent chunk BEFORE invoking <c>MergeFrom</c>. Each
///     <c>await</c> unwinds the producer Thread back to its
///     <c>TryWrite</c> call site, freeing it to deliver the next
///     chunk. Once all chunks are in hand we build a
///     <see cref="ReadOnlySequence{T}"/> and call <c>MergeFrom</c>
///     synchronously — it never pulls, so it never deadlocks. Holding
///     all N chunks costs zero extra ring memory for chain-ZC
///     (the ZC anchor already freezes <c>header.ReadIdx</c> for the
///     entire LPM duration regardless; see
///     <see cref="ShmRing.OpenZcChain"/> and
///     <see cref="ShmRing.IsChainZcStartEligible"/>'s remaining-fits
///     pre-flight gate) and at most O(N × 32 B) extra
///     <see cref="InboundFrame"/> stack-bookkeeping over the lazy
///     path.
///   </description></item>
///   <item><description>
///     <b>Yield-then-lazy fallback</b> (<c>lpmBodyLen &gt;
///     ChainZcBudget</c>): the message is too large for chain-ZC
///     (see <see cref="ShmRing.ChainZcBudget"/>, default
///     <c>cap - 1 KiB</c> ≈ 64 MiB) so each chunk is a pool-rented
///     copy from the ring. Eager pre-fetch here would balloon
///     pool-buffer memory to the entire message size (e.g. 256 MiB).
///     Instead, if we detect we are on the reader Thread, we
///     <see cref="System.Threading.Tasks.Task.Yield"/> once to hop
///     to a ThreadPool worker, then call the existing
///     <see cref="LazyChainRos"/>-based sync-pull parse. The
///     ThreadPool worker can safely block on
///     <see cref="ShmGrpcStream.ReceiveFrameSync"/> because it is
///     not the reader Thread, so the reader Thread is free to keep
///     producing chunks. Pool footprint stays at the lazy ~2-frame
///     minimum.
///   </description></item>
/// </list>
/// <para>
/// <b>Why FIFO is preserved.</b> The async pre-fetch loop is
/// single-reader; <see cref="ShmGrpcStream.ReceiveFrameAsync"/> reads
/// from a <c>SingleConsumerUnboundedChannel</c> that delivers frames
/// in TryWrite order. There is no extra dispatch hop that could
/// reorder chunks.
/// </para>
/// <para>
/// <b>Why the await actually unwinds the producer Thread.</b> An
/// <c>await</c> on an incomplete <see cref="System.Threading.Tasks.ValueTask"/>
/// causes the async state machine's <c>MoveNext</c> to register the
/// continuation and RETURN to its caller. The caller chain unwinds
/// all the way back to the producer Thread's <c>TryWrite</c> call,
/// freeing the producer Thread to read the next frame and call
/// <c>TryWrite</c> again. That next <c>TryWrite</c> fires the
/// continuation inline (<c>AllowSynchronousContinuations=true</c>),
/// resuming the pre-fetch loop on the SAME producer Thread. The
/// pattern is iterative, not recursive — each inline resume starts a
/// fresh stack from the producer's <c>TryWrite</c>, so stack depth
/// stays constant regardless of chunk count.
/// </para>
/// </remarks>
internal static class InboundChainHelper
{
    /// <summary>
    /// Eagerly pulls all remaining chunks of a multi-frame LPM by
    /// awaiting <see cref="ShmGrpcStream.ReceiveFrameAsync"/>. Returns
    /// the chunks in arrival order, with <paramref name="firstFrame"/>
    /// at index 0. The caller owns every returned frame and MUST
    /// release them via <see cref="InboundFrame.ReturnToPool"/> in a
    /// <c>finally</c> block. On any failure (truncation, non-Message
    /// frame mid-body, cancellation) the helper releases every chunk
    /// it has accumulated so far and throws.
    /// </summary>
    /// <param name="stream">Stream owning the inbound channel.</param>
    /// <param name="firstFrame">First chunk; its body bytes after
    /// <paramref name="firstFrameBodyOffset"/> count toward
    /// <paramref name="totalBodyLen"/>.</param>
    /// <param name="firstFrameBodyOffset">Offset inside
    /// <c>firstFrame.Memory</c> where the LPM body begins (5 for
    /// gRPC LPM-framed messages, i.e. after the 5-byte LPM header).</param>
    /// <param name="totalBodyLen">Exact LPM body length declared in the
    /// LPM header on the first chunk.</param>
    /// <param name="sawEndStream">Set to <see langword="true"/> if any
    /// pulled chunk carried <see cref="MessageFlags.EndStream"/>.
    /// Callers driving streaming readers use this to set their
    /// end-of-stream flag.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    /// <returns>Ordered list of chunks. Index 0 is
    /// <paramref name="firstFrame"/>; subsequent entries are
    /// freshly-pulled chunks in arrival order.</returns>
    /// <exception cref="IOException">Truncated (puller returned null
    /// before <paramref name="totalBodyLen"/> bytes accumulated) or
    /// non-Message frame mid-body.</exception>
    public static async ValueTask<List<InboundFrame>> PrefetchAllChunksAsync(
        ShmGrpcStream stream,
        InboundFrame firstFrame,
        int firstFrameBodyOffset,
        long totalBodyLen,
        Action? onEndStream,
        CancellationToken cancellationToken)
    {
        // Pre-size the list assuming the writer-side cap/3 chunk rule:
        // most multi-frame LPMs fit in 2-8 chunks. The list grows
        // naturally if the peer uses a smaller frame cap.
        var chunks = new List<InboundFrame>(8) { firstFrame };
        // EndStream on the first chunk is unusual (More=1 + EndStream)
        // but theoretically valid — record it so the streaming reader
        // sees the half-close once the final chunk arrives.
        if ((firstFrame.Flags & MessageFlags.EndStream) != 0)
        {
            onEndStream?.Invoke();
        }

        long consumed = firstFrame.Length - firstFrameBodyOffset;
        if (consumed < 0)
        {
            // Pathological — first chunk shorter than the LPM header
            // it claimed to carry. Surface as truncation.
            ReleaseAll(chunks);
            throw new IOException(
                $"InboundChainHelper: first chunk length {firstFrame.Length} < " +
                $"firstFrameBodyOffset {firstFrameBodyOffset}.");
        }

        try
        {
            while (consumed < totalBodyLen)
            {
                var pulled = await stream.ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
                if (pulled is null)
                {
                    throw new IOException(
                        $"InboundChainHelper: pullNext returned null at " +
                        $"{consumed}/{totalBodyLen} bytes consumed.");
                }
                var f = pulled.Value;
                if (f.Type != FrameType.Message)
                {
                    // Non-Message frame mid-LPM-body is a protocol
                    // error / peer cancellation. Release this orphan
                    // chunk and surface as truncation.
                    f.ReturnToPool();
                    throw new IOException(
                        $"InboundChainHelper: non-Message frame ({f.Type}) " +
                        $"mid-LPM body at {consumed}/{totalBodyLen}.");
                }
                chunks.Add(f);
                if ((f.Flags & MessageFlags.EndStream) != 0)
                {
                    onEndStream?.Invoke();
                }
                consumed += f.Length;
            }
            return chunks;
        }
        catch
        {
            // Any throw (including the cancellation OCE) must not strand
            // accumulated chunks — they own ring or pool memory.
            ReleaseAll(chunks);
            throw;
        }
    }

    /// <summary>
    /// Builds a <see cref="ReadOnlySequence{T}"/> over a contiguous
    /// list of pre-fetched chunks. The result skips the first
    /// <paramref name="firstFrameBodyOffset"/> bytes of the head
    /// segment (typically the 5-byte gRPC LPM header) and trims the
    /// tail segment to <paramref name="totalBodyLen"/> bytes total.
    /// </summary>
    /// <param name="chunks">Pre-fetched chunks (see
    /// <see cref="PrefetchAllChunksAsync"/>). Must contain at least
    /// the first chunk.</param>
    /// <param name="firstFrameBodyOffset">Bytes to skip on the head
    /// segment.</param>
    /// <param name="totalBodyLen">Exact LPM body length the
    /// sequence should expose. If the accumulated chunk bytes
    /// exceed this (e.g. the producer over-shoots), the tail segment
    /// is trimmed to make the sequence's <c>Length</c> match exactly.</param>
    public static ReadOnlySequence<byte> BuildSequence(
        List<InboundFrame> chunks,
        int firstFrameBodyOffset,
        long totalBodyLen)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count == 0) throw new ArgumentException("Chunks must not be empty.", nameof(chunks));

        var first = chunks[0];
        if (firstFrameBodyOffset < 0 || firstFrameBodyOffset > first.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(firstFrameBodyOffset));
        }

        // Single-chunk fast path — common when the message exactly fits
        // one chunk and was surfaced as a More-flagged frame anyway
        // (chain-ZC start with bodyLength >= totalBodyLen).
        if (chunks.Count == 1)
        {
            var len = (int)Math.Min(first.Length - firstFrameBodyOffset, totalBodyLen);
            var memory = first.Memory.Slice(firstFrameBodyOffset, len);
            return new ReadOnlySequence<byte>(memory);
        }

        // Multi-chunk path: link ReadOnlySequenceSegment<byte> nodes.
        // Each segment wraps the entire chunk's Memory; the final
        // segment is trimmed so the sequence's Length equals totalBodyLen.
        var head = new InboundChainSegment(first.Memory);
        var tail = head;
        long running = first.Memory.Length;

        for (int i = 1; i < chunks.Count; i++)
        {
            var seg = new InboundChainSegment(chunks[i].Memory);
            seg.SetRunningIndex(running);
            tail.SetNext(seg);
            tail = seg;
            running += chunks[i].Memory.Length;
        }

        // Trim the tail so that the sequence Length matches totalBodyLen.
        // running = sum of all chunk lengths (no offset deducted yet).
        // The sequence exposes bytes from head[firstFrameBodyOffset .. ]
        // through tail[.. endIndex]. We want:
        //     (running - firstFrameBodyOffset) - tailMemoryLength + endIndex == totalBodyLen
        // Solving: endIndex = totalBodyLen + firstFrameBodyOffset + tail.Memory.Length - running.
        var tailEndIndex = (int)(totalBodyLen + firstFrameBodyOffset + tail.Memory.Length - running);
        // Defense-in-depth: clamp into [0, tail.Memory.Length]. A
        // negative or out-of-range value indicates the pre-fetch
        // accumulated fewer / more bytes than declared; the caller
        // should have surfaced that as truncation in PrefetchAllChunksAsync.
        if (tailEndIndex < 0) tailEndIndex = 0;
        else if (tailEndIndex > tail.Memory.Length) tailEndIndex = tail.Memory.Length;

        return new ReadOnlySequence<byte>(
            startSegment: head, startIndex: firstFrameBodyOffset,
            endSegment: tail, endIndex: tailEndIndex);
    }

    /// <summary>
    /// Releases every chunk in <paramref name="chunks"/> back to its
    /// pool / ZC anchor. Idempotent — safe to call twice.
    /// </summary>
    public static void ReleaseAll(List<InboundFrame> chunks)
    {
        if (chunks == null) return;
        for (int i = 0; i < chunks.Count; i++)
        {
            chunks[i].ReturnToPool();
        }
        chunks.Clear();
    }

    /// <summary>
    /// Eligibility check: should this LPM take the eager pre-fetch
    /// path or fall back to <see cref="LazyChainRos"/>'s sync-pull
    /// with a Task.Yield hop?
    /// </summary>
    /// <remarks>
    /// <para>
    /// Eager pre-fetch is preferred whenever holding all chunks is
    /// "free" — i.e. the ring is already frozen for the LPM duration
    /// (chain-ZC) — and unpreferred whenever holding all chunks would
    /// balloon pool memory (non-ZC, > <see cref="ShmRing.ChainZcBudget"/>).
    /// </para>
    /// <para>
    /// We approximate the "chain-ZC engaged" condition by checking
    /// <c>lpmBodyLen + 5 &lt;= ChainZcBudget</c> on the stream's RX
    /// ring. False positives (LPMs that took the non-ZC path despite
    /// fitting the budget — e.g. wrap-around, busy ring, or the
    /// first-chunk minimum-size gate) at most cause one eager hold
    /// of pooled byte arrays bounded by <c>ChainZcBudget</c>, which
    /// is the same memory ceiling that chain-ZC itself enforces. No
    /// false negatives.
    /// </para>
    /// </remarks>
    public static bool ShouldEagerPrefetch(ShmGrpcStream stream, long lpmBodyLen)
    {
        // Total bytes the consumer will need to hold simultaneously
        // includes the 5-byte LPM header carried in the first chunk.
        var lpmTotal = lpmBodyLen + 5L;
        return lpmTotal <= stream.Connection.RxRing.ChainZcBudget;
    }

    /// <summary>
    /// If we are currently running on the SHM frame-reader Thread,
    /// hops to a ThreadPool worker via <see cref="Task.Yield"/>. Used
    /// before invoking a sync-pull <see cref="LazyChainRos"/> on the
    /// non-eager path so the sync pull's blocking wait runs on a
    /// pool thread rather than the producer Thread (which would
    /// deadlock — see class remarks).
    /// </summary>
    public static ValueTask HopOffReaderThreadIfNeededAsync()
    {
        if (ShmReaderThreadContext.IsOnReaderThread)
        {
            return YieldAsync();
        }
        return default;

        static async ValueTask YieldAsync()
        {
            await Task.Yield();
        }
    }

    /// <summary>
    /// Minimal <see cref="ReadOnlySequenceSegment{T}"/> subclass for
    /// the eager pre-fetch ROS chain. Wraps a chunk's
    /// <see cref="InboundFrame.Memory"/> directly — no
    /// <see cref="System.Buffers.MemoryManager{T}"/> hooks, no lazy
    /// trampoline.
    /// </summary>
    private sealed class InboundChainSegment : ReadOnlySequenceSegment<byte>
    {
        public InboundChainSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public void SetRunningIndex(long runningIndex) => RunningIndex = runningIndex;
        public void SetNext(InboundChainSegment next) => Next = next;
    }
}
