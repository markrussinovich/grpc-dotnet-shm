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

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Builds a <see cref="ReadOnlySequence{T}"/> over a stream of inbound
/// frames, pulling each frame lazily and releasing the previous frame as
/// the parser advances. Decouples ring footprint from total message
/// size: only ~2 frames are resident at any instant, regardless of
/// message length.
/// </summary>
/// <remarks>
/// <para>
/// Wire memcpy count = 1 (ring → final ByteString backing array,
/// allocated once when the protobuf parser hits a length-delimited
/// field). Equivalent to mode-2 chain ZC's memcpy count, but without
/// the <c>ChainZcBudget ≤ ring cap</c> constraint — a 256 MiB message
/// can be parsed against a 64 MiB ring with peak ring occupancy
/// ~2 × frame_size.
/// </para>
/// <para>
/// Implementation: trampoline pattern over
/// <see cref="ReadOnlySequenceSegment{T}"/>. The first segment is built
/// at construction with the first frame's data already in hand. A
/// placeholder segment for frame[1] (with empty <see cref="Memory"/>
/// initially) is linked after seg[0]. When the parser invokes
/// <see cref="MemoryManager{T}.GetSpan"/> on seg[i], a hook fires that:
/// </para>
/// <list type="number">
///   <item><description>
///     Releases the frame that backed seg[i-1] (parser is finished with
///     it; verified safe by <c>LazyChainRosPocTests</c>).
///   </description></item>
///   <item><description>
///     Pulls frame[i+1] from the upstream channel synchronously,
///     fulfills the seg[i+1] placeholder by setting its
///     <see cref="ReadOnlySequenceSegment{T}.Memory"/> to wrap that
///     frame.
///   </description></item>
///   <item><description>
///     If more frames are still needed, allocates a fresh empty
///     placeholder for seg[i+2] and links it after seg[i+1]. Otherwise
///     links seg[i+1] directly to the sentinel.
///   </description></item>
/// </list>
/// <para>
/// At any instant during parsing, peak in-flight frames = 2: the frame
/// currently being parsed (seg[i]) and the pre-pulled frame for the
/// next segment (seg[i+1]). The frame for seg[i-1] was released at the
/// start of seg[i].GetSpan.
/// </para>
/// <para>
/// Timing invariants validated by <c>LazyChainRosPocTests</c>:
/// </para>
/// <list type="bullet">
///   <item><description>
///     Each segment's <c>GetSpan</c> is called exactly once, in strict
///     forward order.
///   </description></item>
///   <item><description>
///     The parser never re-accesses a segment after advancing — so
///     releasing the prev frame in seg[i].GetSpan is safe.
///   </description></item>
///   <item><description>
///     Setting seg[i+1]'s <see cref="ReadOnlySequenceSegment{T}.Memory"/>
///     during seg[i].GetSpan happens BEFORE the parser's next
///     <c>MoveNext</c>, which captures the updated value into the
///     enumerator's <c>_currentMemory</c>.
///   </description></item>
/// </list>
/// </remarks>
internal sealed class LazyChainRos : IDisposable
{
    private readonly long _totalBodyLen;
    private readonly Func<CancellationToken, InboundFrame?> _pullNext;
    private readonly CancellationToken _ct;
    private readonly LazyHookedSegment _firstSeg;
    private readonly LazySentinelSegment _sentinel;

    /// <summary>
    /// Frame previously consumed; released at the next OnSegmentGetSpan
    /// (when the parser has advanced past the segment that referenced it).
    /// </summary>
    private InboundFrame _prevFrame;

    /// <summary>
    /// Frame currently held; rotated to <see cref="_prevFrame"/> when the
    /// parser advances to the next segment.
    /// </summary>
    private InboundFrame _currentFrame;

    /// <summary>
    /// Frame pulled by the most recent trampoline run but not yet rotated
    /// into <see cref="_currentFrame"/>. Lives between the
    /// <see cref="FulfillPlaceholder"/> in <c>seg[i].GetSpan</c> and the
    /// rotation that fires when the parser enters <c>seg[i+1].GetSpan</c>.
    /// Tracked here so that an exception thrown by the parser (or an early
    /// <see cref="Dispose"/>) does not strand the pre-pulled next frame
    /// in <see cref="LazyHookedSegment.AssignedFrame"/> with no other
    /// owner to return it to the pool.
    /// </summary>
    private InboundFrame _pendingFrame;

    /// <summary>
    /// Cumulative length of body bytes covered by linked-and-fulfilled
    /// segments. Used to decide when the chain has reached
    /// <see cref="_totalBodyLen"/> and must stop pulling.
    /// </summary>
    private long _linkedLength;

