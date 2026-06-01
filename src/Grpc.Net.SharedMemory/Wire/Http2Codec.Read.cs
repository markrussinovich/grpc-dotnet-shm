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
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Grpc.Net.SharedMemory.Wire;
internal static partial class Http2Codec
{
    internal const int MaxPendingSyntheticFrames = 4096;

    // Per-ring decoder state. Tracks which streams have had their initial
    // HEADERS frame so that subsequent HEADERS frames are interpreted as
    // gRPC trailers. Cleared when END_STREAM is observed.
    //
    // Keyed weakly by ring instance via a ConditionalWeakTable so that a
    // disposed ring's state is collected automatically.
    private static readonly ConditionalWeakTable<ShmRing, Http2DecoderState> s_decoderState
        = new();

    /// <summary>Per-ring decoder state used to distinguish HEADERS vs trailers.</summary>
    /// <remarks>
    /// All access goes through the per-ring frame-reader thread (see
    /// <c>ShmConnection.FrameReaderLoopAsync</c>), so the maps are
    /// plain <see cref="Dictionary{TKey,TValue}"/> rather than
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/>: the per-frame
    /// CAS overhead inside ConcurrentDictionary's hashing is pure cost
    /// in this single-producer single-consumer use.
    /// <para>
    /// <c>LastStreamId</c> / <c>LastAcc</c> form a one-element MRU
    /// cache for single-stream-mode workloads (the dominant deployment
    /// shape): every DATA frame in single-stream-mode targets the same
    /// stream, so the cache hits 100% and the dictionary lookup is
    /// skipped entirely.
    /// </para>
    /// </remarks>
    private sealed class Http2DecoderState
    {
        public readonly Dictionary<uint, byte> StreamsWithInitialHeaders = new();
        public readonly Dictionary<uint, LpmAccumulator> LpmAccumulators = new();

        // MRU hot cache for the LPM accumulator. SingleStreamMode pins
        // streamId == 1 forever, so the dict lookup is bypassed for the
        // entire stream lifetime. Multi-stream workloads still hit the
        // dict but pay only per-stream-switch overhead.
        public uint LastStreamId;
        public LpmAccumulator? LastAcc;

        // Synthetic-frame queue: a single H2 wire frame can produce more
        // than one logical internal frame. Two scenarios both depend on
        // this:
        //
        //   1) Trailers-only HEADERS (gRFC G3): one HEADERS+END_STREAM
        //      surfaces as Headers + Trailers (see EmitDecodedHeaders).
        //
        //   2) DATA-frame coalescing: a peer-side optimisation (or a
        //      different gRPC implementation) may pack two or more
        //      complete gRPC LPM messages into one H2 DATA frame, which
        //      RFC 7540 §6.1 explicitly permits (DATA carries an opaque
        //      byte stream; LPM message boundaries are not aligned with
        //      H2 frame boundaries). The reader emits the first completed
        //      LPM and stashes the remaining ones here.
        //
        // The queue is drained at the head of every ReadFramePayloadInternal
        // call before the ring is touched, preserving FIFO order between
        // synthetic frames and any subsequent wire frames.
        public readonly Queue<(FrameHeader Header, FramePayload Payload)> PendingFrames = new();

        /// <summary>
        /// Bench-only and HTTP/2-FC hook. Invoked synchronously the moment
        /// <see cref="FeedAccumulator"/> finishes parsing a gRPC LPM's
        /// 5-byte header, BEFORE any DATA frame body bytes have been
        /// accumulated. Receives <c>(streamId, lpmSize)</c> where
        /// <c>lpmSize = 5 + bodyLen</c>. Wired by <see cref="ShmConnection"/>
        /// to drive stream-level pre-credit via
        /// <c>InFlow.MaybeAdjustAdditive(lpmSize)</c> per the gRFC SHM
        /// v3.4+ MUST requirement (stream-level pre-credit at LPM parse
        /// — see <c>shm-rfc/A-shared-memory-transport.md</c> §"Stream-level
        /// pre-credit at LPM parse"). Fires once per LPM at the
        /// HeaderBytesSeen 4→5 transition.
        ///
        /// Thread-safety contract: set exclusively by
        /// <see cref="ShmConnection"/> BEFORE the frame reader task starts
        /// (happens-before via task launch). Read exclusively by the
        /// single reader thread inside <see cref="FeedAccumulator"/>.
        /// </summary>
        public Action<uint, uint>? OnMessageStart;

        /// <summary>
        /// HTTP/2-FC hook. Invoked synchronously by <see cref="TryReadDataFrame"/>
        /// once per inbound H2 DATA frame, receiving
        /// <c>(streamId, payloadLen)</c> where <c>payloadLen</c> is the full
        /// on-wire payload size (including Pad Length byte and padding,
        /// per RFC 7540 §6.9.1 "the entire DATA frame payload is included
        /// in flow control"). Wired by <see cref="ShmConnection"/> to drive
        /// conn-level drip-on-receive via <c>TrInFlow.OnData</c> and
        /// stream-level over-window enforcement via <c>InFlow.OnData</c>.
        /// </summary>
        public Action<uint, uint>? OnDataFrame;
    }

    private static void EnqueuePendingFrame(
        Http2DecoderState state, FrameHeader header, FramePayload payload)
    {
        if (state.PendingFrames.Count >= MaxPendingSyntheticFrames)
        {
            throw new InvalidDataException(
                $"H2 DATA frame produced more than {MaxPendingSyntheticFrames + 1} logical gRPC messages");
        }
        state.PendingFrames.Enqueue((header, payload));
    }

    private static void ReleasePendingFrames(Http2DecoderState state)
    {
        while (state.PendingFrames.Count > 0)
        {
            state.PendingFrames.Dequeue().Payload.Release();
        }
    }

    /// <summary>
    /// Per-stream LPM accumulation mode.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>None</b> — no LPM in progress (no body bytes seen since last
    /// complete LPM). The next DATA frame may take any path: single-frame
    /// ZC fast path, chain-ZC start, or accumulator copy.
    /// </para>
    /// <para>
    /// <b>ChainZcInProgress</b> — first DATA frame of a multi-frame LPM
    /// was surfaced as a <see cref="MessageFlags.More"/> chunk with
    /// <see cref="FramePayload.FromRingMemorySpeculative"/>; ring's
    /// chain-ZC anchor is open (<see cref="ShmRing.IsChainOpen"/>=true,
    /// <see cref="ShmRing.IsZcChainActive"/>=true). Subsequent DATA
    /// frames are surfaced per-frame as More chunks (or copy-on-wrap)
    /// until <see cref="LpmAccumulator.ChainRemaining"/> reaches 0, at
    /// which point the chain ends with a no-More chunk and
    /// <see cref="ShmRing.CloseZcChain"/> fires.
    /// </para>
    /// <para>
    /// The accumulator copy path is detected via the
    /// <see cref="LpmAccumulator.Pos"/> field (Pos &gt; 0 = mid-LPM-copy);
    /// it does not write to this enum so there is no
    /// <c>CopyInProgress</c> value here.
    /// </para>
    /// </remarks>
    internal enum AccMode : byte
    {
        None = 0,
        ChainZcInProgress = 1,
    }

    /// <summary>Accumulates a single in-progress gRPC LPM message across multiple DATA frames.</summary>
    private sealed class LpmAccumulator
    {
        public byte[]? Buffer;          // pooled, 0..ExpectedTotal capacity (Copy mode only)
        public int Pos;                 // bytes written so far (Copy mode only)
        public int ExpectedTotal;       // 5 (header) + body length once header is parsed; 0 before that
        public int HeaderBytesSeen;     // 0..5

        // ChainZcInProgress mode state. ChainRemaining counts LPM body bytes
        // (NOT including the 5-byte LPM header, which was carried inline in
        // the first chain frame) still owed by subsequent DATA frames.
        // Reaches 0 → emit final no-More chunk + CloseZcChain.
        public AccMode Mode;
        public long ChainRemaining;

        // Reusable 5-byte LPM header buffer for partial header reads.
        public readonly byte[] HeaderBuf = new byte[5];

        public void Reset()
        {
            if (Buffer != null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(Buffer);
                Buffer = null;
            }
            Pos = 0;
            ExpectedTotal = 0;
            HeaderBytesSeen = 0;
            Mode = AccMode.None;
            ChainRemaining = 0;
        }
    }

    private static Http2DecoderState GetState(ShmRing ring)
    {
        return s_decoderState.GetValue(ring, _ => new Http2DecoderState());
    }

    /// <summary>
    /// Wires a stream-level pre-credit hook on the per-ring decoder
    /// state. The callback is invoked synchronously by the frame
    /// reader thread the moment <see cref="FeedAccumulator"/> finishes
    /// parsing a gRPC LPM's 5-byte header. Receives
    /// <c>(streamId, lpmSize)</c> where <c>lpmSize = 5 + bodyLen</c>.
    /// </summary>
    /// <remarks>
    /// MUST be called BEFORE the connection's frame reader task is
    /// started; the field is plain (no volatile) and relies on the
    /// task-launch happens-before edge for visibility. A <c>null</c>
    /// callback disables the hook (legacy mode); set to a real callback
    /// to drive <c>InFlow.MaybeAdjustAdditive</c> per gRFC SHM v3.4+
    /// stream-level pre-credit MUST.
    /// </remarks>
    internal static void SetOnMessageStart(ShmRing ring, Action<uint, uint>? cb)
    {
        GetState(ring).OnMessageStart = cb;
    }

