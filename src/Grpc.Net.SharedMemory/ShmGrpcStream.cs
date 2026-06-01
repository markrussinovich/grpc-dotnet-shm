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
using System.Buffers.Binary;
using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Core;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Lightweight struct carried through the inbound Channel to avoid LOH allocations.
/// The payload is backed by either a pooled buffer or a ring reservation and
/// must be released after consumption.
/// </summary>
public readonly struct InboundFrame
{
    public readonly FrameType Type;
    public readonly byte Flags;
    private readonly FramePayload _payload;

    public InboundFrame(FrameType type, FramePayload payload, byte flags = 0)
    {
        Type = type;
        Flags = flags;
        _payload = payload;
    }

    /// <summary>Exact-length view into the buffer.</summary>
    public ReadOnlyMemory<byte> Memory => _payload.Memory;

    public int Length => _payload.Length;

    /// <summary>True if this frame's payload is a ring-backed ZC view.</summary>
    public bool IsSpeculativeZeroCopy => _payload.IsSpeculativeZeroCopy;

    /// <summary>
    /// Round-7 PR-B object passthrough: when set, contains the already-
    /// decoded <see cref="HeadersV1"/> or <see cref="TrailersV1"/> for a
    /// HEADERS / TRAILERS frame. Consumers MUST prefer this over decoding
    /// <see cref="Memory"/> bytes whenever it is non-null for HEADERS or
    /// TRAILERS frame types, to avoid the redundant
    /// <c>HeadersV1.Encode</c> &#x2192; bytes &#x2192; <c>HeadersV1.Decode</c>
    /// round-trip. <see langword="null"/> for all other frame types and for
    /// HEADERS frames that took the byte-fallback path.
    /// </summary>
    public object? DecodedHeader => _payload.DecodedHeader;

    /// <summary>
    /// Round-7 PR-B: returns the <see cref="HeadersV1"/> for a HEADERS
    /// frame, preferring the pre-decoded object attached by the codec
    /// (zero-cost fast-path); falls back to decoding <see cref="Memory"/>
    /// bytes if no object is attached (byte fallback path).
    /// </summary>
    public HeadersV1 AsHeaders()
        => DecodedHeader as HeadersV1 ?? HeadersV1.Decode(Memory.Span);

    /// <summary>
    /// Round-7 PR-B: returns the <see cref="TrailersV1"/> for a TRAILERS
    /// frame, preferring the pre-decoded object attached by the codec
    /// (zero-cost fast-path); falls back to decoding <see cref="Memory"/>
    /// bytes if no object is attached (byte fallback path).
    /// </summary>
    public TrailersV1 AsTrailers()
        => DecodedHeader as TrailersV1 ?? TrailersV1.Decode(Memory.Span);

    /// <summary>Returns the buffer to the pool or commits the ring read.</summary>
    public void ReturnToPool()
    {
        _payload.Release();
    }

    /// <summary>Tuple deconstruction without copying payload data.</summary>
    public void Deconstruct(out FrameType type, out ReadOnlyMemory<byte> payload)
    {
        type = Type;
        payload = Memory;
    }
}

/// <summary>
/// Represents a single gRPC stream (call) over a shared memory connection.
/// Handles frame routing, flow control, and message sequencing for one RPC.
/// </summary>
public sealed class ShmGrpcStream : IDisposable, IAsyncDisposable
{
    private readonly ShmConnection _connection;
    private readonly Channel<InboundFrame> _inboundFrames;
    // Round-14 N1: per-stream decision made ONCE at construction. When
    // true, ShmConnection.ProcessFrame skips ReceiveStriper.Enqueue and
    // writes directly into _inboundFrames from the reader thread. Saves
    // one ThreadPool wake-hop per frame (~10-17 us x64, ~25-45 us ARM64)
    // for the single-stream Unary hot path while preserving per-stream
    // FIFO (the decision never changes mid-stream, so every frame for
    // this stream follows the same route).
    //
    // Inline continuations stay enabled in default (non-Fair) mode
    // even when bypassing \u2014 the reader thread runs the consumer's
    // awaiter continuation directly, saving an additional ThreadPool
    // hop. The existing FairMaxFramePayload == int.MaxValue guard on
    // inlineContinuations is sufficient to prevent the LazyChainRos
    // sync-pull self-deadlock (multi-frame messages only occur under
    // Fair caps, and only then can a sync-pull stall the reader
    // waiting on a chunk only the reader can deliver).
    internal readonly bool _bypassStriper;
    private readonly CancellationTokenSource _disposeCts;
    // Round-9 PR-I: lazy-allocate the call-cancellation CTS. The CLIENT-
    // side stream never reads CancellationToken (only the server-side
    // ShmServerCallContext ctor reads it once), so client RPCs paid for
    // an unused CTS per call. Lazy via interlocked CAS so concurrent
    // first-read + Cancel race deterministically: a Cancel that arrives
    // before the CTS is created sets _cancelRequested, and the lazy
    // creator returns a CTS that is *already* cancelled.
    private CancellationTokenSource? _cancellationCts;
    private int _cancelRequested; // 1 = Cancel happened (possibly pre-CTS)
    private readonly SemaphoreSlim _sendLock;

    private HeadersV1? _requestHeaders;
    private HeadersV1? _responseHeaders;
    private TrailersV1? _trailers;
    private string? _responseEncoding;
    private int _halfCloseSent; // 0=not sent, 1=sent; use Interlocked for thread safety
    // Round-10 BUG-FIX (Opus #7): _halfCloseReceived + _cancelled are
    // written on the stripe/reader thread (OnFrameReceived) and read
    // on user-call threads (via IsRemoteHalfClosed / IsCancelled
    // getters AND ReserveSendQuotaOrBlock's loop guard added by
    // round-10 FIX-1). Plain bool reads have no cross-thread barrier
    // and could observe a stale value for an unbounded time,
    // contradicting the wake-and-abort contract the Cancel path now
    // depends on. Use `volatile bool` so every read/write is a
    // releasing/acquiring access consistent with the other state
    // flags in this type (_disposed, _halfCloseSent both use
    // Volatile/Interlocked).
    private volatile bool _halfCloseReceived;
    private volatile bool _cancelled;
    private int _disposed;
    private volatile Exception? _sendFailure; // set by SendBodyAsync on failure

    // Wake-coalescing (unary only): when set, an inline batch was opened
    // in SendRequestHeadersAsync. SendHalfCloseAsync's inline path closes
    // it so Headers+Message+HalfClose share a single OS-level data-signal.
    // Caller (ShmControlHandler) sets this hint only for unary requests
    // (known content length) where HalfClose is guaranteed to follow
    // Message in microseconds. For streaming the hint is false and the
    // per-frame wakes proceed normally to avoid starving the response.
    private int _pendingInlineBatch; // 0=closed, 1=open

    // Client-unary headers staging (next-PR client-side coalesce path).
    // ShmControlHandler.SendOnStreamAsync calls StageRequestHeaders for
    // Unary content types instead of sending immediately. The staged
    // payload is encoded once and held in a pooled buffer; the body-write
    // path (ShmGrpcRequestStream.WriteSerializedMessageAsync) reads
    // HasStagedHeaders and, if the protobuf body fits in one wrap-safe
    // H2 DATA frame, writes Headers + DATA(END_STREAM) under one inline
    // batch -> 1 SignalData wake for the whole request. If the body is
    // too big or non-protobuf, FlushStagedHeadersAsync sends the staged
    // Headers via the existing queued path (today's behavior preserved).
    //
    // Round-7 PR-B: stores the HeadersV1 OBJECT directly (no upfront
    // Encode to bytes). WriteStagedHeadersInline HPACK-encodes from the
    // object via the new object-passthrough API; FlushStagedHeadersAsync
    // encodes on demand only for the (cold) queued fallback path.
    private HeadersV1? _stagedHeaders;
    private int _stagedHeadersConsumed; // 0=available, 1=already sent or aborted

    // Diagnostic counters for the wake-coalescing path. Static across
    // all streams so the bench can read them via reflection.
    private static long s_coalesceOpened;
    private static long s_coalesceClosed;
    internal static (long Opened, long Closed) GetCoalesceDiag()
        => (Volatile.Read(ref s_coalesceOpened), Volatile.Read(ref s_coalesceClosed));

    // Diagnostic counters (env-gated SHM_DIAG_HOPTIMING=1) for measuring
    // the client-side reader-thread → user-thread channel hop cost.
    // _hopPushTicks: set by reader thread just before/after Channel.TryWrite
    // _hopReceiveTicks: set by user thread just after Channel TryRead/wait returns
    // Diff aggregates "in-process hop" overhead — quantifies the upside of
    // a future inline-reader optimization that lets the user thread read
    // directly from RxRing, skipping the channel.
    private static readonly bool s_diagHopTiming =
        string.Equals(Environment.GetEnvironmentVariable("SHM_DIAG_HOPTIMING"),
            "1", StringComparison.Ordinal);
    private long _lastHopPushTicks;
    private static long s_hopTicksTotal;          // slow path: user awaited
    private static long s_hopCount;
    private static long s_hopFastTicksTotal;      // fast path: frame already queued
    private static long s_hopFastCount;
    internal static (long TicksTotal, long Count, long FastTicksTotal, long FastCount) GetHopDiag()
        => (Volatile.Read(ref s_hopTicksTotal), Volatile.Read(ref s_hopCount),
            Volatile.Read(ref s_hopFastTicksTotal), Volatile.Read(ref s_hopFastCount));

    // Env-gated SHM_CHANNEL_INLINE=1: lets the inbound Channel<InboundFrame>
    // run continuations synchronously on the reader thread (skipping
    // ThreadPool dispatch). Saves ~17µs/hop on Windows where the
    // ThreadPool worker wake dominates the channel hop cost. Default OFF
    // preserves the current threading model — opt in only after validating
    // your receive-side continuations are pure async and never block
    // synchronously (sync waits would stall the reader thread and
    // serialize concurrent streams). See repo/grpc-dotnet-shm-channel-hop-finding.
    //
    // CRITICAL incompatibility: when strict-fair forces a small
    // SHM_FAIR_MAX_FRAME (e.g. 16384), large messages get split across
    // multiple H2 DATA frames. LazyChainRos then activates and pulls each
    // chunk synchronously via ReceiveFrameSync → if sync continuations is
    // also on, the user-code MergeFrom runs on the reader thread, blocks
    // on the next chunk, and the reader thread can never read that next
    // chunk → deadlock. We disable sync continuations whenever the fair
    // frame cap is in effect so the multi-frame path stays correct.
    private static readonly bool s_channelInlineContinuations =
        string.Equals(Environment.GetEnvironmentVariable("SHM_CHANNEL_INLINE"),
            "1", StringComparison.Ordinal)
        && ShmConstants.FairMaxFramePayload == int.MaxValue;


    /// <summary>
    /// Records a send-side failure so that response readers can surface
    /// the real root cause instead of treating truncated responses as EOF.
    /// </summary>
    internal void SetSendFailure(Exception ex) => _sendFailure = ex;

    /// <summary>Gets the send-side failure, if any.</summary>
    internal Exception? SendFailure => _sendFailure;

    /// <summary>
    /// Gets the stream ID.
    /// </summary>
    public uint StreamId { get; }

    /// <summary>Gets the owning connection (for direct ring write).</summary>
    internal ShmConnection Connection => _connection;

    /// <summary>
    /// Gets whether this stream is from the client side.
    /// </summary>
    public bool IsClientStream => _connection.IsClient;

    /// <summary>
    /// Gets the request headers (available after sending for client, after receiving for server).
    /// </summary>
    public HeadersV1? RequestHeaders => _requestHeaders;

    /// <summary>
    /// Gets the response headers (available after receiving for client, after sending for server).
    /// </summary>
    public HeadersV1? ResponseHeaders => _responseHeaders;

    /// <summary>
    /// Gets the trailers (available after stream completes).
    /// </summary>
    public TrailersV1? Trailers => _trailers;

    /// <summary>
    /// Gets whether the remote side has half-closed (no more messages).
    /// </summary>
    public bool IsRemoteHalfClosed => _halfCloseReceived;

