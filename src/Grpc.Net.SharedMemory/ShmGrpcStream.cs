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
using System.Buffers.Binary;
using System.Threading.Channels;
using Grpc.Core;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Lightweight struct carried through the inbound Channel to avoid LOH allocations.
/// The payload is backed by either a pooled buffer or a ring reservation and
/// must be released after consumption.
/// </summary>
public readonly struct InboundFrame
{
    public readonly FrameType Type;
    public readonly byte Flags;
    private readonly FramePayload _payload;

    public InboundFrame(FrameType type, FramePayload payload, byte flags = 0)
    {
        Type = type;
        Flags = flags;
        _payload = payload;
    }

    /// <summary>Exact-length view into the buffer.</summary>
    public ReadOnlyMemory<byte> Memory => _payload.Memory;

    public int Length => _payload.Length;

    /// <summary>Returns the buffer to the pool or commits the ring read.</summary>
    public void ReturnToPool()
    {
        _payload.Release();
    }

    /// <summary>Tuple deconstruction without copying payload data.</summary>
    public void Deconstruct(out FrameType type, out ReadOnlyMemory<byte> payload)
    {
        type = Type;
        payload = Memory;
    }
}

/// <summary>
/// Represents a single gRPC stream (call) over a shared memory connection.
/// Handles frame routing, flow control, and message sequencing for one RPC.
/// </summary>
public sealed class ShmGrpcStream : IDisposable, IAsyncDisposable
{
    private readonly ShmConnection _connection;
    private readonly Channel<InboundFrame> _inboundFrames;
    private readonly CancellationTokenSource _disposeCts;
    private readonly SemaphoreSlim _sendLock;
    private readonly SemaphoreSlim _sendWindowSignal;

    private HeadersV1? _requestHeaders;
    private HeadersV1? _responseHeaders;
    private TrailersV1? _trailers;
    private long _sendWindow;
    private bool _halfCloseSent;
    private bool _halfCloseReceived;
    private bool _cancelled;
    private int _disposed;

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
        _inboundFrames = Channel.CreateUnbounded<InboundFrame>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        _disposeCts = new CancellationTokenSource();
        _sendLock = new SemaphoreSlim(1, 1);
        _sendWindowSignal = new SemaphoreSlim(0, int.MaxValue);
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
    public Task SendRequestHeadersAsync(string method, string authority, Metadata? metadata = null, DateTime? deadline = null)
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