    /// <summary>
    /// Wires a per-DATA-frame inbound accounting hook on the per-ring
    /// decoder state. The callback is invoked synchronously by the
    /// frame reader thread once per inbound H2 DATA frame, receiving
    /// <c>(streamId, payloadLen)</c> where <c>payloadLen</c> is the
    /// full on-wire payload size (including Pad Length byte and
    /// padding) per RFC 7540 §6.9.1. Same wiring lifetime contract as
    /// <see cref="SetOnMessageStart"/>.
    /// </summary>
    internal static void SetOnDataFrame(ShmRing ring, Action<uint, uint>? cb)
    {
        GetState(ring).OnDataFrame = cb;
    }

    // ===== LPM accumulator dict access helpers (single-threaded reader) =====
    //
    // Wrap the per-stream LpmAccumulator dict with a one-element MRU
    // cache: the dominant deployment shape (single-stream-mode) targets
    // exactly one streamId, so the dict lookup is bypassed entirely after
    // the first frame. Multi-stream workloads pay the dict lookup only
    // on a stream switch, which is the boundary where any decoder needs
    // some state load anyway.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetAcc(Http2DecoderState state, uint streamId, out LpmAccumulator? acc)
    {
        if (state.LastStreamId == streamId && state.LastAcc != null)
        {
            acc = state.LastAcc;
            return true;
        }
        if (state.LpmAccumulators.TryGetValue(streamId, out var found))
        {
            acc = found;
            state.LastStreamId = streamId;
            state.LastAcc = found;
            return true;
        }
        acc = null;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static LpmAccumulator GetOrAddAcc(Http2DecoderState state, uint streamId)
    {
        if (state.LastStreamId == streamId && state.LastAcc != null)
        {
            return state.LastAcc;
        }
        if (!state.LpmAccumulators.TryGetValue(streamId, out var acc))
        {
            acc = new LpmAccumulator();
            state.LpmAccumulators[streamId] = acc;
        }
        state.LastStreamId = streamId;
        state.LastAcc = acc;
        return acc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool RemoveAcc(Http2DecoderState state, uint streamId, out LpmAccumulator? acc)
    {
        if (state.LastStreamId == streamId)
        {
            state.LastStreamId = 0;
            state.LastAcc = null;
        }
        return state.LpmAccumulators.Remove(streamId, out acc);
    }

    private static (FrameHeader Header, FramePayload Payload) ReadFramePayloadInternal(
        ShmRing ring,
        CancellationToken cancellationToken,
        bool zeroCopy)
    {
        var state = GetState(ring);
        Span<byte> hb = stackalloc byte[Http2FrameHeader.Size];

        // Drain any synthetic frames the previous read split off (used for
        // trailers-only HEADERS, where one H2 frame surfaces as two
        // internal frames, and for DATA-frame coalescing, where one H2
        // DATA carries multiple complete LPM messages). Pulling the queue
        // before touching the ring keeps the FIFO invariant intact: the
        // stash entries we deferred last call must be observed by the
        // upper layer before any subsequent ring frame.
        if (state.PendingFrames.Count > 0)
        {
            return state.PendingFrames.Dequeue();
        }

        while (true)
        {
            // Reserve 9-byte H2 frame header (deferred commit).
            var headerReservation = ring.ReserveRead(Http2FrameHeader.Size, cancellationToken);
            var baseCommitReadIdx = headerReservation.CommitReadIdx;

            CopyFromReservation(headerReservation, hb);
            var (h2Type, h2Flags, payloadLen, streamId) = Http2FrameHeader.Decode(hb);

            if (payloadLen > MaxH2FramePayloadSize)
            {
                ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size);
                throw new InvalidDataException(
                    $"H2 frame payload length {payloadLen} exceeds maximum {MaxH2FramePayloadSize}");
            }

            switch (h2Type)
            {
                case Http2FrameType.Data:
                    {
                        var dataResult = TryReadDataFrame(ring, baseCommitReadIdx, streamId, h2Flags, payloadLen, zeroCopy, state, cancellationToken);
                        if (dataResult is { } completed)
                        {
                            return completed;
                        }
                        // Partial LPM message — keep reading.
                        continue;
                    }

                case Http2FrameType.Headers:
                    return ReadHeadersFrame(ring, baseCommitReadIdx, streamId, h2Flags, payloadLen, state, cancellationToken);

                case Http2FrameType.RstStream:
                    return ReadRstStreamFrame(ring, baseCommitReadIdx, streamId, payloadLen, cancellationToken);

                case Http2FrameType.Settings:
                    HandleSettingsFrame(ring, baseCommitReadIdx, h2Flags, streamId, payloadLen, cancellationToken);
                    continue; // Don't surface to upper layer.

                case Http2FrameType.Ping:
                    return ReadPingFrame(ring, baseCommitReadIdx, h2Flags, streamId, payloadLen, cancellationToken);

                case Http2FrameType.GoAway:
                    return ReadGoAwayFrame(ring, baseCommitReadIdx, streamId, payloadLen, cancellationToken);

                case Http2FrameType.WindowUpdate:
                    return ReadWindowUpdateFrame(ring, baseCommitReadIdx, streamId, payloadLen, cancellationToken);

                case Http2FrameType.Continuation:
                    // CONTINUATION is consumed inline by the HEADERS reader
                    // when it sees a frame without END_HEADERS. Reaching the
                    // dispatcher with a CONTINUATION means the peer emitted
                    // it OUT OF SEQUENCE — there was no preceding HEADERS
                    // (or the preceding HEADERS already had END_HEADERS).
                    // RFC 7540 §6.10: PROTOCOL_ERROR.
                    if (payloadLen > 0)
                    {
                        var _ = ring.ReserveRead((int)payloadLen, cancellationToken);
                    }
                    ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
                    throw new InvalidDataException(
                        "H2 CONTINUATION frame received outside a HEADERS sequence (RFC 7540 §6.10)");

                case Http2FrameType.Priority:
                    // Deprecated by RFC 9113; ignore.
                    if (payloadLen > 0)
                    {
                        var skipReservation = ring.ReserveRead((int)payloadLen, cancellationToken);
                        ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
                    }
                    else
                    {
                        ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size);
                    }
                    continue;

                default:
                    ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
                    throw new InvalidDataException($"Unknown H2 frame type 0x{(byte)h2Type:X}");
            }
        }
    }

    /// <summary>
    /// Reads an H2 DATA frame and either returns a complete internal MESSAGE
    /// (when the LPM accumulator finishes a logical app message) or returns
    /// <c>null</c> to signal the outer loop to keep reading.
    /// </summary>
    /// <remarks>
    /// gRPC over HTTP/2 transmits each app message as a 5-byte LPM header
    /// (compression flag + body length, big-endian) followed by body bytes,
    /// and DATA frames are a byte-stream window into that LPM stream.
    /// One DATA frame may carry a fragment of a single message, multiple
    /// complete messages, or a mix; the receiver must reassemble.
    /// <para>
    /// Fast path: when the entire DATA payload contains exactly one complete
    /// LPM message and there is no in-flight accumulator state, we surface
    /// the body directly as the internal MESSAGE payload — preserving the
    /// speculative zero-copy capability of the underlying ring.
    /// </para>
    /// </remarks>
    private static (FrameHeader Header, FramePayload Payload)? TryReadDataFrame(
        ShmRing ring, ulong baseCommitReadIdx, uint streamId, byte h2Flags, int payloadLen,
        bool zeroCopy, Http2DecoderState state, CancellationToken ct)
    {
        // BUG-FIX (round-10 GPT-5.5 #7): RFC 7540 §6.1 — DATA frames
        // MUST be associated with a stream. Receiving a DATA frame on
        // stream 0 is a PROTOCOL_ERROR connection error. Previously
        // this was silently accepted and would have driven flow-
        // control state and the LPM accumulator on the connection
        // pseudo-stream, leaving the codec in undefined state.
        // Drain the malformed payload first so the ring read pointer
        // stays in sync (matches the RST_STREAM malformed-payload
        // pattern at ~line 1079).
        if (streamId == 0)
        {
            if (payloadLen > 0)
            {
                var _ = ring.ReserveRead(payloadLen, ct);
            }
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
            throw new InvalidDataException(
                $"H2 DATA frame on connection stream 0 (RFC 7540 §6.1 PROTOCOL_ERROR)");
        }

        // Per RFC 7540 §6.9.1, the entire DATA frame payload (including
        // Pad Length byte and padding) is included in flow control. Fire
        // the per-DATA-frame hook here so the connection can drive
        // conn-level drip-on-receive via TrInFlow.OnData and stream-level
        // over-window enforcement via InFlow.OnData. The hook is invoked
        // even for zero-length payloads to keep the bookkeeping
        // semantically consistent with stock HTTP/2.
        if (payloadLen > 0)
        {
            state.OnDataFrame?.Invoke(streamId, (uint)payloadLen);
        }

        var endStream = (h2Flags & Http2Flags.EndStream) != 0;
        var padded = (h2Flags & Http2Flags.Padded) != 0;

        if (payloadLen == 0)
        {
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size);
            // Empty DATA: only meaningful when END_STREAM is set (closes stream).
            // No body to feed to the LPM accumulator.
            if (endStream)
            {
                state.StreamsWithInitialHeaders.Remove(streamId);
                if (RemoveAcc(state, streamId, out var stale))
                {
                    // Chain-ZC leak guard (2026-06-01): if the stream had
                    // a chain in flight, close the anchor before resetting
                    // so future Releases can publish header.ReadIdx via
                    // the FramePayload.Release gate. Without this,
                    // _chainOpen stays true forever, EndZcReservation
                    // never fires, ring permanently shrinks.
                    if (stale!.Mode == AccMode.ChainZcInProgress)
                    {
                        ring.CloseZcChain();
                    }
                    stale!.Reset(); // free pooled buffer if any
                }
                return (new FrameHeader(FrameType.HalfClose, streamId, 0, 0), FramePayload.Empty);
            }
            // No-op DATA frame, signal outer loop to read next.
            return null;
        }

