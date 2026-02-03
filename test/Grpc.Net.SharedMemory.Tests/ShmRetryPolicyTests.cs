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

using Grpc.Core;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

[TestFixture]
public class ShmRetryPolicyTests
{
    [Test]
    public void Default_HasExpectedValues()
    {
        // Arrange
        var policy = ShmRetryPolicy.Default;

        // Assert
        Assert.That(policy.MaxAttempts, Is.EqualTo(5));
        Assert.That(policy.InitialBackoff, Is.EqualTo(TimeSpan.FromMilliseconds(100)));
        Assert.That(policy.MaxBackoff, Is.EqualTo(TimeSpan.FromSeconds(10)));
        Assert.That(policy.BackoffMultiplier, Is.EqualTo(2.0));
        Assert.That(policy.RetryableStatusCodes, Does.Contain(StatusCode.Unavailable));
        Assert.That(policy.RetryableStatusCodes, Does.Contain(StatusCode.ResourceExhausted));
    }

    [Test]
    public void NoRetry_HasMaxAttemptsOne()
    {
        // Arrange
        var policy = ShmRetryPolicy.NoRetry;

        // Assert
        Assert.That(policy.MaxAttempts, Is.EqualTo(1));
    }

    [Test]
    public void CalculateBackoff_FirstAttempt_ReturnsInitialBackoff()
    {
        // Arrange
        var policy = new ShmRetryPolicy
        {
            InitialBackoff = TimeSpan.FromMilliseconds(100),
            MaxBackoff = TimeSpan.FromSeconds(10),
            BackoffMultiplier = 2.0
        };

        // Act
        var backoff = policy.CalculateBackoff(1);

        // Assert - with jitter, should be between 80ms and 120ms
        Assert.That(backoff, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(80)));
        Assert.That(backoff, Is.LessThanOrEqualTo(TimeSpan.FromMilliseconds(120)));
    }

    [Test]
    public void CalculateBackoff_ExponentialGrowth()
    {
        // Arrange
        var policy = new ShmRetryPolicy
        {
            InitialBackoff = TimeSpan.FromMilliseconds(100),
            MaxBackoff = TimeSpan.FromSeconds(10),
            BackoffMultiplier = 2.0
        };

        // Act
        var backoff1 = policy.CalculateBackoff(1);
        var backoff2 = policy.CalculateBackoff(2);
        var backoff3 = policy.CalculateBackoff(3);

        // Assert - each should be roughly double (accounting for jitter)
        Assert.That(backoff2, Is.GreaterThan(backoff1), "Backoff should increase with attempts");
        Assert.That(backoff3, Is.GreaterThan(backoff2), "Backoff should continue to increase");
    }

    [Test]
    public void CalculateBackoff_RespectsMaxBackoff()
    {
        // Arrange
        var policy = new ShmRetryPolicy
        {
            InitialBackoff = TimeSpan.FromSeconds(1),
            MaxBackoff = TimeSpan.FromSeconds(5),
            BackoffMultiplier = 10.0 // Very aggressive multiplier
        };

        // Act
        var backoff = policy.CalculateBackoff(10);

        // Assert - should be capped at max (plus jitter, so max 6s)
        Assert.That(backoff, Is.LessThanOrEqualTo(TimeSpan.FromSeconds(6)));
    }

    [TestCase(StatusCode.Unavailable, true)]
    [TestCase(StatusCode.ResourceExhausted, true)]
    [TestCase(StatusCode.OK, false)]
    [TestCase(StatusCode.InvalidArgument, false)]
    [TestCase(StatusCode.Internal, false)]
    public void ShouldRetry_RespectsStatusCodes(StatusCode code, bool expected)
    {
        // Arrange
        var policy = ShmRetryPolicy.Default;

        // Act
        var shouldRetry = policy.ShouldRetry(code);

        // Assert
        Assert.That(shouldRetry, Is.EqualTo(expected));
    }

    [Test]
    public void Validate_ThrowsOnInvalidMaxAttempts()
    {
        // Arrange
        var policy = new ShmRetryPolicy { MaxAttempts = 0 };

        // Assert
        Assert.Throws<InvalidOperationException>(() => policy.Validate());
    }

    [Test]
    public void Validate_ThrowsOnInvalidBackoff()
    {
        // Arrange
        var policy = new ShmRetryPolicy { InitialBackoff = TimeSpan.Zero };

        // Assert
        Assert.Throws<InvalidOperationException>(() => policy.Validate());
    }
}

[TestFixture]
public class ShmRetryThrottlingTests
{
    [Test]
    public void Default_StartsWithMaxTokens()
    {
        // Arrange
        var throttling = ShmRetryThrottling.Default;

        // Assert
        Assert.That(throttling.TokenCount, Is.EqualTo(10.0));
        Assert.That(throttling.IsRetryAllowed, Is.True);
    }