    private bool _disposed;

    /// <summary>
    /// Constructs the chain. <paramref name="firstFrame"/>'s data
    /// (starting at <paramref name="firstFrameBodyOffset"/>) backs the
    /// first segment. If the first segment alone covers
    /// <paramref name="totalBodyLen"/> bytes, the chain is short-circuited
    /// and no pull from <paramref name="pullNext"/> is needed.
    /// </summary>
    /// <param name="firstFrame">The first inbound frame, already pulled
    /// by the caller. Ownership transfers to this chain — released by
    /// <see cref="Dispose"/> or rotated out as the parser advances.</param>
    /// <param name="firstFrameBodyOffset">Offset within
    /// <c>firstFrame.Memory</c> where the LPM body bytes begin (typically
    /// 5, after the gRPC LPM header).</param>
    /// <param name="totalBodyLen">Exact bytes of body the chain exposes
    /// via <see cref="Sequence"/>. Must equal the LPM body length read
    /// from the LPM header.</param>
    /// <param name="pullNext">Synchronous puller for subsequent frames.
    /// Returns <c>null</c> on end-of-channel; this chain throws
    /// <see cref="IOException"/> if pull returns null before
    /// <paramref name="totalBodyLen"/> bytes have been linked.</param>
    /// <param name="ct">Cancellation token threaded through to
    /// <paramref name="pullNext"/>.</param>
    public LazyChainRos(
        InboundFrame firstFrame,
        int firstFrameBodyOffset,
        long totalBodyLen,
        Func<CancellationToken, InboundFrame?> pullNext,
        CancellationToken ct)
    {
        if (firstFrameBodyOffset < 0 || firstFrameBodyOffset > firstFrame.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(firstFrameBodyOffset));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(totalBodyLen);
        ArgumentNullException.ThrowIfNull(pullNext);

        _totalBodyLen = totalBodyLen;
        _pullNext = pullNext;
        _ct = ct;
        _sentinel = new LazySentinelSegment(totalBodyLen);
        _currentFrame = firstFrame;

        var firstAvailable = firstFrame.Memory.Slice(firstFrameBodyOffset);
        var firstSegLen = (int)Math.Min(firstAvailable.Length, totalBodyLen);
        var firstBytes = firstAvailable.Slice(0, firstSegLen);
        _linkedLength = firstSegLen;

        // Build the first segment with a hooked memory manager so its
        // GetSpan triggers the trampoline (pull frame[1] + fulfill seg[1]
        // BEFORE the parser advances past seg[0]).
        _firstSeg = new LazyHookedSegment(this, firstBytes, runningIndex: 0, isFirst: true);

        if (_linkedLength >= totalBodyLen)
        {
            // Common case for messages that fit in a single frame: just
            // link seg[0] → sentinel; no placeholder needed.
            _firstSeg.SetNext(_sentinel);
        }
        else
        {
            // Allocate placeholder seg[1] with empty Memory. The trampoline
            // running inside seg[0].GetSpan will set its Memory before the
            // parser's next MoveNext captures it.
            var placeholder = new LazyHookedSegment(
                this, ReadOnlyMemory<byte>.Empty, runningIndex: _linkedLength, isFirst: false);
            placeholder.SetNext(_sentinel);
            _firstSeg.SetNext(placeholder);
        }
    }

    /// <summary>
    /// The lazy-fill <see cref="ReadOnlySequence{T}"/>. Pass to
    /// <c>MergeFrom(ros)</c>. Length is fixed at construction (=
    /// totalBodyLen).
    /// </summary>
    public ReadOnlySequence<byte> Sequence => new(_firstSeg, 0, _sentinel, 0);

