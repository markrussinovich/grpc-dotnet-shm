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
using System.Diagnostics.Metrics;

namespace Grpc.Net.SharedMemory.Telemetry;

/// <summary>
/// Telemetry and metrics for shared memory transport.
/// Modeled on grpc-go stats package and OpenTelemetry integration.
/// </summary>
public static class ShmTelemetry
{
    /// <summary>
    /// The ActivitySource for shared memory transport tracing.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new("Grpc.Net.SharedMemory", "1.0.0");

    /// <summary>
    /// The Meter for shared memory transport metrics.
    /// </summary>
    public static readonly Meter Meter = new("Grpc.Net.SharedMemory", "1.0.0");

    // Counters
    private static readonly Counter<long> _connectionStartedCounter = Meter.CreateCounter<long>(
        "shm.connection.started",
        "connections",
        "Number of shared memory connections started");

    private static readonly Counter<long> _connectionClosedCounter = Meter.CreateCounter<long>(
        "shm.connection.closed",
        "connections",
        "Number of shared memory connections closed");

    private static readonly Counter<long> _streamStartedCounter = Meter.CreateCounter<long>(
        "shm.stream.started",
        "streams",
        "Number of shared memory streams started");

    private static readonly Counter<long> _streamClosedCounter = Meter.CreateCounter<long>(
        "shm.stream.closed",
        "streams",
        "Number of shared memory streams closed");

    private static readonly Counter<long> _messagesSentCounter = Meter.CreateCounter<long>(
        "shm.messages.sent",
        "messages",
        "Number of messages sent over shared memory");

    private static readonly Counter<long> _messagesReceivedCounter = Meter.CreateCounter<long>(
        "shm.messages.received",
        "messages",
        "Number of messages received over shared memory");

    private static readonly Counter<long> _bytesSentCounter = Meter.CreateCounter<long>(
        "shm.bytes.sent",
        "bytes",
        "Number of bytes sent over shared memory");

    private static readonly Counter<long> _bytesReceivedCounter = Meter.CreateCounter<long>(
        "shm.bytes.received",
        "bytes",
        "Number of bytes received over shared memory");

    private static readonly Counter<long> _pingsSentCounter = Meter.CreateCounter<long>(
        "shm.pings.sent",
        "pings",
        "Number of keepalive pings sent");

    private static readonly Counter<long> _pongsReceivedCounter = Meter.CreateCounter<long>(
        "shm.pongs.received",
        "pongs",
        "Number of keepalive pongs received");

    private static readonly Counter<long> _windowUpdatesSentCounter = Meter.CreateCounter<long>(
        "shm.window_updates.sent",
        "updates",
        "Number of flow control window updates sent");

    private static readonly Counter<long> _errorsCounter = Meter.CreateCounter<long>(
        "shm.errors",
        "errors",
        "Number of errors on shared memory transport");

    private static readonly Counter<long> _transportSelectedCounter = Meter.CreateCounter<long>(
        "shm.transport.selected",
        "selections",
        "Number of transport selections (shm vs tcp fallback)");

    // Histograms
    private static readonly Histogram<double> _rttHistogram = Meter.CreateHistogram<double>(
        "shm.rtt",
        "ms",
        "Round-trip time for shared memory operations");

    private static readonly Histogram<double> _messageSizeHistogram = Meter.CreateHistogram<double>(
        "shm.message.size",
        "bytes",
        "Size of messages sent over shared memory");

    private static readonly Histogram<double> _compressionRatioHistogram = Meter.CreateHistogram<double>(
        "shm.compression.ratio",
        "ratio",
        "Compression ratio for compressed messages");

    // Gauges (using observable)
    private static long _activeConnections;
    private static long _activeStreams;
    private static long _currentBdpWindow;

    static ShmTelemetry()
    {
        Meter.CreateObservableGauge("shm.connections.active", () => _activeConnections,
            "connections", "Number of active shared memory connections");

        Meter.CreateObservableGauge("shm.streams.active", () => _activeStreams,
            "streams", "Number of active shared memory streams");

        Meter.CreateObservableGauge("shm.bdp.window", () => _currentBdpWindow,
            "bytes", "Current BDP-estimated flow control window");
    }

    /// <summary>
    /// Records a connection start event.
    /// </summary>
    public static void RecordConnectionStarted(string segmentName, bool isClient)
    {
        _connectionStartedCounter.Add(1,
            new KeyValuePair<string, object?>("segment", segmentName),
            new KeyValuePair<string, object?>("side", isClient ? "client" : "server"));
        Interlocked.Increment(ref _activeConnections);
    }

    /// <summary>
    /// Records a connection close event.
    /// </summary>
    public static void RecordConnectionClosed(string segmentName, bool isClient, string reason)
    {
        _connectionClosedCounter.Add(1,
            new KeyValuePair<string, object?>("segment", segmentName),
            new KeyValuePair<string, object?>("side", isClient ? "client" : "server"),
            new KeyValuePair<string, object?>("reason", reason));
        Interlocked.Decrement(ref _activeConnections);
    }

