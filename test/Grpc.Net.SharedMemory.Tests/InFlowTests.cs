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

using Grpc.Net.SharedMemory.Synchronization;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Unit tests for <see cref="InFlow"/> and <see cref="TrInFlow"/>
/// HTTP/2-compatible inbound flow-control state machines. Mirrors the
/// Go reference test suite in <c>internal/transport/shm_flow_control_test.go</c>
/// and <c>internal/transport/flowcontrol.go</c>.
/// </summary>
[TestFixture]
public class InFlowTests
{
    // ============================================================
    // InFlow basic accounting
    // ============================================================

    [Test]
    public void OnData_WithinLimit_ReturnsNull()
    {
        var f = new InFlow(initialLimit: 65535);
        Assert.That(f.OnData(1024), Is.Null);
        Assert.That(f.PendingData, Is.EqualTo(1024u));
    }

    [Test]
    public void OnData_OverLimit_ReturnsError()
    {
        var f = new InFlow(initialLimit: 1024);
        Assert.That(f.OnData(1024), Is.Null, "first chunk fills exactly");
        var err = f.OnData(1);
        Assert.That(err, Is.Not.Null, "1 byte over limit must surface RFC 7540 §5.2.2 error");
        Assert.That(err, Does.Contain("exceeding the limit"));
    }

    [Test]
    public void OnRead_BelowQuarterThreshold_Returns0()
    {
        var f = new InFlow(initialLimit: 65536);
        Assert.That(f.OnData(20000), Is.Null);
        // Reading 4000 bytes — pendingUpdate=4000 < 65536/4=16384 → no WU
        Assert.That(f.OnRead(4000), Is.EqualTo(0u));
    }

    [Test]
    public void OnRead_AtThreshold_FlushesAccumulator()
    {
        var f = new InFlow(initialLimit: 65536);
        Assert.That(f.OnData(20000), Is.Null);
        // Read enough to cross limit/4 = 16384 byte threshold
        var wu = f.OnRead(16384);
        Assert.That(wu, Is.EqualTo(16384u), "WU emit equals accumulated pendingUpdate");
        Assert.That(f.OnRead(100), Is.EqualTo(0u), "next read accumulates fresh");
    }

    // ============================================================
    // InFlow.MaybeAdjustAdditive — the SHM-specific LPM pre-credit
    // ============================================================

    [Test]
    public void MaybeAdjustAdditive_AnnouncedFitsInWindow_Returns0()
    {
        var f = new InFlow(initialLimit: 65535);
        Assert.That(f.MaybeAdjustAdditive(40000), Is.EqualTo(0u), "fits in baseline window");
        Assert.That(f.Delta, Is.EqualTo(0u));
    }

    [Test]
    public void MaybeAdjustAdditive_OversizeLpm_ReturnsDelta()
    {
        var f = new InFlow(initialLimit: 65535);
        // 200 KiB LPM exceeds 64 KiB baseline window; need (200 KiB - 65535) extra
        var n = 200u * 1024;
        var wu = f.MaybeAdjustAdditive(n);
        Assert.That(wu, Is.EqualTo(n - 65535), "extra credit needed equals lpmSize - avail");
        Assert.That(f.Delta, Is.EqualTo(wu), "delta accumulates the emitted pre-credit");
    }

    [Test]
    public void MaybeAdjustAdditive_PipelinedLargeLpms_AccumulatesDelta_NoFlowControlError()
    {
        // Regression test mirroring Go's TestInFlow_MaybeAdjustAdditive_PipelinedLargeLPMs.
        // Trigger conditions: small initial window (64 KiB) + two pipelined large LPMs
        // (200 KiB each) on one stream with reader delayed past the first message.
        // Stock maybeAdjust (SET) would zero the first LPM's outstanding pre-credit
        // when the second LPM hooks; onData of the second LPM's bytes would then
        // exceed (limit + delta) and falsely trip FLOW_CONTROL_ERROR.
        var f = new InFlow(initialLimit: 65535);
        var lpmSize = 200u * 1024;

        // LPM 1 arrives at parse time. Pre-credit fires.
        var wu1 = f.MaybeAdjustAdditive(lpmSize);
        Assert.That(wu1, Is.GreaterThan(0u), "first LPM pre-credit must be emitted");
        // Sender writes the full LPM into the ring.
        Assert.That(f.OnData(lpmSize), Is.Null, "LPM 1 bytes fit in (limit + delta)");

        // LPM 2 arrives at parse time BEFORE the application has consumed LPM 1
        // from the receive buffer. Pre-credit fires again.
        var wu2 = f.MaybeAdjustAdditive(lpmSize);
        Assert.That(wu2, Is.GreaterThan(0u), "second LPM pre-credit must be emitted");

        // The crucial assertion: with ADDITIVE semantics, delta accumulates both
        // pre-credits, so the second LPM's bytes also fit. With Go's stock SET
        // semantics, delta would have been overwritten and OnData would fail here.
        Assert.That(f.OnData(lpmSize), Is.Null,
            "additive delta must admit LPM 2 even before LPM 1 is read");
    }