    /// <summary>
    /// Hook fired by <see cref="LazyHookedSegment"/>'s memory manager
    /// the first time the parser invokes <c>GetSpan</c> on a segment.
    /// Implements rotation (release prev frame; advance current) and
    /// the trampoline (fulfill next placeholder + link a fresh
    /// placeholder if more frames follow).
    /// </summary>
    /// <param name="seg">The segment whose GetSpan was called.</param>
    /// <param name="isFirst">True if this is the first segment.</param>
    internal void OnSegmentGetSpan(LazyHookedSegment seg, bool isFirst)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!isFirst)
        {
            // Rotation: parser has advanced PAST the segment that referenced
            // _currentFrame; that frame becomes _prevFrame. The frame backing
            // THIS segment (assigned by the previous GetSpan's trampoline)
            // becomes the new _currentFrame.
            //
            // Release any residual _prevFrame (which was the frame TWO
            // segments back; parser has thoroughly finished with it).
            if (_prevFrame.Length > 0)
            {
                _prevFrame.ReturnToPool();
                _prevFrame = default;
            }
            _prevFrame = _currentFrame;
            _currentFrame = seg.AssignedFrame;
            // _currentFrame now owns the frame previously tracked by
            // _pendingFrame (the trampoline pulled it during the prior
            // segment's GetSpan). Clear pending so Dispose doesn't
            // double-release.
            _pendingFrame = default;
        }
        // else: seg[0]. _currentFrame was assigned at construction (= firstFrame).
        // _prevFrame is default (no prior frame). No rotation.

        // Trampoline: if seg.Next is an unfulfilled placeholder, pull
        // frame[i+1] now and fulfill it. The parser's next MoveNext will
        // capture seg.Next's updated Memory.
        if (seg.NextSegment is LazyHookedSegment nextSeg && !nextSeg.IsFulfilled)
        {
            FulfillPlaceholder(nextSeg);
        }
    }

    /// <summary>
    /// Pulls the next frame and fulfills <paramref name="placeholder"/>.
    /// Also extends the chain past <paramref name="placeholder"/> with
    /// either a fresh placeholder (if more frames are still needed) or
    /// leaves the existing sentinel link in place (last frame).
    /// </summary>
    private void FulfillPlaceholder(LazyHookedSegment placeholder)
    {
        var pulled = _pullNext(_ct);
        if (pulled is null)
        {
            throw new IOException(
                $"LazyChainRos: pullNext returned null at " +
                $"{_linkedLength}/{_totalBodyLen} bytes consumed. " +
                "Upstream channel ended before the declared LPM body was complete.");
        }
        var frame = pulled.Value;

        // Cap the segment's effective length so the chain never exposes more
        // bytes than the declared body length. If the producer over-shoots
        // (e.g., multiple LPMs batched into one wire frame), we trim here;
        // the caller is responsible for handling residue.
        var remaining = _totalBodyLen - _linkedLength;
        var effective = (int)Math.Min(frame.Length, remaining);
        if (effective <= 0)
        {
            frame.ReturnToPool();
            throw new IOException(
                "LazyChainRos: pullNext returned a frame after the declared body " +
                "length was already covered.");
        }

        var slice = frame.Memory.Slice(0, effective);
        placeholder.Fulfill(frame, slice);
        _linkedLength += effective;
        // Track the just-pulled frame so Dispose can return it to the
        // pool if the parser throws before reaching the rotation that
        // would have promoted it to _currentFrame.
        _pendingFrame = frame;

        if (_linkedLength < _totalBodyLen)
        {
            // More frames follow. Allocate a fresh empty placeholder and
            // link it after the just-fulfilled segment.
            var nextPlaceholder = new LazyHookedSegment(
                this, ReadOnlyMemory<byte>.Empty,
                runningIndex: _linkedLength, isFirst: false);
            nextPlaceholder.SetNext(_sentinel);
            placeholder.SetNext(nextPlaceholder);
        }
        // else: placeholder is the last segment; placeholder.Next was already
        // set to the sentinel at construction time.
    }

    /// <summary>
    /// Releases any frames still held. Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_prevFrame.Length > 0)
        {
            _prevFrame.ReturnToPool();
            _prevFrame = default;
        }
        if (_currentFrame.Length > 0)
        {
            _currentFrame.ReturnToPool();
            _currentFrame = default;
        }
        // Release any frame the trampoline pre-pulled for the next
        // segment but the parser never advanced into. Without this,
        // exception-during-parse paths would leak that frame's pool
        // buffer (and its ZC ring reservation, if any).
        if (_pendingFrame.Length > 0)
        {
            _pendingFrame.ReturnToPool();
            _pendingFrame = default;
        }
    }
}

/// <summary>
/// Empty-memory sentinel segment placed at the end of the chain to
/// terminate <c>ReadOnlySequence&lt;byte&gt;</c> walks. Its
/// <see cref="ReadOnlySequenceSegment{T}.RunningIndex"/> equals the
/// total body length, ensuring the ROS reports the correct
/// <c>Length</c>.
/// </summary>
internal sealed class LazySentinelSegment : ReadOnlySequenceSegment<byte>
{
    public LazySentinelSegment(long runningIndex)
    {
        Memory = ReadOnlyMemory<byte>.Empty;
        RunningIndex = runningIndex;
    }
}