        var payloadReservation = ring.ReserveRead(payloadLen, ct);

        // Honour H2 PADDED: first byte is pad-length, last <pad-length> bytes
        // are padding. The body is the slice in between.
        var bodyOffset = 0;
        var bodyLength = payloadLen;
        if (padded)
        {
            byte padLenByte = payloadReservation.First.Length > 0
                ? payloadReservation.First.Span[0]
                : payloadReservation.Second.Span[0];
            bodyOffset = 1;
            bodyLength = payloadLen - 1 - padLenByte;
            if (bodyLength < 0)
            {
                ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
                throw new InvalidDataException("H2 DATA pad length exceeds payload");
            }
        }

        var totalBytes = Http2FrameHeader.Size + payloadLen;

        // === Chain-ZC continuation (2026-06-01 PR): when this stream
        // has an active multi-frame chain-ZC anchor, surface this DATA
        // frame's body as the next per-chunk Message and route the
        // CommitReadRaw through the deferred path (`_zcActive=true` in
        // ShmRing). Pre-empts both the single-frame fast path and the
        // accumulator slow path. ===
        var existedAcc = TryGetAcc(state, streamId, out var existingAcc);
        if (existedAcc && existingAcc!.Mode == AccMode.ChainZcInProgress)
        {
            return EmitChainContinuation(
                ring, state, streamId, existingAcc, baseCommitReadIdx,
                payloadReservation, bodyOffset, bodyLength, totalBytes, endStream);
        }

        // === Fast path: single complete LPM message in this DATA frame, ===
        // === no accumulator state, contiguous body. Eligible for zero-copy. ===
        var hasAccumulator = existedAcc && existingAcc!.Pos > 0;
        if (!hasAccumulator && bodyLength >= 5 && payloadReservation.Second.IsEmpty)
        {
            var bodySpan = payloadReservation.First.Span.Slice(bodyOffset, bodyLength);
            var declaredLpmBody = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bodySpan.Slice(1, 4));
            // declaredLpmBody as uint cannot overflow int when added to 5 because
            // bodyLength is bounded by MaxH2FramePayloadSize (~16 MiB) and we only
            // take the fast path when the totals match exactly.
            if (declaredLpmBody <= (uint)int.MaxValue - 5
                && (int)declaredLpmBody + 5 == bodyLength)
            {
                // Exactly one complete LPM message — surface directly.
                byte msgFlags = endStream ? MessageFlags.EndStream : (byte)0;
                var hdr = new FrameHeader(FrameType.Message, streamId, (uint)bodyLength, msgFlags);
                if (endStream)
                {
                    state.StreamsWithInitialHeaders.Remove(streamId);
                }

                if (zeroCopy && bodyOffset == 0
                    && ring.IsSpeculativeZcEligible(bodyLength, contiguous: payloadReservation.Second.IsEmpty))
                {
                    // Fused single-frame ZC: see FrameProtocol hot-path
                    // comment. The single-frame ZC anchor is independent
                    // of the multi-frame chain-ZC anchor (the chain-ZC
                    // start branch lives below this block); both share
                    // the at-most-one-ZC FIFO invariant via
                    // SpeculativeReservedBytes + _zcActive + _chainOpen
                    // gates in IsSpeculativeZcEligible and
                    // IsChainZcStartEligible.
                    ring.BeginSingleFrameZcCommit(baseCommitReadIdx, totalBytes);
                    Interlocked.Add(ref ring.SpeculativeReservedBytes, totalBytes);
                    return (hdr, FramePayload.FromRingMemorySpeculative(
                        payloadReservation.First.Slice(0, bodyLength), ring, totalBytes));
                }

                var pooled = ArrayPool<byte>.Shared.Rent(bodyLength);
                bodySpan.CopyTo(pooled);
                ring.CommitReadRaw(baseCommitReadIdx, totalBytes);
                return (hdr, FramePayload.FromPooled(pooled, bodyLength));
            }

