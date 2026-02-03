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

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Retry policy for shared memory transport.
/// Modeled on grpc-go service config retry policy and gRFC A6.
/// </summary>
public sealed class ShmRetryPolicy
{
    /// <summary>
    /// Maximum number of attempts, including the original RPC.
    /// Must be greater than 1 for retries to occur.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// Initial backoff delay.
    /// </summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Maximum backoff delay.
    /// </summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Backoff multiplier for exponential backoff.
    /// </summary>
    public double BackoffMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Status codes that trigger a retry.
    /// </summary>
    public ISet<StatusCode> RetryableStatusCodes { get; set; } = new HashSet<StatusCode>
    {
        StatusCode.Unavailable,
        StatusCode.ResourceExhausted
    };

    /// <summary>
    /// Creates a default retry policy.
    /// </summary>
    public static ShmRetryPolicy Default => new();

    /// <summary>
    /// Creates a retry policy with no retries.
    /// </summary>
    public static ShmRetryPolicy NoRetry => new() { MaxAttempts = 1 };

    /// <summary>
    /// Calculates the backoff delay for the given attempt number.
    /// </summary>
    /// <param name="attemptNumber">The 1-based attempt number (1 = first retry).</param>
    /// <returns>The backoff delay.</returns>
    public TimeSpan CalculateBackoff(int attemptNumber)
    {
        if (attemptNumber <= 0)
        {
            return TimeSpan.Zero;
        }

        // Exponential backoff: initial * multiplier^(attempt-1)
        var backoff = InitialBackoff.TotalMilliseconds * Math.Pow(BackoffMultiplier, attemptNumber - 1);

        // Add jitter (±20%)
        var jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
        backoff *= jitter;

        // Cap at max backoff
        backoff = Math.Min(backoff, MaxBackoff.TotalMilliseconds);

        return TimeSpan.FromMilliseconds(backoff);
    }

    /// <summary>
    /// Determines if the given status code should trigger a retry.
    /// </summary>
    /// <param name="statusCode">The status code to check.</param>
    /// <returns>True if the RPC should be retried.</returns>
    public bool ShouldRetry(StatusCode statusCode)
    {
        return RetryableStatusCodes.Contains(statusCode);
    }

    /// <summary>
    /// Validates the retry policy configuration.
    /// </summary>
    /// <exception cref="InvalidOperationException">If the configuration is invalid.</exception>
    public void Validate()
    {
        if (MaxAttempts < 1)
        {
            throw new InvalidOperationException("MaxAttempts must be at least 1");
        }
        if (InitialBackoff <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("InitialBackoff must be positive");
        }
        if (MaxBackoff <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("MaxBackoff must be positive");
        }
        if (BackoffMultiplier <= 0)
        {
            throw new InvalidOperationException("BackoffMultiplier must be positive");
        }
    }
}

/// <summary>
/// Retry throttling policy for shared memory transport.
/// Modeled on grpc-go retry throttling and gRFC A6.
/// </summary>
public sealed class ShmRetryThrottling
{
    private readonly object _lock = new();
    private double _tokenCount;

    /// <summary>
    /// Maximum number of tokens. Tokens start at this value.
    /// </summary>
    public double MaxTokens { get; set; } = 10.0;

    /// <summary>
    /// Ratio of tokens to add on each successful RPC.
    /// Typically between 0 and 1 (e.g., 0.1).
    /// </summary>
    public double TokenRatio { get; set; } = 0.1;

    /// <summary>
    /// Creates a new retry throttling policy.
    /// </summary>
    public ShmRetryThrottling()
    {
        _tokenCount = MaxTokens;
    }

    /// <summary>
    /// Creates a default retry throttling policy.
    /// </summary>
    public static ShmRetryThrottling Default => new();

    /// <summary>
    /// Records a successful RPC (adds tokens).
    /// </summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            _tokenCount = Math.Min(MaxTokens, _tokenCount + TokenRatio);
        }
    }

    /// <summary>
    /// Attempts to consume a token for a retry.
    /// </summary>
    /// <returns>True if a token was consumed (retry allowed), false otherwise.</returns>
    public bool TryConsume()
    {
        lock (_lock)
        {
            if (_tokenCount >= 1)
            {
                _tokenCount -= 1;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Checks if retries are allowed (token count > MaxTokens/2).
    /// </summary>
    public bool IsRetryAllowed
    {
        get
        {
            lock (_lock)
            {
                return _tokenCount > MaxTokens / 2;
            }
        }
    }

    /// <summary>
    /// Gets the current token count.
    /// </summary>
    public double TokenCount
    {
        get
        {
            lock (_lock)
            {
                return _tokenCount;
            }
        }
    }

    /// <summary>
    /// Validates the throttling configuration.
    /// </summary>
    public void Validate()
    {
        if (MaxTokens <= 0)
        {
            throw new InvalidOperationException("MaxTokens must be positive");
        }
        if (TokenRatio <= 0)
        {
            throw new InvalidOperationException("TokenRatio must be positive");
        }
    }
}

/// <summary>
/// Retry state for a single RPC.
/// </summary>
public sealed class ShmRetryState
{
    private readonly ShmRetryPolicy _policy;
    private readonly ShmRetryThrottling? _throttling;
    private int _attemptCount;
    private bool _committed;

    /// <summary>
    /// Creates a new retry state.
    /// </summary>
    public ShmRetryState(ShmRetryPolicy policy, ShmRetryThrottling? throttling = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _throttling = throttling;
    }

    /// <summary>
    /// Gets the current attempt number (1-based).
    /// </summary>
    public int AttemptNumber => _attemptCount;

    /// <summary>
    /// Gets whether the RPC has been committed (no more retries possible).
    /// </summary>
    public bool IsCommitted => _committed;

    /// <summary>
    /// Marks the RPC as committed (no more retries possible).
    /// Call this when response headers are received.
    /// </summary>
    public void Commit()
    {
        _committed = true;
    }

    /// <summary>
    /// Records a successful attempt.
    /// </summary>
    public void RecordSuccess()
    {
        _throttling?.RecordSuccess();
    }

    /// <summary>
    /// Determines if a retry should be attempted after a failure.
    /// </summary>
    /// <param name="statusCode">The status code from the failed attempt.</param>
    /// <param name="backoff">The backoff delay before the next attempt.</param>
    /// <returns>True if a retry should be attempted.</returns>
    public bool ShouldRetry(StatusCode statusCode, out TimeSpan backoff)
    {
        backoff = TimeSpan.Zero;

        if (_committed)
        {
            return false;
        }

        if (_attemptCount >= _policy.MaxAttempts)
        {
            return false;
        }

        if (!_policy.ShouldRetry(statusCode))
        {
            return false;
        }

        // Check throttling
        if (_throttling != null && !_throttling.IsRetryAllowed)
        {
            return false;
        }

        // Consume a retry token
        if (_throttling != null && !_throttling.TryConsume())
        {
            return false;
        }

        _attemptCount++;
        backoff = _policy.CalculateBackoff(_attemptCount);
        return true;
    }

    /// <summary>
    /// Increments the attempt count without checking retry conditions.
    /// Call this when starting each attempt.
    /// </summary>
    public void IncrementAttempt()
    {
        _attemptCount++;
    }
}
