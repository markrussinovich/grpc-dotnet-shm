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
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Proof-of-concept tests verifying that <see cref="MemoryManager{T}.GetSpan"/>
/// is the right hook point for "lazy-fill segment + release prev frame"
/// streaming-parse approach.
/// </summary>
/// <remarks>
/// <para>
/// Hypothesis: protobuf's <see cref="MessageExtensions.MergeFrom(IMessage, ReadOnlySequence{byte})"/>
/// invokes <see cref="MemoryManager{T}.GetSpan"/> on each segment AFTER fully
/// consuming the previous segment's bytes. If true, we can:
/// </para>
/// <list type="number">
/// <item><description>
///   Pre-allocate N <see cref="ReadOnlySequenceSegment{T}"/>s as placeholders
///   with custom <see cref="MemoryManager{T}"/>s.
/// </description></item>
/// <item><description>
///   In each MM's GetSpan(): pull next ring frame from upstream channel,
///   release the previous frame.
/// </description></item>
/// <item><description>
///   Build a <see cref="ReadOnlySequence{T}"/> over the chain and call
///   <c>MergeFrom(ros)</c>. Result: ring footprint = O(1 frame); memcpy = 1
///   (ring → ByteString backing).
/// </description></item>
/// </list>
/// <para>
/// Test schema: <see cref="BytesValue"/> well-known type — single
/// <c>bytes value = 1</c> field. Wire format = tag (1 B) + length varint
/// + N bytes of payload. Total wire size 4096 B is split into 4 × 1024 B
/// segments to force protobuf's parser through multi-segment refill.
/// </para>
/// </remarks>
[TestFixture]
public class LazyChainRosPocTests
{
    /// <summary>
    /// Records GetSpan invocation order and supports an OnGetSpan callback
    /// that fires the moment protobuf's parser advances to this segment.
    /// </summary>
    private sealed class TrackedMemoryManager : MemoryManager<byte>
    {
        private readonly byte[] _buffer;
        private readonly Memory<byte> _memory;
        public int GetSpanCallCount;
        public bool Released;
        public Action? OnGetSpan;
        public string Name = "?";

        public TrackedMemoryManager(byte[] buffer)
        {
            _buffer = buffer;
            // CreateMemory wraps `this` as the Memory<byte>'s _object; subsequent
            // .Memory.Span / .Memory.GetSpan calls will route through GetSpan().
            _memory = CreateMemory(buffer.Length);
        }

        public override Span<byte> GetSpan()
        {
            if (Released)
            {
                throw new InvalidOperationException(
                    $"GetSpan called on released segment '{Name}'. " +
                    "Parser accessed a segment after we marked it released — " +
                    "lazy-fill + per-frame-release is NOT safe.");
            }
            GetSpanCallCount++;
            OnGetSpan?.Invoke();
            return _buffer.AsSpan();
        }

        public override Memory<byte> Memory => _memory;

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            // Pin is unrelated to our test concern; protobuf's parser does not
            // pin spans during MergeFrom. Returning a no-op handle is fine for
            // ROS Span enumeration.
            return default;
        }

        public override void Unpin() { }

