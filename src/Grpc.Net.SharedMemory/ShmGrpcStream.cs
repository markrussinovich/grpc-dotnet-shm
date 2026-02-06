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

using System.Buffers;
using System.Threading.Channels;
using Grpc.Core;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Represents a single gRPC stream (call) over a shared memory connection.
/// Handles frame routing, flow control, and message sequencing for one RPC.
/// </summary>
public sealed class ShmGrpcStream : IDisposable, IAsyncDisposable
{
    private readonly ShmConnection _connection;
    private readonly Channel<(FrameType Type, byte[] Payload, int PayloadLength)> _inboundFrames;
    private readonly CancellationTokenSource _disposeCts;
    private readonly SemaphoreSlim _sendLock;

    private HeadersV1? _requestHeaders;
    private HeadersV1? _responseHeaders;
    private TrailersV1? _trailers;
    private long _sendWindow;
    private bool _halfCloseSent;
    private bool _halfCloseReceived;
    private bool _cancelled;
    private bool _disposed;

    /// <summary>
    /// Gets the stream ID.
    /// </summary>
    public uint StreamId { get; }

    /// <summary>
    /// Gets whether this stream is from the client side.
    /// </summary>
    public bool IsClientStream => _connection.IsClient;

    /// <summary>
    /// Gets the request headers (available after sending for client, after receiving for server).
    /// </summary>
    public HeadersV1? RequestHeaders => _requestHeaders;

    /// <summary>
    /// Gets the response headers (available after receiving for client, after sending for server).
    /// </summary>
    public HeadersV1? ResponseHeaders => _responseHeaders;

    /// <summary>
    /// Gets the trailers (available after stream completes).
    /// </summary>
    public TrailersV1? Trailers => _trailers;

    /// <summary>
    /// Gets whether the remote side has half-closed (no more messages).
    /// </summary>
    public bool IsRemoteHalfClosed => _halfCloseReceived;

    /// <summary>
    /// Gets whether this side has half-closed (no more messages will be sent).
    /// </summary>
    public bool IsLocalHalfClosed => _halfCloseSent;

    /// <summary>
    /// Gets whether the stream was cancelled.
    /// </summary>
    public bool IsCancelled => _cancelled;

    /// <summary>
    /// Gets whether this is a server-initiated stream (i.e., server received request).
    /// </summary>
    public bool IsServerStream { get; }

    internal ShmGrpcStream(uint streamId, ShmConnection connection, bool isServerStream = false)
    {
        StreamId = streamId;
        _connection = connection;
        IsServerStream = isServerStream;
        _inboundFrames = Channel.CreateUnbounded<(FrameType, byte[], int)>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        _disposeCts = new CancellationTokenSource();
        _sendLock = new SemaphoreSlim(1, 1);
        _sendWindow = ShmConstants.InitialWindowSize;
    }

    /// <summary>
    /// Sets the request headers from incoming HEADERS frame (server-side).
    /// </summary>
    internal void SetRequestHeaders(HeadersV1 headers)
    {
        _requestHeaders = headers;
    }

    /// <summary>
    /// Sends request headers (client-side, must be called first).
    /// </summary>
    public async Task SendRequestHeadersAsync(string method, string authority, Metadata? metadata = null, DateTime? deadline = null)
    {
        ThrowIfDisposed();
        if (!IsClientStream)
            throw new InvalidOperationException("Only client can send request headers");
        if (_requestHeaders != null)
            throw new InvalidOperationException("Request headers already sent");

        _requestHeaders = new HeadersV1
        {
            Version = 1,
            HeaderType = 0, // client-initial
            Method = method,
            Authority = authority,
            DeadlineUnixNano = deadline.HasValue
                ? (ulong)new DateTimeOffset(deadline.Value).ToUnixTimeMilliseconds() * 1_000_000
                : 0,
            Metadata = ConvertMetadata(metadata)
        };

        var payload = _requestHeaders.Encode();
        await SendFrameAsync(FrameType.Headers, HeadersFlags.Initial, payload);
    }

