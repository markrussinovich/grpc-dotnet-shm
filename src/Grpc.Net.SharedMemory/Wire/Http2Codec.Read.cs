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
    }

    /// <summary>Accumulates a single in-progress gRPC LPM message across multiple DATA frames.</summary>
    private sealed class LpmAccumulator
    {
        public byte[]? Buffer;          // pooled, 0..ExpectedTotal capacity
        public int Pos;                 // bytes written so far
        public int ExpectedTotal;       // 5 (header) + body length once header is parsed; 0 before that
        public int HeaderBytesSeen;     // 0..5

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
        }
    }

    private static Http2DecoderState GetState(ShmRing ring)
    {
        return s_decoderState.GetValue(ring, _ => new Http2DecoderState());
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
                    HandleSettingsFrame(ring, baseCommitReadIdx, h2Flags, payloadLen, cancellationToken);
                    continue; // Don't surface to upper layer.

                case Http2FrameType.Ping:
                    return ReadPingFrame(ring, baseCommitReadIdx, h2Flags, payloadLen, cancellationToken);

                case Http2FrameType.GoAway:
                    return ReadGoAwayFrame(ring, baseCommitReadIdx, payloadLen, cancellationToken);

                case Http2FrameType.WindowUpdate:
                    return ReadWindowUpdateFrame(ring, baseCommitReadIdx, streamId, payloadLen, cancellationToken);

                case Http2FrameType.Continuation:
                    ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
                    throw new InvalidDataException(
                        "H2 CONTINUATION frame received but not supported (peer must respect SETTINGS_MAX_FRAME_SIZE)");

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

        // === Fast path: single complete LPM message in this DATA frame, ===
        // === no accumulator state, contiguous body. Eligible for zero-copy. ===
        var hasAccumulator = TryGetAcc(state, streamId, out var existingAcc)
            && existingAcc!.Pos > 0;
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
                    // comment. H2 reader only does single-frame ZC (multi-
                    // frame H2 messages always go through the LpmAccumulator
                    // copy path), so the fused commit is always safe here.
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
                    var (completed, consumed) = FeedAccumulator(acc, remaining);
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
                            state.PendingFrames.Enqueue((
                                new FrameHeader(FrameType.Message, streamId,
                                    (uint)bufferedTail.Value.Length, 0),
                                bufferedTail.Value));
                            bufferedTail = payload;
                        }
                    }
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
                    // their SpeculativeReservedBytes increment. Do NOT
                    // release entries already in PendingFrames — those are
                    // committed to the queue contract and would be a
                    // use-after-free if a subsequent ReadFramePayload call
                    // dequeues them.
                    firstCompleted?.Release();
                    bufferedTail?.Release();
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
                    state.PendingFrames.Enqueue((
                        new FrameHeader(FrameType.Message, streamId,
                            (uint)tail.Length, tailFlags),
                        tail));
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
    private static (FramePayload? Completed, int Consumed) FeedAccumulator(LpmAccumulator acc, ReadOnlySpan<byte> body)
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
        var endStream = (h2Flags & Http2Flags.EndStream) != 0;
        var endHeaders = (h2Flags & Http2Flags.EndHeaders) != 0;
        var padded = (h2Flags & Http2Flags.Padded) != 0;
        var hasPriority = (h2Flags & Http2Flags.Priority) != 0;

        if (!endHeaders)
        {
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
            throw new InvalidDataException(
                "HEADERS frame without END_HEADERS not supported (peer must respect SETTINGS_MAX_FRAME_SIZE so CONTINUATION is unnecessary)");
        }

        if (payloadLen == 0)
        {
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size);
            // Empty HEADERS — treat as initial header with no fields.
            return EmitDecodedHeaders(ReadOnlySpan<byte>.Empty, streamId, state, endStream);
        }

        var payloadReservation = ring.ReserveRead(payloadLen, ct);

        // Materialise to a contiguous buffer (HPACK decode requires continuous bytes).
        var pooled = ArrayPool<byte>.Shared.Rent(payloadLen);
        try
        {
            CopyFromReservationSlice(payloadReservation, 0, pooled.AsSpan(0, payloadLen));
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);

            var fragment = pooled.AsSpan(0, payloadLen);

            // Skip PADDED + PRIORITY prefixes per RFC 7540 §6.2.
            var padLength = 0;
            var headerOffset = 0;
            if (padded)
            {
                padLength = fragment[0];
                headerOffset += 1;
            }
            if (hasPriority)
            {
                headerOffset += 5;
            }
            var headerBlockLength = payloadLen - headerOffset - padLength;
            if (headerBlockLength < 0)
            {
                throw new InvalidDataException("HEADERS frame: invalid pad/priority prefix");
            }
            var headerBlock = fragment.Slice(headerOffset, headerBlockLength);

            return EmitDecodedHeaders(headerBlock, streamId, state, endStream);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pooled);
        }
    }

    private static (FrameHeader Header, FramePayload Payload) EmitDecodedHeaders(
        ReadOnlySpan<byte> headerBlock, uint streamId, Http2DecoderState state, bool endStream)
    {
        var hasInitial = state.StreamsWithInitialHeaders.ContainsKey(streamId);

        FrameType internalType;
        byte internalFlags;
        byte[] payloadBytes;
        int payloadLen;

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
            if (endStream)
            {
                var (headersV1, trailersV1) = HpackHeadersAdapter.DecodeTrailersOnly(headerBlock);

                var (hPayload, hLen) = headersV1.Encode();
                var (tPayload, tLen) = trailersV1.Encode();

                state.PendingFrames.Enqueue((
                    new FrameHeader(FrameType.Trailers, streamId, (uint)tLen, TrailersFlags.EndStream),
                    FramePayload.FromPooled(tPayload, tLen)));

                // No persistent stream state to retain: trailers-only means
                // the stream ended in this single HEADERS frame; any further
                // wire frames on this stream id (none expected from a well-
                // behaved peer) get treated as a fresh stream.
                var hHdr = new FrameHeader(
                    FrameType.Headers, streamId, (uint)hLen, HeadersFlags.Initial);
                return (hHdr, FramePayload.FromPooled(hPayload, hLen));
            }

            var v1 = HpackHeadersAdapter.DecodeHeaders(headerBlock);
            (payloadBytes, payloadLen) = v1.Encode();
            internalType = FrameType.Headers;
            internalFlags = (byte)HeadersFlags.Initial;
            state.StreamsWithInitialHeaders[streamId] = 1;
        }
        else
        {
            // Subsequent HEADERS → trailers.
            var v1 = HpackHeadersAdapter.DecodeTrailers(headerBlock);
            (payloadBytes, payloadLen) = v1.Encode();
            internalType = FrameType.Trailers;
            internalFlags = endStream ? TrailersFlags.EndStream : (byte)0;
            state.StreamsWithInitialHeaders.Remove(streamId);
        }

        var hdr = new FrameHeader(internalType, streamId, (uint)payloadLen, internalFlags);
        return (hdr, FramePayload.FromPooled(payloadBytes, payloadLen));
    }

    private static (FrameHeader Header, FramePayload Payload) ReadRstStreamFrame(
        ShmRing ring, ulong baseCommitReadIdx, uint streamId, int payloadLen, CancellationToken ct)
    {
        // Drain payload (4 bytes error code; we don't propagate the code).
        if (payloadLen > 0)
        {
            var _ = ring.ReserveRead(payloadLen, ct);
        }
        ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);

        // Clean up all per-stream state. A pending LPM accumulator may still
        // hold a pooled buffer; calling Reset() returns it to ArrayPool to
        // prevent a buffer leak when the peer cancels mid-message.
        var state = GetState(ring);
        state.StreamsWithInitialHeaders.Remove(streamId);
        if (RemoveAcc(state, streamId, out var pendingAcc))
        {
            pendingAcc!.Reset();
        }

        var hdr = new FrameHeader(FrameType.Cancel, streamId, 0, 0);
        return (hdr, FramePayload.Empty);
    }

    private static void HandleSettingsFrame(ShmRing ring, ulong baseCommitReadIdx,
        byte h2Flags, int payloadLen, CancellationToken ct)
    {
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
        ShmRing ring, ulong baseCommitReadIdx, byte h2Flags, int payloadLen, CancellationToken ct)
    {
        // PING is always 8 bytes.
        if (payloadLen != 8)
        {
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
            throw new InvalidDataException($"H2 PING frame payload length {payloadLen} != 8");
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
        ShmRing ring, ulong baseCommitReadIdx, int payloadLen, CancellationToken ct)
    {
        // RFC 7540 §6.8: 4-byte last-stream-id + 4-byte error code + optional debug data.
        if (payloadLen < 8)
        {
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
            throw new InvalidDataException($"H2 GOAWAY frame payload length {payloadLen} < 8");
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
        if (payloadLen != 4)
        {
            ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);
            throw new InvalidDataException($"H2 WINDOW_UPDATE frame payload length {payloadLen} != 4");
        }

        var payloadReservation = ring.ReserveRead(payloadLen, ct);
        Span<byte> raw = stackalloc byte[4];
        CopyFromReservationSlice(payloadReservation, 0, raw);
        ring.CommitReadRaw(baseCommitReadIdx, Http2FrameHeader.Size + payloadLen);

        var increment = BinaryPrimitives.ReadUInt32BigEndian(raw) & 0x7FFFFFFFu;

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