    /// <summary>
    /// Records a stream start event.
    /// </summary>
    public static void RecordStreamStarted(uint streamId, string method)
    {
        _streamStartedCounter.Add(1,
            new KeyValuePair<string, object?>("stream_id", streamId),
            new KeyValuePair<string, object?>("method", method));
        Interlocked.Increment(ref _activeStreams);
    }

    /// <summary>
    /// Records a stream close event.
    /// </summary>
    public static void RecordStreamClosed(uint streamId, int statusCode)
    {
        _streamClosedCounter.Add(1,
            new KeyValuePair<string, object?>("stream_id", streamId),
            new KeyValuePair<string, object?>("status_code", statusCode));
        Interlocked.Decrement(ref _activeStreams);
    }

    /// <summary>
    /// Records a message sent.
    /// </summary>
    public static void RecordMessageSent(uint streamId, int size, bool compressed, int? originalSize = null)
    {
        _messagesSentCounter.Add(1,
            new KeyValuePair<string, object?>("stream_id", streamId),
            new KeyValuePair<string, object?>("compressed", compressed));

        _bytesSentCounter.Add(size,
            new KeyValuePair<string, object?>("stream_id", streamId));

        _messageSizeHistogram.Record(size);

        if (compressed && originalSize.HasValue && originalSize.Value > 0)
        {
            var ratio = (double)size / originalSize.Value;
            _compressionRatioHistogram.Record(ratio);
        }
    }

    /// <summary>
    /// Records a message received.
    /// </summary>
    public static void RecordMessageReceived(uint streamId, int size, bool compressed)
    {
        _messagesReceivedCounter.Add(1,
            new KeyValuePair<string, object?>("stream_id", streamId),
            new KeyValuePair<string, object?>("compressed", compressed));

        _bytesReceivedCounter.Add(size,
            new KeyValuePair<string, object?>("stream_id", streamId));
    }

    /// <summary>
    /// Records a ping sent.
    /// </summary>
    public static void RecordPingSent()
    {
        _pingsSentCounter.Add(1);
    }

    /// <summary>
    /// Records a pong received.
    /// </summary>
    public static void RecordPongReceived(double rttMs)
    {
        _pongsReceivedCounter.Add(1);
        _rttHistogram.Record(rttMs);
    }

    /// <summary>
    /// Records a window update sent.
    /// </summary>
    public static void RecordWindowUpdateSent(uint streamId, long delta)
    {
        _windowUpdatesSentCounter.Add(1,
            new KeyValuePair<string, object?>("stream_id", streamId),
            new KeyValuePair<string, object?>("delta", delta));
    }

    /// <summary>
    /// Records which transport was selected (shared memory or TCP fallback).
    /// </summary>
    public static void RecordTransportSelected(string transport, string target)
    {
        _transportSelectedCounter.Add(1,
            new KeyValuePair<string, object?>("transport", transport),
            new KeyValuePair<string, object?>("target", target));
    }

    /// <summary>
    /// Records an error.
    /// </summary>
    public static void RecordError(string errorType, string message)
    {
        _errorsCounter.Add(1,
            new KeyValuePair<string, object?>("type", errorType),
            new KeyValuePair<string, object?>("message", message));
    }

    /// <summary>
    /// Updates the current BDP window estimate.
    /// </summary>
    public static void UpdateBdpWindow(long windowSize)
    {
        Interlocked.Exchange(ref _currentBdpWindow, windowSize);
    }

    /// <summary>
    /// Starts an activity for a stream operation.
    /// </summary>
    public static Activity? StartStreamActivity(uint streamId, string method, bool isClient)
    {
        var activity = ActivitySource.StartActivity(
            isClient ? "shm.client.call" : "shm.server.call",
            ActivityKind.Client);

        if (activity != null)
        {
            activity.SetTag("rpc.system", "grpc");
            activity.SetTag("rpc.transport", "shm");
            activity.SetTag("rpc.method", method);
            activity.SetTag("shm.stream_id", streamId);
        }

        return activity;
    }
}

/// <summary>
/// Event handler interface for shared memory transport events.
/// Modeled on grpc-go stats.Handler.
/// </summary>
public interface IShmStatsHandler
{
    /// <summary>
    /// Called when a connection is started.
    /// </summary>
    void OnConnectionStart(ShmConnectionStartEvent e);

    /// <summary>
    /// Called when a connection is closed.
    /// </summary>
    void OnConnectionEnd(ShmConnectionEndEvent e);

    /// <summary>
    /// Called when a stream is started.
    /// </summary>
    void OnStreamStart(ShmStreamStartEvent e);

    /// <summary>
    /// Called when a stream ends.
    /// </summary>
    void OnStreamEnd(ShmStreamEndEvent e);

