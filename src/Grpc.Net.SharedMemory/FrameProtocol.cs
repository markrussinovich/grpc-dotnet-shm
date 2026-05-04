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

namespace Grpc.Net.SharedMemory;

using System.Buffers;

/// <summary>
/// High-level frame protocol operations for reading and writing gRPC frames
/// to the shared memory ring buffer.
/// </summary>
public static class FrameProtocol
{
    /// <summary>
    /// Maximum allowed frame payload size (128 MiB). Any frame header claiming a
    /// payload larger than this is treated as data corruption (e.g., from a SPSC
    /// ring buffer violation or stale shared memory) and will throw rather than
    /// attempting a huge allocation that would hang or OOM.
    /// </summary>
    internal const int MaxFramePayloadSize = 128 * 1024 * 1024;

    /// <summary>
    /// Reads a frame from the ring and returns a pooled-buffer payload.
    /// Dispatches to the configured <see cref="Wire.WireFormat"/> codec.
    /// </summary>
    public static (FrameHeader Header, FramePayload Payload) ReadFramePayload(
        ShmRing ring,
        CancellationToken cancellationToken = default,
        bool zeroCopy = false)
    {
        // Wire is set once at connection establishment and never changes.
        // The branch predictor learns the per-ring direction after one
        // frame; the JIT inlines this wrapper and the impl into
        // FrameReaderLoop. Cost is one field load + a 100%-predictable
        // branch.
        return ring.Wire == Wire.WireFormat.Http2
            ? Wire.Http2Codec.ReadFramePayload(ring, cancellationToken, zeroCopy)
            : ReadFramePayloadCustom16(ring, cancellationToken, zeroCopy);
    }

    /// <summary>
    /// Test/diagnostic helper retained for back-compat with
    /// <c>RingBench</c>. Returns zeros: per-frame counters were dropped
    /// in favour of branch-predictor-friendly dispatch. To check which
    /// codec a connection negotiated, inspect <see cref="ShmRing.Wire"/>
    /// on the ring directly.
    /// </summary>
    public static (long Custom16Read, long Http2Read, long Custom16Write, long Http2Write) GetCodecCounters()
        => (0L, 0L, 0L, 0L);

    /// <summary>No-op retained for <c>RingBench</c> back-compat.</summary>
    public static void ResetCodecCounters() { }