    /// <summary>
    /// Gets whether this side has half-closed (no more messages will be sent).
    /// </summary>
    public bool IsLocalHalfClosed => Volatile.Read(ref _halfCloseSent) != 0;

    /// <summary>Marks half-close as sent without actually sending it
    /// (used when HalfClose was written inline by the caller).</summary>
    internal void MarkHalfCloseSent() => Volatile.Write(ref _halfCloseSent, 1);

    /// <summary>
    /// Gets whether the stream was cancelled.
    /// </summary>
    public bool IsCancelled => _cancelled;

    /// <summary>
    /// Gets whether this is a server-initiated stream (i.e., server received request).
    /// </summary>
    public bool IsServerStream { get; }

    internal CancellationToken CancellationToken
    {
        get
        {
            // Round-9 PR-I lazy init. Common client-side case: never
            // read, never allocated. Server-side reads once at
            // ShmServerCallContext ctor.
            //
            // Round-10 BUG-FIX (Opus #5): guard against Dispose races.
            // (a) Pre-check _disposed so we don't allocate a CTS that
            //     Dispose has already missed (which would leak it).
            // (b) Wrap existing.Token in ODE-catch so a getter that
            //     races with the Dispose that disposed our published
            //     CTS returns a clean cancelled token instead of
            //     surfacing ObjectDisposedException to user code.
            // (c) Post-CAS re-check of _disposed: if Dispose ran
            //     between (a) and our CAS publish, atomically swap our
            //     CTS back out of the field and dispose it ourselves
            //     (Dispose may have already drained the field).
            if (Volatile.Read(ref _disposed) != 0)
            {
                return new CancellationToken(canceled: true);
            }

            var existing = _cancellationCts;
            if (existing != null)
            {
                try { return existing.Token; }
                catch (ObjectDisposedException)
                {
                    return new CancellationToken(canceled: true);
                }
            }

            var fresh = new CancellationTokenSource();
            // Pre-cancel if a Cancel raced ahead while we were
            // allocating but before we publish below.
            if (Volatile.Read(ref _cancelRequested) != 0)
            {
                fresh.Cancel();
            }
            var prev = Interlocked.CompareExchange(ref _cancellationCts, fresh, null);
            if (prev != null)
            {
                // Lost the publish race — another reader beat us. Throw
                // away our local CTS and use the winner. Cancel-flag
                // propagation already handled by the winning thread.
                fresh.Dispose();
                try { return prev.Token; }
                catch (ObjectDisposedException)
                {
                    return new CancellationToken(canceled: true);
                }
            }
            // Won. Round-10 BUG-FIX (Opus #5): if Dispose ran between
            // our pre-check and our CAS publish, the freshly-published
            // CTS would leak. Detect by re-reading _disposed; if set,
            // atomically swap our CTS back out. If we win the swap we
            // own dispose; if Dispose already drained the field, it
            // disposed the CTS already.
            if (Volatile.Read(ref _disposed) != 0)
            {
                if (Interlocked.CompareExchange(ref _cancellationCts, null, fresh) == fresh)
                {
                    fresh.Dispose();
                }
                return new CancellationToken(canceled: true);
            }
            // If Cancel raced between our flag pre-check and our
            // CAS publish, it would have observed _cancellationCts as
            // null and done nothing; close the gap by re-checking the
            // flag after publish.
            if (Volatile.Read(ref _cancelRequested) != 0 && !fresh.IsCancellationRequested)
            {
                try { fresh.Cancel(); } catch (ObjectDisposedException) { }
            }
            return fresh.Token;
        }
    }

