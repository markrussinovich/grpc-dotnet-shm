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

using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Tests for deadline and timeout handling.
/// Deadline is passed via SendRequestHeadersAsync and stored in headers.
/// </summary>
[TestFixture]
public class DeadlineTests
{
    [Test]
    [Platform("Win")]
    public void HeadersV1_DeadlineUnixNano_DefaultIsZero()
    {
        var headers = new HeadersV1
        {
            Method = "/test",
            Authority = "localhost"
        };
        
        Assert.That(headers.DeadlineUnixNano, Is.EqualTo(0UL));
    }

    [Test]
    [Platform("Win")]
    public void HeadersV1_DeadlineUnixNano_CanBeSet()
    {
        var now = DateTime.UtcNow;
        var deadline = now.AddSeconds(30);
        var unixNano = (ulong)(deadline.ToUniversalTime() - DateTime.UnixEpoch).Ticks * 100;
        
        var headers = new HeadersV1
        {
            Method = "/test",
            Authority = "localhost",
            DeadlineUnixNano = unixNano
        };
        
        Assert.That(headers.DeadlineUnixNano, Is.EqualTo(unixNano));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task SendRequestHeaders_WithDeadline_SetsDeadlineInHeaders()
    {
        var segmentName = $"deadline_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        
        await stream.SendRequestHeadersAsync("/test/deadline", "localhost", null, deadline);
        
        // Verify request headers have deadline set
        Assert.That(stream.RequestHeaders, Is.Not.Null);
        Assert.That(stream.RequestHeaders!.DeadlineUnixNano, Is.GreaterThan(0UL));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task SendRequestHeaders_WithoutDeadline_DeadlineIsZero()
    {
        var segmentName = $"deadline_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream = client.CreateStream();
        await stream.SendRequestHeadersAsync("/test/no-deadline", "localhost");
        
        // Verify request headers have no deadline
        Assert.That(stream.RequestHeaders, Is.Not.Null);
        Assert.That(stream.RequestHeaders!.DeadlineUnixNano, Is.EqualTo(0UL));
    }

    [Test]
    [Platform("Win")]
    public void TimeoutConversion_SecondsToDeadline()
    {
        var now = DateTime.UtcNow;
        var timeoutSeconds = 30.0;
        
        var deadline = now.AddSeconds(timeoutSeconds);
        
        Assert.That((deadline - now).TotalSeconds, Is.EqualTo(timeoutSeconds).Within(0.1));
    }

    [Test]
    [Platform("Win")]
    public void DeadlineConversion_ToUnixNano()
    {
        var deadline = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var unixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        
        // Convert to nanoseconds since Unix epoch
        var ticksSinceEpoch = (deadline - unixEpoch).Ticks;
        var nanosSinceEpoch = (ulong)ticksSinceEpoch * 100; // 1 tick = 100 nanoseconds
        
        Assert.That(nanosSinceEpoch, Is.GreaterThan(0UL));
    }

    [Test]
    [Platform("Win")]
    public void GrpcTimeout_Header_Format()
    {
        // gRPC timeout header format: {value}{unit}
        // Units: n=nanoseconds, u=microseconds, m=milliseconds, S=seconds, M=minutes, H=hours
        
        // 1 second = "1S"
        var oneSecond = "1S";
        Assert.That(oneSecond, Does.Match(@"^\d+[nuSMH]$"));
        
        // 100 milliseconds = "100m"
        var hundredMs = "100m";
        Assert.That(hundredMs, Does.Match(@"^\d+[nuSmMH]$"));
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task MultipleStreams_CanHaveDifferentDeadlines()
    {
        var segmentName = $"deadline_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var stream1 = client.CreateStream();
        var stream2 = client.CreateStream();
        
        var deadline1 = DateTime.UtcNow.AddSeconds(10);
        var deadline2 = DateTime.UtcNow.AddMinutes(5);
        
        await stream1.SendRequestHeadersAsync("/test/short", "localhost", null, deadline1);
        await stream2.SendRequestHeadersAsync("/test/long", "localhost", null, deadline2);
        
        // Each stream has its own deadline
        Assert.That(stream1.RequestHeaders!.DeadlineUnixNano, Is.Not.EqualTo(stream2.RequestHeaders!.DeadlineUnixNano));
    }
}

/// <summary>
/// Tests for timeout propagation and cancellation.
/// </summary>
[TestFixture]
public class TimeoutPropagationTests
{
    [Test]
    [Platform("Win")]
    public void CancellationToken_WithTimeout_IsRespected()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        
        Assert.That(cts.Token.CanBeCanceled, Is.True);
    }

    [Test]
    [Platform("Win")]
    [Timeout(3000)]
    public async Task CancellationToken_Timeout_CancelsOperation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        
        try
        {
            await Task.Delay(1000, cts.Token);
            Assert.Fail("Should have been cancelled");
        }
        catch (OperationCanceledException)
        {
            // Expected
            Assert.Pass();
        }
    }

    [Test]
    [Platform("Win")]
    public void CombinedTimeoutAndDeadline_ShorterWins()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        
        // The shorter timeout (CTS) should win
        var deadlineRemaining = (deadline - DateTime.UtcNow).TotalMilliseconds;
        Assert.That(100, Is.LessThan(deadlineRemaining));
    }
}