        var (payload, payloadLength) = _requestHeaders.Encode();
        if (payloadLength <= 512)
        {
            Task task;
            try
            {
                task = SendFrameAsync(FrameType.Headers, HeadersFlags.Initial,
                    payload.AsMemory(0, payloadLength));
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(payload);
                throw;
            }
            if (task.IsCompletedSuccessfully)
            {
                ArrayPool<byte>.Shared.Return(payload);
                return Task.CompletedTask;
            }
            return SendRequestHeadersReturnPoolAsync(task, payload);
        }
        else
        {
            return SendFrameZeroCopyAsync(FrameType.Headers, HeadersFlags.Initial,
                payload.AsMemory(0, payloadLength), payload);
        }
    }

    private static async Task SendRequestHeadersReturnPoolAsync(Task sendTask, byte[] payload)
    {
        try
        {
            await sendTask.ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
        }
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

        var (payload, payloadLength) = _responseHeaders.Encode();
        if (payloadLength <= 512)
        {
            try
            {
                await SendFrameAsync(FrameType.Headers, HeadersFlags.Initial,
                    payload.AsMemory(0, payloadLength));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }
        else
        {
            await SendFrameZeroCopyAsync(FrameType.Headers, HeadersFlags.Initial,
                payload.AsMemory(0, payloadLength), payload);
        }
    }

    /// <summary>
    /// Sends a message payload.
    /// </summary>
    public Task SendMessageAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_halfCloseSent)
            throw new InvalidOperationException("Cannot send after half-close");

        // Fast path: when the send window has enough space (the common case for
        // small payloads — 120B vs 32 MiB window), skip the async machinery and
        // the LinkedCTS allocation entirely. Each CreateLinkedTokenSource costs
        // ~200 bytes; at 50K+ messages/sec this dominates GC gen0 pressure.
        //
        // Note: the Read-then-Add is not strictly atomic, so two concurrent
        // callers could both pass the check and drive _sendWindow negative.
        // This is safe because (1) gRPC serializes sends per stream — concurrent
        // SendMessageAsync on the same stream does not happen in practice, and
        // (2) even if it did, a negative window just causes the next send to
        // enter the slow path and wait for WINDOW_UPDATE, which is self-correcting.
        if (Interlocked.Read(ref _sendWindow) >= data.Length)
        {
            Interlocked.Add(ref _sendWindow, -data.Length);
            var ct = cancellationToken.CanBeCanceled ? cancellationToken : _disposeCts.Token;

            if (data.Length >= 65536 && Volatile.Read(ref _disposed) == 0)
            {
#pragma warning disable CA2016
                if (_sendLock.Wait(0))
#pragma warning restore CA2016
                {
                    try
                    {
                        _connection.SendFrameZeroCopyAndWait(FrameType.Message, StreamId, 0, data, ct);
                    }
                    catch
                    {
                        Interlocked.Add(ref _sendWindow, data.Length);
                        throw;
                    }
                    finally
                    {
                        _sendLock.Release();
                    }
                    return Task.CompletedTask;
                }
                // Lock contended — fall through to normal SendFrameAsync
                // (which will acquire the lock and do defensive copy)
            }
            var task = SendFrameAsync(FrameType.Message, 0, data, ct);
            // Sync completion (the common case): no restore needed.
            if (task.IsCompletedSuccessfully)
            {
                return task;
            }
            // Async or failed: restore window on failure.
            return SendMessageWithWindowRestoreAsync(task, data.Length);
        }

        // Slow path: need to wait for send window — requires linked CTS.
        return SendMessageSlowAsync(data, cancellationToken);
    }

    private async Task SendMessageWithWindowRestoreAsync(Task sendTask, int dataLength)
    {
        try
        {
            await sendTask.ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Add(ref _sendWindow, dataLength);
            throw;
        }
    }

    private async Task SendMessageSlowAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);

        // Wait for send window
        while (Interlocked.Read(ref _sendWindow) < data.Length)
        {
            linkedCts.Token.ThrowIfCancellationRequested();
            await _sendWindowSignal.WaitAsync(linkedCts.Token);
        }

        Interlocked.Add(ref _sendWindow, -data.Length);
        try
        {
            if (data.Length >= 65536 && Volatile.Read(ref _disposed) == 0)
            {
                await _sendLock.WaitAsync(linkedCts.Token);
                try
                {
                    _connection.SendFrameZeroCopyAndWait(FrameType.Message, StreamId, 0, data, linkedCts.Token);
                }
                finally
                {
                    _sendLock.Release();
                }
            }
            else
            {
                await SendFrameAsync(FrameType.Message, 0, data, linkedCts.Token);
            }
        }
        catch
        {
            Interlocked.Add(ref _sendWindow, data.Length);
            throw;
        }
    }

    /// <summary>
    /// Sends a message with an implicit half-close in one frame (EndStream flag).
    /// Eliminates the separate HalfClose frame, reducing a unary RPC from 3 to 2
    /// client-side frames and saving one ring write + signal round-trip.
    /// </summary>
    public Task SendMessageAndHalfCloseAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_halfCloseSent)
            throw new InvalidOperationException("Cannot send after half-close");

        if (Interlocked.Read(ref _sendWindow) >= data.Length)
        {
            Interlocked.Add(ref _sendWindow, -data.Length);
            var ct = cancellationToken.CanBeCanceled ? cancellationToken : _disposeCts.Token;

            // Large payloads: wait for the ring write to complete before
            // returning, so the caller can safely reuse the buffer.
            if (data.Length >= 65536 && Volatile.Read(ref _disposed) == 0)
            {
#pragma warning disable CA2016
                if (_sendLock.Wait(0))
#pragma warning restore CA2016
                {
                    try
                    {
                        _connection.SendFrameZeroCopyAndWait(FrameType.Message, StreamId, MessageFlags.EndStream, data, ct);
                        _halfCloseSent = true;
                        return Task.CompletedTask;
                    }
                    catch
                    {
                        Interlocked.Add(ref _sendWindow, data.Length);
                        throw;
                    }
                    finally
                    {
                        _sendLock.Release();
                    }
                }
            }

            var task = SendFrameAsync(FrameType.Message, MessageFlags.EndStream, data, ct);
            if (task.IsCompletedSuccessfully)
            {
                _halfCloseSent = true;
                return Task.CompletedTask;
            }
            return SendMessageAndHalfCloseCompleteAsync(task);
        }

        return SendMessageAndHalfCloseSlowAsync(data, cancellationToken);
    }

    private async Task SendMessageAndHalfCloseCompleteAsync(Task sendTask)
    {
        await sendTask.ConfigureAwait(false);
        _halfCloseSent = true;
    }

    private async Task SendMessageAndHalfCloseSlowAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        while (Interlocked.Read(ref _sendWindow) < data.Length)
        {
            linkedCts.Token.ThrowIfCancellationRequested();
            await _sendWindowSignal.WaitAsync(linkedCts.Token);
        }
        Interlocked.Add(ref _sendWindow, -data.Length);
        try
        {
            if (data.Length >= 65536 && Volatile.Read(ref _disposed) == 0)
            {
                await _sendLock.WaitAsync(linkedCts.Token);
                try
                {
                    _connection.SendFrameZeroCopyAndWait(FrameType.Message, StreamId, MessageFlags.EndStream, data, linkedCts.Token);
                }
                finally
                {
                    _sendLock.Release();
                }
            }
            else
            {
                await SendFrameAsync(FrameType.Message, MessageFlags.EndStream, data, linkedCts.Token);
            }
            _halfCloseSent = true;
        }
        catch
        {
            Interlocked.Add(ref _sendWindow, data.Length);
            throw;
        }
    }

    /// <summary>
    /// Sends a message payload using zero-copy. The <paramref name="pooledBuffer"/>
    /// is returned to <see cref="ArrayPool{T}"/> after the data has been written
    /// to the ring buffer, replacing the caller's <c>finally</c> block.
    /// </summary>
    public Task SendMessageZeroCopyAsync(ReadOnlyMemory<byte> data, byte[] pooledBuffer, CancellationToken cancellationToken = default)
    {
        try
        {
            ThrowIfDisposed();
            if (_halfCloseSent)
                throw new InvalidOperationException("Cannot send after half-close");
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(pooledBuffer);
            throw;
        }

        // Fast path: window available — skip LinkedCTS allocation.
        // See SendMessageAsync for the Read-then-Add safety rationale.
        if (Interlocked.Read(ref _sendWindow) >= data.Length)
        {
            Interlocked.Add(ref _sendWindow, -data.Length);
            var ct = cancellationToken.CanBeCanceled ? cancellationToken : _disposeCts.Token;
            var task = SendFrameZeroCopyAsync(FrameType.Message, 0, data, pooledBuffer, ct);
            if (task.IsCompletedSuccessfully)
            {
                return task;
            }
            return SendZeroCopyWithWindowRestoreAsync(task, data.Length);
        }

        // Slow path: need to wait for window.
        return SendMessageZeroCopySlowAsync(data, pooledBuffer, cancellationToken);
    }

    private async Task SendZeroCopyWithWindowRestoreAsync(Task sendTask, int dataLength)
    {
        try
        {
            await sendTask.ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Add(ref _sendWindow, dataLength);
            throw;
        }
    }

    private async Task SendMessageZeroCopySlowAsync(ReadOnlyMemory<byte> data, byte[] pooledBuffer, CancellationToken cancellationToken)
    {
        CancellationTokenSource? linkedCts = null;
        try
        {
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);

            // Wait for send window
            while (Interlocked.Read(ref _sendWindow) < data.Length)
            {
                linkedCts.Token.ThrowIfCancellationRequested();
                await _sendWindowSignal.WaitAsync(linkedCts.Token);
            }
        }
        catch
        {
            linkedCts?.Dispose();
            ArrayPool<byte>.Shared.Return(pooledBuffer);
            throw;
        }

        Interlocked.Add(ref _sendWindow, -data.Length);
        try
        {
            await SendFrameZeroCopyAsync(FrameType.Message, 0, data, pooledBuffer, linkedCts!.Token);
        }
        catch
        {
            Interlocked.Add(ref _sendWindow, data.Length);
            throw;
        }
        finally
        {
            linkedCts!.Dispose();
        }
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

        var (payload, payloadLength) = _trailers.Encode();
        if (payloadLength <= 512)
        {
            try
            {
                await SendFrameAsync(FrameType.Trailers, TrailersFlags.EndStream,
                    payload.AsMemory(0, payloadLength));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }
        else
        {
            await SendFrameZeroCopyAsync(FrameType.Trailers, TrailersFlags.EndStream,
                payload.AsMemory(0, payloadLength), payload);
        }
        _halfCloseSent = true;
    }

    /// <summary>
    /// Signals that no more messages will be sent from this side.
    /// </summary>
    public Task SendHalfCloseAsync()
    {
        ThrowIfDisposed();
        if (_halfCloseSent) return Task.CompletedTask;

        var task = SendFrameAsync(FrameType.HalfClose, 0, Array.Empty<byte>());
        if (task.IsCompletedSuccessfully)
        {
            _halfCloseSent = true;
            return Task.CompletedTask;
        }
        return SendHalfCloseSlowAsync(task);
    }

    private async Task SendHalfCloseSlowAsync(Task sendTask)
    {
        await sendTask.ConfigureAwait(false);
        _halfCloseSent = true;
    }

    /// <summary>
    /// Cancels the stream.
    /// </summary>
    public async Task CancelAsync()
    {
        if (_cancelled || Volatile.Read(ref _disposed) != 0) return;
        _cancelled = true;

        try
        {
            await SendFrameAsync(FrameType.Cancel, 0, Array.Empty<byte>());
        }
        catch { }

        _inboundFrames.Writer.TryComplete();
    }

    /// <summary>
    /// Receives the next frame from the stream.
    /// </summary>
    /// <returns>The frame, or null if the stream is closed.</returns>
    public Task<InboundFrame?> ReceiveFrameAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // Fast path: if a frame is already queued, return it immediately
        // without allocating a LinkedCTS or async state machine.
        if (_inboundFrames.Reader.TryRead(out var frame))
        {
            return Task.FromResult<InboundFrame?>(frame);
        }

        // Slow path: need to wait for a frame.
        return ReceiveFrameSlowAsync(cancellationToken);
    }

    private async Task<InboundFrame?> ReceiveFrameSlowAsync(CancellationToken cancellationToken)
    {
        // Only create LinkedCTS when the caller provided a cancellable token.
        // In streaming steady state, grpc-dotnet typically passes default.
        CancellationToken ct;
        CancellationTokenSource? linkedCts = null;
        if (cancellationToken.CanBeCanceled)
        {
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
            ct = linkedCts.Token;
        }
        else
        {
            ct = _disposeCts.Token;
        }

        try
        {
            if (await _inboundFrames.Reader.WaitToReadAsync(ct))
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
        finally
        {
            linkedCts?.Dispose();
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
            var frame = await ReceiveFrameAsync(cancellationToken);
            if (frame == null)
                throw new InvalidOperationException("Stream closed before receiving headers");

            if (frame.Value.Type == FrameType.Headers)
            {
                _requestHeaders = HeadersV1.Decode(frame.Value.Memory.Span);
                frame.Value.ReturnToPool();
                return _requestHeaders;
            }

            frame.Value.ReturnToPool();
        }
    }

    /// <summary>
    /// Receives response headers (client-side).
    /// </summary>
    /// <exception cref="ShmStreamRefusedException">
    /// Thrown when the server refuses the stream (sends TRAILERS before HEADERS),
    /// typically because the maximum concurrent stream limit was reached.
    /// </exception>
    public Task<HeadersV1> ReceiveResponseHeadersAsync(CancellationToken cancellationToken = default)
    {
        if (!IsClientStream)
            throw new InvalidOperationException("Only client receives response headers");

        var frameTask = ReceiveFrameAsync(cancellationToken);
        if (frameTask.IsCompletedSuccessfully)
        {
            var frame = frameTask.Result;
            if (frame != null && frame.Value.Type == FrameType.Headers)
            {
                _responseHeaders = HeadersV1.Decode(frame.Value.Memory.Span);
                frame.Value.ReturnToPool();
                return Task.FromResult(_responseHeaders);
            }
        }
        return ReceiveResponseHeadersSlowAsync(frameTask, cancellationToken);
    }

    private async Task<HeadersV1> ReceiveResponseHeadersSlowAsync(
        Task<InboundFrame?> firstFrameTask, CancellationToken cancellationToken)
    {
        var firstFrame = await firstFrameTask.ConfigureAwait(false);
        if (firstFrame == null)
            throw new InvalidOperationException("Stream closed before receiving headers");

        if (firstFrame.Value.Type == FrameType.Headers)
        {
            _responseHeaders = HeadersV1.Decode(firstFrame.Value.Memory.Span);
            firstFrame.Value.ReturnToPool();
            return _responseHeaders;
        }

        if (firstFrame.Value.Type == FrameType.Trailers)
        {
            var trailers = TrailersV1.Decode(firstFrame.Value.Memory.Span);
            firstFrame.Value.ReturnToPool();
            _trailers = trailers;
            _halfCloseReceived = true;
            throw new ShmStreamRefusedException(trailers.GrpcStatusMessage ?? "Stream refused by server");
        }

        firstFrame.Value.ReturnToPool();

        while (true)
        {
            var frame = await ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame == null)
                throw new InvalidOperationException("Stream closed before receiving headers");

            if (frame.Value.Type == FrameType.Headers)
            {
                _responseHeaders = HeadersV1.Decode(frame.Value.Memory.Span);
                frame.Value.ReturnToPool();
                return _responseHeaders;
            }

            // Server sent TRAILERS before HEADERS — stream was refused.
            // This happens when the server's max concurrent streams is exceeded.
            if (frame.Value.Type == FrameType.Trailers)
            {
                var trailers = TrailersV1.Decode(frame.Value.Memory.Span);
                frame.Value.ReturnToPool();
                _trailers = trailers;
                _halfCloseReceived = true;
                throw new ShmStreamRefusedException(trailers.GrpcStatusMessage ?? "Stream refused by server");
            }

            frame.Value.ReturnToPool();
        }
    }

    /// <summary>
    /// Receives messages from the stream.
    /// Each yielded <c>byte[]</c> is an owned, exact-size array safe to hold indefinitely.
    /// </summary>
    public async IAsyncEnumerable<byte[]> ReceiveMessagesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        MemoryStream? messageAccumulator = null;
        while (true)
        {
            if (_cancelled) yield break;

            var frame = await ReceiveFrameAsync(cancellationToken);
            if (frame == null) yield break;

            var f = frame.Value;
            switch (f.Type)
            {
                case FrameType.Message:
                    // Send stream-level window update via the batched writer
                    var increment = (uint)f.Length;
                    if (increment > 0)
                    {
                        _connection.SendStreamWindowUpdate(StreamId, increment);
                    }

                    if ((f.Flags & MessageFlags.More) != 0)
                    {
                        messageAccumulator ??= new MemoryStream();
                        messageAccumulator.Write(f.Memory.Span);
                        f.ReturnToPool();
                        break;
                    }

                    if (messageAccumulator != null)
                    {
                        messageAccumulator.Write(f.Memory.Span);
                        f.ReturnToPool();
                        yield return messageAccumulator.ToArray();
                        messageAccumulator.SetLength(0);
                        if ((f.Flags & MessageFlags.EndStream) != 0) { _halfCloseReceived = true; yield break; }
                        break;
                    }

                    // Yield an owned copy so payload buffers can be released safely.
                    var owned = f.Memory.ToArray();
                    f.ReturnToPool();
                    yield return owned;
                    if ((f.Flags & MessageFlags.EndStream) != 0) { _halfCloseReceived = true; yield break; }
                    break;

                case FrameType.HalfClose:
                    f.ReturnToPool();
                    _halfCloseReceived = true;
                    yield break;

                case FrameType.Trailers:
                    _trailers = TrailersV1.Decode(f.Memory.Span);
                    f.ReturnToPool();
                    _halfCloseReceived = true;
                    yield break;

                case FrameType.Cancel:
                    f.ReturnToPool();
                    _cancelled = true;
                    yield break;

                default:
                    f.ReturnToPool();
                    break;
            }
        }
    }

    /// <summary>
    /// Receives the next complete message from the stream, accepting a per-call
    /// cancellation token. Unlike <see cref="ReceiveMessageBuffersAsync"/> (which
    /// binds a token at enumerator-creation time and ignores subsequent tokens),
    /// this method propagates the caller's <paramref name="cancellationToken"/>
    /// on every call, so client-side cancel/deadline always takes effect — even
    /// on a long-blocked read.
    /// <para>
    /// The returned <see cref="ReadOnlyMemory{T}"/> may be backed by a pooled
    /// buffer (single-frame fast path) or by an owned array assembled from
    /// multiple fragments (multi-frame path via <see cref="MemoryStream.ToArray"/>).
    /// The caller must release <paramref name="previousFrame"/> (the frame from
    /// the prior call) before calling again — or pass <c>default</c> on first call.
    /// </para>
    /// </summary>
    /// <param name="previousFrame">
    /// The <see cref="InboundFrame"/> from the previous call. Its pooled buffer
    /// is released at the start of this call (deferred release for zero-copy).
    /// Pass <c>default</c> on the first call.
    /// </param>
    /// <param name="cancellationToken">Per-call cancellation token.</param>
    /// <returns>
    /// A tuple of (memory, frame, endOfStream). When <c>endOfStream</c> is true,
    /// the stream is complete and no more calls should be made.
    /// </returns>
    internal async Task<(ReadOnlyMemory<byte> Memory, InboundFrame Frame, bool EndOfStream)> ReceiveNextMessageBufferAsync(
        InboundFrame previousFrame,
        CancellationToken cancellationToken = default)
    {
        previousFrame.ReturnToPool();
        MemoryStream? messageAccumulator = null;

        while (true)
        {
            if (_cancelled)
            {
                return (default, default, true);
            }

            var frame = await ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame == null)
            {
                return (default, default, true);
            }

            var f = frame.Value;
            switch (f.Type)
            {
                case FrameType.Message:
                    // Send stream-level window update via the batched writer
                    var increment = (uint)f.Length;
                    if (increment > 0)
                    {
                        _connection.SendStreamWindowUpdate(StreamId, increment);
                    }

                    if ((f.Flags & MessageFlags.More) != 0)
                    {
                        // Multi-fragment: accumulate and continue reading
                        messageAccumulator ??= new MemoryStream();
                        messageAccumulator.Write(f.Memory.Span);
                        f.ReturnToPool();
                        continue;
                    }

                    if (messageAccumulator != null && messageAccumulator.Length > 0)
                    {
                        // Last fragment of multi-fragment message
                        messageAccumulator.Write(f.Memory.Span);
                        f.ReturnToPool();
                        var assembled = messageAccumulator.ToArray();
                        messageAccumulator.SetLength(0);
                        var endStream1 = (f.Flags & MessageFlags.EndStream) != 0;
                        if (endStream1) _halfCloseReceived = true;
                        return (assembled, default, endStream1);
                    }

                    // Single-frame message: return zero-copy view.
                    // Caller must hold onto 'f' and pass it back as previousFrame
                    // on the next call so the pooled buffer can be released.
                    if ((f.Flags & MessageFlags.EndStream) != 0)
                    {
                        _halfCloseReceived = true;
                        return (f.Memory, f, true);
                    }
                    return (f.Memory, f, false);

                case FrameType.HalfClose:
                    f.ReturnToPool();
                    _halfCloseReceived = true;
                    return (default, default, true);

                case FrameType.Trailers:
                    _trailers = TrailersV1.Decode(f.Memory.Span);
                    f.ReturnToPool();
                    _halfCloseReceived = true;
                    return (default, default, true);

                case FrameType.Cancel:
                    f.ReturnToPool();
                    _cancelled = true;
                    return (default, default, true);

                default:
                    f.ReturnToPool();
                    continue;
            }
        }
    }

    /// <summary>
    /// Internal high-performance message receiver that yields <see cref="ReadOnlyMemory{T}"/>
    /// views backed by pooled buffers. The memory is only
    /// valid until the next <c>MoveNextAsync</c> call — callers must copy any data
    /// they need to retain.
    /// Used by <see cref="ShmGrpcResponseStream"/> to avoid LOH allocations.
    /// </summary>
    internal async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveMessageBuffersAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        InboundFrame previousFrame = default;
        MemoryStream? messageAccumulator = null;
        try
        {
            while (true)
            {
                if (_cancelled) yield break;

                var frame = await ReceiveFrameAsync(cancellationToken);
                if (frame == null)
                {
                    yield break;
                }

                var f = frame.Value;
                switch (f.Type)
                {
                    case FrameType.Message:
                        // Send stream-level window update via the batched writer
                        var increment = (uint)f.Length;
                        if (increment > 0)
                        {
                            _connection.SendStreamWindowUpdate(StreamId, increment);
                        }

                        if ((f.Flags & MessageFlags.More) != 0)
                        {
                            messageAccumulator ??= new MemoryStream();
                            messageAccumulator.Write(f.Memory.Span);
                            f.ReturnToPool();
                            break;
                        }

                        if (messageAccumulator != null)
                        {
                            messageAccumulator.Write(f.Memory.Span);
                            f.ReturnToPool();

                            // Returning accumulated payload as owned memory avoids
                            // lifetime issues while still preventing many intermediate copies.
                            previousFrame.ReturnToPool();
                            yield return messageAccumulator.ToArray();
                            messageAccumulator.SetLength(0);
                            if ((f.Flags & MessageFlags.EndStream) != 0) { _halfCloseReceived = true; yield break; }
                            break;
                        }

                        // Return the PREVIOUS payload now that the consumer
                        // has advanced past it.
                        previousFrame.ReturnToPool();

                        previousFrame = f;
                        yield return f.Memory;
                        if ((f.Flags & MessageFlags.EndStream) != 0) { _halfCloseReceived = true; yield break; }
                        break;

                    case FrameType.HalfClose:
                        f.ReturnToPool();
                        _halfCloseReceived = true;
                        yield break;

                    case FrameType.Trailers:
                        _trailers = TrailersV1.Decode(f.Memory.Span);
                        f.ReturnToPool();
                        _halfCloseReceived = true;
                        yield break;

                    case FrameType.Cancel:
                        f.ReturnToPool();
                        _cancelled = true;
                        yield break;

                    default:
                        f.ReturnToPool();
                        break;
                }
            }
        }
        finally
        {
            previousFrame.ReturnToPool();
        }
    }

    internal void OnFrameReceived(InboundFrame frame)
    {
        if (Volatile.Read(ref _disposed) != 0 || _cancelled)
        {
            frame.ReturnToPool();
            return;
        }

        switch (frame.Type)
        {
            case FrameType.Cancel:
                _cancelled = true;
                frame.ReturnToPool();
                _inboundFrames.Writer.TryComplete();
                _connection.RemoveStream(StreamId);
                break;

            case FrameType.HalfClose:
                _halfCloseReceived = true;
                if (!_inboundFrames.Writer.TryWrite(frame))
                {
                    frame.ReturnToPool();
                }
                break;

            case FrameType.Trailers:
                _halfCloseReceived = true;
                if (!_inboundFrames.Writer.TryWrite(frame))
                {
                    frame.ReturnToPool();
                }
                _inboundFrames.Writer.TryComplete();
                // Auto-remove from connection to prevent accumulation when
                // callers don't dispose the stream (e.g., undisposed AsyncUnaryCall).
                // No more frames will arrive after TRAILERS.
                _connection.RemoveStream(StreamId);
                break;

            default:
                if (!_inboundFrames.Writer.TryWrite(frame))
                {
                    frame.ReturnToPool();
                }
                break;
        }
    }

    internal void OnWindowUpdate(uint increment)
    {
        Interlocked.Add(ref _sendWindow, increment);
        if (increment > 0 && Volatile.Read(ref _disposed) == 0)
        {
            try { _sendWindowSignal.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private Task SendFrameAsync(FrameType type, byte flags, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // Wait(0) is a non-blocking try-acquire; cancellation token is irrelevant.
#pragma warning disable CA2016
        if (_sendLock.Wait(0))
#pragma warning restore CA2016
        {
            try
            {
                _connection.SendFrame(type, StreamId, flags, payload.Span);
            }
            finally
            {
                _sendLock.Release();
            }

            return Task.CompletedTask;
        }

        // Contended: fall back to async wait.
        return SendFrameAsyncContended(type, flags, payload, cancellationToken);
    }

    private async Task SendFrameAsyncContended(FrameType type, byte flags, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _connection.SendFrame(type, StreamId, flags, payload.Span);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Zero-copy variant: enqueues without copying; the pooled buffer is returned
    /// to <see cref="ArrayPool{T}"/> by the writer thread after the ring write.
    /// </summary>
    private Task SendFrameZeroCopyAsync(FrameType type, byte flags,
        ReadOnlyMemory<byte> payload, byte[]? pooledBuffer, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            if (pooledBuffer != null) ArrayPool<byte>.Shared.Return(pooledBuffer);
            throw new ObjectDisposedException(nameof(ShmGrpcStream));
        }
        if (cancellationToken.IsCancellationRequested)
        {
            if (pooledBuffer != null) ArrayPool<byte>.Shared.Return(pooledBuffer);
            return Task.FromCanceled(cancellationToken);
        }

        // Wait(0) is a non-blocking try-acquire; cancellation token is irrelevant.
#pragma warning disable CA2016
        if (_sendLock.Wait(0))
#pragma warning restore CA2016
        {
            try
            {
                _connection.SendFrameZeroCopy(type, StreamId, flags, payload, pooledBuffer);
            }
            catch
            {
                if (pooledBuffer != null) ArrayPool<byte>.Shared.Return(pooledBuffer);
                throw;
            }
            finally
            {
                _sendLock.Release();
            }

            return Task.CompletedTask;
        }

        return SendFrameZeroCopyAsyncContended(type, flags, payload, pooledBuffer, cancellationToken);
    }

    private async Task SendFrameZeroCopyAsyncContended(FrameType type, byte flags,
        ReadOnlyMemory<byte> payload, byte[]? pooledBuffer, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            if (pooledBuffer != null) ArrayPool<byte>.Shared.Return(pooledBuffer);
            throw new ObjectDisposedException(nameof(ShmGrpcStream));
        }
        try
        {
            await _sendLock.WaitAsync(cancellationToken);
        }
        catch
        {
            if (pooledBuffer != null) ArrayPool<byte>.Shared.Return(pooledBuffer);
            throw;
        }

        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                if (pooledBuffer != null) ArrayPool<byte>.Shared.Return(pooledBuffer);
                throw new ObjectDisposedException(nameof(ShmGrpcStream));
            }
            _connection.SendFrameZeroCopy(type, StreamId, flags, payload, pooledBuffer);
        }
        catch
        {
            if (pooledBuffer != null) ArrayPool<byte>.Shared.Return(pooledBuffer);
            throw;
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

        var items = new MetadataKV[metadata.Count];
        var index = 0;
        foreach (var entry in metadata)
        {
            items[index++] = entry.IsBinary
                ? new MetadataKV(entry.Key, entry.ValueBytes)
                : new MetadataKV(entry.Key, entry.Value);
        }

        return items;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _disposeCts.Cancel();
        _inboundFrames.Writer.TryComplete();

        // Drain any remaining queued frames to return pooled buffers.
        while (_inboundFrames.Reader.TryRead(out var frame))
        {
            frame.ReturnToPool();
        }

        _connection.RemoveStream(StreamId);
        _sendLock.Dispose();
        _sendWindowSignal.Dispose();
        _disposeCts.Dispose();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