            // Multi-frame LPM (declared body + 5 > this frame's body). Try
            // chain-ZC START: each subsequent DATA frame is surfaced per-chunk
            // as a More-flagged Message with FromRingMemorySpeculative; the
            // existing downstream LazyChainRos consumer (already used for
            // streaming RPCs at ShmControlHandler.cs#L1758) builds a
            // lazy-pull ReadOnlySequence over the frame stream so the
            // receive-side full-payload memcpy floor is eliminated.
            // Eligibility gated by ring.IsChainZcStartEligible (back-pressure,
            // budget, at-most-one-ZC, compression flag, ring size, etc).
            if (declaredLpmBody <= (uint)int.MaxValue - 5
                && (int)declaredLpmBody + 5 > bodyLength)
            {
                var lpmTotal = 5 + (long)declaredLpmBody;
                var compFlag = bodySpan[0];

                // Malformed-peer guard (2026-06-01 PR round-2 review):
                // an H2 DATA frame carrying END_STREAM with an
                // incomplete LPM is a protocol error (the writer
                // declared an N-byte body but is closing the stream
                // after only `bodyLength` bytes). Our own writer never
                // emits this; symmetric with the empty-DATA-with-EOS
                // chain leak guard above and the chain-continuation
                // over-run guard below. Without this, chain-start would
                // open the anchor and surface a `More`-flagged chunk;
                // the consumer's LazyChainRos would then block forever
                // on `Pull` waiting for body bytes that never arrive,
                // and the chain anchor would leak (`_chainOpen` stays
                // true, ring permanently shrinks).
                if (endStream)
                {
                    ring.CommitReadRaw(baseCommitReadIdx, totalBytes);
                    state.StreamsWithInitialHeaders.Remove(streamId);
                    throw new InvalidDataException(
                        $"H2 stream {streamId} carries END_STREAM on the " +
                        $"first DATA frame of a multi-frame LPM (declared " +
                        $"body={declaredLpmBody}, this frame body={bodyLength}). " +
                        "Incomplete-LPM END_STREAM is a protocol error.");
                }

                if (zeroCopy && bodyOffset == 0
                    && ring.IsChainZcStartEligible(lpmTotal, bodyLength, compFlag))
                {
                    // Open the chain anchor BEFORE the deferred commit so
                    // EndZcReservation cannot be triggered out from under
                    // us by a concurrent Release before we've published
                    // the first chunk.
                    //
                    // EXCEPTION SAFETY (per Opus 4.8 review 2026-06-01):
                    // any throw between OpenZcChain and the successful
                    // FromRingMemorySpeculative return must roll back the
                    // anchor state, otherwise _chainOpen / _zcActive stay
                    // true forever and the ring permanently shrinks.
                    // GetOrAddAcc allocates and OnMessageStart calls into
                    // user code (InFlow.MaybeAdjustAdditive) which can
                    // raise — both happen AFTER the deferred commit, so
                    // we must catch and unwind.
                    ring.OpenZcChain();
                    ring.BeginZcReservation(baseCommitReadIdx);
                    Interlocked.Add(ref ring.SpeculativeReservedBytes, totalBytes);
                    ring.CommitReadRaw(baseCommitReadIdx, totalBytes); // deferred (_zcActive)

                    try
                    {
                        // Initialize per-stream chain state.
                        var acc = GetOrAddAcc(state, streamId);
                        acc.Mode = AccMode.ChainZcInProgress;
                        acc.ExpectedTotal = (int)Math.Min(lpmTotal, int.MaxValue);
                        acc.ChainRemaining = lpmTotal - bodyLength;

                        // Fire OnMessageStart at the LPM-header parse moment,
                        // matching FeedAccumulator's HeaderBytesSeen 4→5
                        // transition timing (line ~795). lpmTotal is always
                        // > 0 here because it equals 5 + declaredLpmBody (uint)
                        // so the bounds check is for cast safety only:
                        // OnMessageStart's parameter is uint, and our
                        // ChainZcBudget gate above already capped lpmTotal
                        // far below uint.MaxValue, but Math.Min is kept
                        // as defense-in-depth against future budget changes.
                        if (state.OnMessageStart != null)
                        {
                            state.OnMessageStart(streamId, (uint)Math.Min(lpmTotal, uint.MaxValue));
                        }

                        // Surface the first chunk: H2 DATA body (which starts
                        // with the 5-byte gRPC LPM header) as one Message
                        // with the More flag. The downstream LazyChainRos
                        // consumer treats the first 5 bytes as the LPM header
                        // (firstFrameBodyOffset:5) and the rest as body.
                        var firstHdr = new FrameHeader(
                            FrameType.Message, streamId, (uint)bodyLength, MessageFlags.More);
                        return (firstHdr, FramePayload.FromRingMemorySpeculative(
                            payloadReservation.First.Slice(bodyOffset, bodyLength),
                            ring, totalBytes));
                    }
                    catch
                    {
                        // Roll back the chain-ZC anchor so the ring's
                        // ReadIdx can resume normal advance. The
                        // CommitReadRaw above already published the
                        // deferred target for this frame; undo
                        // SpeculativeReservedBytes, close the chain
                        // (which fires EndZcReservation defensively when
                        // SpecReserved is now 0), and drop the per-stream
                        // acc state so a future first frame on this
                        // stream re-evaluates eligibility cleanly.
                        Interlocked.Add(ref ring.SpeculativeReservedBytes, -totalBytes);
                        ring.CloseZcChain();
                        if (RemoveAcc(state, streamId, out var failedAcc))
                        {
                            failedAcc!.Reset();
                        }
                        throw;
                    }
                }
                // Chain-ZC ineligible — fall through to existing accumulator
                // copy path. (LpmAccumulator.Mode is currently unused by the
                // copy path; FeedAccumulator uses the Pos field as its
                // mid-LPM marker.)
            }
            // Falls through to slow path: more bytes needed, or multiple LPMs in this frame.
        }

        // === Slow path: copy body into the per-stream LPM accumulator. ===
        // Materialise body to a contiguous buffer.
        byte[]? bodyHeap = null;
        ReadOnlySpan<byte> bodyBytes;
        if (payloadReservation.Second.IsEmpty)
        {
            bodyBytes = payloadReservation.First.Span.Slice(bodyOffset, bodyLength);
        }
        else
        {
            bodyHeap = ArrayPool<byte>.Shared.Rent(bodyLength == 0 ? 1 : bodyLength);
            CopyFromReservationSlice(payloadReservation, bodyOffset, bodyHeap.AsSpan(0, bodyLength));
            bodyBytes = bodyHeap.AsSpan(0, bodyLength);
        }

        try
        {
            // Feed body into the per-stream accumulator. RFC 7540 §6.1
            // permits an H2 DATA frame to carry an arbitrary slice of the
            // stream's byte sequence: that slice MAY contain a partial
            // LPM, exactly one complete LPM, or multiple complete LPMs
            // back-to-back (writer-side coalescing — common when peers
            // batch small messages, and explicitly allowed by gRFC G3).
            //
            // We loop, consuming as much of <c>bodyBytes</c> as
            // <see cref="FeedAccumulator"/> can. Each completion produces
            // one logical internal Message frame. The first completion is
            // returned from this call; subsequent completions go into
            // <see cref="Http2DecoderState.PendingFrames"/>.
            //
            // EndStream semantics: the H2 frame's END_STREAM flag applies
            // logically to whichever Message is the LAST one this DATA
            // frame produces. To stamp EndStream correctly without
            // patching a queued entry after the fact, we hold the most
            // recent post-first completion in <c>bufferedTail</c> and
            // only enqueue it when we see another completion overtake
            // it. After the loop the still-buffered tail (if any) is the
            // true terminal Message and gets stamped with EndStream.
            //
            // Allocation profile on the dominant single-LPM-per-DATA
            // path (typical multi-frame chunked message, or a coalescing
            // peer that happened to land one LPM per frame): zero heap
            // allocations beyond the FramePayload itself. Coalesced
            // 2-LPM DATA: one Queue.Enqueue (the Queue itself is
            // amortised; lazily grown only when first used). 3+ LPMs:
            // one Enqueue per extra completion. No List or array on the
            // common paths.
            var acc = GetOrAddAcc(state, streamId);
            FramePayload? firstCompleted = null;
            FramePayload? bufferedTail = null;
            var remaining = bodyBytes;
            var ringCommitted = false;

            try
            {
                while (remaining.Length > 0)
                {
                    var (completed, consumed) = FeedAccumulator(acc, remaining, streamId, state.OnMessageStart);
                    if (consumed == 0)
                    {
                        // Defensive: FeedAccumulator made no progress on a
                        // non-empty input. Should not happen given the logic
                        // above (Phase 1/2 always consumes at least one byte
                        // when src is non-empty), but break to avoid infinite
                        // loop in case of future regressions.
                        break;
                    }
                    remaining = remaining.Slice(consumed);

                    if (completed is { } payload)
                    {
                        if (firstCompleted == null)
                        {
                            firstCompleted = payload;
                        }
                        else if (bufferedTail == null)
                        {
                            bufferedTail = payload;
                        }
                        else
                        {
                            // bufferedTail is no longer the terminal Message —
                            // a newer completion has arrived. Flush the old
                            // tail to the queue WITHOUT EndStream (that flag
                            // belongs to whoever ends up final) and adopt the
                            // new payload as the new buffered tail.
                            try
                            {
                                EnqueuePendingFrame(state,
                                    new FrameHeader(FrameType.Message, streamId,
                                        (uint)bufferedTail.Value.Length, 0),
                                    bufferedTail.Value);
                            }
                            catch
                            {
                                payload.Release();
                                throw;
                            }
                            bufferedTail = payload;
                        }
                    }
                }

                if (bufferedTail != null && state.PendingFrames.Count >= MaxPendingSyntheticFrames)
                {
                    throw new InvalidDataException(
                        $"H2 DATA frame produced more than {MaxPendingSyntheticFrames + 1} logical gRPC messages");
                }

                // Commit the ring read regardless — we've materialised everything we need.
                ring.CommitReadRaw(baseCommitReadIdx, totalBytes);
                ringCommitted = true;
            }
            finally
            {
                if (!ringCommitted)
                {
                    // FeedAccumulator (or any other inner step) threw. We
                    // already advanced <c>_pendingReadIdx</c> by
                    // <c>payloadLen</c> via the <see cref="ShmRing.ReserveRead"/>
                    // call above, but never published the matching
                    // <see cref="ShmRing.CommitReadRaw"/> on the shared
                    // <c>header.ReadIdx</c>. Without this defensive
                    // commit, the cross-process writer would see ring
                    // capacity skewed by the unconsumed (from its view)
                    // bytes for the rest of the connection's life — and
                    // any future ReadFramePayload retry would re-read
                    // the same bytes from <c>_pendingReadIdx</c>. Even
                    // though InvalidDataException currently tears the
                    // connection down (so the leak is bounded), defense
                    // in depth: keep the two indices in sync at all
                    // exit points.
                    try { ring.CommitReadRaw(baseCommitReadIdx, totalBytes); }
                    catch { /* swallow during exception unwind */ }

                    // Release any locally-held completions to return their
                    // pooled buffers and (if speculative — currently never,
                    // since FeedAccumulator only emits FromPooled) drop
                    // their SpeculativeReservedBytes increment.
                    //
                    // Also drain <see cref="Http2DecoderState.PendingFrames"/>:
                    // any exception that bubbles out here is connection-fatal
                    // (FrameReaderLoopAsync's outer catch tears the connection
                    // down on InvalidDataException), so no future
                    // ReadFramePayload call will dequeue these frames. Without
                    // <see cref="ReleasePendingFrames"/> their pooled buffers
                    // would leak until GC. Because no future dequeue is
                    // possible, there is no use-after-free risk in releasing
                    // them here.
                    firstCompleted?.Release();
                    bufferedTail?.Release();
                    ReleasePendingFrames(state);
                }
            }

            if (firstCompleted is { } first)
            {
                // Stamp EndStream on the LAST surfaced Message when the
                // wire frame's H2 END_STREAM was set; everything before
                // it gets plain Message flags. This is the canonical
                // gRPC mapping: H2 END_STREAM marks the END of the
                // stream's byte sequence, and the LAST LPM message in
                // that sequence is the one carrying call termination
                // semantics.
                if (bufferedTail is { } tail)
                {
                    // Two or more completions: <c>first</c> goes back as
                    // the call's response, <c>tail</c> is the genuine
                    // terminal Message and rides the EndStream flag.
                    var tailFlags = endStream ? MessageFlags.EndStream : (byte)0;
                    EnqueuePendingFrame(state,
                        new FrameHeader(FrameType.Message, streamId,
                            (uint)tail.Length, tailFlags),
                        tail);
                    var firstHdr = new FrameHeader(FrameType.Message, streamId,
                        (uint)first.Length, 0);

                    if (endStream)
                    {
                        state.StreamsWithInitialHeaders.Remove(streamId);
                        if (RemoveAcc(state, streamId, out var doneAcc))
                        {
                            doneAcc!.Reset();
                        }
                    }
                    return (firstHdr, first);
                }

                // Single completion (the dominant case for our own
                // writer and for most multi-frame chunked paths): stamp
                // EndStream directly onto <c>first</c>. Zero heap
                // allocations beyond the FramePayload itself.
                var msgFlags = endStream ? MessageFlags.EndStream : (byte)0;
                var hdr = new FrameHeader(FrameType.Message, streamId,
                    (uint)first.Length, msgFlags);
                if (endStream)
                {
                    state.StreamsWithInitialHeaders.Remove(streamId);
                    if (RemoveAcc(state, streamId, out var doneAcc))
                    {
                        doneAcc!.Reset();
                    }
                }
                return (hdr, first);
            }

            // No complete message yet. If END_STREAM was set without finishing
            // the LPM, that's a protocol error.
            if (endStream)
            {
                if (RemoveAcc(state, streamId, out var orphan))
                {
                    orphan!.Reset();
                }
                state.StreamsWithInitialHeaders.Remove(streamId);
                throw new InvalidDataException(
                    $"H2 stream {streamId} ended mid-LPM (accumulator pos={acc.Pos}, expected={acc.ExpectedTotal})");
            }
            return null; // outer loop will read next frame
        }
        finally
        {
            if (bodyHeap != null)
            {
                ArrayPool<byte>.Shared.Return(bodyHeap);
            }
        }
    }

    /// <summary>
    /// Emits the next per-chunk synthetic Message frame for a multi-frame
    /// chain-ZC LPM in progress on <paramref name="acc"/>. Each call
    /// consumes exactly one H2 DATA frame and surfaces one internal
    /// Message frame whose payload is either a ring memory speculative
    /// view (the common path) or a pooled copy (only when the ring
    /// reservation wraps the buffer boundary). The CommitReadRaw call
    /// goes through the deferred path (<see cref="ShmRing"/>'s
    /// <c>_zcActive</c> is still true) so shared <c>header.ReadIdx</c>
    /// remains frozen until the final chunk's Release publishes the
    /// accumulated target.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Wrap-around (mixed-mode chain):</b> when this frame's ring
    /// reservation wraps the buffer boundary
    /// (<c>payloadReservation.Second</c> non-empty), copy the body into a
    /// pooled buffer instead of holding the ring slice. SpeculativeReservedBytes
    /// is NOT incremented for the copied frame (no ring memory held), but
    /// the chain anchor stays open via <see cref="ShmRing.IsChainOpen"/>
    /// so earlier ZC frames' Release calls cannot fire EndZcReservation
    /// prematurely. On the final frame's <see cref="ShmRing.CloseZcChain"/>,
    /// the defensive <c>(SpecReserved==0 &amp;&amp; IsZcChainActive)</c>
    /// check fires EndZcReservation if all ZC frames were already
    /// released before close.
    /// </para>
    /// <para>
    /// <b>Over-run protection:</b> if a peer sends a DATA frame whose
    /// body exceeds the remaining chain bytes (writer-side coalesced
    /// "next LPM tail in same frame"), throw InvalidDataException.
    /// Our writer never coalesces inside a chain; this is purely a
    /// defensive guard against a misbehaving peer.
    /// </para>
    /// </remarks>
    private static (FrameHeader Header, FramePayload Payload) EmitChainContinuation(
        ShmRing ring,
        Http2DecoderState state,
        uint streamId,
        LpmAccumulator acc,
        ulong baseCommitReadIdx,
        ReadReservation payloadReservation,
        int bodyOffset,
        int bodyLength,
        int totalBytes,
        bool endStream)
    {
        // Peer over-run check: a chain frame must carry AT MOST the
        // remaining body bytes. Coalesced-tail-after-chain (an extra
        // LPM packed into the same wire frame after the current chain's
        // final bytes) is legal per RFC 7540 §6.1 but our own writer
        // never produces it. Tear down the chain to prevent corruption;
        // InvalidDataException is connection-fatal per FrameReaderLoopAsync.
        if (bodyLength > acc.ChainRemaining)
        {
            // Publish the ring read so the cross-process writer sees
            // these bytes consumed; the deferred commit path advances
            // _deferredReadIdxTarget which CloseZcChain will publish.
            // SpeculativeReservedBytes is NOT incremented (no ring memory
            // is being held — we're abandoning the frame), so CloseZcChain's
            // defensive (SpecReserved==0 && _zcActive) check fires
            // EndZcReservation and ReadIdx advances past everything.
            ring.CommitReadRaw(baseCommitReadIdx, totalBytes);
            ring.CloseZcChain();
            RemoveAcc(state, streamId, out _);
            throw new InvalidDataException(
                $"H2 DATA frame on chain-ZC stream {streamId} carries " +
                $"{bodyLength} bytes but only {acc.ChainRemaining} bytes " +
                "remain in the LPM body. Coalesced-tail-after-chain is " +
                "not supported on the chain-ZC path.");
        }

        acc.ChainRemaining -= bodyLength;
        var isLastChunk = acc.ChainRemaining == 0;

        // Protocol error guard (2026-06-01 PR final review): an H2 DATA
        // frame carrying END_STREAM mid-chain is illegal per gRPC framing
        // (the LPM is incomplete). Our own writer never produces this,
        // but defend defensively: tear down the chain and throw. Without
        // this guard the END_STREAM flag would be silently dropped (only
        // the isLastChunk branch consults endStream below), the anchor
        // would never close, and the ring would permanently shrink as
        // _chainOpen/_zcActive leak forever.
        if (endStream && !isLastChunk)
        {
            // CommitReadRaw publishes this frame's bytes into the deferred
            // target; SpecReserved is NOT incremented (we're abandoning
            // the body), so CloseZcChain's defensive
            // (SpecReserved==0 && _zcActive) check fires EndZcReservation.
            ring.CommitReadRaw(baseCommitReadIdx, totalBytes);
            ring.CloseZcChain();
            RemoveAcc(state, streamId, out _);
            throw new InvalidDataException(
                $"H2 stream {streamId} ended mid-chain-ZC LPM " +
                $"({acc.ChainRemaining} body bytes still expected, this " +
                $"frame carried {bodyLength}). END_STREAM mid-LPM is " +
                "a protocol error.");
        }

        var flags = isLastChunk
            ? (endStream ? MessageFlags.EndStream : (byte)0)
            : MessageFlags.More;
        var hdr = new FrameHeader(FrameType.Message, streamId, (uint)bodyLength, flags);

        FramePayload payload;
        if (payloadReservation.Second.IsEmpty)
        {
            // Common path: contiguous ring slice → keep as speculative ZC.
            // SpeculativeReservedBytes is incremented BEFORE CommitReadRaw
            // to match the single-frame ZC ordering (single-frame ZC at
            // line ~531 also increments before the speculative payload is
            // returned; FramePayload.Release decrements via Interlocked).
            Interlocked.Add(ref ring.SpeculativeReservedBytes, totalBytes);
            ring.CommitReadRaw(baseCommitReadIdx, totalBytes); // deferred (_zcActive)
            payload = FramePayload.FromRingMemorySpeculative(
                payloadReservation.First.Slice(bodyOffset, bodyLength),
                ring, totalBytes);
        }
        else
        {
            // Wrap-around: copy this frame to a pooled buffer. Anchor
            // stays open via IsChainOpen; CommitReadRaw still goes
            // through the deferred path because _zcActive is true.
            var pooled = ArrayPool<byte>.Shared.Rent(bodyLength);
            CopyFromReservationSlice(payloadReservation, bodyOffset, pooled.AsSpan(0, bodyLength));
            ring.CommitReadRaw(baseCommitReadIdx, totalBytes); // deferred
            payload = FramePayload.FromPooled(pooled, bodyLength);
        }

        if (isLastChunk)
        {
            // Close the chain marker so the in-flight chunks' Release
            // calls can fire EndZcReservation via the
            // (remaining == 0 && !IsChainOpen) gate in
            // FramePayload.Release. CloseZcChain itself fires
            // EndZcReservation defensively if SpeculativeReservedBytes
            // is already 0 at close time (e.g., all ZC chunks released
            // and the final chunk is the wrap-copy variant).
            ring.CloseZcChain();
            if (endStream)
            {
                state.StreamsWithInitialHeaders.Remove(streamId);
            }
            // Drop per-stream chain state. In chain mode acc.Buffer is
            // always null so Reset() is a cheap field reset.
            if (RemoveAcc(state, streamId, out var doneAcc))
            {
                doneAcc!.Reset();
            }
        }

        return (hdr, payload);
    }

    /// <summary>
    /// Copies as many bytes from <paramref name="body"/> into <paramref name="acc"/>
    /// as needed to either (a) complete one in-progress LPM message or
    /// (b) reach end-of-input. Returns the completed message (or
    /// <c>null</c> if more bytes are still needed) and the number of
    /// bytes consumed from <paramref name="body"/>.
    /// </summary>
    /// <remarks>
    /// One call advances the accumulator by AT MOST one LPM message. If
    /// <paramref name="body"/> contains additional LPM bytes after the
    /// first completion, the caller is expected to invoke this method
    /// again with the residual span (see
    /// <see cref="TryReadDataFrame"/>'s consumption loop). This split
    /// keeps the per-LPM logic simple and lets the caller decide how to
    /// surface multiple completed messages (head returned to the upper
    /// layer; rest stashed in <see cref="Http2DecoderState.PendingFrames"/>).
    /// </remarks>
    private static (FramePayload? Completed, int Consumed) FeedAccumulator(LpmAccumulator acc, ReadOnlySpan<byte> body, uint streamId = 0, Action<uint, uint>? onMessageStart = null)
    {
        var src = body;
        var consumed = 0;

        // Phase 1: complete the 5-byte LPM header if necessary.
        if (acc.HeaderBytesSeen < 5)
        {
            var need = 5 - acc.HeaderBytesSeen;
            var take = Math.Min(need, src.Length);
            src.Slice(0, take).CopyTo(acc.HeaderBuf.AsSpan(acc.HeaderBytesSeen));
            acc.HeaderBytesSeen += take;
            src = src.Slice(take);
            consumed += take;

            if (acc.HeaderBytesSeen < 5)
            {
                return (null, consumed); // header still partial
            }

            var bodyLen = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                acc.HeaderBuf.AsSpan(1, 4));
            if (bodyLen < 0)
            {
                throw new InvalidDataException("Invalid gRPC LPM body length (negative)");
            }
            // Defend against a peer declaring a giant body length and
            // forcing the receiver to allocate hundreds of megabytes from
            // ArrayPool. The cap mirrors what gRPC implementations use as
            // the default per-message receive ceiling.
            if (bodyLen > MaxLpmBodyLength)
            {
                throw new InvalidDataException(
                    $"gRPC LPM body length {bodyLen} exceeds receiver maximum {MaxLpmBodyLength}");
            }
            acc.ExpectedTotal = 5 + bodyLen;
            acc.Buffer = ArrayPool<byte>.Shared.Rent(acc.ExpectedTotal == 0 ? 1 : acc.ExpectedTotal);
            // Stamp header at start of buffer.
            acc.HeaderBuf.AsSpan(0, 5).CopyTo(acc.Buffer);
            acc.Pos = 5;

            // Fire the parse-time pre-credit hook. The 5-byte LPM header
            // just transitioned ExpectedTotal: 0 → N, so the receiver
            // can decide BEFORE any body bytes flow whether the announced
            // message needs a stream-level WINDOW_UPDATE pre-credit to
            // admit the rest of the LPM. This is the gRFC SHM v3.4+ MUST
            // (see shm-rfc/A-shared-memory-transport.md §"Stream-level
            // pre-credit at LPM parse"). lpmSize includes the 5-byte
            // header so the caller's MaybeAdjustAdditive cap math is
            // self-consistent with the on-the-wire bytes actually
            // counted by inFlow.OnData per DATA frame.
            //
            // streamId == 0 means the legacy caller pre-LPM-hook signature;
            // skip the hook in that case. All real callsites pass the
            // streamId from the H2 DATA frame header.
            if (streamId != 0 && onMessageStart != null && acc.ExpectedTotal > 0)
            {
                onMessageStart(streamId, (uint)acc.ExpectedTotal);
            }
        }

        // Phase 2: copy body bytes into accumulator buffer. Stop at this
        // LPM's expected total — any leftover belongs to the next LPM
        // and is surfaced via <c>consumed</c> so the caller can re-invoke
        // with the residual span.
        if (src.Length > 0)
        {
            var room = acc.ExpectedTotal - acc.Pos;
            var take = Math.Min(room, src.Length);
            src.Slice(0, take).CopyTo(acc.Buffer.AsSpan(acc.Pos));
            acc.Pos += take;
            consumed += take;
        }

        if (acc.Pos == acc.ExpectedTotal)
        {
            // Hand off the buffer to a FramePayload (pool ownership transfers).
            var buf = acc.Buffer!;
            var len = acc.ExpectedTotal;
            acc.Buffer = null;
            acc.Pos = 0;
            acc.ExpectedTotal = 0;
            acc.HeaderBytesSeen = 0;
            return (FramePayload.FromPooled(buf, len), consumed);
        }
        return (null, consumed);
    }

    private static (FrameHeader Header, FramePayload Payload) ReadHeadersFrame(
        ShmRing ring, ulong baseCommitReadIdx, uint streamId, byte h2Flags, int payloadLen,
        Http2DecoderState state, CancellationToken ct)
    {
        // BUG-FIX (round-10 GPT-5.5 #7): RFC 7540 §6.2 — HEADERS frames
        // MUST be associated with a stream. Receiving HEADERS on stream
        // 0 is a PROTOCOL_ERROR connection error. Previously this was
        // silently routed to EmitDecodedHeaders which would have driven
        // header state on a non-existent stream.
        if (streamId == 0)
        {
            if (payloadLen > 0)
            {
                var _ = ring.ReserveRead(payloadLen, ct);
            }
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
            throw new InvalidDataException(
                $"H2 HEADERS frame on connection stream 0 (RFC 7540 §6.2 PROTOCOL_ERROR)");
        }

        var endStream = (h2Flags & Http2Flags.EndStream) != 0;
        var endHeaders = (h2Flags & Http2Flags.EndHeaders) != 0;
        var padded = (h2Flags & Http2Flags.Padded) != 0;
        var hasPriority = (h2Flags & Http2Flags.Priority) != 0;

        // Empty HEADERS w/ END_HEADERS: trivial path.
        if (endHeaders && payloadLen == 0)
        {
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size);
            return EmitDecodedHeaders(ReadOnlySpan<byte>.Empty, streamId, state, endStream);
        }

        // Materialise the first fragment, accounting for PADDED + PRIORITY
        // prefixes per RFC 7540 §6.2 (these flags appear ONLY on the first
        // HEADERS frame; CONTINUATION carries no flags except END_HEADERS).
        var firstFragment = ArrayPool<byte>.Shared.Rent(payloadLen == 0 ? 1 : payloadLen);
        int firstHeaderBlockOffset;
        int firstHeaderBlockLength;
        try
        {
            if (payloadLen > 0)
            {
                var payloadReservation = ring.ReserveRead(payloadLen, ct);
                CopyFromReservationSlice(payloadReservation, 0, firstFragment.AsSpan(0, payloadLen));
            }
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);

            firstHeaderBlockOffset = 0;
            firstHeaderBlockLength = payloadLen;
            if (padded)
            {
                if (payloadLen < 1)
                {
                    throw new InvalidDataException("HEADERS PADDED flag with empty payload");
                }
                int padLen = firstFragment[0];
                firstHeaderBlockOffset += 1;
                firstHeaderBlockLength = payloadLen - 1 - padLen;
            }
            if (hasPriority)
            {
                firstHeaderBlockOffset += 5;
                firstHeaderBlockLength -= 5;
            }
            if (firstHeaderBlockLength < 0)
            {
                throw new InvalidDataException("HEADERS frame: invalid pad/priority prefix");
            }

            // Fast path: single HEADERS w/ END_HEADERS (the dominant case
            // for gRPC traffic — SETTINGS_MAX_FRAME_SIZE = 16 MiB makes
            // CONTINUATION almost never necessary).
            if (endHeaders)
            {
                // BUG-FIX (round-10 GPT-5.5 #9): enforce MaxHeaderListSize
                // here too. The CONTINUATION path already checks the
                // cumulative payload (see line ~984), but the single-
                // HEADERS fast path skipped the check. A peer could send
                // one oversized HEADERS frame up to the per-frame cap
                // (MaxH2FramePayloadSize) and force us to HPACK-decode
                // and materialise the entire block despite our declared
                // MaxHeaderListSize budget. Reject early.
                if (firstHeaderBlockLength > MaxHeaderListSize)
                {
                    throw new InvalidDataException(
                        $"H2 HEADERS payload {firstHeaderBlockLength} bytes exceeds " +
                        $"MaxHeaderListSize ({MaxHeaderListSize})");
                }
                var single = firstFragment.AsSpan(firstHeaderBlockOffset, firstHeaderBlockLength);
                return EmitDecodedHeaders(single, streamId, state, endStream);
            }

            // Slow path: HEADERS without END_HEADERS — reassemble the
            // header block by reading CONTINUATION frames per RFC 7540
            // §6.10. CONTINUATION constraints we enforce:
            //   - frame type MUST be Continuation (PROTOCOL_ERROR otherwise)
            //   - streamId MUST match the originating HEADERS stream
            //   - cumulative payload bounded by MaxHeaderListSize
            //   - any non-CONTINUATION frame from the peer mid-sequence
            //     is a PROTOCOL_ERROR (peer cannot interleave other
            //     frames between HEADERS and the terminal CONTINUATION)
            return ReadHeadersWithContinuations(
                ring, firstFragment, firstHeaderBlockOffset, firstHeaderBlockLength,
                streamId, endStream, state, ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(firstFragment);
        }
    }

    private static (FrameHeader Header, FramePayload Payload) ReadHeadersWithContinuations(
        ShmRing ring, byte[] firstFragment, int firstOff, int firstLen,
        uint streamId, bool endStream, Http2DecoderState state, CancellationToken ct)
    {
        // Accumulate the HPACK header block into a contiguous pooled
        // buffer. Start it at 4× the first fragment's size to avoid
        // growth in the typical 2-3 frame case; cap at MaxHeaderListSize.
        var initialCap = Math.Max(firstLen * 4, 1024);
        if (initialCap > MaxHeaderListSize) initialCap = MaxHeaderListSize;
        var assembled = ArrayPool<byte>.Shared.Rent(initialCap);
        int assembledLen = 0;
        try
        {
            firstFragment.AsSpan(firstOff, firstLen).CopyTo(assembled.AsSpan(0, firstLen));
            assembledLen = firstLen;

            Span<byte> hb = stackalloc byte[Http2FrameHeader.Size];
            while (true)
            {
                var headerReservation = ring.ReserveRead(Http2FrameHeader.Size, ct);
                var contBaseIdx = headerReservation.CommitReadIdx;
                CopyFromReservation(headerReservation, hb);
                var (contType, contFlags, contPayloadLen, contStreamId) = Http2FrameHeader.Decode(hb);

                if (contPayloadLen > MaxH2FramePayloadSize)
                {
                    ring.CommitReadRaw(contBaseIdx, Http2FrameHeader.Size);
                    throw new InvalidDataException(
                        $"H2 frame payload length {contPayloadLen} exceeds maximum {MaxH2FramePayloadSize}");
                }
                if (contType != Http2FrameType.Continuation)
                {
                    // PROTOCOL_ERROR: peer cannot interleave non-CONTINUATION
                    // frames between HEADERS and the terminal CONTINUATION.
                    if (contPayloadLen > 0)
                    {
                        var _ = ring.ReserveRead(contPayloadLen, ct);
                    }
                    ring.CommitReadRaw(contBaseIdx, Http2FrameHeader.Size + contPayloadLen);
                    throw new InvalidDataException(
                        $"H2 expected CONTINUATION (type=9) for stream {streamId}, got type=0x{(byte)contType:X2}");
                }
                if (contStreamId != streamId)
                {
                    if (contPayloadLen > 0)
                    {
                        var _ = ring.ReserveRead(contPayloadLen, ct);
                    }
                    ring.CommitReadRaw(contBaseIdx, Http2FrameHeader.Size + contPayloadLen);
                    throw new InvalidDataException(
                        $"H2 CONTINUATION streamId mismatch (expected {streamId}, got {contStreamId})");
                }

                // Cumulative header-list size check BEFORE materialising.
                if (assembledLen + contPayloadLen > MaxHeaderListSize)
                {
                    if (contPayloadLen > 0)
                    {
                        var _ = ring.ReserveRead(contPayloadLen, ct);
                    }
                    ring.CommitReadRaw(contBaseIdx, Http2FrameHeader.Size + contPayloadLen);
                    throw new InvalidDataException(
                        $"H2 HEADERS+CONTINUATION cumulative payload exceeds {MaxHeaderListSize} bytes");
                }

                // Grow the assembled buffer if needed.
                if (assembledLen + contPayloadLen > assembled.Length)
                {
                    var newSize = Math.Max(assembled.Length * 2, assembledLen + contPayloadLen);
                    if (newSize > MaxHeaderListSize) newSize = MaxHeaderListSize;
                    var bigger = ArrayPool<byte>.Shared.Rent(newSize);
                    assembled.AsSpan(0, assembledLen).CopyTo(bigger);
                    ArrayPool<byte>.Shared.Return(assembled);
                    assembled = bigger;
                }

                if (contPayloadLen > 0)
                {
                    var contReservation = ring.ReserveRead(contPayloadLen, ct);
                    CopyFromReservationSlice(
                        contReservation, 0,
                        assembled.AsSpan(assembledLen, contPayloadLen));
                }
                ring.CommitReadRaw(contBaseIdx, Http2FrameHeader.Size + contPayloadLen);
                assembledLen += contPayloadLen;

                if ((contFlags & Http2Flags.EndHeaders) != 0)
                {
                    return EmitDecodedHeaders(
                        assembled.AsSpan(0, assembledLen), streamId, state, endStream);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(assembled);
        }
    }

    private static (FrameHeader Header, FramePayload Payload) EmitDecodedHeaders(
        ReadOnlySpan<byte> headerBlock, uint streamId, Http2DecoderState state, bool endStream)
    {
        var hasInitial = state.StreamsWithInitialHeaders.ContainsKey(streamId);

        FrameType internalType;
        byte internalFlags;

        if (!hasInitial)
        {
            // First HEADERS on this stream.
            //
            // Two cases:
            //  1) Normal: HEADERS without END_STREAM → initial response
            //     headers; subsequent DATA frames carry the body and a
            //     follow-up HEADERS w/ END_STREAM carries the trailers.
            //  2) Trailers-only (gRFC G3): HEADERS w/ END_STREAM as the
            //     SOLE frame on this stream — server is returning a status
            //     without a response body (e.g. NotFound, Unauthenticated).
            //     The single HEADERS block carries response pseudo-headers
            //     (`:status`, `content-type`, …) AND gRPC trailing fields
            //     (`grpc-status`, `grpc-message`, custom trailer metadata).
            //     The upper-layer state machine expects a Headers frame
            //     followed by a Trailers frame to complete the call; if we
            //     surface only one frame the call hangs forever waiting for
            //     trailers that never arrive.
            //
            // For case 2 we split the HPACK block into a Headers half and a
            // Trailers half (see <see cref="HpackHeadersAdapter.DecodeTrailersOnly"/>),
            // emit the Headers immediately, and stash the Trailers in
            // <see cref="Http2DecoderState.PendingFrameHeader"/>. The next
            // call to <see cref="ReadFramePayloadInternal"/> returns the
            // stash before touching the ring, preserving FIFO order.
            //
            // Round-7 PR-B: pass the decoded HeadersV1 / TrailersV1 OBJECT
            // straight through via <see cref="FramePayload.FromDecodedHeader"/>
            // instead of re-serializing it to bytes for the upper layer to
            // re-parse. Eliminates ~50% of the read-side header path cost
            // (see HeaderPathProfileTests measurements).
            if (endStream)
            {
                var (headersV1, trailersV1) = HpackHeadersAdapter.DecodeTrailersOnly(headerBlock);

                EnqueuePendingFrame(state,
                    new FrameHeader(FrameType.Trailers, streamId, 0, TrailersFlags.EndStream),
                    FramePayload.FromDecodedHeader(trailersV1));

                // No persistent stream state to retain: trailers-only means
                // the stream ended in this single HEADERS frame; any further
                // wire frames on this stream id (none expected from a well-
                // behaved peer) get treated as a fresh stream.
                var hHdr = new FrameHeader(
                    FrameType.Headers, streamId, 0, HeadersFlags.Initial);
                return (hHdr, FramePayload.FromDecodedHeader(headersV1));
            }

            var v1 = HpackHeadersAdapter.DecodeHeaders(headerBlock);
            internalType = FrameType.Headers;
            internalFlags = (byte)HeadersFlags.Initial;
            state.StreamsWithInitialHeaders[streamId] = 1;
            var hdrFirst = new FrameHeader(internalType, streamId, 0, internalFlags);
            return (hdrFirst, FramePayload.FromDecodedHeader(v1));
        }
        else
        {
            // Subsequent HEADERS → trailers per gRPC over HTTP/2.
            // BUG-FIX (round-10 GPT-5.5 #8): trailers MUST carry
            // END_STREAM. Per RFC 7540 §8.1 + the gRPC over HTTP/2
            // wire spec, the trailing metadata HEADERS block MUST set
            // the END_STREAM flag (it is the final frame of the stream).
            // Without this guard a malformed/malicious peer could send
            // "trailers" without END_STREAM and the upper layer would
            // unconditionally mark _halfCloseReceived = true, complete
            // the inbound channel, and remove the stream — leaving any
            // subsequent peer frames routed to nothing (silent data
            // loss + a spurious "successful" RPC completion).
            if (!endStream)
            {
                throw new InvalidDataException(
                    $"H2 trailers HEADERS on stream {streamId} missing END_STREAM " +
                    "(RFC 7540 §8.1 + gRPC over HTTP/2: trailing metadata must end the stream)");
            }
            var v1 = HpackHeadersAdapter.DecodeTrailers(headerBlock);
            internalType = FrameType.Trailers;
            internalFlags = TrailersFlags.EndStream;
            state.StreamsWithInitialHeaders.Remove(streamId);
            var hdrSub = new FrameHeader(internalType, streamId, 0, internalFlags);
            return (hdrSub, FramePayload.FromDecodedHeader(v1));
        }
    }

    private static (FrameHeader Header, FramePayload Payload) ReadRstStreamFrame(
        ShmRing ring, ulong baseCommitReadIdx, uint streamId, int payloadLen, CancellationToken ct)
    {
        // RFC 7540 §6.4: RST_STREAM
        //   - payload length MUST be exactly 4 (treat other lengths as
        //     FRAME_SIZE_ERROR connection error)
        //   - stream identifier MUST be non-zero (treat zero as
        //     PROTOCOL_ERROR connection error)
        // We must drain the full malformed payload before throwing so the
        // ring read pointer stays in sync (any subsequent read would
        // otherwise mis-interpret leftover bytes as a new frame header).
        if (payloadLen != 4 || streamId == 0)
        {
            if (payloadLen > 0)
            {
                var _ = ring.ReserveRead(payloadLen, ct);
            }
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
            throw new InvalidDataException(
                $"H2 RST_STREAM malformed (streamId={streamId}, payloadLen={payloadLen}; require streamId != 0 && payloadLen == 4)");
        }

        // Drain payload (4 bytes error code; we don't propagate the code).
        var _drain = ring.ReserveRead(payloadLen, ct);
        ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);

        // Clean up all per-stream state. A pending LPM accumulator may still
        // hold a pooled buffer; calling Reset() returns it to ArrayPool to
        // prevent a buffer leak when the peer cancels mid-message.
        var state = GetState(ring);
        state.StreamsWithInitialHeaders.Remove(streamId);
        if (RemoveAcc(state, streamId, out var pendingAcc))
        {
            // Chain-ZC leak guard (2026-06-01): if the canceled stream had
            // a chain anchor open, close it now so subsequent Releases on
            // already-surfaced ZC frames can publish ReadIdx via the
            // FramePayload.Release gate. Without this, _chainOpen stays
            // true after the cancel, EndZcReservation never fires, ring
            // permanently shrinks from the cross-process writer's view.
            if (pendingAcc!.Mode == AccMode.ChainZcInProgress)
            {
                ring.CloseZcChain();
            }
            pendingAcc!.Reset();
        }

        var hdr = new FrameHeader(FrameType.Cancel, streamId, 0, 0);
        return (hdr, FramePayload.Empty);
    }

    private static void HandleSettingsFrame(ShmRing ring, ulong baseCommitReadIdx,
        byte h2Flags, uint streamId, int payloadLen, CancellationToken ct)
    {
        // RFC 7540 §6.5: SETTINGS frame
        //   - stream identifier MUST be 0 (treat non-zero as PROTOCOL_ERROR)
        //   - on non-ACK: payload length MUST be a multiple of 6
        //     (treat otherwise as FRAME_SIZE_ERROR)
        //   - on ACK: payload length MUST be 0 (already enforced below)
        if (streamId != 0)
        {
            if (payloadLen > 0)
            {
                var _ = ring.ReserveRead(payloadLen, ct);
            }
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
            throw new InvalidDataException(
                $"H2 SETTINGS frame must have streamId=0 (got {streamId})");
        }
        if ((h2Flags & Http2Flags.Ack) != 0)
        {
            // RFC 7540 §6.5.3 / RFC 9113 §6.5.3: a SETTINGS frame with the
            // ACK flag set MUST have a payload length of zero. A peer that
            // sends ACK with a non-zero payload is malformed; the spec
            // requires treating this as a connection error of type
            // FRAME_SIZE_ERROR.
            //
            // We must consume the bogus payload bytes from the ring BEFORE
            // throwing, otherwise the next ReadFramePayload call would
            // interpret those bytes as the start of a new H2 frame header
            // and the ring read pointer would desync (the connection would
            // then dump cryptic "Unknown H2 frame type" errors and either
            // hang or terminate). Committing all 9 + payloadLen bytes
            // matches the spec's "fully consume the frame, then fail the
            // connection" expectation.
            if (payloadLen != 0)
            {
                if (payloadLen > 0)
                {
                    var _ = ring.ReserveRead(payloadLen, ct);
                }
                ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
                throw new InvalidDataException(
                    $"H2 SETTINGS ACK frame must have empty payload (got {payloadLen} bytes)");
            }
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size);
            return;
        }

        // RFC 7540 §6.5: non-ACK SETTINGS payload length MUST be a
        // multiple of 6 (each setting is 2-byte id + 4-byte value).
        if (payloadLen % 6 != 0)
        {
            if (payloadLen > 0)
            {
                var _ = ring.ReserveRead(payloadLen, ct);
            }
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
            throw new InvalidDataException(
                $"H2 SETTINGS frame payload length {payloadLen} is not a multiple of 6");
        }

        // Drain settings payload (we don't dynamically apply peer settings;
        // we negotiate via the control segment).
        if (payloadLen > 0)
        {
            var _ = ring.ReserveRead(payloadLen, ct);
        }
        ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);

        // SETTINGS ACK is intentionally NOT emitted from this read path.
        //
        // The <paramref name="ring"/> here is the connection's RxRing (the
        // ring this side reads from); the peer is its sole writer. Issuing
        // <see cref="WriteSettings"/> on it would violate the SPSC ring
        // invariant and corrupt the peer's in-flight writes.
        //
        // The transport's wire-format negotiation happens via the control
        // segment (see <c>ShmControlHandler.HandleConnectAsync</c>); peers
        // do not gate behaviour on receiving an HTTP/2 SETTINGS ACK over
        // the data segment, so dropping the ACK here is safe. If a future
        // peer does require the ACK, route the write through this side's
        // <c>ShmFrameWriter</c> on the matching TxRing (would need to map
        // RxRing → ShmConnection); leaving as a no-op until needed.
    }

    private static (FrameHeader Header, FramePayload Payload) ReadPingFrame(
        ShmRing ring, ulong baseCommitReadIdx, byte h2Flags, uint streamId, int payloadLen, CancellationToken ct)
    {
        // RFC 7540 §6.7: PING
        //   - stream identifier MUST be 0 (PROTOCOL_ERROR otherwise)
        //   - payload length MUST be exactly 8 (FRAME_SIZE_ERROR otherwise)
        if (streamId != 0 || payloadLen != 8)
        {
            if (payloadLen > 0)
            {
                var _ = ring.ReserveRead(payloadLen, ct);
            }
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
            throw new InvalidDataException(
                $"H2 PING malformed (streamId={streamId}, payloadLen={payloadLen}; require streamId == 0 && payloadLen == 8)");
        }

        var payloadReservation = ring.ReserveRead(payloadLen, ct);
        var pooled = ArrayPool<byte>.Shared.Rent(payloadLen);
        CopyFromReservationSlice(payloadReservation, 0, pooled.AsSpan(0, payloadLen));
        ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);

        var ack = (h2Flags & Http2Flags.Ack) != 0;
        var hdr = new FrameHeader(ack ? FrameType.Pong : FrameType.Ping, 0, (uint)payloadLen, 0);
        return (hdr, FramePayload.FromPooled(pooled, payloadLen));
    }

    private static (FrameHeader Header, FramePayload Payload) ReadGoAwayFrame(
        ShmRing ring, ulong baseCommitReadIdx, uint streamId, int payloadLen, CancellationToken ct)
    {
        // RFC 7540 §6.8: GOAWAY
        //   - stream identifier MUST be 0 (PROTOCOL_ERROR otherwise)
        //   - payload at minimum 8 bytes: 4-byte last-stream-id + 4-byte
        //     error code + optional debug data.
        if (streamId != 0 || payloadLen < 8)
        {
            if (payloadLen > 0)
            {
                var _ = ring.ReserveRead(payloadLen, ct);
            }
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
            throw new InvalidDataException(
                $"H2 GOAWAY malformed (streamId={streamId}, payloadLen={payloadLen}; require streamId == 0 && payloadLen >= 8)");
        }

        var payloadReservation = ring.ReserveRead(payloadLen, ct);
        var pooled = ArrayPool<byte>.Shared.Rent(payloadLen);
        CopyFromReservationSlice(payloadReservation, 0, pooled.AsSpan(0, payloadLen));
        ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);

        // Internal GoAway payload is just a UTF-8 debug string. Skip the 8-byte header.
        var debugLen = payloadLen - 8;
        var debugBuf = ArrayPool<byte>.Shared.Rent(debugLen == 0 ? 1 : debugLen);
        if (debugLen > 0)
        {
            Array.Copy(pooled, 8, debugBuf, 0, debugLen);
        }
        ArrayPool<byte>.Shared.Return(pooled);

        var hdr = new FrameHeader(FrameType.GoAway, 0, (uint)debugLen, 0);
        return (hdr, FramePayload.FromPooled(debugBuf, debugLen));
    }

    private static (FrameHeader Header, FramePayload Payload) ReadWindowUpdateFrame(
        ShmRing ring, ulong baseCommitReadIdx, uint streamId, int payloadLen, CancellationToken ct)
    {
        // RFC 7540 §6.9.1: WINDOW_UPDATE payload length MUST be exactly 4.
        if (payloadLen != 4)
        {
            if (payloadLen > 0)
            {
                var _ = ring.ReserveRead(payloadLen, ct);
            }
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
            throw new InvalidDataException($"H2 WINDOW_UPDATE frame payload length {payloadLen} != 4");
        }

        var payloadReservation = ring.ReserveRead(payloadLen, ct);
        Span<byte> raw = stackalloc byte[4];
        CopyFromReservationSlice(payloadReservation, 0, raw);
        ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);

        // RFC 7540 §6.9.1: increment MUST be a non-zero 31-bit value.
        // Zero is a stream-error / connection-error PROTOCOL_ERROR (a peer
        // that emits zero-increment is malformed; silently accepting it
        // would mask the protocol violation and could mask underlying
        // peer bugs at integration time).
        var increment = BinaryPrimitives.ReadUInt32BigEndian(raw) & 0x7FFFFFFFu;
        if (increment == 0)
        {
            throw new InvalidDataException(
                "H2 WINDOW_UPDATE increment must be non-zero (RFC 7540 §6.9.1)");
        }

        // Internal payload is 4-byte little-endian increment.
        var pooled = ArrayPool<byte>.Shared.Rent(4);
        BinaryPrimitives.WriteUInt32LittleEndian(pooled.AsSpan(0, 4), increment);
        var hdr = new FrameHeader(FrameType.WindowUpdate, streamId, 4, 0);
        return (hdr, FramePayload.FromPooled(pooled, 4));
    }

    /// <summary>Copies the entire contents of a read reservation into <paramref name="destination"/>.</summary>
    private static void CopyFromReservation(ReadReservation reservation, Span<byte> destination)
    {
        var copied = 0;
        if (reservation.First.Length > 0)
        {
            var toCopy = Math.Min(reservation.First.Length, destination.Length);
            reservation.First.Span[..toCopy].CopyTo(destination);
            copied += toCopy;
        }
        if (reservation.Second.Length > 0 && copied < destination.Length)
        {
            var toCopy = Math.Min(reservation.Second.Length, destination.Length - copied);
            reservation.Second.Span[..toCopy].CopyTo(destination[copied..]);
        }
    }

    /// <summary>
    /// Copies <paramref name="length"/> bytes starting at <paramref name="srcOffset"/>
    /// of a read reservation into <paramref name="destination"/>.
    /// </summary>
    private static void CopyFromReservationSlice(ReadReservation reservation, int srcOffset, Span<byte> destination)
    {
        var firstLen = reservation.First.Length;
        var copied = 0;

        if (srcOffset < firstLen)
        {
            var available = firstLen - srcOffset;
            var toCopy = Math.Min(available, destination.Length);
            reservation.First.Span.Slice(srcOffset, toCopy).CopyTo(destination);
            copied += toCopy;
        }

        if (copied < destination.Length)
        {
            var secondOffset = Math.Max(0, srcOffset - firstLen);
            var remaining = destination.Length - copied;
            var toCopy = Math.Min(remaining, reservation.Second.Length - secondOffset);
            if (toCopy > 0)
            {
                reservation.Second.Span.Slice(secondOffset, toCopy).CopyTo(destination[copied..]);
            }
        }
    }
}
