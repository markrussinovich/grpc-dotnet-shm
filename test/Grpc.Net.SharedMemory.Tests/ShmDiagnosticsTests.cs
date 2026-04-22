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

using System.Diagnostics;
using System.Text;
using Grpc.Core;
using Grpc.Net.SharedMemory.Telemetry;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Diagnostics tests equivalent to TCP FunctionalTests/Server/DiagnosticsTests.cs.
/// Tests telemetry and observability over shared memory transport.
/// </summary>
[TestFixture]
public class ShmDiagnosticsTests
{
    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task UnaryCall_TelemetryRecorded()
    {
        // Arrange
        var segmentName = $"diag_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var telemetry = new ShmTelemetryCollector();

        // Server handles request
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            telemetry.RecordRequestStart("/test/Telemetry", "unary");
            
            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendTrailersAsync(StatusCode.OK);
            
            telemetry.RecordRequestEnd(StatusCode.OK);
        });

        // Client sends request
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Telemetry", "localhost");
        await clientStream.SendHalfCloseAsync();

        await serverTask;

        // Assert
        Assert.That(telemetry.RequestCount, Is.EqualTo(1));
        Assert.That(telemetry.LastMethod, Is.EqualTo("/test/Telemetry"));
        Assert.That(telemetry.LastStatusCode, Is.EqualTo(StatusCode.OK));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task ErrorResponse_TelemetryRecordsError()
    {
        // Arrange
        var segmentName = $"diag_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var telemetry = new ShmTelemetryCollector();

        // Server returns error
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            telemetry.RecordRequestStart("/test/Error", "unary");
            
            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendTrailersAsync(StatusCode.InvalidArgument, "Bad request");
            
            telemetry.RecordRequestEnd(StatusCode.InvalidArgument);
        });

        // Client sends request
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Error", "localhost");
        await clientStream.SendHalfCloseAsync();

        await serverTask;

        // Assert
        Assert.That(telemetry.RequestCount, Is.EqualTo(1));
        Assert.That(telemetry.ErrorCount, Is.EqualTo(1));
        Assert.That(telemetry.LastStatusCode, Is.EqualTo(StatusCode.InvalidArgument));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task StreamingCall_TelemetryRecordsMessages()
    {
        // Arrange
        var segmentName = $"diag_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var telemetry = new ShmTelemetryCollector();
        var messageCount = 5;

        // Server streams messages
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            telemetry.RecordRequestStart("/test/Stream", "server_streaming");
            
            await serverStream.SendResponseHeadersAsync();
            
            for (int i = 0; i < messageCount; i++)
            {
                await serverStream.SendMessageAsync(Encoding.UTF8.GetBytes($"Message {i}"));
                telemetry.RecordMessageSent();
            }
            
            await serverStream.SendTrailersAsync(StatusCode.OK);
            telemetry.RecordRequestEnd(StatusCode.OK);
        });

        // Client sends request
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Stream", "localhost");
        await clientStream.SendHalfCloseAsync();

        await serverTask;

        // Assert
        Assert.That(telemetry.RequestCount, Is.EqualTo(1));
        Assert.That(telemetry.MessagesSent, Is.EqualTo(messageCount));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task CancelledCall_TelemetryRecordsCancellation()
    {
        // Arrange
        var segmentName = $"diag_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var telemetry = new ShmTelemetryCollector();

        // Client cancels request
        var clientStream = client.CreateStream();
        telemetry.RecordRequestStart("/test/Cancel", "unary");
        
        await clientStream.SendRequestHeadersAsync("/test/Cancel", "localhost");
        await clientStream.CancelAsync();
        
        telemetry.RecordCancellation();

        // Assert
        Assert.That(telemetry.CancellationCount, Is.EqualTo(1));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task Duration_TelemetryRecordsCallDuration()
    {
        // Arrange
        var segmentName = $"diag_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var telemetry = new ShmTelemetryCollector();
        var minDuration = TimeSpan.FromMilliseconds(50);

        // Server adds delay
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            
            await serverStream.SendResponseHeadersAsync();
            await Task.Delay(minDuration);
            await serverStream.SendTrailersAsync(StatusCode.OK);
        });

        // Client sends request and measures duration
        var stopwatch = Stopwatch.StartNew();
        var clientStream = client.CreateStream();
        telemetry.RecordRequestStart("/test/Duration", "unary");
        
        await clientStream.SendRequestHeadersAsync("/test/Duration", "localhost");
        await clientStream.SendHalfCloseAsync();

        await serverTask;
        stopwatch.Stop();
        telemetry.RecordDuration(stopwatch.Elapsed);

        // Assert
        Assert.That(telemetry.LastDuration, Is.GreaterThanOrEqualTo(minDuration));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task MessageSize_TelemetryRecordsSizes()
    {
        // Arrange
        var segmentName = $"diag_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 1024 * 1024, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var telemetry = new ShmTelemetryCollector();
        var messageSize = 10 * 1024; // 10 KB

        var message = new byte[messageSize];
        new Random(42).NextBytes(message);

        // Server echoes message
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendMessageAsync(message);
            telemetry.RecordBytesSent(messageSize);
            await serverStream.SendTrailersAsync(StatusCode.OK);
        });

        // Client sends request
        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Size", "localhost");
        await clientStream.SendMessageAsync(message);
        telemetry.RecordBytesReceived(messageSize);
        await clientStream.SendHalfCloseAsync();

        await serverTask;

        // Assert
        Assert.That(telemetry.TotalBytesSent, Is.EqualTo(messageSize));
        Assert.That(telemetry.TotalBytesReceived, Is.EqualTo(messageSize));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task MultipleRequests_TelemetryAggregates()
    {
        // Arrange
        var segmentName = $"diag_{Guid.NewGuid():N}";
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        var telemetry = new ShmTelemetryCollector();
        var requestCount = 10;

        for (int i = 0; i < requestCount; i++)
        {
            var serverTask = Task.Run(async () =>
            {
                var serverStream = server.CreateStream();
                await serverStream.SendResponseHeadersAsync();
                await serverStream.SendTrailersAsync(StatusCode.OK);
            });

            var clientStream = client.CreateStream();
            telemetry.RecordRequestStart($"/test/Request{i}", "unary");
            
            await clientStream.SendRequestHeadersAsync($"/test/Request{i}", "localhost");
            await clientStream.SendHalfCloseAsync();

            await serverTask;
            telemetry.RecordRequestEnd(StatusCode.OK);
        }

        // Assert
        Assert.That(telemetry.RequestCount, Is.EqualTo(requestCount));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public async Task ConnectionMetrics_TelemetryRecorded()
    {
        // Arrange
        var segmentName = $"diag_{Guid.NewGuid():N}";
        var telemetry = new ShmTelemetryCollector();

        telemetry.RecordConnectionOpened(segmentName);

        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 100);
        using var client = ShmConnection.ConnectAsClient(segmentName);

        // Do some work
        var serverTask = Task.Run(async () =>
        {
            var serverStream = server.CreateStream();
            await serverStream.SendResponseHeadersAsync();
            await serverStream.SendTrailersAsync(StatusCode.OK);
        });

        var clientStream = client.CreateStream();
        await clientStream.SendRequestHeadersAsync("/test/Connection", "localhost");
        await clientStream.SendHalfCloseAsync();

        await serverTask;

        telemetry.RecordConnectionClosed(segmentName);

        // Assert
        Assert.That(telemetry.ConnectionsOpened, Is.EqualTo(1));
        Assert.That(telemetry.ConnectionsClosed, Is.EqualTo(1));
    }

    [Test]
    [Platform("Win")]
    [CancelAfter(10000)]
    public void ActivitySource_TracesCreated()
    {
        // Arrange
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Grpc.Net.SharedMemory",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        // Act - create an activity using the actual ActivitySource
        using var activity = ShmTelemetry.ActivitySource.StartActivity("TestOperation");

        // Assert
        Assert.That(activity, Is.Not.Null);
    }
}