    /// <summary>
    /// Reads a Custom16-encoded frame and returns a pooled-buffer payload.
    /// </summary>
    internal static (FrameHeader Header, FramePayload Payload) ReadFramePayloadCustom16(
        ShmRing ring,
        CancellationToken cancellationToken = default,
        bool zeroCopy = false)
    {
        while (true)
        {
            // Read frame header — reserve but defer CommitRead until payload
            // is also read, so we issue a single Volatile.Write to shared
            // ReadIdx per frame instead of two.
            var headerReservation = ring.ReserveRead(ShmConstants.FrameHeaderSize, cancellationToken);
            var baseCommitReadIdx = headerReservation.CommitReadIdx;

            Span<byte> headerBytes = stackalloc byte[ShmConstants.FrameHeaderSize];
            CopyFromReservation(headerReservation, headerBytes);
            // Note: CommitRead deferred — will be batched with payload below.

            var header = FrameHeader.DecodeFrom(headerBytes);

            // Guard against corrupted frame headers that could cause huge
            // allocations or block forever trying to read from the ring.
            if (header.Length > MaxFramePayloadSize)
            {
                ring.CommitReadRaw(baseCommitReadIdx, ShmConstants.FrameHeaderSize);
                throw new InvalidDataException(
                    $"Frame payload length {header.Length} exceeds maximum {MaxFramePayloadSize}. " +
                    "This may indicate data corruption in the shared memory ring buffer.");
            }

            if (!Enum.IsDefined(header.Type) && header.Type != FrameType.Pad)
            {
                ring.CommitReadRaw(baseCommitReadIdx, ShmConstants.FrameHeaderSize);
                throw new InvalidDataException(
                    $"Unknown frame type 0x{(byte)header.Type:X2} with length {header.Length}. " +
                    "This may indicate data corruption in the shared memory ring buffer.");
            }

            // Skip PAD frames
            if (header.Type == FrameType.Pad)
            {
                if (header.Length > 0)
                {
                    var padReservation = ring.ReserveRead((int)header.Length, cancellationToken);
                    ring.CommitReadRaw(baseCommitReadIdx, ShmConstants.FrameHeaderSize + (int)header.Length);
                }
                else
                {
                    ring.CommitReadRaw(baseCommitReadIdx, ShmConstants.FrameHeaderSize);
                }
                continue;
            }

            if (header.Length == 0)
            {
                ring.CommitReadRaw(baseCommitReadIdx, ShmConstants.FrameHeaderSize);
                return (header, FramePayload.Empty);
            }

            var payloadLength = (int)header.Length;
            var payloadReservation = ring.ReserveRead(payloadLength, cancellationToken);
            var isMore = (header.Flags & MessageFlags.More) != 0;
            var totalBytes = ShmConstants.FrameHeaderSize + payloadLength;
            var contiguous = payloadReservation.Second.IsEmpty;

            // ===== Hot path: single-frame, no chain in flight, no copy-mode in progress =====
            //
            // Covers the dominant case: every single-frame ping-pong / unary
            // RPC. Reads only IsChainOpen and ChainCopyMode (Volatile.Reads
            // the slow path would have read anyway, so this is no extra cost
            // on cold path) and short-circuits BOTH the ZC happy path and the
            // sub-threshold copy fallback. The slow path's chain-decision
            // tree is bypassed entirely here.
            //
            // Falls through to the slow path when:
            //   - isMore=true (multi-frame continuation)
            //   - IsChainOpen=true (the !isMore frame is the FINAL frame of
            //     an active chain ZC, which must call CloseZcChain)
            //   - ChainCopyMode=true (the !isMore frame is the FINAL frame
            //     of a copy-mode multi-frame message, which must reset the
            //     ChainCopyMode flag)
            if (!isMore && !ring.IsChainOpen && !ring.ChainCopyMode)
            {
                if (zeroCopy && contiguous
                    && ring.IsSpeculativeZcEligible(payloadLength, contiguous: true))
                {
                    // Fused single-frame ZC: BeginSingleFrameZcCommit sets
                    // _deferredReadIdxTarget to its post-frame value
                    // directly, saving 1 Volatile.Read + 1 Volatile.Write
                    // vs the BeginZc + CommitReadRaw two-step.
                    ring.BeginSingleFrameZcCommit(baseCommitReadIdx, totalBytes);
                    Interlocked.Add(ref ring.SpeculativeReservedBytes, totalBytes);
                    return (header, FramePayload.FromRingMemorySpeculative(
                        payloadReservation.First.Slice(0, payloadLength), ring, totalBytes));
                }

                // Single-frame copy: sub-ZC-threshold, ZC disabled, or
                // wrap. CommitReadRaw advances the shared ReadIdx
                // immediately (no chain anchor in flight under this gate).
                var pooled = ArrayPool<byte>.Shared.Rent(payloadLength);
                if (contiguous)
                {
                    payloadReservation.First.Span.Slice(0, payloadLength).CopyTo(pooled);
                }
                else
                {
                    CopyFromReservation(payloadReservation, pooled.AsSpan(0, payloadLength));
                }
                ring.CommitReadRaw(baseCommitReadIdx, totalBytes);
                return (header, FramePayload.FromPooled(pooled, payloadLength));
            }

            // ===== Slow path: multi-frame chain, copy-mode continuation, or chain-final =====
            //
            // Modes for a multi-frame logical message:
            //
            //   2) Multi-frame chain ZC (isMore on first frame): every
            //      frame ZC. Eligibility decided ONCE on the first frame:
            //      <c>totalMsg ≤ ring.ChainZcBudget</c>. Anchor opens on
            //      frame 1, releases on consumer's final Release for the
            //      chain. CommitReadRaw is deferred throughout;
            //      EndZcReservation fires from the consumer's LAST
            //      Release (gated by SpeculativeReservedBytes==0 AND
            //      !IsChainOpen).
            //
            //      Mid-chain fallback to copy is forbidden by design:
            //      once the anchor is open, every subsequent frame
            //      MUST also ZC (going to the copy path while
            //      <c>_zcActive=true</c> would defer the readIdx
            //      advance forever, deadlocking the writer).
            //
            //      The budget is <c>cap/2</c> (see <see cref="ShmRing.ChainZcBudget"/>):
            //      under back-to-back streaming the writer must have
            //      enough headroom (≈ another <c>cap/2</c>) to start
            //      emitting the next message while the current chain
            //      anchor is still held by the consumer. Any larger
            //      budget risks deadlock.
            //
            //   3) Pure copy: every frame copies (chain anchor never
            //      opens). Used when the first frame fails eligibility
            //      (totalMsg too big, wrap, ZC disabled, or sub-MinZc).
            // chainActive: codec is in the middle of a multi-frame chain
            // (isMore=true on first frame opened it; not yet closed).
            // Note: we use IsChainOpen, NOT IsZcChainActive. The latter
            // is also true for in-flight single-frame ZC anchors, which
            // do not impose "must ZC the next frame" semantics.
            var chainActive = ring.IsChainOpen;
            var copyMode = ring.ChainCopyMode;

            bool tryZc = false;
            bool startChain = false;

            if (zeroCopy && contiguous)
            {
                if (chainActive)
                {
                    // Mode 2 continuation. ZC unconditionally — once
                    // the anchor is opened we must keep ZCing through
                    // the chain end (mid-chain copy would freeze readIdx
                    // forever).
                    tryZc = true;
                }
                else if (copyMode)
                {
                    // Continuation of a copy-mode multi-frame message.
                    tryZc = false;
                }
                else if (isMore)
                {
                    // First frame of a multi-frame message. Decide chain
                    // ZC vs copy mode based on declared LPM total length.
                    if (payloadLength >= 5
                        && ring.IsSpeculativeZcEligible(payloadLength, contiguous: true))
                    {
                        var firstSpan = payloadReservation.First.Span;
                        var lpmBodyLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(
                            firstSpan.Slice(1, 4));
                        var totalMsg = 5L + lpmBodyLen;

                        if (totalMsg <= ring.ChainZcBudget)
                        {
                            tryZc = true;
                            startChain = true;
                        }
                        else
                        {
                            ring.ChainCopyMode = true;
                        }
                    }
                    else
                    {
                        ring.ChainCopyMode = true;
                    }
                }
                else
                {
                    // Final frame of an in-progress non-chain message,
                    // or single-frame final that the hot path rejected
                    // (e.g. sub-threshold). Re-evaluate eligibility.
                    if (ring.IsSpeculativeZcEligible(payloadLength, contiguous: true))
                    {
                        tryZc = true;
                    }
                }
            }
            else if (isMore && !chainActive && !copyMode)
            {
                // First frame of multi-frame message but ZC disabled or
                // wrapped. Mark copy mode for the rest of the message.
                ring.ChainCopyMode = true;
            }

            if (tryZc)
            {
                if (startChain)
                {
                    // Open the anchor (cross-process visible) and the
                    // codec-side chain marker (gates EndZc on Release).
                    ring.BeginZcReservation(baseCommitReadIdx);
                    ring.OpenZcChain();
                }
                else if (!chainActive)
                {
                    // Single-frame ZC (mode 1) or chain continuation
                    // when the anchor was already opened by an earlier
                    // call — only call BeginZc if no anchor exists yet.
                    // (IsZcChainActive distinguishes "anchor exists"
                    // from IsChainOpen which means "codec still adding
                    // chain frames".)
                    if (!ring.IsZcChainActive)
                    {
                        ring.BeginZcReservation(baseCommitReadIdx);
                    }
                }
                Interlocked.Add(ref ring.SpeculativeReservedBytes, totalBytes);
                ring.CommitReadRaw(baseCommitReadIdx, totalBytes);
                if (!isMore && ring.IsChainOpen)
                {
                    // Final frame of an active chain — codec-side close.
                    ring.CloseZcChain();
                }
                return (header, FramePayload.FromRingMemorySpeculative(
                    payloadReservation.First.Slice(0, payloadLength), ring, totalBytes));
            }

            // Copy path. CommitReadRaw advances readIdx normally (no
            // anchor open in copy mode).
            var payload = ArrayPool<byte>.Shared.Rent(payloadLength);
            if (contiguous)
            {
                payloadReservation.First.Span.Slice(0, payloadLength).CopyTo(payload);
            }
            else
            {
                CopyFromReservation(payloadReservation, payload.AsSpan(0, payloadLength));
            }
            ring.CommitReadRaw(baseCommitReadIdx, totalBytes);

            // Reset copy-mode on the message's final frame so the next
            // logical message gets a fresh decision.
            if (!isMore && copyMode)
            {
                ring.ChainCopyMode = false;
            }
            return (header, FramePayload.FromPooled(payload, payloadLength));
        }
    }