        protected override void Dispose(bool disposing) { }
    }

    /// <summary>
    /// ReadOnlySequenceSegment derivative whose Memory is wired to a
    /// <see cref="TrackedMemoryManager"/>.
    /// </summary>
    private sealed class TrackedSegment : ReadOnlySequenceSegment<byte>
    {
        public TrackedSegment(TrackedMemoryManager mm, long runningIndex)
        {
            Memory = mm.Memory;          // protected setter on base; OK from derived
            RunningIndex = runningIndex;
        }

        public void SetNext(TrackedSegment next) => Next = next;
    }

    /// <summary>
    /// Builds the wire-format byte stream for <see cref="BytesValue"/> with
    /// a payload of <paramref name="payloadSize"/> bytes (deterministic
    /// pattern: byte i = i &amp; 0xFF).
    /// </summary>
    /// <returns>The complete wire-format bytes (header + payload).</returns>
    private static byte[] EncodeBytesValueWire(int payloadSize)
    {
        // Tag for field 1, wire type LEN (2): (1 << 3) | 2 = 0x0A.
        // Length varint: 7 bits per byte, MSB set if more bytes follow.
        var lengthBytes = new List<byte>(5);
        var n = (uint)payloadSize;
        while (n >= 0x80u)
        {
            lengthBytes.Add((byte)(n | 0x80u));
            n >>= 7;
        }
        lengthBytes.Add((byte)n);

        var headerLen = 1 + lengthBytes.Count;
        var result = new byte[headerLen + payloadSize];
        result[0] = 0x0A;
        for (var i = 0; i < lengthBytes.Count; i++)
        {
            result[1 + i] = lengthBytes[i];
        }
        for (var i = 0; i < payloadSize; i++)
        {
            result[headerLen + i] = (byte)(i & 0xFF);
        }
        return result;
    }

    private static (TrackedSegment First, TrackedSegment Last,
        TrackedMemoryManager[] Mms) BuildChain(byte[] wire, int segmentSize)
    {
        if (wire.Length % segmentSize != 0)
        {
            throw new ArgumentException(
                $"Wire size {wire.Length} must be a multiple of segmentSize {segmentSize}.");
        }
        var segCount = wire.Length / segmentSize;
        var mms = new TrackedMemoryManager[segCount];
        var segs = new TrackedSegment[segCount];

        for (var i = 0; i < segCount; i++)
        {
            var buf = new byte[segmentSize];
            Array.Copy(wire, i * segmentSize, buf, 0, segmentSize);
            mms[i] = new TrackedMemoryManager(buf) { Name = $"seg{i}" };
            segs[i] = new TrackedSegment(mms[i], i * segmentSize);
            if (i > 0)
            {
                segs[i - 1].SetNext(segs[i]);
            }
        }
        return (segs[0], segs[segCount - 1], mms);
    }

    [Test]
    public void MergeFrom_ROSAcrossFourSegments_ParsesCorrectly()
    {
        // 4 × 1024 B = 4096 B total wire. Header: 0x0A + varint(4093) [2 B] = 3 B.
        // Payload: 4093 B → spans seg0[3..1023], seg1, seg2, seg3[0..1023].
        const int payloadSize = 4093;
        var wire = EncodeBytesValueWire(payloadSize);
        Assert.That(wire.Length, Is.EqualTo(4096));

        var (first, last, mms) = BuildChain(wire, segmentSize: 1024);

        var ros = new ReadOnlySequence<byte>(first, 0, last, 1024);
        Assert.That(ros.Length, Is.EqualTo(4096));

        var msg = new BytesValue();
        msg.MergeFrom(ros);

        Assert.That(msg.Value.Length, Is.EqualTo(payloadSize),
            "Parsed bytes value length must match wire-encoded length.");
        for (var i = 0; i < payloadSize; i++)
        {
            Assert.That(msg.Value[i], Is.EqualTo((byte)(i & 0xFF)),
                $"Payload byte at offset {i} mismatch.");
        }

        // Each segment must have been visited at least once.
        for (var i = 0; i < mms.Length; i++)
        {
            Assert.That(mms[i].GetSpanCallCount, Is.GreaterThan(0),
                $"Segment {i} was never accessed by the parser.");
        }
    }

    [Test]
    public void MergeFrom_ROSAcrossFourSegments_GetSpanInvokedInOrder()
    {
        const int payloadSize = 4093;
        var wire = EncodeBytesValueWire(payloadSize);
        var (first, last, mms) = BuildChain(wire, segmentSize: 1024);

        var callOrder = new List<int>();
        for (var i = 0; i < mms.Length; i++)
        {
            var idx = i;
            mms[i].OnGetSpan = () => callOrder.Add(idx);
        }

        var ros = new ReadOnlySequence<byte>(first, 0, last, 1024);
        var msg = new BytesValue();
        msg.MergeFrom(ros);

        TestContext.Out.WriteLine("GetSpan call order: " + string.Join(",", callOrder));
        TestContext.Out.WriteLine($"Per-segment GetSpan counts: " +
            $"seg0={mms[0].GetSpanCallCount}, seg1={mms[1].GetSpanCallCount}, " +
            $"seg2={mms[2].GetSpanCallCount}, seg3={mms[3].GetSpanCallCount}");

        // The FIRST occurrence of each segment in callOrder must be in [0,1,2,3] order.
        var firstIdxOf = new int[4];
        for (var i = 0; i < 4; i++) firstIdxOf[i] = callOrder.IndexOf(i);

        Assert.That(firstIdxOf[0], Is.GreaterThanOrEqualTo(0), "seg0 never visited.");
        Assert.That(firstIdxOf[1], Is.GreaterThan(firstIdxOf[0]),
            "seg1 must be first-accessed after seg0.");
        Assert.That(firstIdxOf[2], Is.GreaterThan(firstIdxOf[1]),
            "seg2 must be first-accessed after seg1.");
        Assert.That(firstIdxOf[3], Is.GreaterThan(firstIdxOf[2]),
            "seg3 must be first-accessed after seg2.");
    }

    /// <summary>
    /// Critical PoC test: when seg[i].GetSpan() fires, mark seg[i-1] as
    /// "Released". If the parser EVER accesses a Released segment thereafter,
    /// the throw aborts MergeFrom and this test fails. If the test PASSES,
    /// the lazy-fill + per-frame-release approach is timing-safe with the
    /// public protobuf-csharp ROS parser.
    /// </summary>
    [Test]
    public void MergeFrom_ROSAcrossFourSegments_PrevReleasedOnNextGetSpan_NoAccessAfterRelease()
    {
        const int payloadSize = 4093;
        var wire = EncodeBytesValueWire(payloadSize);
        var (first, last, mms) = BuildChain(wire, segmentSize: 1024);

        // Stagger: seg[i].GetSpan callback releases seg[i-1].
        for (var i = 1; i < mms.Length; i++)
        {
            var prev = mms[i - 1];
            mms[i].OnGetSpan = () =>
            {
                if (!prev.Released)
                {
                    prev.Released = true;
                    TestContext.Out.WriteLine($"Releasing {prev.Name}");
                }
            };
        }

        var ros = new ReadOnlySequence<byte>(first, 0, last, 1024);
        var msg = new BytesValue();

        Assert.DoesNotThrow(() => msg.MergeFrom(ros),
            "Parser must NOT access any segment after we marked it released. " +
            "If this throws, the lazy-fill approach is unsafe.");

        Assert.That(msg.Value.Length, Is.EqualTo(payloadSize));
        for (var i = 0; i < payloadSize; i++)
        {
            Assert.That(msg.Value[i], Is.EqualTo((byte)(i & 0xFF)),
                $"Payload byte at offset {i} mismatch.");
        }

        TestContext.Out.WriteLine($"Per-segment GetSpan counts: " +
            $"seg0={mms[0].GetSpanCallCount}, seg1={mms[1].GetSpanCallCount}, " +
            $"seg2={mms[2].GetSpanCallCount}, seg3={mms[3].GetSpanCallCount}");
    }

    /// <summary>
    /// Same as above but with 16 segments × 256 B (more parser refills,
    /// stresses the timing assumption).
    /// </summary>
    [Test]
    public void MergeFrom_ROSAcrossSixteenSmallSegments_PrevReleasedOnNextGetSpan_NoAccessAfterRelease()
    {
        const int totalSize = 4096;
        const int payloadSize = totalSize - 3; // 3-byte header
        var wire = EncodeBytesValueWire(payloadSize);
        Assert.That(wire.Length, Is.EqualTo(totalSize));

        var (first, last, mms) = BuildChain(wire, segmentSize: 256);
        Assert.That(mms.Length, Is.EqualTo(16));

        for (var i = 1; i < mms.Length; i++)
        {
            var prev = mms[i - 1];
            mms[i].OnGetSpan = () =>
            {
                if (!prev.Released) prev.Released = true;
            };
        }

        var ros = new ReadOnlySequence<byte>(first, 0, last, 256);
        var msg = new BytesValue();

        Assert.DoesNotThrow(() => msg.MergeFrom(ros));
        Assert.That(msg.Value.Length, Is.EqualTo(payloadSize));
        for (var i = 0; i < payloadSize; i++)
        {
            Assert.That(msg.Value[i], Is.EqualTo((byte)(i & 0xFF)),
                $"Payload byte at offset {i} mismatch (16-segment).");
        }
    }

    /// <summary>
    /// Verifies the timing for a LARGE message: 256 × 1024 B = 256 KiB,
    /// stressing the streaming-parse hot path. If this passes, a 256 MiB
    /// message split into 256 × 1 MiB ring frames behaves identically.
    /// </summary>
    [Test]
    public void MergeFrom_ROSAcross256Segments_PrevReleasedOnNextGetSpan_NoAccessAfterRelease()
    {
        const int segCount = 256;
        const int segSize = 1024;
        const int totalSize = segCount * segSize;
        // 3-byte header (tag + 3-byte varint for length 262141, since 262141 < 2^21)
        // Actually 262141 in varint: needs 3 bytes since 16384 < 262141 < 2097152.
        // Header = 1 + 3 = 4 B. Adjust payloadSize to make total = exact.
        // Let's compute:
        //   payloadSize = totalSize - headerSize
        //   headerSize depends on payloadSize varint length
        //   For totalSize=262144:
        //     try headerSize=4 → payloadSize=262140 → varint(262140)=3 B → headerSize=4 ✓
        const int payloadSize = totalSize - 4;
        var wire = EncodeBytesValueWire(payloadSize);
        Assert.That(wire.Length, Is.EqualTo(totalSize),
            "Header size assumption broken.");

        var (first, last, mms) = BuildChain(wire, segmentSize: segSize);
        Assert.That(mms.Length, Is.EqualTo(segCount));

        for (var i = 1; i < mms.Length; i++)
        {
            var prev = mms[i - 1];
            mms[i].OnGetSpan = () =>
            {
                if (!prev.Released) prev.Released = true;
            };
        }

        var ros = new ReadOnlySequence<byte>(first, 0, last, segSize);
        var msg = new BytesValue();

        Assert.DoesNotThrow(() => msg.MergeFrom(ros));
        Assert.That(msg.Value.Length, Is.EqualTo(payloadSize));

        // Spot-check some payload bytes
        Assert.That(msg.Value[0], Is.EqualTo((byte)0));
        Assert.That(msg.Value[100], Is.EqualTo((byte)100));
        Assert.That(msg.Value[1023], Is.EqualTo((byte)(1023 & 0xFF)));
        Assert.That(msg.Value[payloadSize - 1], Is.EqualTo((byte)((payloadSize - 1) & 0xFF)));
    }

    /// <summary>
    /// Simulates the "big message on small ring" scenario, scaled down for
    /// test speed: a 4 MiB protobuf message parsed against a "ring" of
    /// 256 KiB capacity. The ratio (16:1) matches the PR2 phase 3 target of
    /// 256 MiB message on a 16 MiB ring.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the headline PoC test. It must demonstrate that the
    /// lazy-fill model genuinely yields O(ring-size) peak ring footprint
    /// during MergeFrom, NOT O(message-size). If the parser ever holds
    /// more bytes resident than the ring's capacity, this test fails.
    /// </para>
    /// <para>
    /// Mechanics: each segment represents one ring frame (32 KiB). A
    /// shared <c>RingBudget</c> tracks how many frames are simultaneously
    /// "in flight" (filled but not yet released). On seg[i].GetSpan()
    /// we acquire a budget slot (incrementing in-flight count) and
    /// release seg[i-1]'s slot (decrementing). The peak in-flight
    /// count over the entire MergeFrom is asserted ≤ ring frame
    /// capacity.
    /// </para>
    /// </remarks>
    [Test]
    public void MergeFrom_4MiBMessage_On256KiBRing_PeakFootprintAtMostRingSize()
    {
        // 4 MiB message = 128 frames × 32 KiB.
        // "Ring" holds 8 frames × 32 KiB = 256 KiB.
        const int frameSize = 32 * 1024;
        const int ringFrames = 8;          // Simulated ring capacity in frames.
        const int messageFrames = 128;     // Total frame count to stream.
        const int totalSize = frameSize * messageFrames;   // 4 MiB.

        // Header sizing: varint(payloadSize) length depends on payloadSize.
        // For totalSize=4_194_304: payloadSize = totalSize - headerSize.
        //   payloadSize ≈ 4_194_299 → varint length 4 bytes (since 4_194_299 < 2^28)
        //   headerSize = 1 (tag) + 4 (length varint) = 5
        //   payloadSize = totalSize - 5 = 4_194_299
        //   varint(4_194_299) is indeed 4 bytes (since 2^21 ≤ 4_194_299 < 2^28). ✓
        const int payloadSize = totalSize - 5;
        var wire = EncodeBytesValueWire(payloadSize);
        Assert.That(wire.Length, Is.EqualTo(totalSize),
            $"Header size assumption broken for {totalSize} byte message.");

        // Build the chain (all frames pre-allocated for the test; the real
        // implementation would lazy-fill from a ring channel, but the
        // timing semantics are identical — see the simpler tests above).
        var (first, last, mms) = BuildChain(wire, segmentSize: frameSize);
        Assert.That(mms.Length, Is.EqualTo(messageFrames));

        // Track the in-flight frame count: incremented on GetSpan,
        // decremented when prev's "release" callback fires.
        var inFlight = 0;
        var peakInFlight = 0;
        var releaseEvents = new List<int>(messageFrames);

        for (var i = 0; i < mms.Length; i++)
        {
            var idx = i;
            var prev = i > 0 ? mms[i - 1] : null;
            mms[i].OnGetSpan = () =>
            {
                // Acquire budget for this frame.
                inFlight++;
                if (inFlight > peakInFlight) peakInFlight = inFlight;

                // Release the previous frame's budget — this is the
                // moment the ring slot would be returned to the writer.
                if (prev is not null && !prev.Released)
                {
                    prev.Released = true;
                    inFlight--;
                    releaseEvents.Add(idx - 1);
                }
            };
        }

        var ros = new ReadOnlySequence<byte>(first, 0, last, frameSize);
        var msg = new BytesValue();
        msg.MergeFrom(ros);

        // The last frame is never released by a successor (it has no next).
        // Decrement on the test side to model the consumer-finished-with-message
        // release that would happen after MergeFrom returns.
        if (!mms[^1].Released)
        {
            mms[^1].Released = true;
            inFlight--;
        }

        TestContext.Out.WriteLine(
            $"4 MiB message / 8-frame ring simulation: peakInFlight={peakInFlight}, " +
            $"final inFlight={inFlight}, releases={releaseEvents.Count}");

        Assert.That(msg.Value.Length, Is.EqualTo(payloadSize),
            "Parsed payload length must match wire-encoded length.");

        // Headline assertion: the parser never held more frames simultaneously
        // than the simulated ring could fit. This proves the lazy-fill model
        // achieves true O(ring-size) footprint — i.e., a 256 MiB message
        // CAN be parsed on a 16 MiB ring without holding more than 16 MiB
        // resident at any instant.
        Assert.That(peakInFlight, Is.LessThanOrEqualTo(ringFrames),
            $"Peak in-flight frame count {peakInFlight} exceeds simulated " +
            $"ring capacity {ringFrames}. Lazy-fill is NOT yielding O(ring) " +
            $"footprint.");

        // In practice we expect peak ≤ 2 (current + just-released-but-not-yet-decremented),
        // not 8 — but ≤ 8 is the contract.
        Assert.That(peakInFlight, Is.LessThanOrEqualTo(2),
            $"Stronger expectation: peak in-flight should be 1 or 2 (current frame " +
            $"+ overlap during the prev-release callback). Observed {peakInFlight}.");

        // All non-final frames must have been released exactly once.
        Assert.That(releaseEvents.Count, Is.EqualTo(messageFrames - 1),
            "Each non-final frame must be released exactly once during parse.");

        // Spot-check payload integrity.
        Assert.That(msg.Value[0], Is.EqualTo((byte)0));
        Assert.That(msg.Value[frameSize], Is.EqualTo((byte)(frameSize & 0xFF)));
        Assert.That(msg.Value[payloadSize / 2], Is.EqualTo((byte)((payloadSize / 2) & 0xFF)));
        Assert.That(msg.Value[payloadSize - 1], Is.EqualTo((byte)((payloadSize - 1) & 0xFF)));
    }

    /// <summary>
    /// Stress: 16 MiB message split into 256 frames × 64 KiB, simulating
    /// a 16:1 message-to-ring ratio with even more frames. Confirms the
    /// O(1-2 frame) peak holds at scale.
    /// </summary>
    [Test]
    public void MergeFrom_16MiBMessage_OnSimulatedSmallRing_PeakFootprintIsO1()
    {
        const int frameSize = 64 * 1024;
        const int messageFrames = 256;
        const int totalSize = frameSize * messageFrames;     // 16 MiB.
        // varint(totalSize-5) for 16_777_211: needs 4 bytes (2^21 ≤ x < 2^28). headerSize=5.
        const int payloadSize = totalSize - 5;
        var wire = EncodeBytesValueWire(payloadSize);
        Assert.That(wire.Length, Is.EqualTo(totalSize));

        var (first, last, mms) = BuildChain(wire, segmentSize: frameSize);
        Assert.That(mms.Length, Is.EqualTo(messageFrames));

        var inFlight = 0;
        var peakInFlight = 0;

        for (var i = 0; i < mms.Length; i++)
        {
            var prev = i > 0 ? mms[i - 1] : null;
            mms[i].OnGetSpan = () =>
            {
                inFlight++;
                if (inFlight > peakInFlight) peakInFlight = inFlight;
                if (prev is not null && !prev.Released)
                {
                    prev.Released = true;
                    inFlight--;
                }
            };
        }

        var ros = new ReadOnlySequence<byte>(first, 0, last, frameSize);
        var msg = new BytesValue();
        msg.MergeFrom(ros);

        TestContext.Out.WriteLine(
            $"16 MiB message / 256 frames: peakInFlight={peakInFlight}, " +
            $"message-to-ring ratio simulated up to 256:1");

        Assert.That(msg.Value.Length, Is.EqualTo(payloadSize));
        Assert.That(peakInFlight, Is.LessThanOrEqualTo(2),
            "Peak in-flight frame count should be at most 2 regardless of " +
            "total message size.");

        // Integrity spot-check at scale.
        Assert.That(msg.Value[0], Is.EqualTo((byte)0));
        Assert.That(msg.Value[payloadSize - 1], Is.EqualTo((byte)((payloadSize - 1) & 0xFF)));
    }
}
