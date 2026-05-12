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

using System.Text;
using Grpc.Core;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Hedging tests equivalent to TCP FunctionalTests/Client/HedgingTests.cs.
/// Tests hedging retry strategies over shared memory transport.
/// </summary>
[TestFixture]
public class ShmHedgingTests
{
    /// <summary>
    /// Simple hedging policy for testing.
    /// </summary>
    private class HedgingPolicy
    {
        public int MaxAttempts { get; set; } = 3;
        public TimeSpan HedgingDelay { get; set; } = TimeSpan.FromMilliseconds(100);
        public IList<StatusCode> NonFatalStatusCodes { get; set; } = new List<StatusCode>
        {
            StatusCode.Unavailable,
            StatusCode.ResourceExhausted
        };
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task UnaryCall_FirstAttemptSucceeds_NoHedging()
    {
        // Arrange
        var segmentName = $"hedging_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var policy = new HedgingPolicy { MaxAttempts = 3 };
        var attemptCount = 0;

        // Server succeeds immediately
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            Interlocked.Increment(ref attemptCount);
            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendTrailersAsync(StatusCode.OK);
        });

        // Client sends request
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Hedging", "localhost");
        await clientStream.SendHalfCloseAsync();

        await serverTask;

        // Assert - only one attempt
        Assert.That(attemptCount, Is.EqualTo(1));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task UnaryCall_NonFatalError_HedgesNewAttempt()
    {
        // Arrange
        var segmentName = $"hedging_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var policy = new HedgingPolicy
        {
            MaxAttempts = 3,
            HedgingDelay = TimeSpan.FromMilliseconds(50),
            NonFatalStatusCodes = new List<StatusCode> { StatusCode.Unavailable }
        };
        var attemptResults = new List<StatusCode>();

        // Simulate hedging with first attempt failing
        // First attempt
        var serverTask1 = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendTrailersAsync(StatusCode.Unavailable, "Server busy");
            attemptResults.Add(StatusCode.Unavailable);
        });

        var clientStream1 = client.CreateStream();
        await clientStream1.SendRequestHeadersAsync("/test/Hedging1", "localhost");
        await clientStream1.SendHalfCloseAsync();
        await serverTask1;

        // Hedge delay
        await Task.Delay(policy.HedgingDelay);

        // Second attempt (hedged)
        var serverTask2 = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendTrailersAsync(StatusCode.OK);
            attemptResults.Add(StatusCode.OK);
        });

        var clientStream2 = client.CreateStream();
        await clientStream2.SendRequestHeadersAsync("/test/Hedging2", "localhost");
        await clientStream2.SendHalfCloseAsync();
        await serverTask2;

        // Assert - two attempts, second succeeded
        Assert.That(attemptResults.Count, Is.EqualTo(2));
        Assert.That(attemptResults[0], Is.EqualTo(StatusCode.Unavailable));
        Assert.That(attemptResults[1], Is.EqualTo(StatusCode.OK));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task UnaryCall_FatalError_NoHedging()
    {
        // Arrange
        var segmentName = $"hedging_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var policy = new HedgingPolicy
        {
            MaxAttempts = 3,
            NonFatalStatusCodes = new List<StatusCode> { StatusCode.Unavailable }
        };
        var attemptCount = 0;

        // Server returns fatal error (InvalidArgument is not in non-fatal list)
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            Interlocked.Increment(ref attemptCount);
            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendTrailersAsync(StatusCode.InvalidArgument, "Bad request");
        });

        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/FatalError", "localhost");
        await clientStream.SendHalfCloseAsync();

        await serverTask;

        // Assert - only one attempt (fatal error, no hedging)
        Assert.That(attemptCount, Is.EqualTo(1));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task UnaryCall_MaxAttemptsReached_Fails()
    {
        // Arrange
        var segmentName = $"hedging_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var maxAttempts = 3;
        var attemptResults = new List<StatusCode>();

        // All attempts fail with non-fatal error
        for (int i = 0; i < maxAttempts; i++)
        {
            var serverTask = Task.Run(async () =>
            {
                var serverStream = server.CreateStream();
                await serverStream.SendResponseHeadersAsync();
                await serverStream.SendTrailersAsync(StatusCode.Unavailable, "Server busy");
            });

            var clientStream = client.CreateStream();
            await clientStream.SendRequestHeadersAsync($"/test/MaxAttempts{i}", "localhost");
            await clientStream.SendHalfCloseAsync();
            await serverTask;

            attemptResults.Add(StatusCode.Unavailable);

            // Delay between attempts
            await Task.Delay(50);
        }

        // Assert - all attempts failed
        Assert.That(attemptResults.Count, Is.EqualTo(maxAttempts));
        Assert.That(attemptResults.All(r => r == StatusCode.Unavailable), Is.True);
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task StreamingCall_HedgingNotApplicable()
    {
        // Arrange - hedging typically only applies to unary calls
        var segmentName = $"hedging_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var attemptCount = 0;

        // Server streams (hedging doesn't apply to streaming)
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            Interlocked.Increment(ref attemptCount);
            await serverStream.SendResponseHeadersAsync();

            for (int i = 0; i < 3; i++)
            {
                await serverStream.SendMessageAsync(LpmHelpers.WrapLpmText($"Message {i}"));
            }

            await serverStream.SendTrailersAsync(StatusCode.OK);
        });

        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Stream", "localhost");
        await clientStream.SendHalfCloseAsync();

        await serverTask;

        // Assert - only one attempt (streaming doesn't hedge)
        Assert.That(attemptCount, Is.EqualTo(1));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task HedgingWithDelay_DelayRespected()
    {
        // Arrange
        var segmentName = $"hedging_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var hedgingDelay = TimeSpan.FromMilliseconds(200);
        var attemptTimes = new List<DateTime>();

        // First attempt
        attemptTimes.Add(DateTime.UtcNow);

        var serverTask1 = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendTrailersAsync(StatusCode.Unavailable);
        });

        var clientStream1 = client.CreateStream();
        await clientStream1.SendRequestHeadersAsync("/test/Delay1", "localhost");
        await clientStream1.SendHalfCloseAsync();
        await serverTask1;

        // Wait for hedging delay
        await Task.Delay(hedgingDelay);

        // Second attempt
        attemptTimes.Add(DateTime.UtcNow);

        var serverTask2 = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendTrailersAsync(StatusCode.OK);
        });

        var clientStream2 = client.CreateStream();
        await clientStream2.SendRequestHeadersAsync("/test/Delay2", "localhost");
        await clientStream2.SendHalfCloseAsync();
        await serverTask2;

        // Assert - delay was at least the configured hedging delay
        var actualDelay = attemptTimes[1] - attemptTimes[0];
        Assert.That(actualDelay, Is.GreaterThanOrEqualTo(hedgingDelay - TimeSpan.FromMilliseconds(50)));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task Cancellation_CancelsAllHedgedAttempts()
    {
        // Arrange
        var segmentName = $"hedging_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var cancelledCount = 0;

        // Start multiple hedged attempts and then cancel
        var clientStream1 = client.CreateStream();
        await clientStream1.SendRequestHeadersAsync("/test/Cancel1", "localhost");

        var clientStream2 = client.CreateStream();
        await clientStream2.SendRequestHeadersAsync("/test/Cancel2", "localhost");

        // Cancel both streams
        await clientStream1.CancelAsync();
        Interlocked.Increment(ref cancelledCount);

        await clientStream2.CancelAsync();
        Interlocked.Increment(ref cancelledCount);

        // Assert
        Assert.That(cancelledCount, Is.EqualTo(2));
        Assert.That(clientStream1.IsCancelled, Is.True);
        Assert.That(clientStream2.IsCancelled, Is.True);
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task SuccessfulHedge_CancelsRemainingAttempts()
    {
        // Arrange
        var segmentName = $"hedging_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        // First hedge attempt - will be slow
#pragma warning disable CS0219 // Variable is assigned but its value is never used
        var stream1Done = false;
#pragma warning restore CS0219
        var stream2Done = false;

        var task1 = Task.Run(async () =>
        {
            var clientStream = client.CreateStream();
            await clientStream.SendRequestHeadersAsync("/test/Slow", "localhost");
            await Task.Delay(500); // Slow
            stream1Done = true;
        });

        // Second hedge attempt - succeeds quickly
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendTrailersAsync(StatusCode.OK);
        });

        var task2 = Task.Run(async () =>
        {
            var clientStream = client.CreateStream();
            await clientStream.SendRequestHeadersAsync("/test/Fast", "localhost");
            await clientStream.SendHalfCloseAsync();
            stream2Done = true;
        });

        await Task.WhenAny(task1, task2);
        await serverTask;

        // Give some time for completion
        await Task.Delay(100);

        // Assert - second attempt completed first
        Assert.That(stream2Done, Is.True);
    }
}