    internal ShmGrpcStream(uint streamId, ShmConnection connection, bool isServerStream = false)
    {
        StreamId = streamId;
        _connection = connection;
        IsServerStream = isServerStream;
        // Round-14 N1: decide ONCE here whether this stream will bypass
        // the ReceiveStriper. We bypass only when (a) the striper is
        // enabled at all (otherwise the question is moot — DATA already
        // goes direct), and (b) at the moment THIS stream was created
        // there were no other active streams on the connection. The
        // snapshot is intentionally taken before this stream is
        // _streams.TryAdd'd (server) or before the increment is
        // observable (client side is increment-then-construct, so
        // ActiveStreamCount already includes this stream — hence the
        // \u201c<= 1\u201d check, not \u201c== 0\u201d).
        //
        // The decision is locked in for the lifetime of the stream so
        // every frame of this stream takes the same route, preserving
        // per-stream FIFO. A second stream starting after us will get
        // its own independent _bypassStriper decision (likely false at
        // ActiveStreamCount=2) and route through the striper — that's
        // fine, the two streams don't interfere because each has its
        // own _inboundFrames Channel<T>.
        _bypassStriper = connection.UseReceiveStriper
            && connection.ActiveStreamCount <= 1;
        // Inline continuations: enables `AllowSynchronousContinuations=true`
        // on the per-stream inbound channel. When enabled, the user's
        // awaiter continuation runs synchronously on whatever Thread
        // produced the frame, avoiding a ~17 us ThreadPool hop per
        // received frame on Windows.
        //
        // HARD CORRECTNESS INVARIANT: an inline continuation MUST NOT
        // run on the SHM frame-reader Thread. If it does, the user's
        // awaiter can issue a follow-up flow-controlled blocking send
        // (`ReserveSendQuotaOrBlock` parking on `_sendQuotaWake`) that
        // parks the reader Thread itself — at which point no inbound
        // WINDOW_UPDATE can be processed and the call deadlocks.
        // Repro: max-profile 32+ MiB unary, where the LPM exceeds the
        // 32 MiB initial window and the test loop's continuation
        // issues the NEXT warmup inline from the previous Trailers'
        // completion. Bug masked in earlier rounds because tiny diag
        // perturbations rescheduled the continuation off the reader
        // Thread.
        //
        // The reader Thread delivers frames inline iff there is NO
        // stripe Thread between the reader and the inbound channel
        // writer — i.e. `_bypassStriper == true` (the Round-14 N1
        // single-stream Unary fast path) OR `!connection.UseReceiveStriper`.
        // Only the *contrapositive* — `UseReceiveStriper && !_bypassStriper`
        // — has a STRIPE Thread (not the reader Thread) as the channel
        // writer, and only THAT configuration is safe for inline
        // continuations.
        //
        // (a) Default striper path: safe iff `!_bypassStriper`. Saves
        //     ~17 us/hop on the 1000×64B Windows cell.
        // (b) Per-connection opt-in `ShmConnection.InlineReceiveContinuations`:
        //     callers MAY force inline even on the unsafe reader-Thread
        //     delivery path IF AND ONLY IF they guarantee their
        //     awaiter continuations will NOT issue a large
        //     flow-controlled send (else the deadlock above is
        //     unavoidable). Treated as a caller-owned footgun.
        // (c) Legacy env var `SHM_CHANNEL_INLINE=1`: same caller-owned
        //     footgun semantics as (b), process-wide.
        // Safety guard: when the strict-fair frame cap is in effect,
        // multi-frame messages activate LazyChainRos's sync-pull path
        // which would self-deadlock if the same Thread is doing both
        // chunk delivery and chunk consumption. Disable inline
        // continuations in that case regardless of opt-in.
        //
        // The same deadlock previously applied in MAX mode whenever
        // multi-frame chain-ZC was reachable, because chain-ZC's
        // per-chunk More-flagged surface drove the same LazyChainRos
        // sync-pull from the consumer. The 2026-06-01 hybrid eager
        // pre-fetch refactor (see <c>InboundChainHelper</c>) replaces
        // the sync-pull <c>LazyChainRos</c> activation in chain-ZC
        // streams with an async pre-fetch loop that unwinds the reader
        // Thread between chunks; the > <c>ChainZcBudget</c> non-ZC
        // path keeps <c>LazyChainRos</c> but hops off the reader
        // Thread first via <see cref="ShmReaderThreadContext"/>. With
        // those fixes inline continuations are safe on chain-ZC
        // streams; only the strict-fair frame cap remains as a
        // structural inline-cont blocker.
        var inlineContinuations = (
                (connection.UseReceiveStriper && !_bypassStriper)
                || s_channelInlineContinuations
                || connection.InlineReceiveContinuations)
            && ShmConstants.FairMaxFramePayload == int.MaxValue;
        _inboundFrames = Channel.CreateUnbounded<InboundFrame>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = inlineContinuations
        });
        _disposeCts = new CancellationTokenSource();
        // _cancellationCts stays null until first CancellationToken read
        // (lazy init via interlocked CAS — see CancellationToken getter
        // for the race-free flag-then-publish protocol).
        _sendLock = new SemaphoreSlim(1, 1);
        // HTTP/2 per-stream send quota: bytes the peer has granted us via
        // SETTINGS_INITIAL_WINDOW_SIZE + accumulated WINDOW_UPDATE. Decremented
        // by ReserveSendQuota on each outbound DATA chunk; refunded on rollback;
        // grown by AddSendQuota on incoming WU. Sender consults via CAS;
        // if insufficient, sender blocks on _sendQuotaWake until WU arrives.
        // Initial value is the H2-spec SETTINGS_INITIAL_WINDOW_SIZE — at SHM
        // it defaults to ShmConstants.InitialWindowSize (32 MiB).
        Volatile.Write(ref _sendQuota, ShmConstants.InitialWindowSize);
        _sendQuotaWake = new ManualResetEventSlim(initialState: false);

        // HTTP/2 per-stream receive flow control. Tracks inbound DATA against
        // the limit we advertised, accumulates pendingUpdate for limit/4 drip,
        // and supports SHM-specific stream-level pre-credit at LPM-header
        // parse via MaybeAdjustAdditive (gRFC SHM v3.4+ MUST). Caller emits
        // the returned WU through ShmConnection.SendStreamWindowUpdate.
        InFlow = new Synchronization.InFlow(initialLimit: (uint)ShmConstants.InitialWindowSize);
    }

    // ============================================================
    // HTTP/2 send-side flow control (per-stream send window)
    // ============================================================

    /// <summary>
    /// Per-stream send-window quota (bytes the peer has granted us).
    /// Volatile read via <see cref="Interlocked.CompareExchange(ref long, long, long)"/>
    /// for the CAS reserve path. Refilled by <see cref="AddSendQuota"/>
    /// on inbound WINDOW_UPDATE; drained by <see cref="TryReserveSendQuota"/>
    /// on outbound DATA chunks; refunded by <see cref="RefundSendQuota"/>
    /// when the sender rolls back a reservation it could not commit (e.g.
    /// ring-write cancellation).
    /// </summary>
    private long _sendQuota;

    /// <summary>
    /// Wake signal for senders blocked on insufficient send quota.
    /// <see cref="AddSendQuota"/> sets this when quota grows. Senders
    /// (or the writer task) reset it before re-checking quota to avoid
    /// missed wakes (see writer-task loop in <see cref="ShmFrameWriter"/>).
    /// </summary>
    private readonly ManualResetEventSlim _sendQuotaWake;

    /// <summary>
    /// Per-stream inbound flow-control state. <see cref="ShmConnection"/>
    /// invokes <see cref="Synchronization.InFlow.MaybeAdjustAdditive"/>
    /// on LPM-header parse, <see cref="Synchronization.InFlow.OnData"/>
    /// on each inbound DATA frame, and <see cref="Synchronization.InFlow.OnRead"/>
    /// when the application consumes a message.
    /// </summary>
    internal Synchronization.InFlow InFlow { get; }

    /// <summary>
    /// Pending stream-level WINDOW_UPDATE credit awaiting emission.
    /// Mirrors grpc-go-shmem's <c>s.pendingWU</c> in the lockless
    /// WU emission path. Producers (the reader Thread's
    /// <c>OnData</c> drip and the codec's <c>OnMessageStart</c>
    /// pre-credit) atomically <c>Add</c> their delta; the same
    /// producer (or one racing later) reads the threshold gate and
    /// <c>Exchange(0)</c>s the accumulator to claim the full sum
    /// for a single coalesced frame. Eliminates the per-drip
    /// frame-writer Enqueue cost on apples-to-apples 65535 windows
    /// where the previous per-call emit triggered O(messages /
    /// drip-threshold) frame-writer hops per stream.
    /// </summary>
    internal long PendingWU;

    /// <summary>
    /// Snapshot of the current send-window quota (bytes peer has granted).
    /// Lock-free; may be slightly stale relative to a concurrent
    /// <see cref="TryReserveSendQuota"/> / <see cref="AddSendQuota"/> call.
    /// </summary>
    internal long SendQuota => Volatile.Read(ref _sendQuota);

    /// <summary>
    /// Cheap best-effort predicate: would a send of <paramref name="bytes"/>
    /// be unable to reserve quota right now on either the per-stream or
    /// per-connection window? Two <see cref="Volatile.Read"/> calls; no CAS.
    /// </summary>
    /// <remarks>
    /// Used by <see cref="SendMessageAsync(System.ReadOnlyMemory{byte}, System.Threading.CancellationToken)"/>
    /// together with <see cref="ShmReaderThreadContext.IsOnReaderThread"/>
    /// to decide whether the outbound write must hop off the SHM frame-reader
    /// thread BEFORE descending into <see cref="ReserveSendQuotaOrBlock"/> —
    /// otherwise a flow-controlled send issued from an inline-receive
    /// continuation deadlocks the connection. The check is intentionally
    /// approximate: a stale "false" simply means we did NOT hop and the
    /// blocking wait's <see cref="System.Diagnostics.Debug.Assert"/>
    /// tripwire would catch the slip in DEBUG builds. A stale "true"
    /// costs at most one <see cref="System.Threading.Tasks.Task.Yield"/>
    /// hop for a write that turned out not to need it.
    /// </remarks>
    internal bool WouldBlockSendQuota(int bytes)
    {
        if (bytes <= 0) return false;
        if (Volatile.Read(ref _sendQuota) < bytes) return true;
        if (_connection.ConnSendQuota < bytes) return true;
        return false;
    }

    /// <summary>
    /// Attempts to reserve <paramref name="n"/> bytes of send-window quota
    /// for an outbound DATA chunk. Returns <see langword="true"/> with the
    /// quota debited if the reservation succeeds; returns <see langword="false"/>
    /// (and leaves quota unchanged) if the current window is insufficient.
    /// </summary>
    /// <remarks>
    /// Lock-free CAS loop; aborts (returns false) on insufficient quota
    /// rather than spinning. Senders that need to block should call this,
    /// observe false, then await <see cref="_sendQuotaWake"/> before
    /// retrying. Negative <paramref name="n"/> is rejected to prevent the
    /// quota from being inflated past <see cref="int.MaxValue"/> via
    /// signed overflow.
    /// </remarks>
    internal bool TryReserveSendQuota(int n)
    {
        if (n <= 0) return n == 0; // 0 trivially succeeds; negatives are bugs
        while (true)
        {
            var current = Volatile.Read(ref _sendQuota);
            if (current < n) return false;
            var desired = current - n;
            if (Interlocked.CompareExchange(ref _sendQuota, desired, current) == current)
            {
                return true;
            }
            // CAS contention: another thread mutated quota; retry.
        }
    }

    /// <summary>
    /// Returns <paramref name="n"/> bytes of previously-reserved quota
    /// to the send window (called on rollback when a write fails after
    /// quota was debited). Caps at <see cref="Synchronization.InFlow.MaxWindowSize"/>
    /// (HTTP/2 31-bit ceiling) to defend against the race where a
    /// concurrent <c>AddSendQuota</c> already raised the window near the
    /// cap: without the cap, the refund could push our local view above
    /// the peer's advertised window, eventually tripping
    /// FLOW_CONTROL_ERROR on legitimate traffic.
    /// </summary>
    internal void RefundSendQuota(int n)
    {
        if (n <= 0) return;
        while (true)
        {
            var current = Volatile.Read(ref _sendQuota);
            var desired = current + n;
            if (desired > Synchronization.InFlow.MaxWindowSize)
            {
                desired = Synchronization.InFlow.MaxWindowSize;
            }
            if (Interlocked.CompareExchange(ref _sendQuota, desired, current) == current)
            {
                _sendQuotaWake.Set();
                return;
            }
        }
    }

    /// <summary>
    /// Round-10 DEFER-1 fix: attempts to reserve <paramref name="n"/> bytes
    /// from BOTH the per-stream window AND the connection-level window
    /// atomically. Returns <see langword="true"/> with both debits
    /// committed; <see langword="false"/> (both quotas unchanged) on
    /// insufficient credit in either window. Mirrors grpc-go-shmem's
    /// two-resource CAS pattern (shm_client_transport.go ~L343):
    /// reserves stream first, then conn; rolls back the stream debit if
    /// the conn CAS loses to ensure callers see all-or-nothing semantics.
    /// Per RFC 7540/9113 §6.9.1 every outbound DATA frame MUST observe
    /// both windows.
    /// </summary>
    internal bool TryReserveSendQuotaWithConn(int n)
    {
        if (n <= 0) return n == 0;
        // Probe stream first (fail-fast without CAS) — conn quota check
        // moved inside TryReserveConnSendQuota where the fast-path skip
        // lives, so a redundant probe here would just add a wasted
        // Volatile.Read in the dominant SHM-SHM hot path.
        if (Volatile.Read(ref _sendQuota) < n) return false;
        // Reserve stream first.
        if (!TryReserveSendQuota(n)) return false;
        // Reserve conn (fast-path skip when conn quota effectively
        // unbounded; see ShmConnection.TryReserveConnSendQuota for the
        // threshold rationale). Rolls back the stream debit on conn race.
        if (!_connection.TryReserveConnSendQuota(n))
        {
            RefundSendQuota(n);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Round-10 DEFER-1 fix: refunds <paramref name="n"/> bytes to BOTH
    /// the per-stream window and the connection window. Used by every
    /// DATA write site's catch/rollback path, mirroring the
    /// <see cref="TryReserveSendQuotaWithConn"/> two-resource reservation.
    /// </summary>
    internal void RefundSendQuotaWithConn(int n)
    {
        if (n <= 0) return;
        RefundSendQuota(n);
        _connection.RefundConnSendQuota(n);
    }

    /// <summary>
    /// Adds <paramref name="delta"/> bytes to the send window in response
    /// to an incoming <c>WINDOW_UPDATE</c> frame from the peer. Caps at
    /// <see cref="Synchronization.InFlow.MaxWindowSize"/> (HTTP/2 31-bit
    /// ceiling); ignores zero/negative deltas (peer protocol violation
    /// per RFC 7540 §6.9.1 — caller is expected to RST_STREAM at the
    /// codec layer before reaching here).
    /// </summary>
    internal void AddSendQuota(int delta)
    {
        if (delta <= 0) return;
        while (true)
        {
            var current = Volatile.Read(ref _sendQuota);
            var desired = current + delta;
            if (desired > Synchronization.InFlow.MaxWindowSize)
            {
                desired = Synchronization.InFlow.MaxWindowSize;
            }
            if (Interlocked.CompareExchange(ref _sendQuota, desired, current) == current)
            {
                _sendQuotaWake.Set();
                return;
            }
        }
    }

    /// <summary>
    /// Wake handle for senders blocked on insufficient quota; senders
    /// reset before re-checking <see cref="TryReserveSendQuota"/> to
    /// avoid missed wakes.
    /// </summary>
    internal ManualResetEventSlim SendQuotaWake => _sendQuotaWake;

    /// <summary>
    /// Reserves <paramref name="n"/> bytes of per-stream send quota,
    /// blocking until the quota is available. Honors
    /// <paramref name="cancellationToken"/> (throws
    /// <see cref="OperationCanceledException"/>) and the stream's
    /// dispose token (throws <see cref="ObjectDisposedException"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pattern follows the standard missed-wake-safe protocol:
    /// </para>
    /// <list type="number">
    ///   <item><description>Fast path: <c>TryReserveSendQuota(n)</c> — if succeeds, return.</description></item>
    ///   <item><description><c>Reset</c> the wake handle BEFORE re-checking quota.</description></item>
    ///   <item><description>Re-check <c>TryReserveSendQuota</c> after reset; if succeeds (covers the race where WU landed between fast-path-fail and Reset), return.</description></item>
    ///   <item><description>Invoke <paramref name="drainBeforeWait"/> to flush any pending Ping/Pong control traffic owed by this writer (avoids keepalive starvation while we block).</description></item>
    ///   <item><description><c>Wait(ct)</c>. Sticky-set semantics: if WU arrives any time after our Reset (before or during Wait), Wait returns immediately.</description></item>
    ///   <item><description>Loop. Re-Reset, re-check, re-wait until success or cancellation.</description></item>
    /// </list>
    /// <para>
    /// <b>Phase A (this PR):</b> caller (typically <c>FrameProtocol.WriteMessage</c>)
    /// blocks inline. This is correct for ALL paths but stalls the writer task
    /// when called from its loop — addressed in Phase B by relocating multi-frame
    /// chunking into the writer task with a deferred-message map.
    /// </para>
    /// </remarks>
    internal void ReserveSendQuotaOrBlock(int n, Action? drainBeforeWait, CancellationToken cancellationToken)
    {
        if (n <= 0) return;
        // Fast path: both quotas readily available (round-10 DEFER-1
        // upgraded this to reserve both stream + conn atomically).
        if (TryReserveSendQuotaWithConn(n)) return;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            // BUG-FIX (round-10 GPT-5.5 #2): check _cancelled so a
            // remote Cancel/RST during our quota wait aborts the
            // send instead of waiting indefinitely for the caller's
            // own token to fire.
            if (_cancelled)
            {
                throw new OperationCanceledException("Stream cancelled by peer (CANCEL frame received).");
            }
            // Reset BEFORE recheck to ensure we observe any quota added
            // before our Wait starts; sticky semantics of MRESlim mean
            // a Set between Reset and Wait still wakes us. _sendQuotaWake
            // wakes on per-stream WU AND on conn-level WU (DEFER-1:
            // AddSendQuota(streamId=0) explicitly wakes every active
            // stream's MRES so writers parked here re-probe both windows).
            _sendQuotaWake.Reset();
            if (TryReserveSendQuotaWithConn(n)) return;
            // Re-check disposal AFTER Reset to close the missed-wake
            // race where Dispose() ran between our earlier
            // ThrowIfDisposed() and our Reset() — Dispose's wake-Set
            // would have been cleared by Reset, and the subsequent
            // Wait(ct=None) would block forever because no future
            // AddSendQuota arrives for a disposed/removed stream.
            // Cancellation is re-checked symmetrically.
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            // Same re-check for remote-peer cancel as above; covers
            // the race where Cancel arrived between our top-of-loop
            // _cancelled read and our Reset() (Reset would have
            // cleared the Cancel-path's wake-Set, dooming us to a
            // Wait that never wakes).
            if (_cancelled)
            {
                throw new OperationCanceledException("Stream cancelled by peer (CANCEL frame received).");
            }
            // Flush pending control frames (Ping/Pong keepalive) so
            // they are not stranded behind a blocked DATA write while
            // we wait for the peer to grant more quota.
            drainBeforeWait?.Invoke();
            // TRIPWIRE: blocking on the SHM frame-reader thread parks the
            // very thread that processes peer WINDOW_UPDATEs → guaranteed
            // deadlock. Callers entering the slow path from an inline
            // receive continuation MUST first hop off via the
            // WouldBlockSendQuota pre-flight in SendMessageAsync (see
            // ShmReaderThreadContext). DEBUG-only assert keeps the
            // release-build cost zero while surfacing missed call sites
            // during test runs.
            System.Diagnostics.Debug.Assert(
                !ShmReaderThreadContext.IsOnReaderThread,
                "ReserveSendQuotaOrBlock would block on the SHM reader thread — " +
                "an outbound send path is missing a WouldBlockSendQuota pre-flight hop. " +
                "See ShmReaderThreadContext for the deadlock invariant.");
            _sendQuotaWake.Wait(cancellationToken);
        }
    }

    /// <summary>
    /// Sets the request headers from incoming HEADERS frame (server-side).
    /// </summary>
    internal void SetRequestHeaders(HeadersV1 headers)
    {
        _requestHeaders = headers;
    }

    /// <summary>
    /// Sends request headers (client-side, must be called first).
    /// </summary>
    public Task SendRequestHeadersAsync(string method, string authority, Metadata? metadata = null, DateTime? deadline = null, bool coalesceWithHalfClose = false)
    {
        ThrowIfDisposed();
        if (!IsClientStream)
            throw new InvalidOperationException("Only client can send request headers");
        if (_requestHeaders != null)
            throw new InvalidOperationException("Request headers already sent");

        _requestHeaders = new HeadersV1
        {
            Version = 1,
            HeaderType = 0, // client-initial
            Method = method,
            Authority = authority,
            DeadlineUnixNano = deadline.HasValue
                ? (ulong)new DateTimeOffset(deadline.Value).ToUnixTimeMilliseconds() * 1_000_000
                : 0,
            Metadata = ConvertMetadata(metadata)
        };

        // Single-stream-mode inline-write fast path. When the connection
        // negotiated single-stream mode and only one stream is active,
        // bypass the WriterLoop queue and write Headers directly to the
        // ring under TryPauseWriterLoop.
        //
        // This is critical for correctness, not just perf: client unary
        // sends Headers, then (fire-and-forget) writes the body Message
        // via WriteInlineDirectMultiFrame which is also a TryPauseWriterLoop
        // inline write. If Headers went through the queue while Message
        // went inline, the two write paths race against each other on
        // the SPSC ring and produce a "Headers not delivered before
        // Message" failure mode (~1/15 stress runs on Intel Linux).
        // Routing Headers through the same TryPause path serialises the
        // sends through `_inlineWriterActive` CAS; both writes go to the
        // ring in caller-thread order, no race.
        //
        // Round-7 PR-B: inline path now uses WriteInlineHeadersFrame which
        // takes a HeadersV1 OBJECT and HPACK-encodes it directly,
        // skipping the HeadersV1.Encode → bytes → DecodeHeadersV1
        // round-trip the byte path requires.
        //
        // Falls back to the queued path when:
        //   * not in single-stream mode (multi-stream pipelining wants
        //     Headers in the WriterLoop's batch), or
        //   * TryPauseWriterLoop fails (WriterLoop busy or another inline
        //     writer holds the CAS); the queued path is correct (single
        //     writer = WriterLoop) and Just Slower.
        if (_connection.SingleStreamMode && _connection.ActiveStreamCount <= 1)
        {
            var writer = _connection.FrameWriter;
            if (writer != null && writer.TryPauseWriterLoop())
            {
                try
                {
                    if (coalesceWithHalfClose)
                    {
                        // Wake-coalescing (unary): suppress the per-frame
                        // SignalData for Headers; SendHalfCloseAsync will
                        // close the batch and fire a single SignalData
                        // covering Headers+Message+HalfClose. Only safe
                        // when caller guarantees HalfClose follows shortly
                        // (i.e., unary request with known body length).
                        writer.BeginInlineBatch();
                        Interlocked.Increment(ref s_coalesceOpened);
                        var batchOpened = true;
                        try
                        {
                            writer.WriteInlineHeadersFrame(StreamId, _requestHeaders, default);
                            Volatile.Write(ref _pendingInlineBatch, 1);
                            batchOpened = false; // ownership transferred to HalfClose / Dispose
                        }
                        finally
                        {
                            if (batchOpened) writer.EndInlineBatch();
                        }
                    }
                    else
                    {
                        writer.WriteInlineHeadersFrame(StreamId, _requestHeaders, default);
                    }
                    return Task.CompletedTask;
                }
                finally
                {
                    writer.ResumeWriterLoop();
                }
            }
        }

        // Round-9 PR-F: queued fallback uses the object passthrough path
        // (was Encode → bytes → SendFrameAsync → DecodeHeadersV1 round-
        // trip). PR-B closed this on the inline single-stream branch;
        // PR-D closed it for SendResponseHeadersAsync / SendTrailersAsync;
        // PR-F now covers the client request HEADERS queued fallback so
        // every Headers send path uses the new API.
        return SendHeadersFrameAsync(HeadersFlags.Initial, _requestHeaders);
    }

    /// <summary>
    /// Encodes Unary request headers and stages them in a pooled buffer
    /// WITHOUT writing to the ring. Used by <c>ShmControlHandler.SendOnStreamAsync</c>
    /// when the request content is a known-Unary <c>PushUnaryContent</c>:
    /// the body-write path (<c>ShmGrpcRequestStream.WriteSerializedMessageAsync</c>)
    /// can then coalesce HEADERS + DATA(END_STREAM) into ONE inline batch
    /// (single peer SignalData wake) if the protobuf body fits in one
    /// wrap-safe H2 DATA frame.
    /// </summary>
    /// <remarks>
    /// MUST be followed by either <see cref="FlushStagedHeadersAsync"/>
    /// (fall-back: sends Headers separately) OR an inline coalesced send
    /// via <see cref="WriteStagedHeadersInline"/>. <c>Dispose</c>
    /// defensively returns any unflushed staged buffer to the pool to
    /// avoid a leak if the caller fails to do either.
    /// </remarks>
    internal void StageRequestHeaders(string method, string authority, Metadata? metadata = null, DateTime? deadline = null)
    {
        ThrowIfDisposed();
        if (!IsClientStream)
            throw new InvalidOperationException("Only client can stage request headers");
        if (_requestHeaders != null)
            throw new InvalidOperationException("Request headers already sent or staged");

        _requestHeaders = new HeadersV1
        {
            Version = 1,
            HeaderType = 0, // client-initial
            Method = method,
            Authority = authority,
            DeadlineUnixNano = deadline.HasValue
                ? (ulong)new DateTimeOffset(deadline.Value).ToUnixTimeMilliseconds() * 1_000_000
                : 0,
            Metadata = ConvertMetadata(metadata)
        };

        // Round-7 PR-B: store the HeadersV1 object directly — the inline
        // flush path (WriteStagedHeadersInline) HPACK-encodes from the
        // object without a HeadersV1.Encode round-trip. Encoding to bytes
        // happens lazily only if FlushStagedHeadersAsync (cold queued
        // fallback) is invoked instead.
        _stagedHeaders = _requestHeaders;
        Volatile.Write(ref _stagedHeadersConsumed, 0);
    }

    /// <summary>
    /// True iff <see cref="StageRequestHeaders"/> was called and the
    /// staged Headers have not yet been written or aborted.
    /// </summary>
    internal bool HasStagedHeaders =>
        Volatile.Read(ref _stagedHeadersConsumed) == 0
        && _stagedHeaders != null;

    /// <summary>
    /// Writes the staged Headers frame inline via <paramref name="writer"/>'s
    /// direct ring-write path. Caller MUST already hold
    /// <c>writer.TryPauseWriterLoop</c>. No-op if Headers were already
    /// consumed (idempotent under CAS race with
    /// <see cref="FlushStagedHeadersAsync"/>). Round-7 PR-B: uses the
    /// object-passthrough API — no buffer to manage.
    /// </summary>
    internal void WriteStagedHeadersInline(ShmFrameWriter writer)
    {
        if (Interlocked.Exchange(ref _stagedHeadersConsumed, 1) == 1)
        {
            return; // already consumed
        }
        var headers = _stagedHeaders;
        _stagedHeaders = null;
        if (headers == null) return;
        writer.WriteInlineHeadersFrame(StreamId, headers, default);
    }

    /// <summary>
    /// Fall-back: sends the staged Headers via the standard queued send
    /// path (separate SignalData wake). Used by <c>WriteSerializedMessageAsync</c>
    /// when the body is too big or non-coalesce-eligible, AND by
    /// <c>ShmControlHandler.SendOnStreamAsync</c>'s post-body catch-all
    /// for the no-body Unary case. Idempotent.
    /// </summary>
    internal Task FlushStagedHeadersAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stagedHeadersConsumed, 1) == 1)
        {
            return Task.CompletedTask; // already consumed
        }
        var headers = _stagedHeaders;
        _stagedHeaders = null;
        if (headers == null) return Task.CompletedTask;

        // Round-9 PR-F: cold queued fall-back now uses the object
        // passthrough path too — no Encode/Decode round-trip even on
        // this rare path. PR-B closed the WriteStagedHeadersInline
        // fast path; PR-F closes the symmetric async fallback.
        return SendHeadersFrameAsync(HeadersFlags.Initial, headers, cancellationToken);
    }

    /// <summary>
    /// Marks HalfClose as already sent (used by the client-coalesce
    /// path that emits DATA with H2 END_STREAM flag — there is no
    /// separate HalfClose frame to send). Subsequent calls to
    /// <see cref="SendHalfCloseAsync"/> become no-ops via the existing
    /// <c>_halfCloseSent</c> CAS gate.
    /// </summary>
    internal void MarkHalfClosed()
    {
        Volatile.Write(ref _halfCloseSent, 1);
    }

    /// <summary>Sets the grpc-encoding for response compression.
    /// Will be automatically included in response headers.</summary>
    internal void SetResponseEncoding(string encoding)
    {
        _responseEncoding = encoding;
    }

    private Metadata? InjectResponseEncoding(Metadata? metadata)
    {
        if (_responseEncoding == null) return metadata;
        metadata ??= new Metadata();
        metadata.Add("grpc-encoding", _responseEncoding);
        return metadata;
    }

    /// <summary>
    /// Sends response headers (server-side, before first message).
    /// </summary>
    public async Task SendResponseHeadersAsync(Metadata? metadata = null)
    {
        ThrowIfDisposed();
        if (IsClientStream)
            throw new InvalidOperationException("Only server can send response headers");
        if (_responseHeaders != null)
            throw new InvalidOperationException("Response headers already sent");

        metadata = InjectResponseEncoding(metadata);

        _responseHeaders = new HeadersV1
        {
            Version = 1,
            HeaderType = 1, // server-initial
            Metadata = ConvertMetadata(metadata)
        };

        // Round-8 PR-D: queued/async fallback uses the object passthrough
        // path (was Encode → bytes → SendFrameAsync → DecodeHeadersV1
        // round-trip). Companion to PR-B's inline single-stream fix —
        // multi-stream / N>1 RPCs now also benefit.
        await SendHeadersFrameAsync(HeadersFlags.Initial, _responseHeaders).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes response headers directly to the ring via the given writer.
    /// Used by singleStreamMode inline write paths (TryPause/ExecuteInline).
    /// </summary>
    internal void SendResponseHeadersInline(ShmFrameWriter writer, Metadata? metadata = null)
    {
        if (_responseHeaders != null) return;
        metadata = InjectResponseEncoding(metadata);
        _responseHeaders = new HeadersV1 { Version = 1, HeaderType = 1, Metadata = ConvertMetadata(metadata) };
        // Round-7 PR-B: object-passthrough inline write (no upfront
        // HeadersV1.Encode → bytes → DecodeHeadersV1 round-trip).
        writer.WriteInlineHeadersFrame(StreamId, _responseHeaders, default);
    }

    private bool HasSentInitialHeaders()
    {
        return IsClientStream ? _requestHeaders != null : _responseHeaders != null;
    }

    private void ThrowIfCannotSendMessage()
    {
        if (_cancelled)
            throw new InvalidOperationException("Cannot send after cancel");
        if (!HasSentInitialHeaders())
            throw new InvalidOperationException("Cannot send message before headers");
        if (Volatile.Read(ref _halfCloseSent) != 0)
            throw new InvalidOperationException("Cannot send after half-close");
    }

    private void ThrowIfCannotSendTrailers()
    {
        if (_cancelled)
            throw new InvalidOperationException("Cannot send after cancel");
        if (_trailers != null)
            throw new InvalidOperationException("Cannot send trailers after trailers");
    }

    /// <summary>
    /// Sends a message payload as raw bytes — does NOT wrap the gRPC
    /// 5-byte LPM (length-prefixed-message) header. Use this overload
    /// when the caller already owns the framed wire form (e.g. relaying
    /// a pre-framed payload). For typical gRPC use cases prefer the
    /// <see cref="SendMessageAsync(Google.Protobuf.IMessage, CancellationToken)"/>
    /// overload, which is zero-allocation by construction.
    /// </summary>
    public Task SendMessageAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfCannotSendMessage();

        // SAFE-INLINE-RECEIVE DEADLOCK GUARD: if a user inline-receive
        // continuation is calling us on the SHM frame-reader thread AND
        // the upcoming send would block on per-stream or connection
        // send-quota, hop to the ThreadPool before descending — otherwise
        // ReserveSendQuotaOrBlock parks the reader thread on
        // _sendQuotaWake and the peer's WINDOW_UPDATE can never be
        // processed (no thread left to read it).  See
        // ShmReaderThreadContext for the full invariant. The hop is paid
        // ONLY for inline-RX writes that would block — common-case fast
        // path (quota available) stays sync.
        if (ShmReaderThreadContext.IsOnReaderThread
            && WouldBlockSendQuota(data.Length))
        {
            return SendMessageWithReaderThreadHopAsync(data, cancellationToken);
        }

        // No per-stream flow control: the ring's WaitForSpace provides
        // back-pressure via the SPSC ring buffer.
        var ct = cancellationToken.CanBeCanceled ? cancellationToken : _disposeCts.Token;

        if (data.Length >= 65536 && Volatile.Read(ref _disposed) == 0)
        {
#pragma warning disable CA2016
            if (_sendLock.Wait(0))
#pragma warning restore CA2016
            {
                try
                {
                    _connection.SendFrameZeroCopyAndWait(FrameType.Message, StreamId, 0, data, ct);
                }
                finally
                {
                    _sendLock.Release();
                }
                return Task.CompletedTask;
            }
            // Lock contended — fall through to normal SendFrameAsync
        }
        return SendFrameAsync(FrameType.Message, 0, data, ct);
    }

    /// <summary>
    /// Sends a message with an implicit half-close in one frame (EndStream flag).
    /// Eliminates the separate HalfClose frame, reducing a unary RPC from 3 to 2
    /// client-side frames and saving one ring write + signal round-trip.
    /// </summary>
    public Task SendMessageAndHalfCloseAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfCannotSendMessage();

        // SAFE-INLINE-RECEIVE DEADLOCK GUARD: same invariant as
        // SendMessageAsync — see ShmReaderThreadContext for details.
        if (ShmReaderThreadContext.IsOnReaderThread
            && WouldBlockSendQuota(data.Length))
        {
            return SendMessageAndHalfCloseWithReaderThreadHopAsync(data, cancellationToken);
        }

        var ct = cancellationToken.CanBeCanceled ? cancellationToken : _disposeCts.Token;

        // Large payloads: wait for the ring write to complete before
        // returning, so the caller can safely reuse the buffer.
        if (data.Length >= 65536 && Volatile.Read(ref _disposed) == 0)
        {
#pragma warning disable CA2016
            if (_sendLock.Wait(0))
#pragma warning restore CA2016
            {
                try
                {
                    _connection.SendFrameZeroCopyAndWait(FrameType.Message, StreamId, MessageFlags.EndStream, data, ct);
                    Volatile.Write(ref _halfCloseSent, 1);
                    return Task.CompletedTask;
                }
                finally
                {
                        _sendLock.Release();
                    }
                }
            }

            var task = SendFrameAsync(FrameType.Message, MessageFlags.EndStream, data, ct);
            if (task.IsCompletedSuccessfully)
            {
                Volatile.Write(ref _halfCloseSent, 1);
                return Task.CompletedTask;
            }
            return SendMessageAndHalfCloseCompleteAsync(task);
    }

    private async Task SendMessageAndHalfCloseCompleteAsync(Task sendTask)
    {
        await sendTask.ConfigureAwait(false);
        Volatile.Write(ref _halfCloseSent, 1);
    }

    /// <summary>
    /// Slow-path helper for <see cref="SendMessageAsync(System.ReadOnlyMemory{byte}, System.Threading.CancellationToken)"/>:
    /// hops off the SHM frame-reader thread via <see cref="Task.Yield"/>
    /// before recursing into the normal send path. After the yield the
    /// continuation runs on a ThreadPool worker, so
    /// <see cref="ShmReaderThreadContext.IsOnReaderThread"/> is false and
    /// the recursive call takes the fast sync path (or blocks safely on
    /// the worker thread, never on the reader). See
    /// <see cref="ShmReaderThreadContext"/> for the deadlock invariant.
    /// </summary>
    private async Task SendMessageWithReaderThreadHopAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await Task.Yield();
        await SendMessageAsync(data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Slow-path helper for <see cref="SendMessageAndHalfCloseAsync"/> —
    /// see <see cref="SendMessageWithReaderThreadHopAsync"/> for rationale.
    /// </summary>
    private async Task SendMessageAndHalfCloseWithReaderThreadHopAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await Task.Yield();
        await SendMessageAndHalfCloseAsync(data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a message payload using zero-copy. The <paramref name="pooledBuffer"/>
    /// is returned to <see cref="ArrayPool{T}"/> after the data has been written
    /// to the ring buffer, replacing the caller's <c>finally</c> block.
    /// </summary>
    public Task SendMessageZeroCopyAsync(ReadOnlyMemory<byte> data, byte[] pooledBuffer, CancellationToken cancellationToken = default)
    {
        try
        {
            ThrowIfDisposed();
            ThrowIfCannotSendMessage();
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(pooledBuffer);
            throw;
        }

        // SAFE-INLINE-RECEIVE DEADLOCK GUARD: same invariant as
        // SendMessageAsync — hop off the SHM frame-reader thread if the
        // upcoming write may block on per-stream / connection send-quota.
        // See ShmReaderThreadContext for the full invariant.
        if (ShmReaderThreadContext.IsOnReaderThread
            && WouldBlockSendQuota(data.Length))
        {
            return SendMessageZeroCopyWithReaderThreadHopAsync(data, pooledBuffer, cancellationToken);
        }

        // No per-stream flow control: ring WaitForSpace provides back-pressure.
        var ct = cancellationToken.CanBeCanceled ? cancellationToken : _disposeCts.Token;
        return SendFrameZeroCopyAsync(FrameType.Message, 0, data, pooledBuffer, ct);
    }

    /// <summary>
    /// Slow-path helper for <see cref="SendMessageZeroCopyAsync"/> — see
    /// <see cref="SendMessageWithReaderThreadHopAsync"/> for rationale.
    /// </summary>
    private async Task SendMessageZeroCopyWithReaderThreadHopAsync(ReadOnlyMemory<byte> data, byte[] pooledBuffer, CancellationToken cancellationToken)
    {
        await Task.Yield();
        await SendMessageZeroCopyAsync(data, pooledBuffer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a gRPC message: the protobuf body is serialized DIRECTLY
    /// into a pooled ring-sized buffer (no intermediate
    /// <c>ToByteArray()</c> allocation) and wrapped with the 5-byte
    /// gRPC LPM header inline (<c>[compFlag=0(1)][len(4 BE)][body]</c>).
    /// The pooled buffer is returned to <see cref="ArrayPool{T}"/> after
    /// the ring write completes.
    ///
    /// <para>This is the recommended path for hand-written
    /// server/client implementations that use <see cref="ShmGrpcStream"/>
    /// directly (e.g. via <c>ShmGrpcServer</c>). Kestrel-hosted gRPC
    /// services already get equivalent zero-allocation framing through
    /// <c>SerializationContext.GetBufferWriter()</c> + the SHM
    /// <c>PipeWriter</c> adapter; they should NOT call this method.</para>
    /// </summary>
    public Task SendMessageAsync(Google.Protobuf.IMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var size = message.CalculateSize();
        var totalLen = 5 + size;
        var buf = ArrayPool<byte>.Shared.Rent(totalLen);
        try
        {
            buf[0] = 0; // uncompressed (gRPC LPM compression flag)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                buf.AsSpan(1, 4), (uint)size);
            if (size > 0)
            {
                message.WriteTo(buf.AsSpan(5, size));
            }
            // SendMessageZeroCopyAsync owns `buf` and returns it to the pool
            // after the ring write completes. We must NOT return it here.
            return SendMessageZeroCopyAsync(buf.AsMemory(0, totalLen), buf, cancellationToken);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buf);
            throw;
        }
    }

    /// <summary>
    /// Strips the 5-byte gRPC LPM header from a wire-format MESSAGE body
    /// (<c>[compFlag(1)][len(4 BE)][body]</c>) and returns the body slice.
    /// Throws on malformed or compressed (compFlag != 0) blobs; callers
    /// that need to handle compression should peek the flag byte first.
    ///
    /// Counterpart to <see cref="SendMessageWithLpmZeroCopyAsync"/> for
    /// hand-written servers/clients reading raw frames out of
    /// <see cref="ReceiveMessagesAsync"/>.
    /// </summary>
    public static ReadOnlySpan<byte> UnwrapLpm(ReadOnlySpan<byte> framed)
    {
        if (framed.Length < 5)
            throw new ArgumentException($"LPM blob too short: {framed.Length} bytes", nameof(framed));
        if (framed[0] != 0)
            throw new InvalidDataException(
                $"Compressed gRPC LPM not supported (compFlag=0x{framed[0]:X2}); set grpc-encoding=identity or handle compression at the application layer.");
        var len = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(framed.Slice(1, 4));
        if (5 + len > framed.Length)
            throw new InvalidDataException(
                $"LPM declares {len} bytes but only {framed.Length - 5} are available.");
        return framed.Slice(5, len);
    }

    /// <summary>
    /// Writes trailers directly to the ring via the given (paused) writer.
    /// Caller MUST hold the WriterLoop pause. No queue, no async, no signal race.
    /// </summary>
    internal void SendTrailersInline(ShmFrameWriter writer, StatusCode statusCode, string? statusMessage = null, Metadata? metadata = null)
    {
        _trailers = new TrailersV1
        {
            Version = 1,
            GrpcStatusCode = statusCode,
            GrpcStatusMessage = statusMessage,
            Metadata = ConvertMetadata(metadata)
        };

        // Round-7 PR-B: object-passthrough inline write.
        writer.WriteInlineTrailersFrame(StreamId, _trailers, default);
        Volatile.Write(ref _halfCloseSent, 1);
    }

    /// <summary>
    /// Sends trailers and closes the stream (server-side).
    /// </summary>
    public async Task SendTrailersAsync(StatusCode statusCode, string? statusMessage = null, Metadata? metadata = null)
    {
        ThrowIfDisposed();
        if (IsClientStream)
            throw new InvalidOperationException("Only server can send trailers");
        ThrowIfCannotSendTrailers();

        _trailers = new TrailersV1
        {
            Version = 1,
            GrpcStatusCode = statusCode,
            GrpcStatusMessage = statusMessage,
            Metadata = ConvertMetadata(metadata)
        };

        // Round-8 PR-D: queued/async fallback uses the object passthrough
        // path. Inline single-stream fast path (SendTrailersInline) was
        // already PR-B; this covers the multi-stream / N>1 fallback.
        await SendTrailersFrameAsync(TrailersFlags.EndStream, _trailers).ConfigureAwait(false);
        Volatile.Write(ref _halfCloseSent, 1);
    }

    /// <summary>
    /// Signals that no more messages will be sent from this side.
    /// </summary>
    public Task SendHalfCloseAsync()
    {
        ThrowIfDisposed();
        // Atomic: ensure only one HalfClose is ever sent.
        if (Interlocked.CompareExchange(ref _halfCloseSent, 1, 0) != 0)
            return Task.CompletedTask;

        // In singleStreamMode, write HalfClose inline to avoid queue overhead.
        // Always use TryPause here (never ExecuteInline) because:
        // 1. HalfClose is a zero-payload frame — ring write is ~100ns.
        // 2. TryPause spin is bounded: WriterLoop checks _paused every Phase 2
        //    iteration (~30ns), so pause completes within a few µs.
        // 3. ExecuteInline would allocate a lambda closure + two kernel signals,
        //    adding ~2-5µs overhead per unary call that dominates small payloads.
        if (_connection.SingleStreamMode && _connection.ActiveStreamCount <= 1)
        {
            var writer = _connection.FrameWriter;
            if (writer != null && writer.TryPauseWriterLoop())
            {
                try
                {
                    FrameProtocol.WriteHalfClose(_connection.TxRing, StreamId, default);
                    // Wake-coalescing close: if SendRequestHeadersAsync
                    // opened an inline batch (unary path), close it now
                    // so the single coalesced SignalData fires.
                    if (Interlocked.Exchange(ref _pendingInlineBatch, 0) == 1)
                    {
                        writer.EndInlineBatch();
                        Interlocked.Increment(ref s_coalesceClosed);
                    }
                }
                finally
                {
                    writer.ResumeWriterLoop();
                }
                return Task.CompletedTask;
            }
        }

        var task = SendFrameAsync(FrameType.HalfClose, 0, Array.Empty<byte>());
        if (task.IsCompletedSuccessfully)
        {
            return Task.CompletedTask;
        }
        return SendHalfCloseSlowAsync(task);
    }

    private async Task SendHalfCloseSlowAsync(Task sendTask)
    {
        await sendTask.ConfigureAwait(false);
        Volatile.Write(ref _halfCloseSent, 1);
    }

    /// <summary>
    /// Cancels the stream.
    /// </summary>
    public async Task CancelAsync()
    {
        ThrowIfDisposed();
        if (_cancelled) return;
        _cancelled = true;
        CancelCancellationToken();
        // BUG-FIX (round-10 GPT-5.5 #1): wake any sender parked on
        // send-quota so it observes _cancelled and aborts. Mirrors
        // the inbound-Cancel handler (FIX-1) and AbortForFlowControl.
        // Without this, a writer parked in ReserveSendQuotaOrBlock
        // stays parked even after the local user cancels its own RPC.
        _sendQuotaWake.Set();

        try
        {
            await SendFrameAsync(FrameType.Cancel, 0, Array.Empty<byte>());
        }
        catch { }

        _inboundFrames.Writer.TryComplete();

        // BUG-FIX (round-10 GPT-5.5 #1): release the stream slot in
        // the connection's stream-map. Previously CancelAsync did not
        // do this -- only Dispose() did. ShmControlHandler.SendAsync
        // catches OperationCanceledException, calls CancelAsync, then
        // rethrows without disposing the stream. The stream stayed in
        // the connection's stream-map until the connection itself was
        // torn down, holding a permanent slot per cancelled RPC.
        // Repeated cancellations on a long-lived connection would
        // eventually exhaust the per-connection MAX_CONCURRENT_STREAMS
        // budget and produce false "no available stream slot" errors
        // for new RPCs.
        //
        // RemoveStream is idempotent so the subsequent Dispose() (when
        // the stream object is garbage-collected or explicitly disposed)
        // re-removing is a no-op. Mirrors what the inbound-Cancel
        // handler at ~line 1812 already does on remote cancel.
        _connection.RemoveStream(StreamId);
    }

    /// <summary>
    /// Receives the next frame from the stream.
    /// </summary>
    /// <returns>The frame, or null if the stream is closed.</returns>
    /// <remarks>
    /// Round-8 PR-C2: returns <see cref="ValueTask{TResult}"/> so the
    /// synchronously-completed fast path (frame already buffered in the
    /// per-stream channel — the dominant case under load) does not
    /// allocate a <see cref="Task{TResult}"/>. <c>await</c> works
    /// identically for ValueTask and Task callers.
    /// </remarks>
    public ValueTask<InboundFrame?> ReceiveFrameAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // Fast path: if a frame is already queued, return it immediately
        // without allocating a LinkedCTS or async state machine.
        if (_inboundFrames.Reader.TryRead(out var frame))
        {
            if (s_diagHopTiming)
            {
                var push = Volatile.Read(ref _lastHopPushTicks);
                if (push != 0)
                {
                    var hopTicks = System.Diagnostics.Stopwatch.GetTimestamp() - push;
                    if (hopTicks >= 0)
                    {
                        Interlocked.Add(ref s_hopFastTicksTotal, hopTicks);
                        Interlocked.Increment(ref s_hopFastCount);
                    }
                }
            }
            return new ValueTask<InboundFrame?>(frame);
        }

        // Slow path: need to wait for a frame.
        return ReceiveFrameSlowAsync(cancellationToken);
    }

    /// <summary>
    /// Synchronous variant of <see cref="ReceiveFrameAsync"/>. Used by
    /// <see cref="LazyChainRos"/>'s pull callback inside protobuf's
    /// synchronous <c>MergeFrom(ros)</c> parse loop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns the next queued frame immediately if one is buffered.
    /// Otherwise blocks the calling thread on the inbound frames channel
    /// until a frame arrives, the stream is disposed, or
    /// <paramref name="cancellationToken"/> fires.
    /// </para>
    /// <para>
    /// Sync-over-async safety: the underlying <c>Channel&lt;InboundFrame&gt;</c>
    /// uses <c>ManualResetValueTaskSourceCore</c> internally with no
    /// SynchronizationContext capture; awaiting it via
    /// <c>GetAwaiter().GetResult()</c> blocks the calling thread on a
    /// kernel signal that the producer (the per-connection
    /// <c>FrameReaderLoopAsync</c> running on its own dedicated task)
    /// fires asynchronously. Cannot self-deadlock because consumer and
    /// producer are on different threads.
    /// </para>
    /// </remarks>
    public InboundFrame? ReceiveFrameSync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_inboundFrames.Reader.TryRead(out var frame))
        {
            if (s_diagHopTiming)
            {
                var push = Volatile.Read(ref _lastHopPushTicks);
                if (push != 0)
                {
                    var hopTicks = System.Diagnostics.Stopwatch.GetTimestamp() - push;
                    if (hopTicks >= 0)
                    {
                        Interlocked.Add(ref s_hopFastTicksTotal, hopTicks);
                        Interlocked.Increment(ref s_hopFastCount);
                    }
                }
            }
            return frame;
        }

        CancellationToken ct;
        CancellationTokenSource? linkedCts = null;
        if (cancellationToken.CanBeCanceled)
        {
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
            ct = linkedCts.Token;
        }
        else
        {
            // Steady-state hot path: pass CancellationToken.None so the
            // runtime SingleConsumerUnboundedChannel.WaitToReadAsync can
            // use its pooled _waiterSingleton (the !CanBeCanceled fast
            // path) instead of allocating a new WaitingReadAsyncOperation
            // + cancellation registration per receive. GPT-5.5 review
            // identified this as the hidden per-frame tax that explains
            // why even SHM-off was losing 1000x64B Linux to UDS.
            //
            // Wake-on-dispose still works via
            // _inboundFrames.Writer.TryComplete() called from
            // ShmGrpcStream.Dispose() and from the connection-level
            // early-wake loop in ShmConnection.Dispose() / DisposeAsync.
            ct = CancellationToken.None;
        }

        try
        {
            // ValueTask<bool>: if synchronously completed, read directly; else
            // block on the underlying Task.
            var waitTask = _inboundFrames.Reader.WaitToReadAsync(ct);
            bool hasMore = waitTask.IsCompleted
                ? waitTask.Result
                : waitTask.AsTask().GetAwaiter().GetResult();

            if (hasMore && _inboundFrames.Reader.TryRead(out frame))
            {
                if (s_diagHopTiming)
                {
                    var push = Volatile.Read(ref _lastHopPushTicks);
                    if (push != 0)
                    {
                        var hopTicks = System.Diagnostics.Stopwatch.GetTimestamp() - push;
                        if (hopTicks >= 0)
                        {
                            Interlocked.Add(ref s_hopTicksTotal, hopTicks);
                            Interlocked.Increment(ref s_hopCount);
                        }
                    }
                }
                return frame;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ChannelClosedException)
        {
        }
        finally
        {
            linkedCts?.Dispose();
        }

        return null;
    }

    private async ValueTask<InboundFrame?> ReceiveFrameSlowAsync(CancellationToken cancellationToken)
    {
        // Only create LinkedCTS when the caller provided a cancellable token.
        // In streaming steady state, grpc-dotnet typically passes default.
        CancellationToken ct;
        CancellationTokenSource? linkedCts = null;
        if (cancellationToken.CanBeCanceled)
        {
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
            ct = linkedCts.Token;
        }
        else
        {
            // See ReceiveFrameSync's parallel rationale: passing
            // CancellationToken.None lets the runtime
            // SingleConsumerUnboundedChannel pick its pooled
            // _waiterSingleton fast path. Dispose wake propagates via
            // _inboundFrames.Writer.TryComplete().
            ct = CancellationToken.None;
        }

        try
        {
            if (await _inboundFrames.Reader.WaitToReadAsync(ct))
            {
                if (_inboundFrames.Reader.TryRead(out var frame))
                {
                    if (s_diagHopTiming)
                    {
                        var push = Volatile.Read(ref _lastHopPushTicks);
                        if (push != 0)
                        {
                            var hopTicks = System.Diagnostics.Stopwatch.GetTimestamp() - push;
                            if (hopTicks >= 0)
                            {
                                Interlocked.Add(ref s_hopTicksTotal, hopTicks);
                                Interlocked.Increment(ref s_hopCount);
                            }
                        }
                    }
                    return frame;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ChannelClosedException)
        {
        }
        finally
        {
            linkedCts?.Dispose();
        }

        return null;
    }

    /// <summary>
    /// Receives request headers (server-side).
    /// </summary>
    public async Task<HeadersV1> ReceiveRequestHeadersAsync(CancellationToken cancellationToken = default)
    {
        if (IsClientStream)
            throw new InvalidOperationException("Only server receives request headers");

        while (true)
        {
            var frame = await ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame == null)
                throw new InvalidOperationException("Stream closed before receiving headers");

            if (frame.Value.Type == FrameType.Headers)
            {
                _requestHeaders = frame.Value.AsHeaders();
                frame.Value.ReturnToPool();
                return _requestHeaders;
            }

            frame.Value.ReturnToPool();
        }
    }

    /// <summary>
    /// Receives response headers (client-side).
    /// </summary>
    /// <exception cref="ShmStreamRefusedException">
    /// Thrown when the server refuses the stream (sends TRAILERS before HEADERS),
    /// typically because the maximum concurrent stream limit was reached.
    /// </exception>
    public Task<HeadersV1> ReceiveResponseHeadersAsync(CancellationToken cancellationToken = default)
    {
        if (!IsClientStream)
            throw new InvalidOperationException("Only client receives response headers");

        var frameTask = ReceiveFrameAsync(cancellationToken);
        if (!frameTask.IsCompletedSuccessfully)
        {
            // Slow path: hand the unresolved ValueTask to the async slow
            // helper which awaits it exactly once (ValueTask single-
            // consume invariant).
            return ReceiveResponseHeadersSlowAsync(frameTask, cancellationToken);
        }

        // Fast path completed synchronously: consume the ValueTask result
        // here (exactly once) and dispatch on the resolved frame.
        var firstFrame = frameTask.Result;
        if (firstFrame != null && firstFrame.Value.Type == FrameType.Headers)
        {
            _responseHeaders = firstFrame.Value.AsHeaders();
            firstFrame.Value.ReturnToPool();
            return Task.FromResult(_responseHeaders);
        }
        // Fast path completed but the first frame wasn't HEADERS (e.g.,
        // server returned trailers-only refusal). Re-wrap the already-
        // resolved frame as a completed ValueTask so the slow path can
        // await it without re-consuming the original.
        return ReceiveResponseHeadersSlowAsync(
            new ValueTask<InboundFrame?>(firstFrame), cancellationToken);
    }

    private async Task<HeadersV1> ReceiveResponseHeadersSlowAsync(
        ValueTask<InboundFrame?> firstFrameTask, CancellationToken cancellationToken)
    {
        var firstFrame = await firstFrameTask.ConfigureAwait(false);
        if (firstFrame == null)
        {
            var sendEx = _sendFailure;
            throw sendEx != null
                ? new InvalidOperationException("Request body send failed", sendEx)
                : new InvalidOperationException("Stream closed before receiving headers");
        }

        if (firstFrame.Value.Type == FrameType.Headers)
        {
            _responseHeaders = firstFrame.Value.AsHeaders();
            firstFrame.Value.ReturnToPool();
            return _responseHeaders;
        }

        if (firstFrame.Value.Type == FrameType.Trailers)
        {
            var trailers = firstFrame.Value.AsTrailers();
            firstFrame.Value.ReturnToPool();
            _trailers = trailers;
            _halfCloseReceived = true;
            throw new ShmStreamRefusedException(trailers.GrpcStatusMessage ?? "Stream refused by server");
        }

        firstFrame.Value.ReturnToPool();

        while (true)
        {
            var frame = await ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame == null)
            {
                var sendEx2 = _sendFailure;
                throw sendEx2 != null
                    ? new InvalidOperationException("Request body send failed", sendEx2)
                    : new InvalidOperationException("Stream closed before receiving headers");
            }

            if (frame.Value.Type == FrameType.Headers)
            {
                _responseHeaders = frame.Value.AsHeaders();
                frame.Value.ReturnToPool();
                return _responseHeaders;
            }

            // Server sent TRAILERS before HEADERS — stream was refused.
            // This happens when the server's max concurrent streams is exceeded.
            if (frame.Value.Type == FrameType.Trailers)
            {
                var trailers = frame.Value.AsTrailers();
                frame.Value.ReturnToPool();
                _trailers = trailers;
                _halfCloseReceived = true;
                throw new ShmStreamRefusedException(trailers.GrpcStatusMessage ?? "Stream refused by server");
            }

            frame.Value.ReturnToPool();
        }
    }

    /// <summary>
    /// Receives messages from the stream.
    /// Each yielded <c>byte[]</c> is an owned, exact-size array safe to hold indefinitely.
    /// </summary>
    public async IAsyncEnumerable<byte[]> ReceiveMessagesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        MemoryStream? messageAccumulator = null;
        while (true)
        {
            if (_cancelled) yield break;

            var frame = await ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame == null)
            {
                // If the send side failed, surface it instead of silently
                // treating a truncated response as normal EOF.
                var sendEx = _sendFailure;
                if (sendEx != null)
                    throw new InvalidOperationException("Request body send failed during streaming", sendEx);
                yield break;
            }

            var f = frame.Value;
            switch (f.Type)
            {
                case FrameType.Message:
                    if ((f.Flags & MessageFlags.More) != 0)
                    {
                        messageAccumulator ??= new MemoryStream();
                        messageAccumulator.Write(f.Memory.Span);
                        f.ReturnToPool();
                        break;
                    }

                    if (messageAccumulator != null)
                    {
                        messageAccumulator.Write(f.Memory.Span);
                        f.ReturnToPool();
                        var msgBytes = messageAccumulator.ToArray();
                        // Note: stream-level drip is handled at the codec
                        // hook (ShmConnection.OnDataFrame → InFlow.OnRead),
                        // not here — SHM has no separate copy buffer, so
                        // receive == read for FC purposes.
                        yield return msgBytes;
                        messageAccumulator.SetLength(0);
                        if ((f.Flags & MessageFlags.EndStream) != 0) { _halfCloseReceived = true; yield break; }
                        break;
                    }

                    // Yield an owned copy so payload buffers can be released safely.
                    var owned = f.Memory.ToArray();
                    f.ReturnToPool();
                    yield return owned;
                    if ((f.Flags & MessageFlags.EndStream) != 0) { _halfCloseReceived = true; yield break; }
                    break;

                case FrameType.HalfClose:
                    f.ReturnToPool();
                    _halfCloseReceived = true;
                    yield break;

                case FrameType.Trailers:
                    _trailers = f.AsTrailers();
                    f.ReturnToPool();
                    _halfCloseReceived = true;
                    yield break;

                case FrameType.Cancel:
                    f.ReturnToPool();
                    _cancelled = true;
                    yield break;

                default:
                    f.ReturnToPool();
                    break;
            }
        }
    }

    /// <summary>
    /// Receives the next complete message from the stream, accepting a per-call
    /// cancellation token. Unlike <see cref="ReceiveMessageBuffersAsync"/> (which
    /// binds a token at enumerator-creation time and ignores subsequent tokens),
    /// this method propagates the caller's <paramref name="cancellationToken"/>
    /// on every call, so client-side cancel/deadline always takes effect — even
    /// on a long-blocked read.
    /// <para>
    /// The returned <see cref="ReadOnlyMemory{T}"/> may be backed by a pooled
    /// buffer (single-frame fast path) or by an owned array assembled from
    /// multiple fragments (multi-frame path via <see cref="MemoryStream.ToArray"/>).
    /// The caller must release <paramref name="previousFrame"/> (the frame from
    /// the prior call) before calling again — or pass <c>default</c> on first call.
    /// </para>
    /// </summary>
    /// <param name="previousFrame">
    /// The <see cref="InboundFrame"/> from the previous call. Its pooled buffer
    /// is released at the start of this call (deferred release for zero-copy).
    /// Pass <c>default</c> on the first call.
    /// </param>
    /// <param name="cancellationToken">Per-call cancellation token.</param>
    /// <returns>
    /// A tuple of (memory, frame, endOfStream). When <c>endOfStream</c> is true,
    /// the stream is complete and no more calls should be made.
    /// </returns>
    internal async Task<(ReadOnlyMemory<byte> Memory, InboundFrame Frame, bool EndOfStream)> ReceiveNextMessageBufferAsync(
        InboundFrame previousFrame,
        CancellationToken cancellationToken = default)
    {
        previousFrame.ReturnToPool();
        MemoryStream? messageAccumulator = null;

        while (true)
        {
            if (_cancelled)
            {
                return (default, default, true);
            }

            var frame = await ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame == null)
            {
                var sendEx = _sendFailure;
                if (sendEx != null)
                    throw new InvalidOperationException("Request body send failed during streaming", sendEx);
                return (default, default, true);
            }

            var f = frame.Value;
            switch (f.Type)
            {
                case FrameType.Message:
                    if ((f.Flags & MessageFlags.More) != 0)
                    {
                        // Multi-fragment: accumulate and continue reading
                        messageAccumulator ??= new MemoryStream();
                        messageAccumulator.Write(f.Memory.Span);
                        f.ReturnToPool();
                        continue;
                    }

                    if (messageAccumulator != null && messageAccumulator.Length > 0)
                    {
                        // Last fragment of multi-fragment message.
                        // Strip the 5-byte gRPC LPM header so the consumer
                        // receives just the message body — same convention
                        // as ReceiveMessageBuffersAsync.
                        messageAccumulator.Write(f.Memory.Span);
                        f.ReturnToPool();
                        var assembled = messageAccumulator.ToArray();
                        messageAccumulator.SetLength(0);
                        var endStream1 = (f.Flags & MessageFlags.EndStream) != 0;
                        if (endStream1) _halfCloseReceived = true;
                        return (assembled.AsMemory(5), default, endStream1);
                    }

                    // Single-frame message: return zero-copy view sliced past
                    // the 5-byte LPM header. Caller must hold onto 'f' and
                    // pass it back as previousFrame on the next call so the
                    // pooled buffer can be released.
                    if ((f.Flags & MessageFlags.EndStream) != 0)
                    {
                        _halfCloseReceived = true;
                        return (f.Memory.Slice(5), f, true);
                    }
                    return (f.Memory.Slice(5), f, false);

                case FrameType.HalfClose:
                    f.ReturnToPool();
                    _halfCloseReceived = true;
                    return (default, default, true);

                case FrameType.Trailers:
                    _trailers = f.AsTrailers();
                    f.ReturnToPool();
                    _halfCloseReceived = true;
                    return (default, default, true);

                case FrameType.Cancel:
                    f.ReturnToPool();
                    _cancelled = true;
                    return (default, default, true);

                default:
                    f.ReturnToPool();
                    continue;
            }
        }
    }

    /// <summary>
    /// Internal high-performance message receiver that yields <see cref="ReadOnlyMemory{T}"/>
    /// views backed by pooled buffers. The memory is only
    /// valid until the next <c>MoveNextAsync</c> call — callers must copy any data
    /// they need to retain.
    /// Used by <see cref="ShmGrpcResponseStream"/> to avoid LOH allocations.
    /// </summary>
    internal async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveMessageBuffersAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        InboundFrame previousFrame = default;
        MemoryStream? messageAccumulator = null;
        try
        {
            while (true)
            {
                if (_cancelled) yield break;

                var frame = await ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
                if (frame == null)
                {
                    var sendEx = _sendFailure;
                    if (sendEx != null)
                        throw new InvalidOperationException("Request body send failed during streaming", sendEx);
                    yield break;
                }

                var f = frame.Value;
                switch (f.Type)
                {
                    case FrameType.Message:
                        if ((f.Flags & MessageFlags.More) != 0)
                        {
                            messageAccumulator ??= new MemoryStream();
                            messageAccumulator.Write(f.Memory.Span);
                            f.ReturnToPool();
                            break;
                        }

                        if (messageAccumulator != null)
                        {
                            messageAccumulator.Write(f.Memory.Span);
                            f.ReturnToPool();

                            // Returning accumulated payload as owned memory avoids
                            // lifetime issues while still preventing many intermediate copies.
                            // Strip the 5-byte gRPC LPM header so the consumer
                            // receives just the message body.
                            previousFrame.ReturnToPool();
                            var assembled = messageAccumulator.ToArray();
                            // Note: stream-level drip is handled at the codec
                            // hook (ShmConnection.OnDataFrame → InFlow.OnRead),
                            // not here — SHM has no separate copy buffer.
                            yield return assembled.AsMemory(5);
                            messageAccumulator.SetLength(0);
                            if ((f.Flags & MessageFlags.EndStream) != 0) { _halfCloseReceived = true; yield break; }
                            break;
                        }

                        // Return the PREVIOUS payload now that the consumer
                        // has advanced past it.
                        previousFrame.ReturnToPool();

                        previousFrame = f;
                        // Strip the 5-byte gRPC LPM header — the H2 wire carries
                        // each MESSAGE as `[compFlag(1)][len(4)][body]`; the
                        // consumer (e.g. <see cref="ShmControlResponseContent"/>'s
                        // SerializeToStreamAsync) re-frames around the body.
                        yield return f.Memory.Slice(5);
                        if ((f.Flags & MessageFlags.EndStream) != 0) { _halfCloseReceived = true; yield break; }
                        break;

                    case FrameType.HalfClose:
                        f.ReturnToPool();
                        _halfCloseReceived = true;
                        yield break;

                    case FrameType.Trailers:
                        _trailers = f.AsTrailers();
                        f.ReturnToPool();
                        _halfCloseReceived = true;
                        yield break;

                    case FrameType.Cancel:
                        f.ReturnToPool();
                        _cancelled = true;
                        yield break;

                    default:
                        f.ReturnToPool();
                        break;
                }
            }
        }
        finally
        {
            previousFrame.ReturnToPool();
        }
    }

    internal void OnFrameReceived(InboundFrame frame)
    {
        // Diag: stamp the moment we're about to push to the inbound
        // channel. ReceiveFrameSlowAsync / ReceiveFrameSync read this
        // back and accumulate the gap = "in-process reader→user hop".
        if (s_diagHopTiming)
        {
            Volatile.Write(ref _lastHopPushTicks, System.Diagnostics.Stopwatch.GetTimestamp());
        }
        var ownsFrame = true;
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || _cancelled)
            {
                frame.ReturnToPool();
                ownsFrame = false;
                return;
            }

            switch (frame.Type)
            {
                case FrameType.Cancel:
                    _cancelled = true;
                    CancelCancellationToken();
                    // BUG-FIX (round-10 GPT-5.5 #2): wake any sender
                    // parked in ReserveSendQuotaOrBlock so it observes
                    // _cancelled at the top of its loop and aborts
                    // promptly instead of waiting for the caller's
                    // (unrelated) cancellation token or the eventual
                    // RPC deadline. Mirrors the AbortForFlowControl
                    // pattern at line ~2175 which already does this.
                    _sendQuotaWake.Set();
                    frame.ReturnToPool();
                    ownsFrame = false;
                    _inboundFrames.Writer.TryComplete();
                    _connection.RemoveStream(StreamId);
                    break;

                case FrameType.HalfClose:
                    _halfCloseReceived = true;
                    if (_inboundFrames.Writer.TryWrite(frame))
                    {
                        ownsFrame = false;
                    }
                    else
                    {
                        frame.ReturnToPool();
                        ownsFrame = false;
                    }
                    break;

                case FrameType.Trailers:
                    _halfCloseReceived = true;
                    if (_inboundFrames.Writer.TryWrite(frame))
                    {
                        ownsFrame = false;
                    }
                    else
                    {
                        frame.ReturnToPool();
                        ownsFrame = false;
                    }
                    _inboundFrames.Writer.TryComplete();
                    // Auto-remove from connection to prevent accumulation when
                    // callers don't dispose the stream (e.g., undisposed AsyncUnaryCall).
                    // No more frames will arrive after TRAILERS.
                    _connection.RemoveStream(StreamId);
                    break;

                default:
                    if (_inboundFrames.Writer.TryWrite(frame))
                    {
                        ownsFrame = false;
                    }
                    else
                    {
                        frame.ReturnToPool();
                        ownsFrame = false;
                    }
                    break;
            }
        }
        catch
        {
            if (ownsFrame)
            {
                frame.ReturnToPool();
            }
            throw;
        }
    }

    /// <summary>
    /// Tries to dequeue a frame from the inbound channel without waiting.
    /// Used by IDirectMessageReader sync fast path.
    /// </summary>
    internal bool TryReceiveFrame(out InboundFrame frame)
    {
        return _inboundFrames.Reader.TryRead(out frame);
    }

    /// <summary>Waits until a frame is available in the channel.</summary>
    internal ValueTask<bool> WaitForFrameAsync(CancellationToken cancellationToken)
    {
        return _inboundFrames.Reader.WaitToReadAsync(cancellationToken);
    }

    /// <summary>Exposes the dispose cancellation token for direct readers.</summary>
    internal CancellationToken DisposeCancellationToken => _disposeCts.Token;

    /// <summary>
    /// Completes the inbound frame channel so that any pending
    /// ReceiveFrameAsync/WaitToReadAsync returns immediately.
    /// Safe to call even if already completed or disposed.
    /// </summary>
    internal void CompleteInbound()
    {
        _inboundFrames.Writer.TryComplete();
    }

    /// <summary>Marks half-close as received from the remote side.</summary>
    internal void MarkHalfCloseReceived()
    {
        _halfCloseReceived = true;
    }

    /// <summary>Sets trailers from a Trailers frame.</summary>
    internal void SetTrailers(InboundFrame frame)
    {
        _trailers = frame.AsTrailers();
    }

    internal static void OnWindowUpdate(uint increment)
    {
        // No-op: per-stream flow control is disabled.
        // Ring WaitForSpace provides back-pressure.
    }

    private Task SendFrameAsync(FrameType type, byte flags, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // Wait(0) is a non-blocking try-acquire; cancellation token is irrelevant.
#pragma warning disable CA2016
        if (_sendLock.Wait(0))
#pragma warning restore CA2016
        {
            try
            {
                _connection.SendFrame(type, StreamId, flags, payload.Span);
            }
            finally
            {
                _sendLock.Release();
            }

            return Task.CompletedTask;
        }

        // Contended: fall back to async wait.
        return SendFrameAsyncContended(type, flags, payload, cancellationToken);
    }

    /// <summary>
    /// Round-8 PR-D: enqueues a HEADERS frame from a
    /// <see cref="HeadersV1"/> object (no upfront byte encode). Used by
    /// <see cref="SendResponseHeadersAsync"/>'s queued/async fallback when
    /// the inline single-stream fast path is unavailable.
    /// </summary>
    private Task SendHeadersFrameAsync(byte flags, HeadersV1 headers, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

#pragma warning disable CA2016
        if (_sendLock.Wait(0))
#pragma warning restore CA2016
        {
            try
            {
                _connection.SendHeadersFrame(StreamId, flags, headers);
            }
            finally
            {
                _sendLock.Release();
            }
            return Task.CompletedTask;
        }
        return SendHeadersFrameAsyncContended(flags, headers, cancellationToken);
    }

    private async Task SendHeadersFrameAsyncContended(byte flags, HeadersV1 headers, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _connection.SendHeadersFrame(StreamId, flags, headers);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Round-8 PR-D companion to <see cref="SendHeadersFrameAsync"/> for TRAILERS.
    /// </summary>
    private Task SendTrailersFrameAsync(byte flags, TrailersV1 trailers, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

#pragma warning disable CA2016
        if (_sendLock.Wait(0))
#pragma warning restore CA2016
        {
            try
            {
                _connection.SendTrailersFrame(StreamId, flags, trailers);
            }
            finally
            {
                _sendLock.Release();
            }
            return Task.CompletedTask;
        }
        return SendTrailersFrameAsyncContended(flags, trailers, cancellationToken);
    }

    private async Task SendTrailersFrameAsyncContended(byte flags, TrailersV1 trailers, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _connection.SendTrailersFrame(StreamId, flags, trailers);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendFrameAsyncContended(FrameType type, byte flags, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _connection.SendFrame(type, StreamId, flags, payload.Span);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Zero-copy variant: enqueues without copying; the pooled buffer is returned
    /// to <see cref="ArrayPool{T}"/> by the writer thread after the ring write.
    /// </summary>
    private Task SendFrameZeroCopyAsync(FrameType type, byte flags,
        ReadOnlyMemory<byte> payload, byte[]? pooledBuffer, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            if (pooledBuffer != null) ArrayPool<byte>.Shared.Return(pooledBuffer);
            throw new ObjectDisposedException(nameof(ShmGrpcStream));
        }
        if (cancellationToken.IsCancellationRequested)
        {
            if (pooledBuffer != null) ArrayPool<byte>.Shared.Return(pooledBuffer);
            return Task.FromCanceled(cancellationToken);
        }

        // Wait(0) is a non-blocking try-acquire; cancellation token is irrelevant.
#pragma warning disable CA2016
        if (_sendLock.Wait(0))
#pragma warning restore CA2016
        {
            try
            {
                _connection.SendFrameZeroCopy(type, StreamId, flags, payload, pooledBuffer);
            }
            catch
            {
                if (pooledBuffer != null) ArrayPool<byte>.Shared.Return(pooledBuffer);
                throw;
            }
            finally
            {
                _sendLock.Release();
            }

            return Task.CompletedTask;
        }

        return SendFrameZeroCopyAsyncContended(type, flags, payload, pooledBuffer, cancellationToken);
    }

    private async Task SendFrameZeroCopyAsyncContended(FrameType type, byte flags,
        ReadOnlyMemory<byte> payload, byte[]? pooledBuffer, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            if (pooledBuffer != null) ArrayPool<byte>.Shared.Return(pooledBuffer);
            throw new ObjectDisposedException(nameof(ShmGrpcStream));
        }
        try
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (pooledBuffer != null) ArrayPool<byte>.Shared.Return(pooledBuffer);
            throw;
        }

        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                if (pooledBuffer != null) ArrayPool<byte>.Shared.Return(pooledBuffer);
                throw new ObjectDisposedException(nameof(ShmGrpcStream));
            }
            _connection.SendFrameZeroCopy(type, StreamId, flags, payload, pooledBuffer);
        }
        catch
        {
            if (pooledBuffer != null) ArrayPool<byte>.Shared.Return(pooledBuffer);
            throw;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static MetadataKV[] ConvertMetadata(Metadata? metadata)
    {
        if (metadata == null || metadata.Count == 0)
            return Array.Empty<MetadataKV>();

        var items = new MetadataKV[metadata.Count];
        var index = 0;
        foreach (var entry in metadata)
        {
            items[index++] = entry.IsBinary
                ? new MetadataKV(entry.Key, entry.ValueBytes)
                : new MetadataKV(entry.Key, entry.Value);
        }

        return items;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private void CancelCancellationToken()
    {
        // Round-9 PR-I: lazy _cancellationCts. Set flag first so a
        // first-read happening concurrently produces an already-
        // cancelled CTS even if it loses our null-check race.
        Volatile.Write(ref _cancelRequested, 1);
        var cts = _cancellationCts;
        if (cts != null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// Local-side teardown for receiver-driven FC violation. Invoked by
    /// <see cref="ShmConnection.OnDataFrame"/> when inbound DATA pushes
    /// this stream over its receive window (RFC 7540 §5.2.2 /§6.9.1).
    /// The remote RST_STREAM(FLOW_CONTROL_ERROR) is sent by the connection
    /// itself; here we cancel local readers and complete the inbound
    /// frame channel so any pending await surfaces a cancellation.
    /// Also wakes any sender parked on insufficient send quota so it
    /// observes the canceled state and aborts (matches grpc-go-shmem's
    /// closeStream-unblocks-acquireSendQuota fix).
    /// </summary>
    internal void AbortForFlowControl(string reason)
    {
        System.Diagnostics.Debug.WriteLine($"[shm-fc] stream {StreamId} AbortForFlowControl: {reason}");
        CancelCancellationToken();
        _sendQuotaWake.Set();
        _inboundFrames.Writer.TryComplete(
            new System.IO.IOException($"HTTP/2 FLOW_CONTROL_ERROR: {reason}"));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Wake any sender parked inside ReserveSendQuotaOrBlock so it
        // observes _disposed == 1 at the top of the for{} loop and
        // throws ObjectDisposedException promptly instead of waiting
        // for the (possibly long-lived) caller cancellation token to
        // fire. Mirrors grpc-go-shmem's TestConnWaiterElem_CloseStream
        // UnblocksParkedAcquire fix (acquireSendQuota deadlock after
        // closeStream).
        _sendQuotaWake.Set();

        // Safety: if a wake-coalescing batch was opened in
        // SendRequestHeadersAsync but never closed (because the request
        // was cancelled before HalfClose ran), close it now so the
        // ring's _batchWriteDepth doesn't leak.
        if (Interlocked.Exchange(ref _pendingInlineBatch, 0) == 1)
        {
            try { _connection.FrameWriter?.EndInlineBatch(); }
            catch { /* best effort */ }
        }

        // Safety: if StageRequestHeaders was called but neither
        // WriteStagedHeadersInline nor FlushStagedHeadersAsync ran
        // (e.g., request cancelled before body write), clear the staged
        // HeadersV1 reference so the object can be GC'd. Round-7 PR-B:
        // no pooled buffer to return — staged storage is now just the
        // managed object reference.
        if (Interlocked.Exchange(ref _stagedHeadersConsumed, 1) == 0)
        {
            _stagedHeaders = null;
        }

        _disposeCts.Cancel();
        CancelCancellationToken();
        _inboundFrames.Writer.TryComplete();

        // Drain any remaining queued frames to return pooled buffers.
        while (_inboundFrames.Reader.TryRead(out var frame))
        {
            frame.ReturnToPool();
        }

        _connection.RemoveStream(StreamId);
        _sendLock.Dispose();
        // BUG-FIX (round-10 GPT-5.5 #10): dispose the send-quota wake
        // MRES so its lazily-allocated kernel wait handle is released.
        // Wake the MRES first (already done at top of Dispose) so any
        // pending Wait observes the signal before we dispose; final
        // pending waiter will get ObjectDisposedException which is
        // caught by the existing send-path try/catch wrappers.
        try { _sendQuotaWake.Dispose(); }
        catch { /* defensive: never throw from Dispose */ }
        // Round-9 PR-I + Round-10 Opus #5: atomically take ownership
        // of the lazy CTS via Exchange so a concurrent CancellationToken
        // getter (or one that races past our _disposed pre-check) can
        // detect via its post-CAS disposed re-check that we no longer
        // hold the field, and refrain from double-disposing.
        var ctsToDispose = Interlocked.Exchange(ref _cancellationCts, null);
        ctsToDispose?.Dispose();
        _disposeCts.Dispose();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