    /// <summary>
    /// Writes a frame (header + payload) to the ring buffer atomically.
    /// Blocks until space is available.
    /// </summary>
    /// <param name="ring">The ring buffer to write to.</param>
    /// <param name="header">The frame header.</param>
    /// <param name="payload">The frame payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static void WriteFrame(ShmRing ring, FrameHeader header, ReadOnlySpan<byte> payload, CancellationToken cancellationToken = default)
    {
        // Delegate to the scatter-write overload with an empty second payload
        WriteFrame(ring, header, payload, ReadOnlySpan<byte>.Empty, cancellationToken);
    }

    /// <summary>
    /// Writes a Custom16-encoded frame to the ring. Used by the dispatch shim
    /// when <see cref="ShmRing.Wire"/> is <see cref="Wire.WireFormat.Custom16"/>
    /// and by the H2 codec for the rare cases where it falls back.
    /// </summary>
    internal static void WriteFrameCustom16(ShmRing ring, FrameHeader header,
        ReadOnlySpan<byte> payload1, ReadOnlySpan<byte> payload2, CancellationToken cancellationToken = default)
    {
        WriteFrameCore(ring, header, payload1, payload2, cancellationToken);
    }



    /// <summary>
    /// Writes a frame with a two-part payload (scatter write) to the ring buffer atomically.
    /// This avoids an intermediate copy when the payload is logically split (e.g., gRPC prefix + data).
    /// The frame header's Length is set to payload1.Length + payload2.Length.
    /// </summary>
    /// <param name="ring">The ring buffer to write to.</param>
    /// <param name="header">The frame header.</param>
    /// <param name="payload1">The first part of the frame payload (e.g., gRPC length-prefix header).</param>
    /// <param name="payload2">The second part of the frame payload (e.g., protobuf message data).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static void WriteFrame(ShmRing ring, FrameHeader header, ReadOnlySpan<byte> payload1, ReadOnlySpan<byte> payload2, CancellationToken cancellationToken = default)
    {
        if (ring.Wire == Wire.WireFormat.Http2)
        {
            Wire.Http2Codec.WriteFrame(ring, header, payload1, payload2, cancellationToken);
            return;
        }
        WriteFrameCore(ring, header, payload1, payload2, cancellationToken);
    }