/// <summary>
/// Chain segment whose <see cref="ReadOnlySequenceSegment{T}.Memory"/>
/// is wired through a <see cref="MemoryManager{T}"/>. The MM's
/// <c>GetSpan</c> fires a hook into <see cref="LazyChainRos"/> exactly
/// once (on first access by the parser), driving rotation and the
/// trampoline.
/// </summary>
internal sealed class LazyHookedSegment : ReadOnlySequenceSegment<byte>
{
    private readonly LazyChainRos _owner;
    private readonly bool _isFirst;
    private readonly HookMemoryManager _mm;
    private InboundFrame _assignedFrame;
    private bool _isFulfilled;

    public LazyHookedSegment(
        LazyChainRos owner,
        ReadOnlyMemory<byte> initialBytes,
        long runningIndex,
        bool isFirst)
    {
        _owner = owner;
        _isFirst = isFirst;
        _mm = new HookMemoryManager(this, initialBytes);
        // Wrap MM as the segment's Memory. The captured length is
        // initialBytes.Length AT THIS MOMENT. For the first segment that's
        // already correct (firstFrame data); for placeholders it's 0 and
        // gets replaced via Fulfill before the parser MoveNexts into it.
        Memory = _mm.AsMemory();
        RunningIndex = runningIndex;
        _isFulfilled = isFirst;  // first segment is born fulfilled
    }

    /// <summary>True if a frame has been bound to this segment.</summary>
    public bool IsFulfilled => _isFulfilled;

    /// <summary>The frame backing this segment after fulfillment.</summary>
    public InboundFrame AssignedFrame => _assignedFrame;

    /// <summary>The next segment in the chain (typed access for hot loop).</summary>
    public ReadOnlySequenceSegment<byte>? NextSegment => Next;

    public void SetNext(ReadOnlySequenceSegment<byte> next) => Next = next;

    /// <summary>
    /// Binds <paramref name="frame"/> to this segment and updates
    /// <see cref="ReadOnlySequenceSegment{T}.Memory"/> to expose
    /// <paramref name="bytes"/>. Called by the trampoline running inside
    /// the previous segment's GetSpan, BEFORE the parser's next MoveNext.
    /// </summary>
    public void Fulfill(InboundFrame frame, ReadOnlyMemory<byte> bytes)
    {
        if (_isFulfilled)
        {
            throw new InvalidOperationException("Segment already fulfilled.");
        }
        _assignedFrame = frame;
        _mm.SetBackingMemory(bytes);
        Memory = _mm.AsMemory();
        _isFulfilled = true;
    }

    internal void RaiseGetSpan() => _owner.OnSegmentGetSpan(this, _isFirst);

    /// <summary>
    /// Memory manager whose <c>GetSpan</c> fires the chain hook exactly
    /// once, then returns the underlying byte view.
    /// </summary>
    private sealed class HookMemoryManager : MemoryManager<byte>
    {
        private readonly LazyHookedSegment _seg;
        private ReadOnlyMemory<byte> _backing;
        private bool _hookFired;

        public HookMemoryManager(LazyHookedSegment seg, ReadOnlyMemory<byte> initialBacking)
        {
            _seg = seg;
            _backing = initialBacking;
        }

        public void SetBackingMemory(ReadOnlyMemory<byte> bytes) => _backing = bytes;

        /// <summary>Returns the segment's <see cref="Memory{T}"/> handle
        /// rooted at this manager with the current backing length.</summary>
        public Memory<byte> AsMemory() => CreateMemory(_backing.Length);

        public override Span<byte> GetSpan()
        {
            if (!_hookFired)
            {
                _hookFired = true;
                _seg.RaiseGetSpan();
                // After RaiseGetSpan returns, _backing may have been updated
                // (for a placeholder fulfilled by the trampoline running in
                // a PREVIOUS segment's GetSpan — but that already happened
                // before now; the trampoline modifies LATER segments, not
                // the current one). For first-call placeholders, _backing
                // was set during prev seg's GetSpan via SetBackingMemory.
            }
            return GetReadOnlyBackingSpan();
        }

        /// <summary>
        /// Returns a writable Span<byte> wrapper around the read-only
        /// backing memory. Safe because protobuf's parser only READS from
        /// it (the parser never writes through the span).
        /// </summary>
        private Span<byte> GetReadOnlyBackingSpan()
        {
            // System.Runtime.InteropServices.MemoryMarshal.AsMemory + .Span
            // is the canonical way to expose a ROM as a writable Memory.
            // For our use, parser only reads, so this is sound.
            var asMem = System.Runtime.InteropServices.MemoryMarshal.AsMemory(_backing);
            return asMem.Span;
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            // Pin is unused by protobuf's ROS parser; return a no-op handle.
            return default;
        }

        public override void Unpin() { }

        protected override void Dispose(bool disposing) { }
    }
}