    /// <summary>
    /// Called when a message is sent.
    /// </summary>
    void OnMessageSent(ShmMessageSentEvent e);

    /// <summary>
    /// Called when a message is received.
    /// </summary>
    void OnMessageReceived(ShmMessageReceivedEvent e);
}

/// <summary>
/// Base class for shared memory transport events.
/// </summary>
public abstract class ShmStatsEvent
{
    /// <summary>
    /// Gets the timestamp when the event occurred.
    /// </summary>
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Event raised when a connection starts.
/// </summary>
public sealed class ShmConnectionStartEvent : ShmStatsEvent
{
    /// <summary>
    /// Gets the segment name.
    /// </summary>
    public required string SegmentName { get; init; }

    /// <summary>
    /// Gets whether this is a client connection.
    /// </summary>
    public required bool IsClient { get; init; }
}

/// <summary>
/// Event raised when a connection ends.
/// </summary>
public sealed class ShmConnectionEndEvent : ShmStatsEvent
{
    /// <summary>
    /// Gets the segment name.
    /// </summary>
    public required string SegmentName { get; init; }

    /// <summary>
    /// Gets whether this is a client connection.
    /// </summary>
    public required bool IsClient { get; init; }

    /// <summary>
    /// Gets the reason for closing.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Gets any error that occurred.
    /// </summary>
    public Exception? Error { get; init; }
}

/// <summary>
/// Event raised when a stream starts.
/// </summary>
public sealed class ShmStreamStartEvent : ShmStatsEvent
{
    /// <summary>
    /// Gets the stream ID.
    /// </summary>
    public required uint StreamId { get; init; }

    /// <summary>
    /// Gets the RPC method.
    /// </summary>
    public required string Method { get; init; }

    /// <summary>
    /// Gets whether this is a client stream.
    /// </summary>
    public required bool IsClient { get; init; }
}

/// <summary>
/// Event raised when a stream ends.
/// </summary>
public sealed class ShmStreamEndEvent : ShmStatsEvent
{
    /// <summary>
    /// Gets the stream ID.
    /// </summary>
    public required uint StreamId { get; init; }

    /// <summary>
    /// Gets the gRPC status code.
    /// </summary>
    public required int StatusCode { get; init; }

    /// <summary>
    /// Gets the duration of the stream.
    /// </summary>
    public required TimeSpan Duration { get; init; }
}

/// <summary>
/// Event raised when a message is sent.
/// </summary>
public sealed class ShmMessageSentEvent : ShmStatsEvent
{
    /// <summary>
    /// Gets the stream ID.
    /// </summary>
    public required uint StreamId { get; init; }

    /// <summary>
    /// Gets the message size in bytes.
    /// </summary>
    public required int Size { get; init; }

    /// <summary>
    /// Gets whether the message was compressed.
    /// </summary>
    public required bool Compressed { get; init; }

    /// <summary>
    /// Gets the original size before compression (if compressed).
    /// </summary>
    public int? OriginalSize { get; init; }
}

/// <summary>
/// Event raised when a message is received.
/// </summary>
public sealed class ShmMessageReceivedEvent : ShmStatsEvent
{
    /// <summary>
    /// Gets the stream ID.
    /// </summary>
    public required uint StreamId { get; init; }

    /// <summary>
    /// Gets the message size in bytes.
    /// </summary>
    public required int Size { get; init; }

    /// <summary>
    /// Gets whether the message was compressed.
    /// </summary>
    public required bool Compressed { get; init; }
}

/// <summary>
/// Default stats handler that records to ShmTelemetry.
/// </summary>
public sealed class DefaultShmStatsHandler : IShmStatsHandler
{
    /// <summary>
    /// Singleton instance.
    /// </summary>
    public static readonly DefaultShmStatsHandler Instance = new();

    public void OnConnectionStart(ShmConnectionStartEvent e)
    {
        ShmTelemetry.RecordConnectionStarted(e.SegmentName, e.IsClient);
    }

    public void OnConnectionEnd(ShmConnectionEndEvent e)
    {
        ShmTelemetry.RecordConnectionClosed(e.SegmentName, e.IsClient, e.Reason);
        if (e.Error != null)
        {
            ShmTelemetry.RecordError("connection", e.Error.Message);
        }
    }

    public void OnStreamStart(ShmStreamStartEvent e)
    {
        ShmTelemetry.RecordStreamStarted(e.StreamId, e.Method);
    }

    public void OnStreamEnd(ShmStreamEndEvent e)
    {
        ShmTelemetry.RecordStreamClosed(e.StreamId, e.StatusCode);
    }

    public void OnMessageSent(ShmMessageSentEvent e)
    {
        ShmTelemetry.RecordMessageSent(e.StreamId, e.Size, e.Compressed, e.OriginalSize);
    }

    public void OnMessageReceived(ShmMessageReceivedEvent e)
    {
        ShmTelemetry.RecordMessageReceived(e.StreamId, e.Size, e.Compressed);
    }
}
