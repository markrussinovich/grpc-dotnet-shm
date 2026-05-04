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
            // Feed body into the per-stream accumulator. If the body completes
            // a message, we return it. We don't surface multi-message DATA
            // frames in one return; only the first completed message per call.
            // (Our writer never packs >1 message per DATA frame, so this is
            // safe in practice.)
            var acc = GetOrAddAcc(state, streamId);
            var feedResult = FeedAccumulator(acc, bodyBytes);

            // Commit the ring read regardless — we've materialised everything we need.
            ring.CommitReadRaw(baseCommitReadIdx, totalBytes);

            if (feedResult is { } completedPayload)
            {
                byte msgFlags = endStream ? MessageFlags.EndStream : (byte)0;
                var hdr = new FrameHeader(FrameType.Message, streamId, (uint)completedPayload.Length, msgFlags);
                if (endStream)
                {
                    state.StreamsWithInitialHeaders.Remove(streamId);
                    // FeedAccumulator already nulled out acc.Buffer on completion;
                    // Reset() here is defensive in case future changes leave state.
                    if (RemoveAcc(state, streamId, out var doneAcc))
                    {
                        doneAcc!.Reset();
                    }
                }
                return (hdr, completedPayload);
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
    /// Copies <paramref name="body"/> into <paramref name="acc"/> and, if a
    /// complete LPM message was produced, returns it as a pooled
    /// <see cref="FramePayload"/>; otherwise <c>null</c>.
    /// </summary>
    private static FramePayload? FeedAccumulator(LpmAccumulator acc, ReadOnlySpan<byte> body)
    {
        var src = body;

        // Phase 1: complete the 5-byte LPM header if necessary.
        if (acc.HeaderBytesSeen < 5)
        {
            var need = 5 - acc.HeaderBytesSeen;
            var take = Math.Min(need, src.Length);
            src.Slice(0, take).CopyTo(acc.HeaderBuf.AsSpan(acc.HeaderBytesSeen));
            acc.HeaderBytesSeen += take;
            src = src.Slice(take);

            if (acc.HeaderBytesSeen < 5)
            {
                return null; // header still partial
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

        // Phase 2: copy body bytes into accumulator buffer.
        if (src.Length > 0)
        {
            var room = acc.ExpectedTotal - acc.Pos;
            var take = Math.Min(room, src.Length);
            src.Slice(0, take).CopyTo(acc.Buffer.AsSpan(acc.Pos));
            acc.Pos += take;
            // Note: if take < src.Length, the DATA frame contains the start of a
            // *second* LPM message. Our writer never produces this, and the upper
            // layer can't consume two messages from one ReadFramePayload call,
            // so reject as a protocol error.
            if (take < src.Length)
            {
                throw new InvalidDataException(
                    "H2 DATA frame contains multiple gRPC LPM messages — not supported by this transport (writer must emit one message per DATA stream segment)");
            }
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
            return FramePayload.FromPooled(buf, len);
        }
        return null;
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
            // First HEADERS on this stream → initial headers (mapped to FrameType.Headers).
            var v1 = HpackHeadersAdapter.DecodeHeaders(headerBlock);
            (payloadBytes, payloadLen) = v1.Encode();
            internalType = FrameType.Headers;
            internalFlags = (byte)HeadersFlags.Initial;
            state.StreamsWithInitialHeaders[streamId] = 1;

            if (endStream)
            {
                // Trailers-only response: peer sent both initial-status and grpc-status
                // in a single HEADERS w/ END_STREAM. Surface as Headers; the upper layer
                // (or a future enhancement) can split if needed. We keep stream state to
                // allow a synthetic Trailers to be emitted by upper-layer logic. For now,
                // we simply remove initial state and let the next frame on this stream
                // (likely none) be treated as fresh.
                state.StreamsWithInitialHeaders.Remove(streamId);
            }
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
            // SETTINGS ACK has no payload.
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
