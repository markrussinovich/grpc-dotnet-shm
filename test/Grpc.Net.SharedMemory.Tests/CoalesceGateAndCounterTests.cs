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

using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Regression tests for the GPT-5.5 PR review findings on
/// <c>feat/shm-http2-flow-control</c> (May 2026):
///
///   F1/F2: server &amp; client wake-coalesce gates must use the same
///          single-frame threshold as <see cref="ShmFrameWriter.WriteInlineDirectMultiFrame"/>
///          actually applies for chunking. A looser gate causes
///          deadlock when the writer chunks under the suppressed-signal
///          batch. Validated by <see cref="CanCoalesceInlineMessage_BoundaryIsConsistentWithChunkingThreshold"/>.
///   H1:    <see cref="ShmConnection.ActiveStreamCount"/> uses atomic
///          counters that MUST reset on disposal. Validated by
///          <see cref="ActiveStreamCount_ResetsAfterDispose"/>.
/// </summary>
[TestFixture]
public class CoalesceGateAndCounterTests
{
    [Test]
    public void CanCoalesceInlineMessage_BoundaryIsConsistentWithChunkingThreshold()
    {
        // 64 MiB ring (matches RingBench default). With no
        // SHM_FAIR_MAX_FRAME env override the threshold should be
        // min(cap/3, H2 24-bit max) = min(~21.3 MiB, ~16 MiB - 1) =
        // 16 MiB - 1 = 16_777_215 bytes.
        var name = $"test_coalesce_{Guid.NewGuid():N}";
        using var connection = ShmConnection.CreateAsServer(name, ringCapacity: 64 * 1024 * 1024, maxStreams: 100);
        var writer = connection.FrameWriter;
        Assert.That(writer, Is.Not.Null, "FrameWriter should be available on server connection");

        // Binary-search the boundary to be robust to env-driven cap
        // overrides (e.g. SHM_FAIR_MAX_FRAME).
        int low = 1, high = 64 * 1024 * 1024;
        while (low < high)
        {
            int mid = (int)(((long)low + high + 1) / 2);
            if (writer!.CanCoalesceInlineMessage(mid))
                low = mid;
            else
                high = mid - 1;
        }
        int threshold = low;

        // Sanity: threshold must not exceed cap/3 (one of the
        // factors in the canonical formula).
        Assert.That(threshold, Is.LessThanOrEqualTo(64 * 1024 * 1024 / 3),
            "threshold must be at most ringCapacity / 3");

        // Boundary is exact: threshold passes, threshold+1 fails.
        // This is the property that the server / client coalesce
        // gates rely on (writer chunks at threshold+1, coalesce gate
        // must refuse at the same point).
        Assert.That(writer!.CanCoalesceInlineMessage(threshold), Is.True,
            "exactly at threshold MUST be coalesceable");
        Assert.That(writer!.CanCoalesceInlineMessage(threshold + 1), Is.False,
            "one byte past threshold MUST NOT be coalesceable");

        // Trivially small messages always coalesce when below the
        // discovered threshold. (Don't hard-code a size since
        // SHM_FAIR_MAX_FRAME may be set to a small value at test time.)
        Assert.That(writer!.CanCoalesceInlineMessage(0), Is.True);
        if (threshold >= 1)
        {
            Assert.That(writer!.CanCoalesceInlineMessage(1), Is.True);
        }
        if (threshold >= 5 + 1024)
        {
            Assert.That(writer!.CanCoalesceInlineMessage(5 + 1024), Is.True, "1 KiB Unary always fits unless SHM_FAIR_MAX_FRAME is set extremely low");
        }
    }

    [Test]
    public void ActiveStreamCount_ResetsAfterDispose()
    {
        // Drive the server-side counter via reflection so we don't
        // need a fully-connected client/server pair: in production it
        // is incremented inside HandleHeadersFrame after the inbound
        // HEADERS frame's stream is added to _streams; that path is
        // only reachable through the frame-reader loop and is hard
        // to stage in a unit test. The Dispose-reset behavior is
        // the symmetric concern.
        var name = $"test_count_dispose_{Guid.NewGuid():N}";
        var connection = ShmConnection.CreateAsServer(name, ringCapacity: 64 * 1024, maxStreams: 100);

        SetPrivateCounter(connection, "_serverStreamCount", 5);
        SetPrivateCounter(connection, "_clientStreamCount", 7);
        Assert.That(connection.ActiveStreamCount, Is.EqualTo(5),
            "sanity: ActiveStreamCount reads _serverStreamCount on server connection");

        connection.Dispose();

        // Both counters MUST be 0 after Dispose so post-dispose
        // diagnostics report the actual state (which is 0 — _streams
        // was cleared). Prior to GPT-5.5 H1 fix the counters stayed
        // at their pre-dispose values.
        Assert.That(GetPrivateCounter(connection, "_serverStreamCount"), Is.EqualTo(0),
            "_serverStreamCount MUST reset to 0 after Dispose");
        Assert.That(GetPrivateCounter(connection, "_clientStreamCount"), Is.EqualTo(0),
            "_clientStreamCount MUST reset to 0 after Dispose");
        Assert.That(connection.ActiveStreamCount, Is.EqualTo(0));
    }

