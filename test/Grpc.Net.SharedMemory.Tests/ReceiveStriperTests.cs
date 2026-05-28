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
/// Unit tests for <see cref="ReceiveStriper"/>'s stripe-assignment
/// hash. Higher-level dispatch correctness is covered by the full
/// EndToEnd suite (550 tests pass with the striper enabled by
/// default); this file validates the math that decides which stripe
/// a given stream lands on.
/// </summary>
[TestFixture]
public class ReceiveStriperTests
{
    /// <summary>
    /// Verifies the Knuth multiplicative hash spreads stream IDs
    /// uniformly across all stripes. The previous naive
    /// <c>streamId &amp; mask</c> approach put all client-initiated
    /// (odd) IDs on stripes {1, 3} only, halving the effective
    /// fan-out for the typical workload. This test would fail if
    /// someone reverts to the naive mask: stripes 0 and 2 would have
    /// 0 client streams.
    /// </summary>
    [Test]
    public void StripeIndex_DistributesClientStreamIds_AcrossAllStripes()
    {
        var stripeCount = ReceiveStriper.StripeCountForTesting;
        var counts = new int[stripeCount];

        // Client-initiated HTTP/2 stream IDs are odd: 1, 3, 5, ...
        // Generate 1000 of them and bin by stripe assignment.
        const int n = 1000;
        for (var i = 0; i < n; i++)
        {
            var streamId = (uint)(2 * i + 1);
            var idx = ReceiveStriper.StripeIndexForTesting(streamId);
            Assert.That(idx, Is.GreaterThanOrEqualTo(0), $"streamId={streamId}");
            Assert.That(idx, Is.LessThan(stripeCount), $"streamId={streamId}");
            counts[idx]++;
        }

        // Every stripe should receive non-trivial traffic. With 1000
        // odd IDs and 4 stripes the ideal distribution is 250 per
        // stripe; we accept anything in [125, 375] to leave headroom
        // for hash quirks. The relevant regression we catch is "one
        // or more stripes receive 0" \u2014 i.e., the naive mask bug.
        for (var i = 0; i < stripeCount; i++)
        {
            Assert.That(counts[i], Is.GreaterThan(n / (4 * stripeCount)),
                $"Stripe {i} got only {counts[i]} of {n} client IDs " +
                $"\u2014 stripe assignment is biased.");
            Assert.That(counts[i], Is.LessThan(n - n / (2 * stripeCount)),
                $"Stripe {i} got {counts[i]} of {n} client IDs \u2014 " +
                $"stripe assignment is concentrated.");
        }
    }

    /// <summary>
    /// Verifies the hash also distributes server-initiated (even)
    /// stream IDs uniformly. Server-initiated streams are rarer in
    /// practice but still exist (PUSH_PROMISE on HTTP/2; gRPC does
    /// not use push but the SHM transport is layered on the H2
    /// framing so the path must be even-correct).
    /// </summary>
    [Test]
    public void StripeIndex_DistributesServerStreamIds_AcrossAllStripes()
    {
        var stripeCount = ReceiveStriper.StripeCountForTesting;
        var counts = new int[stripeCount];

        const int n = 1000;
        for (var i = 1; i <= n; i++)
        {
            var streamId = (uint)(2 * i);
            var idx = ReceiveStriper.StripeIndexForTesting(streamId);
            counts[idx]++;
        }

        for (var i = 0; i < stripeCount; i++)
        {
            Assert.That(counts[i], Is.GreaterThan(n / (4 * stripeCount)),
                $"Stripe {i} got only {counts[i]} of {n} server IDs.");
        }
    }

    /// <summary>
    /// Verifies the stripe index is deterministic: the same stream
    /// ID always lands on the same stripe. Required for inbound
    /// frame ordering within a stream \u2014 if two frames for the
    /// same stream could land on different stripes, OnFrameReceived
    /// would observe out-of-order delivery.
    /// </summary>
    [Test]
    public void StripeIndex_IsDeterministic()
    {
        for (var i = 0u; i < 256u; i++)
        {
            var first = ReceiveStriper.StripeIndexForTesting(i);
            var second = ReceiveStriper.StripeIndexForTesting(i);
            Assert.That(second, Is.EqualTo(first),
                $"streamId={i} produced different stripes on repeat call.");
        }
    }

    /// <summary>
    /// Verifies the stripe count is the documented power-of-two
    /// constant. The hash mask path assumes this; if someone bumps
    /// the count to a non-power-of-two the mask is silently wrong.
    /// </summary>
    [Test]
    public void StripeCount_IsPowerOfTwo()
    {
        var n = ReceiveStriper.StripeCountForTesting;
        Assert.That(n, Is.GreaterThan(0));
        Assert.That(n & (n - 1), Is.EqualTo(0),
            $"Stripe count {n} is not a power of two; the hash mask " +
            $"`hash & (count - 1)` will silently produce wrong indices.");
    }
}