    [Test]
    public void MaybeAdjustAdditive_CapsAtMaxWindowSize()
    {
        var f = new InFlow(initialLimit: 65535);
        // Push delta toward MaxWindowSize = int.MaxValue
        f.MaybeAdjustAdditive(1u << 30); // ~1 GiB
        f.MaybeAdjustAdditive(1u << 30);
        f.MaybeAdjustAdditive(1u << 30);
        // limit + delta must stay <= MaxWindowSize = int.MaxValue
        var sum = (long)f.Limit + f.Delta;
        Assert.That(sum, Is.LessThanOrEqualTo((long)InFlow.MaxWindowSize),
            $"limit + delta={sum} must not exceed MaxWindowSize={InFlow.MaxWindowSize}");
    }

    [Test]
    public void OnRead_DrainsDeltaBeforeAccumulating()
    {
        var f = new InFlow(initialLimit: 65535);
        var lpmSize = 200u * 1024;

        // Pre-credit for oversize LPM.
        f.MaybeAdjustAdditive(lpmSize);
        var initialDelta = f.Delta;
        Assert.That(initialDelta, Is.GreaterThan(0u));

        // Receive the LPM.
        Assert.That(f.OnData(lpmSize), Is.Null);

        // Application reads. The first bytes go toward draining delta; only the
        // residual contributes to pendingUpdate.
        var wu = f.OnRead(initialDelta);
        Assert.That(wu, Is.EqualTo(0u), "consuming exactly delta drains it; no WU yet");
        Assert.That(f.Delta, Is.EqualTo(0u));

        // Now read enough more to cross limit/4 threshold.
        var quarter = f.Limit / 4;
        Assert.That(f.OnRead(quarter), Is.EqualTo(quarter), "WU emitted at limit/4");
    }

    // ============================================================
    // Concurrency sanity (lock-protected fields)
    // ============================================================

    [Test]
    [CancelAfter(10_000)]
    public void ConcurrentOnDataAndOnRead_NoCorruption()
    {
        var f = new InFlow(initialLimit: 65536 * 32);
        const int n = 10_000;
        var produced = 0;
        var consumed = 0;

        var producer = Task.Run(() =>
        {
            for (int i = 0; i < n; i++)
            {
                if (f.OnData(8) == null)
                {
                    Interlocked.Increment(ref produced);
                }
            }
        });
        var consumer = Task.Run(() =>
        {
            for (int i = 0; i < n; i++)
            {
                // Tight read loop will produce some WUs; we just ensure no exception.
                _ = f.OnRead(8);
                Interlocked.Increment(ref consumed);
            }
        });
        Task.WaitAll(producer, consumer);

        Assert.That(produced, Is.EqualTo(n), "all OnData calls succeeded under sane window");
        Assert.That(consumed, Is.EqualTo(n));
    }
}

/// <summary>
/// Unit tests for <see cref="TrInFlow"/> conn-level inbound flow control.
/// </summary>
[TestFixture]
public class TrInFlowTests
{
    [Test]
    public void OnData_BelowQuarterThreshold_Returns0()
    {
        var t = new TrInFlow(initialLimit: 65536);
        Assert.That(t.OnData(8000), Is.EqualTo(0u), "below limit/4 = 16384");
    }

    [Test]
    public void OnData_AtThreshold_FlushesAccumulator()
    {
        var t = new TrInFlow(initialLimit: 65536);
        Assert.That(t.OnData(8000), Is.EqualTo(0u));
        var wu = t.OnData(8400); // total 16400 >= 16384 = limit/4
        Assert.That(wu, Is.EqualTo(16400u));
        Assert.That(t.Unacked, Is.EqualTo(0u), "accumulator reset after flush");
    }

    [Test]
    public void Reset_FlushesPendingBytes()
    {
        var t = new TrInFlow(initialLimit: 65536);
        Assert.That(t.OnData(8000), Is.EqualTo(0u));
        Assert.That(t.Reset(), Is.EqualTo(8000u), "Reset flushes accumulator below threshold");
        Assert.That(t.Reset(), Is.EqualTo(0u), "second Reset is idempotent");
    }

    [Test]
    public void EffectiveWindowSize_ReflectsUnacked()
    {
        var t = new TrInFlow(initialLimit: 100_000);
        Assert.That(t.EffectiveWindowSize, Is.EqualTo(100_000u));
        t.OnData(10_000);
        Assert.That(t.EffectiveWindowSize, Is.EqualTo(90_000u));
    }

    [Test]
    public void NewLimit_GrowsAndReturnsDelta()
    {
        var t = new TrInFlow(initialLimit: 65535);
        var d = t.NewLimit(131072);
        Assert.That(d, Is.EqualTo(131072u - 65535));
        Assert.That(t.Limit, Is.EqualTo(131072u));
    }
}