    [Test]
    public async Task ActiveStreamCount_ResetsAfterDisposeAsync()
    {
        var name = $"test_count_dispose_async_{Guid.NewGuid():N}";
        var connection = ShmConnection.CreateAsServer(name, ringCapacity: 64 * 1024, maxStreams: 100);

        SetPrivateCounter(connection, "_serverStreamCount", 3);
        SetPrivateCounter(connection, "_clientStreamCount", 9);

        await connection.DisposeAsync();

        Assert.That(GetPrivateCounter(connection, "_serverStreamCount"), Is.EqualTo(0),
            "_serverStreamCount MUST reset to 0 after DisposeAsync");
        Assert.That(GetPrivateCounter(connection, "_clientStreamCount"), Is.EqualTo(0),
            "_clientStreamCount MUST reset to 0 after DisposeAsync");
    }

    [Test]
    public void CanCoalesceMultiFrameMessage_BoundaryIsCapDivEight()
    {
        // Round-11 multi-frame coalesce predicate is LOOSER than
        // the single-frame CanCoalesceInlineMessage: it drops the
        // FairMaxFramePayload clamp and admits messages up to
        // min(cap/8, H2_24bit_max). The writer still chunks at
        // FairMaxFramePayload boundaries but all chunks happen
        // under one suppressed-signal batch.
        var name = $"test_coalesce_multiframe_{Guid.NewGuid():N}";
        using var connection = ShmConnection.CreateAsServer(name, ringCapacity: 64 * 1024 * 1024, maxStreams: 100);
        var writer = connection.FrameWriter;
        Assert.That(writer, Is.Not.Null);

        // Binary-search boundary.
        int low = 1, high = 64 * 1024 * 1024;
        while (low < high)
        {
            int mid = (int)(((long)low + high + 1) / 2);
            if (writer!.CanCoalesceMultiFrameMessage(mid))
                low = mid;
            else
                high = mid - 1;
        }
        int threshold = low;

        // Boundary should be exactly min(cap/8, H2max).
        var expectedThreshold = Math.Min(64 * 1024 * 1024 / 8,
            Grpc.Net.SharedMemory.Wire.Http2FrameHeader.MaxAllowedPayloadLength);
        Assert.That(threshold, Is.EqualTo(expectedThreshold),
            "MultiFrame threshold must be min(cap/8, H2max) — NOT FairMax-clamped");

        // Multi-frame predicate MUST admit messages above FairMaxFramePayload
        // (when FairMax is set, e.g. 16384) as long as cumulative bytes
        // fit in cap/8. This is the whole point of the new predicate.
        Assert.That(writer!.CanCoalesceMultiFrameMessage(threshold), Is.True);
        Assert.That(writer!.CanCoalesceMultiFrameMessage(threshold + 1), Is.False);

        // Multi-frame must be at least as permissive as single-frame.
        // If single-frame returns true, multi-frame must also return true.
        for (int probe = 1; probe < threshold; probe = Math.Max(probe + 1, probe + probe / 4))
        {
            if (writer!.CanCoalesceInlineMessage(probe))
            {
                Assert.That(writer!.CanCoalesceMultiFrameMessage(probe), Is.True,
                    $"MultiFrame predicate must admit any size SingleFrame admits (probe={probe})");
            }
        }
    }

    [Test]
    public void CoalesceLatencyCapBytes_Is128KiB()
    {
        // Round-11 bumped CoalesceLatencyCapBytes from 64 KiB to 128 KiB
        // so mid-payload sizes (32K-128K) can coalesce in non-Fair windows.
        // Fair mode is still capped by SendQuota=65535 regardless.
        Assert.That(ShmFrameWriter.CoalesceLatencyCapBytes, Is.EqualTo(128 * 1024),
            "CoalesceLatencyCapBytes constant must be 128 KiB (round-11)");
    }

    private static void SetPrivateCounter(ShmConnection connection, string fieldName, int value)
    {
        var f = typeof(ShmConnection).GetField(fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(f, Is.Not.Null, $"private field '{fieldName}' must exist");
        f!.SetValue(connection, value);
    }

    private static int GetPrivateCounter(ShmConnection connection, string fieldName)
    {
        var f = typeof(ShmConnection).GetField(fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(f, Is.Not.Null, $"private field '{fieldName}' must exist");
        return (int)f!.GetValue(connection)!;
    }
}