    private static void WriteFrameCore(ShmRing ring, FrameHeader header, ReadOnlySpan<byte> payload1, ReadOnlySpan<byte> payload2, CancellationToken cancellationToken = default)
    {
        var totalPayloadSize = payload1.Length + payload2.Length;
        header.Length = (uint)totalPayloadSize;
        header.Reserved = 0;
        header.Reserved2 = 0;

        var totalSize = ShmConstants.FrameHeaderSize + totalPayloadSize;

        // Reserve space for the entire frame atomically
        var reservation = ring.ReserveWrite(totalSize, cancellationToken);

        // Encode header
        Span<byte> headerBytes = stackalloc byte[ShmConstants.FrameHeaderSize];
        header.EncodeTo(headerBytes);

        // We need to write 3 parts into potentially 2 slices (First/Second).
        // Use a helper approach: treat the reservation as a linear span and write sequentially.
        var firstSpan = reservation.First.Span;
        var secondSpan = reservation.Second.Span;
        var written = 0;

        // Write header
        written = WriteToReservation(firstSpan, secondSpan, written, headerBytes);

        // Write payload1
        if (payload1.Length > 0)
        {
            written = WriteToReservation(firstSpan, secondSpan, written, payload1);
        }

        // Write payload2
        if (payload2.Length > 0)
        {
            written = WriteToReservation(firstSpan, secondSpan, written, payload2);
        }

        // Commit the write
        ring.CommitWrite(reservation, written);
    }