    [Test]
    public void TryConsume_DecrementsTokens()
    {
        // Arrange
        var throttling = new ShmRetryThrottling { MaxTokens = 10.0 };

        // Act
        var result1 = throttling.TryConsume();
        var result2 = throttling.TryConsume();

        // Assert
        Assert.That(result1, Is.True);
        Assert.That(result2, Is.True);
        Assert.That(throttling.TokenCount, Is.EqualTo(8.0));
    }

    [Test]
    public void RecordSuccess_AddsTokens()
    {
        // Arrange
        var throttling = new ShmRetryThrottling { MaxTokens = 10.0, TokenRatio = 0.5 };
        throttling.TryConsume(); // 9.0 tokens

        // Act
        throttling.RecordSuccess();

        // Assert
        Assert.That(throttling.TokenCount, Is.EqualTo(9.5));
    }

    [Test]
    public void RecordSuccess_CapsAtMaxTokens()
    {
        // Arrange
        var throttling = new ShmRetryThrottling { MaxTokens = 10.0, TokenRatio = 0.5 };

        // Act
        throttling.RecordSuccess();

        // Assert
        Assert.That(throttling.TokenCount, Is.EqualTo(10.0)); // Should not exceed max
    }

    [Test]
    public void TryConsume_FailsWhenTokensLow()
    {
        // Arrange
        var throttling = new ShmRetryThrottling { MaxTokens = 2.0 };
        throttling.TryConsume(); // 1.0 tokens
        throttling.TryConsume(); // 0.0 tokens

        // Act
        var result = throttling.TryConsume();

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public void IsRetryAllowed_FalseWhenBelowHalf()
    {
        // Arrange
        var throttling = new ShmRetryThrottling { MaxTokens = 10.0 };
        
        // Consume 6 tokens to get below 50%
        for (int i = 0; i < 6; i++)
        {
            throttling.TryConsume();
        }

        // Assert
        Assert.That(throttling.IsRetryAllowed, Is.False);
    }
}

[TestFixture]
public class ShmRetryStateTests
{
    [Test]
    public void ShouldRetry_ReturnsTrueForRetryableStatus()
    {
        // Arrange
        var policy = ShmRetryPolicy.Default;
        var state = new ShmRetryState(policy);
        state.IncrementAttempt();

        // Act
        var shouldRetry = state.ShouldRetry(StatusCode.Unavailable, out var backoff);

        // Assert
        Assert.That(shouldRetry, Is.True);
        Assert.That(backoff, Is.GreaterThan(TimeSpan.Zero));
    }

    [Test]
    public void ShouldRetry_ReturnsFalseWhenCommitted()
    {
        // Arrange
        var policy = ShmRetryPolicy.Default;
        var state = new ShmRetryState(policy);
        state.Commit();

        // Act
        var shouldRetry = state.ShouldRetry(StatusCode.Unavailable, out _);

        // Assert
        Assert.That(shouldRetry, Is.False);
    }

    [Test]
    public void ShouldRetry_ReturnsFalseWhenMaxAttemptsReached()
    {
        // Arrange
        var policy = new ShmRetryPolicy { MaxAttempts = 2 };
        var state = new ShmRetryState(policy);
        state.IncrementAttempt();
        state.ShouldRetry(StatusCode.Unavailable, out _); // 1st retry
        state.ShouldRetry(StatusCode.Unavailable, out _); // 2nd retry - should be at max

        // Act
        var shouldRetry = state.ShouldRetry(StatusCode.Unavailable, out _);

        // Assert
        Assert.That(shouldRetry, Is.False);
    }

    [Test]
    public void ShouldRetry_ReturnsFalseForNonRetryableStatus()
    {
        // Arrange
        var policy = ShmRetryPolicy.Default;
        var state = new ShmRetryState(policy);

        // Act
        var shouldRetry = state.ShouldRetry(StatusCode.InvalidArgument, out _);

        // Assert
        Assert.That(shouldRetry, Is.False);
    }

    [Test]
    public void RecordSuccess_UpdatesThrottling()
    {
        // Arrange
        var throttling = new ShmRetryThrottling { MaxTokens = 10.0, TokenRatio = 0.5 };
        throttling.TryConsume(); // 9.0 tokens
        var policy = ShmRetryPolicy.Default;
        var state = new ShmRetryState(policy, throttling);

        // Act
        state.RecordSuccess();

        // Assert
        Assert.That(throttling.TokenCount, Is.EqualTo(9.5));
    }
}