    /// <summary>
    /// Sends response headers (server-side, before first message).
    /// </summary>
    public async Task SendResponseHeadersAsync(Metadata? metadata = null)
    {
        ThrowIfDisposed();
        if (IsClientStream)
            throw new InvalidOperationException("Only server can send response headers");
        if (_responseHeaders != null)
            throw new InvalidOperationException("Response headers already sent");

        _responseHeaders = new HeadersV1
        {
            Version = 1,
            HeaderType = 1, // server-initial
            Metadata = ConvertMetadata(metadata)
        };

        var payload = _responseHeaders.Encode();
        await SendFrameAsync(FrameType.Headers, HeadersFlags.Initial, payload);
    }

    /// <summary>
    /// Sends a message payload with gRPC length-prefix header.
    /// The data should be the raw protobuf message (without gRPC header).
    /// </summary>
    public async Task SendMessageAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_halfCloseSent)
            throw new InvalidOperationException("Cannot send after half-close");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);

        // gRPC wire format total: [compressed:1][length:4 BE][data]
        var totalGrpcSize = 5 + data.Length;

        // Wait for send window (account for full gRPC message size)
        while (Interlocked.Read(ref _sendWindow) < totalGrpcSize)
        {
            linkedCts.Token.ThrowIfCancellationRequested();
            await Task.Delay(1, linkedCts.Token); // Simple backpressure
        }

        Interlocked.Add(ref _sendWindow, -totalGrpcSize);

        // Build 5-byte gRPC length-prefix header separately and use scatter write
        // to avoid copying data into an intermediate buffer
        var grpcPrefix = new byte[5];
        grpcPrefix[0] = 0; // Not compressed
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(grpcPrefix.AsSpan(1, 4), (uint)data.Length);

        await SendFrameScatterAsync(FrameType.Message, 0, grpcPrefix, data, linkedCts.Token);
    }

    /// <summary>
    /// Sends trailers and closes the stream (server-side).
    /// </summary>
    public async Task SendTrailersAsync(StatusCode statusCode, string? statusMessage = null, Metadata? metadata = null)
    {
        ThrowIfDisposed();
        if (IsClientStream)
            throw new InvalidOperationException("Only server can send trailers");

        _trailers = new TrailersV1
        {
            Version = 1,
            GrpcStatusCode = statusCode,
            GrpcStatusMessage = statusMessage,
            Metadata = ConvertMetadata(metadata)
        };

        var payload = _trailers.Encode();
        await SendFrameAsync(FrameType.Trailers, TrailersFlags.EndStream, payload);
        _halfCloseSent = true;
        
        // After sending trailers, the stream is complete - remove from connection
        _connection.RemoveStream(StreamId);
    }

    /// <summary>
    /// Signals that no more messages will be sent from this side.
    /// </summary>
    public async Task SendHalfCloseAsync()
    {
        ThrowIfDisposed();
        if (_halfCloseSent) return;

        await SendFrameAsync(FrameType.HalfClose, 0, Array.Empty<byte>());
        _halfCloseSent = true;
    }

    /// <summary>
    /// Cancels the stream.
    /// </summary>
    public async Task CancelAsync()
    {
        if (_cancelled || _disposed) return;
        _cancelled = true;

        try
        {
            await SendFrameAsync(FrameType.Cancel, 0, Array.Empty<byte>());
        }
        catch { }

        _inboundFrames.Writer.TryComplete();
        
        // Remove from connection's stream tracking
        _connection.RemoveStream(StreamId);
    }

    /// <summary>
    /// Receives the next frame from the stream.
    /// Returns a correctly-sized copy of the payload (for external/raw-wire callers).
    /// </summary>
    /// <returns>The frame type and payload, or null if the stream is closed.</returns>
    public async Task<(FrameType Type, byte[] Payload)?> ReceiveFrameAsync(CancellationToken cancellationToken = default)
    {
        var pooledResult = await ReceiveFramePooledAsync(cancellationToken);
        if (pooledResult == null) return null;

        var (type, payload, payloadLength) = pooledResult.Value;
        // Copy to a correctly-sized array and return the pooled buffer
        byte[] result;
        if (payloadLength == 0)
        {
            result = Array.Empty<byte>();
        }
        else
        {
            result = payload.AsSpan(0, payloadLength).ToArray();
            ArrayPool<byte>.Shared.Return(payload);
        }
        return (type, result);
    }

    /// <summary>
    /// Receives the next frame using pooled buffers (internal hot path).
    /// The caller is responsible for returning the pooled buffer via ReturnPooledBuffer.
    /// </summary>
    internal async Task<(FrameType Type, byte[] Payload, int PayloadLength)?> ReceiveFramePooledAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);

        try
        {
            if (await _inboundFrames.Reader.WaitToReadAsync(linkedCts.Token))
            {
                if (_inboundFrames.Reader.TryRead(out var frame))
                {
                    return frame;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ChannelClosedException)
        {
        }

        return null;
    }

    /// <summary>
    /// Receives request headers (server-side).
    /// </summary>
    public async Task<HeadersV1> ReceiveRequestHeadersAsync(CancellationToken cancellationToken = default)
    {
        if (IsClientStream)
            throw new InvalidOperationException("Only server receives request headers");

        while (true)
        {
            var frame = await ReceiveFramePooledAsync(cancellationToken);
            if (frame == null)
                throw new InvalidOperationException("Stream closed before receiving headers");

            var (type, payload, payloadLength) = frame.Value;
            try
            {
                if (type == FrameType.Headers)
                {
                    _requestHeaders = HeadersV1.Decode(payload.AsSpan(0, payloadLength).ToArray());
                    return _requestHeaders;
                }
            }
            finally
            {
                ReturnPooledBuffer(payload, payloadLength);
            }
        }
    }

    /// <summary>
    /// Receives response headers (client-side).
    /// </summary>
    public async Task<HeadersV1> ReceiveResponseHeadersAsync(CancellationToken cancellationToken = default)
    {
        if (!IsClientStream)
            throw new InvalidOperationException("Only client receives response headers");

        while (true)
        {
            var frame = await ReceiveFramePooledAsync(cancellationToken);
            if (frame == null)
                throw new InvalidOperationException("Stream closed before receiving headers");

            var (type, payload, payloadLength) = frame.Value;
            try
            {
                if (type == FrameType.Headers)
                {
                    _responseHeaders = HeadersV1.Decode(payload.AsSpan(0, payloadLength).ToArray());
                    return _responseHeaders;
                }
            }
            finally
            {
                ReturnPooledBuffer(payload, payloadLength);
            }
        }
    }

    /// <summary>
    /// Receives messages from the stream.
    /// Decodes the gRPC wire format: [compressed:1][length:4 BE][data].
    /// Returns the raw protobuf message data as a ReadOnlyMemory&lt;byte&gt;.
    /// Uses pooled buffers: the yielded memory is valid until the next iteration.
    /// </summary>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveMessagesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Track the previous iteration's pooled buffer so we can return it
        // after the caller has consumed the yielded data.
        byte[]? previousPooledBuffer = null;
        int previousPooledLength = 0;

        try
        {
            // Read from the channel until it's completed or we get end-of-stream indicators
            while (!_cancelled)
            {
                var frame = await ReceiveFramePooledAsync(cancellationToken);
                if (frame == null) break;

                // Return the previous iteration's pooled buffer now that the caller has consumed it
                if (previousPooledBuffer != null)
                {
                    ReturnPooledBuffer(previousPooledBuffer, previousPooledLength);
                    previousPooledBuffer = null;
                }

                var (type, framePayload, framePayloadLength) = frame.Value;

                switch (type)
                {
                    case FrameType.Message:
                        // Send window update (use actual payload length, not array length)
                        var increment = (uint)framePayloadLength;
                        if (increment > 0)
                        {
                            _connection.SendFrame(FrameType.WindowUpdate, StreamId, 0,
                                BitConverter.GetBytes(increment));
                        }

                        // Decode gRPC wire format: [compressed:1][length:4 BE][data]
                        if (framePayloadLength < 5)
                        {
                            ReturnPooledBuffer(framePayload, framePayloadLength);
                            throw new InvalidDataException($"MESSAGE payload too short: {framePayloadLength} bytes, expected at least 5");
                        }

                        var compressed = framePayload[0] != 0;
                        if (compressed)
                        {
                            ReturnPooledBuffer(framePayload, framePayloadLength);
                            throw new NotSupportedException("Compression not yet supported");
                        }

                        var messageLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(framePayload.AsSpan(1, 4));
                        if (framePayloadLength != 5 + messageLength)
                        {
                            ReturnPooledBuffer(framePayload, framePayloadLength);
                            throw new InvalidDataException($"MESSAGE payload length mismatch: got {framePayloadLength} bytes, expected {5 + messageLength}");
                        }

                        // Yield the protobuf data as a slice of the pooled buffer.
                        // Defer returning the buffer until the next iteration when the caller is done.
                        previousPooledBuffer = framePayload;
                        previousPooledLength = framePayloadLength;
                        yield return framePayload.AsMemory(5, (int)messageLength);
                        break;

                    case FrameType.HalfClose:
                        ReturnPooledBuffer(framePayload, framePayloadLength);
                        _halfCloseReceived = true;
                        yield break;

                    case FrameType.Trailers:
                        _trailers = TrailersV1.Decode(framePayload.AsSpan(0, framePayloadLength).ToArray());
                        ReturnPooledBuffer(framePayload, framePayloadLength);
                        _halfCloseReceived = true;
                        yield break;

                    case FrameType.Cancel:
                        ReturnPooledBuffer(framePayload, framePayloadLength);
                        _cancelled = true;
                        yield break;
                }
            }
        }
        finally
        {
            // Return any remaining pooled buffer (e.g., if the iterator was disposed early)
            if (previousPooledBuffer != null)
            {
                ReturnPooledBuffer(previousPooledBuffer, previousPooledLength);
            }
        }
    }

    internal void OnFrameReceived(FrameHeader header, byte[] payload, int payloadLength)
    {
        if (_disposed || _cancelled) return;

        switch (header.Type)
        {
            case FrameType.Cancel:
                _cancelled = true;
                // Return pooled buffer since it won't flow through the channel
                if (payloadLength > 0) ArrayPool<byte>.Shared.Return(payload);
                _inboundFrames.Writer.TryComplete();
                break;

            case FrameType.HalfClose:
                // Queue the frame; the consumer will set _halfCloseReceived when reading
                _inboundFrames.Writer.TryWrite((header.Type, payload, payloadLength));
                _inboundFrames.Writer.TryComplete();
                break;

            case FrameType.Trailers:
                // Queue the frame; the consumer will decode and set _trailers when reading
                _inboundFrames.Writer.TryWrite((header.Type, payload, payloadLength));
                _inboundFrames.Writer.TryComplete();
                break;

            default:
                _inboundFrames.Writer.TryWrite((header.Type, payload, payloadLength));
                break;
        }
    }

    internal void OnWindowUpdate(uint increment)
    {
        Interlocked.Add(ref _sendWindow, increment);
    }

    /// <summary>
    /// Updates the flow control window based on BDP estimation.
    /// </summary>
    /// <param name="newWindow">The new window size.</param>
    internal void UpdateFlowControlWindow(uint newWindow)
    {
        var currentWindow = Interlocked.Read(ref _sendWindow);
        if (newWindow > currentWindow)
        {
            var delta = (long)newWindow - currentWindow;
            Interlocked.Add(ref _sendWindow, delta);
        }
    }

    private async Task SendFrameAsync(FrameType type, byte flags, byte[] payload, CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            _connection.SendFrame(type, StreamId, flags, payload);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Sends a frame with a two-part payload (scatter write) to avoid an intermediate copy.
    /// </summary>
    private async Task SendFrameScatterAsync(FrameType type, byte flags, ReadOnlyMemory<byte> payload1, ReadOnlyMemory<byte> payload2, CancellationToken cancellationToken = default)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            _connection.SendFrame(type, StreamId, flags, payload1.Span, payload2.Span);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static MetadataKV[] ConvertMetadata(Metadata? metadata)
    {
        if (metadata == null || metadata.Count == 0)
            return Array.Empty<MetadataKV>();

        return metadata.Select(e => e.IsBinary
            ? new MetadataKV(e.Key, e.ValueBytes)
            : new MetadataKV(e.Key, e.Value)).ToArray();
    }

    /// <summary>
    /// Returns a pooled buffer to the ArrayPool if it was rented (payloadLength > 0).
    /// Buffers with payloadLength == 0 are Array.Empty&lt;byte&gt; and must not be returned.
    /// </summary>
    private static void ReturnPooledBuffer(byte[] buffer, int payloadLength)
    {
        if (payloadLength > 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _disposeCts.Cancel();
            _inboundFrames.Writer.TryComplete();
            // Drain and return any remaining pooled buffers in the channel
            while (_inboundFrames.Reader.TryRead(out var frame))
            {
                ReturnPooledBuffer(frame.Payload, frame.PayloadLength);
            }
            _connection.RemoveStream(StreamId);
            _sendLock.Dispose();
            _disposeCts.Dispose();
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