    /// <summary>
    /// Writes data to a two-part reservation (First/Second spans) starting at the given offset.
    /// Returns the new offset after writing.
    /// </summary>
    private static int WriteToReservation(Span<byte> first, Span<byte> second, int offset, ReadOnlySpan<byte> data)
    {
        var remaining = data.Length;
        var dataOffset = 0;

        // Write to First span if we haven't passed it yet
        if (offset < first.Length && remaining > 0)
        {
            var available = first.Length - offset;
            var toCopy = Math.Min(remaining, available);
            data.Slice(dataOffset, toCopy).CopyTo(first.Slice(offset));
            offset += toCopy;
            dataOffset += toCopy;
            remaining -= toCopy;
        }

        // Write to Second span for anything remaining
        if (remaining > 0)
        {
            var secondOffset = offset - first.Length;
            data.Slice(dataOffset, remaining).CopyTo(second.Slice(secondOffset));
            offset += remaining;
        }

        return offset;
    }

    /// <summary>
    /// Reads a frame from the ring buffer, skipping PAD frames.
    /// Blocks until a frame is available.
    /// </summary>
    /// <param name="ring">The ring buffer to read from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The frame header and payload.</returns>
    public static (FrameHeader Header, byte[] Payload) ReadFrame(ShmRing ring, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            // Read frame header
            var headerReservation = ring.ReserveRead(ShmConstants.FrameHeaderSize, cancellationToken);

            Span<byte> headerBytes = stackalloc byte[ShmConstants.FrameHeaderSize];
            CopyFromReservation(headerReservation, headerBytes);
            ring.CommitRead(headerReservation, ShmConstants.FrameHeaderSize);

            var header = FrameHeader.DecodeFrom(headerBytes);

            // Guard against corrupted frame headers
            if (header.Length > MaxFramePayloadSize)
            {
                throw new InvalidDataException(
                    $"Frame payload length {header.Length} exceeds maximum {MaxFramePayloadSize}.");
            }

            if (!Enum.IsDefined(header.Type) && header.Type != FrameType.Pad)
            {
                throw new InvalidDataException(
                    $"Unknown frame type 0x{(byte)header.Type:X2} with length {header.Length}.");
            }

            // Skip PAD frames
            if (header.Type == FrameType.Pad)
            {
                if (header.Length > 0)
                {
                    // Skip the padding payload
                    var padReservation = ring.ReserveRead((int)header.Length, cancellationToken);
                    ring.CommitRead(padReservation, (int)header.Length);
                }
                continue;
            }

            // Read payload if present
            byte[] payload;
            if (header.Length > 0)
            {
                payload = new byte[header.Length];
                var payloadReservation = ring.ReserveRead((int)header.Length, cancellationToken);
                CopyFromReservation(payloadReservation, payload);
                ring.CommitRead(payloadReservation, (int)header.Length);
            }
            else
            {
                payload = Array.Empty<byte>();
            }

            return (header, payload);
        }
    }

    /// <summary>
    /// Reads a frame from the ring buffer using ArrayPool to avoid per-frame heap allocation.
    /// The caller is responsible for returning the payload array to <see cref="ArrayPool{T}.Shared"/>
    /// when done (unless PayloadLength is 0, in which case Payload is <see cref="Array.Empty{T}"/>).
    /// </summary>
    /// <param name="ring">The ring buffer to read from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The frame header, pooled payload buffer, and actual payload length.</returns>
    public static (FrameHeader Header, byte[] Payload, int PayloadLength) ReadFramePooled(ShmRing ring, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            // Read frame header
            var headerReservation = ring.ReserveRead(ShmConstants.FrameHeaderSize, cancellationToken);

            Span<byte> headerBytes = stackalloc byte[ShmConstants.FrameHeaderSize];
            CopyFromReservation(headerReservation, headerBytes);
            ring.CommitRead(headerReservation, ShmConstants.FrameHeaderSize);

            var header = FrameHeader.DecodeFrom(headerBytes);

            // Guard against corrupted frame headers
            if (header.Length > MaxFramePayloadSize)
            {
                throw new InvalidDataException(
                    $"Frame payload length {header.Length} exceeds maximum {MaxFramePayloadSize}.");
            }

            if (!Enum.IsDefined(header.Type) && header.Type != FrameType.Pad)
            {
                throw new InvalidDataException(
                    $"Unknown frame type 0x{(byte)header.Type:X2} with length {header.Length}.");
            }

            // Skip PAD frames
            if (header.Type == FrameType.Pad)
            {
                if (header.Length > 0)
                {
                    var padReservation = ring.ReserveRead((int)header.Length, cancellationToken);
                    ring.CommitRead(padReservation, (int)header.Length);
                }
                continue;
            }

            // Read payload into a pooled buffer if present
            if (header.Length > 0)
            {
                var payloadLength = (int)header.Length;
                var payload = ArrayPool<byte>.Shared.Rent(payloadLength);
                var payloadReservation = ring.ReserveRead(payloadLength, cancellationToken);
                CopyFromReservation(payloadReservation, payload.AsSpan(0, payloadLength));
                ring.CommitRead(payloadReservation, payloadLength);
                return (header, payload, payloadLength);
            }

            return (header, Array.Empty<byte>(), 0);
        }
    }

    /// <summary>
    /// Reads a frame without allocating a new payload array.
    /// The payload is written to the provided buffer.
    /// </summary>
    /// <param name="ring">The ring buffer to read from.</param>
    /// <param name="payloadBuffer">Buffer to receive the payload. Must be large enough.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The frame header and actual payload length.</returns>
    public static (FrameHeader Header, int PayloadLength) ReadFrameInto(
        ShmRing ring,
        Span<byte> payloadBuffer,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            // Read frame header
            var headerReservation = ring.ReserveRead(ShmConstants.FrameHeaderSize, cancellationToken);

            Span<byte> headerBytes = stackalloc byte[ShmConstants.FrameHeaderSize];
            CopyFromReservation(headerReservation, headerBytes);
            ring.CommitRead(headerReservation, ShmConstants.FrameHeaderSize);

            var header = FrameHeader.DecodeFrom(headerBytes);

            // Guard against corrupted frame headers
            if (header.Length > MaxFramePayloadSize)
            {
                throw new InvalidDataException(
                    $"Frame payload length {header.Length} exceeds maximum {MaxFramePayloadSize}.");
            }

            if (!Enum.IsDefined(header.Type) && header.Type != FrameType.Pad)
            {
                throw new InvalidDataException(
                    $"Unknown frame type 0x{(byte)header.Type:X2} with length {header.Length}.");
            }

            // Skip PAD frames
            if (header.Type == FrameType.Pad)
            {
                if (header.Length > 0)
                {
                    var padReservation = ring.ReserveRead((int)header.Length, cancellationToken);
                    ring.CommitRead(padReservation, (int)header.Length);
                }
                continue;
            }

            // Read payload if present
            if (header.Length > 0)
            {
                if (payloadBuffer.Length < header.Length)
                {
                    throw new ArgumentException($"Payload buffer too small: need {header.Length}, have {payloadBuffer.Length}");
                }

                var payloadReservation = ring.ReserveRead((int)header.Length, cancellationToken);
                CopyFromReservation(payloadReservation, payloadBuffer[..(int)header.Length]);
                ring.CommitRead(payloadReservation, (int)header.Length);
            }

            return (header, (int)header.Length);
        }
    }

    /// <summary>
    /// Writes a PING frame.
    /// </summary>
    public static void WritePing(ShmRing ring, byte flags, ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        var header = new FrameHeader(FrameType.Ping, 0, (uint)data.Length, flags);
        WriteFrame(ring, header, data, cancellationToken);
    }

    /// <summary>
    /// Writes a PONG frame.
    /// </summary>
    public static void WritePong(ShmRing ring, byte flags, ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        var header = new FrameHeader(FrameType.Pong, 0, (uint)data.Length, flags);
        WriteFrame(ring, header, data, cancellationToken);
    }

    /// <summary>
    /// Writes a GOAWAY frame.
    /// </summary>
    public static void WriteGoAway(ShmRing ring, byte flags, string? debugMessage = null, CancellationToken cancellationToken = default)
    {
        var payload = debugMessage != null ? System.Text.Encoding.UTF8.GetBytes(debugMessage) : Array.Empty<byte>();
        var header = new FrameHeader(FrameType.GoAway, 0, (uint)payload.Length, flags);
        WriteFrame(ring, header, payload.AsSpan(), cancellationToken);
    }

    /// <summary>
    /// Writes a CANCEL frame.
    /// </summary>
    public static void WriteCancel(ShmRing ring, uint streamId, CancellationToken cancellationToken = default)
    {
        var header = new FrameHeader(FrameType.Cancel, streamId, 0, 0);
        WriteFrame(ring, header, ReadOnlySpan<byte>.Empty, cancellationToken);
    }

    /// <summary>
    /// Writes a WINDOW_UPDATE frame.
    /// </summary>
    public static void WriteWindowUpdate(ShmRing ring, uint streamId, uint windowSizeIncrement, CancellationToken cancellationToken = default)
    {
        Span<byte> payload = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload, windowSizeIncrement);
        var header = new FrameHeader(FrameType.WindowUpdate, streamId, 4, 0);
        WriteFrame(ring, header, payload, cancellationToken);
    }

    /// <summary>
    /// Writes a MESSAGE frame, automatically chunking if the payload exceeds
    /// the ring capacity. Matches grpc-go-shmem's writeFrameBuffersChunked.
    /// </summary>
    public static void WriteMessage(ShmRing ring, uint streamId, ReadOnlySpan<byte> data, bool isLast, CancellationToken cancellationToken = default, byte extraFlags = 0)
    {
        var flags = (byte)((isLast ? 0 : MessageFlags.More) | extraFlags);

        var cap = (int)ring.Capacity;
        // Max frame payload = ringCap/3. Chosen so that:
        // 1. Common payloads (4MB, 16MB) fit in a single frame →
        //    speculative zero-copy read path (no More flag).
        //    16MB protobuf CalculateSize ≈ 16.8MB < 64MB/3 = 22.4MB.
        // 2. Speculative safety: N=2 in-flight × cap/3 = 2cap/3 < cap,
        //    so writer needs cap/3 (~22MB) to reach oldest frame.
        // 3. Pipeline: 3 frames fit simultaneously in the ring,
        //    providing good overlap for streaming workloads.
        var maxFramePayload = Math.Max(1, cap / 3);

        // HTTP/2 has an absolute hard cap on per-frame payload length
        // (24-bit field; RFC 7540 §6.5.2 SETTINGS_MAX_FRAME_SIZE upper bound
        // is 2^24 - 1). Cap our chunk size below that so the H2 codec can
        // encode every frame.
        if (ring.Wire == Wire.WireFormat.Http2 && maxFramePayload > Wire.Http2FrameHeader.MaxAllowedPayloadLength)
        {
            maxFramePayload = Wire.Http2FrameHeader.MaxAllowedPayloadLength;
        }

        if (data.Length <= maxFramePayload)
        {
            var header = new FrameHeader(FrameType.Message, streamId, (uint)data.Length, flags);
            WriteFrame(ring, header, data, cancellationToken);
            return;
        }

        var remaining = data;
        while (remaining.Length > 0)
        {
            var chunkSize = Math.Min(maxFramePayload, remaining.Length);
            var chunk = remaining[..chunkSize];
            remaining = remaining[chunkSize..];

            byte chunkFlags;
            if (remaining.Length > 0)
            {
                chunkFlags = MessageFlags.More;
            }
            else
            {
                chunkFlags = flags;
            }

            var header = new FrameHeader(FrameType.Message, streamId, (uint)chunkSize, chunkFlags);
            WriteFrame(ring, header, chunk, cancellationToken);
        }
    }

    /// <summary>
    /// Writes a HALF_CLOSE frame.
    /// </summary>
    public static void WriteHalfClose(ShmRing ring, uint streamId, CancellationToken cancellationToken = default)
    {
        var header = new FrameHeader(FrameType.HalfClose, streamId, 0, 0);
        WriteFrame(ring, header, ReadOnlySpan<byte>.Empty, cancellationToken);
    }

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
}