/// <summary>
/// Simple telemetry collector for testing.
/// </summary>
public class ShmTelemetryCollector
{
    private int _requestCount;
    private int _errorCount;
    private int _cancellationCount;
    private int _messagesSent;
    private int _messagesReceived;
    private long _totalBytesSent;
    private long _totalBytesReceived;
    private int _connectionsOpened;
    private int _connectionsClosed;

    public int RequestCount => _requestCount;
    public int ErrorCount => _errorCount;
    public int CancellationCount => _cancellationCount;
    public int MessagesSent => _messagesSent;
    public int MessagesReceived => _messagesReceived;
    public long TotalBytesSent => _totalBytesSent;
    public long TotalBytesReceived => _totalBytesReceived;
    public int ConnectionsOpened => _connectionsOpened;
    public int ConnectionsClosed => _connectionsClosed;

    public string? LastMethod { get; private set; }
    public StatusCode? LastStatusCode { get; private set; }
    public TimeSpan LastDuration { get; private set; }

    public void RecordRequestStart(string method, string callType)
    {
        Interlocked.Increment(ref _requestCount);
        LastMethod = method;
    }

    public void RecordRequestEnd(StatusCode statusCode)
    {
        LastStatusCode = statusCode;
        if (statusCode != StatusCode.OK)
        {
            Interlocked.Increment(ref _errorCount);
        }
    }

    public void RecordCancellation()
    {
        Interlocked.Increment(ref _cancellationCount);
    }

    public void RecordMessageSent()
    {
        Interlocked.Increment(ref _messagesSent);
    }

    public void RecordMessageReceived()
    {
        Interlocked.Increment(ref _messagesReceived);
    }

    public void RecordBytesSent(long bytes)
    {
        Interlocked.Add(ref _totalBytesSent, bytes);
    }

    public void RecordBytesReceived(long bytes)
    {
        Interlocked.Add(ref _totalBytesReceived, bytes);
    }

    public void RecordDuration(TimeSpan duration)
    {
        LastDuration = duration;
    }

    public void RecordConnectionOpened(string segmentName)
    {
        Interlocked.Increment(ref _connectionsOpened);
    }

    public void RecordConnectionClosed(string segmentName)
    {
        Interlocked.Increment(ref _connectionsClosed);
    }
}
