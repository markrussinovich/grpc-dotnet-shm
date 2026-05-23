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
using Google.Protobuf;
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

    /// <summary>True if this frame's payload is a ring-backed ZC view.</summary>
    public bool IsSpeculativeZeroCopy => _payload.IsSpeculativeZeroCopy;

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
    private readonly CancellationTokenSource _cancellationCts;
    private readonly SemaphoreSlim _sendLock;

    private HeadersV1? _requestHeaders;
    private HeadersV1? _responseHeaders;
    private TrailersV1? _trailers;
    private string? _responseEncoding;
    private int _halfCloseSent; // 0=not sent, 1=sent; use Interlocked for thread safety
    private bool _halfCloseReceived;
    private bool _cancelled;
    private int _disposed;
    private volatile Exception? _sendFailure; // set by SendBodyAsync on failure

    // Wake-coalescing (unary only): when set, an inline batch was opened
    // in SendRequestHeadersAsync. SendHalfCloseAsync's inline path closes
    // it so Headers+Message+HalfClose share a single OS-level data-signal.
    // Caller (ShmControlHandler) sets this hint only for unary requests
    // (known content length) where HalfClose is guaranteed to follow
    // Message in microseconds. For streaming the hint is false and the
    // per-frame wakes proceed normally to avoid starving the response.
    private int _pendingInlineBatch; // 0=closed, 1=open

    // Diagnostic counters for the wake-coalescing path. Static across
    // all streams so the bench can read them via reflection.
    private static long s_coalesceOpened;
    private static long s_coalesceClosed;
    internal static (long Opened, long Closed) GetCoalesceDiag()
        => (Volatile.Read(ref s_coalesceOpened), Volatile.Read(ref s_coalesceClosed));

    // Diagnostic counters (env-gated SHM_DIAG_HOPTIMING=1) for measuring
    // the client-side reader-thread → user-thread channel hop cost.
    // _hopPushTicks: set by reader thread just before/after Channel.TryWrite
    // _hopReceiveTicks: set by user thread just after Channel TryRead/wait returns
    // Diff aggregates "in-process hop" overhead — quantifies the upside of
    // a future inline-reader optimization that lets the user thread read
    // directly from RxRing, skipping the channel.
    private static readonly bool s_diagHopTiming =
        string.Equals(Environment.GetEnvironmentVariable("SHM_DIAG_HOPTIMING"),
            "1", StringComparison.Ordinal);
    private long _lastHopPushTicks;
    private static long s_hopTicksTotal;          // slow path: user awaited
    private static long s_hopCount;
    private static long s_hopFastTicksTotal;      // fast path: frame already queued
    private static long s_hopFastCount;
    internal static (long TicksTotal, long Count, long FastTicksTotal, long FastCount) GetHopDiag()
        => (Volatile.Read(ref s_hopTicksTotal), Volatile.Read(ref s_hopCount),
            Volatile.Read(ref s_hopFastTicksTotal), Volatile.Read(ref s_hopFastCount));

    // Env-gated SHM_CHANNEL_INLINE=1: lets the inbound Channel<InboundFrame>
    // run continuations synchronously on the reader thread (skipping
    // ThreadPool dispatch). Saves ~10-15µs/RT on Windows where the
    // ThreadPool worker wake is the dominant cost (default OFF preserves
    // current threading model). See repo/grpc-dotnet-shm-channel-hop-finding.
    // Risk: with N concurrent streams sharing one connection, the reader
    // thread is briefly serialized while user continuations run inline —
    // OK if continuations don't block (typical gRPC) but pathological if
    // they do. Recommend pairing with concurrent A/B bench before default-on.
    //
    // CRITICAL incompatibility: when strict-fair forces a small
    // SHM_FAIR_MAX_FRAME (e.g. 16384), large messages get split across
    // multiple H2 DATA frames. LazyChainRos then activates and pulls each
    // chunk synchronously via ReceiveFrameSync → if sync continuations is
    // also on, the user-code MergeFrom runs on the reader thread, blocks
    // on the next chunk, and the reader thread can never read that next
    // chunk → deadlock. We disable sync continuations when fair frame cap
    // is in effect so the multi-frame path stays correct. Same disable
    // happens when SHM_FAIR_STREAM_WINDOW is set (it also activates the
    // multi-frame path via the chunking threshold).
    private static readonly bool s_channelInlineContinuations =
        string.Equals(Environment.GetEnvironmentVariable("SHM_CHANNEL_INLINE"),
            "1", StringComparison.Ordinal)
        && ShmConstants.FairMaxFramePayload == int.MaxValue
        && ShmConstants.FairStreamWindow == int.MaxValue;


    /// <summary>
    /// Records a send-side failure so that response readers can surface
    /// the real root cause instead of treating truncated responses as EOF.
    /// </summary>
    internal void SetSendFailure(Exception ex) => _sendFailure = ex;

    /// <summary>Gets the send-side failure, if any.</summary>
    internal Exception? SendFailure => _sendFailure;

    /// <summary>
    /// Gets the stream ID.
    /// </summary>
    public uint StreamId { get; }

    /// <summary>Gets the owning connection (for direct ring write).</summary>
    internal ShmConnection Connection => _connection;

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
    public bool IsLocalHalfClosed => Volatile.Read(ref _halfCloseSent) != 0;

    /// <summary>Marks half-close as sent without actually sending it
    /// (used when HalfClose was written inline by the caller).</summary>
    internal void MarkHalfCloseSent() => Volatile.Write(ref _halfCloseSent, 1);

    /// <summary>
    /// Gets whether the stream was cancelled.
    /// </summary>
    public bool IsCancelled => _cancelled;

    /// <summary>
    /// Gets whether this is a server-initiated stream (i.e., server received request).
    /// </summary>
    public bool IsServerStream { get; }

    internal CancellationToken CancellationToken => _cancellationCts.Token;

    internal ShmGrpcStream(uint streamId, ShmConnection connection, bool isServerStream = false)
    {
        StreamId = streamId;
        _connection = connection;
        IsServerStream = isServerStream;
        _inboundFrames = Channel.CreateUnbounded<InboundFrame>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = s_channelInlineContinuations
        });
        _disposeCts = new CancellationTokenSource();
        _cancellationCts = new CancellationTokenSource();
        _sendLock = new SemaphoreSlim(1, 1);
        // Per-stream flow control is disabled in production: the ring's
        // WaitForSpace provides back-pressure via the SPSC ring buffer.
        // A separate per-stream window would add cross-process WindowUpdate
        // round trips, causing throughput to drop from 1.7 GB/s to 0.75 GB/s.
        //
        // gRFC SHM no-WU alignment (v3.4+ / grpc-go-shmem shmNoWU):
        // even when SHM_FAIR_STREAM_WINDOW is set, the per-stream window
        // is NOT enforced — FairAwaitWindow / FairAddWindow / AccruePeerCredit
        // are all no-ops. SHM_FAIR_STREAM_WINDOW only opts into the
        // FairMaxFramePayload chunking for apples-to-apples wire-format
        // comparison with TCP/HTTP2 (smaller H2 DATA frames). The
        // SHM ring's WaitForSpace backpressure is the sole flow control.
    }

    /// <summary>
    /// No-op stub retained for API stability and reflection-based tests.
    /// Per gRFC SHM no-WU alignment (v3.4+, mirrors grpc-go-shmem
    /// <c>shm_client_transport.go:acquireSendQuota</c> under shmNoWU):
    /// per-stream HTTP/2 send-window enforcement is disabled over shared
    /// memory; the ring's <c>WaitForSpace</c> backpressure in
    /// <see cref="ShmRing.ReserveWrite"/> is the sole flow control.
    /// Previously this blocked on a ManualResetEventSlim until peer
    /// WINDOW_UPDATE credit arrived; the bidirectional WU-credit dance
    /// produced an unfixable TOCTOU deadlock under chunked sends on
    /// fast Linux multi-core hosts.
    /// </summary>
    /// <param name="needed">Ignored. Was the byte count to debit.</param>
    /// <param name="drainBeforeWait">
    /// Ignored. Was a callback to flush owed peer-WUs before sleeping.
    /// </param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "No-op stub kept instance-method for API stability; see method doc.")]
    internal void FairAwaitWindow(int needed, Action? drainBeforeWait = null)
    {
        // No-op: SHM ring backpressure is the only flow control.
        _ = needed;
        _ = drainBeforeWait;
    }

    /// <summary>
    /// Strict-fair mode: previously credited <paramref name="delta"/>
    /// bytes back to the stream send-window in response to a peer
    /// WINDOW_UPDATE. Now a no-op in all modes (see <see cref="FairAwaitWindow"/>);
    /// incoming WU frames are accepted on the wire for spec compliance
    /// but their increment is discarded, mirroring grpc-go-shmem
    /// <c>shm_client_transport.go:addSendQuota</c> under shmNoWU.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "No-op stub kept instance-method for API stability; see method doc.")]
    internal void FairAddWindow(int delta)
    {
        // No-op. See FairAwaitWindow doc.
        _ = delta;
    }

    /// <summary>
    /// Strict-fair mode: previously accumulated incoming wire-byte credit
    /// per stream and returned a coalesced peer-WU amount once threshold
    /// reached. Now a no-op (always returns 0) so no WU frames are ever
    /// emitted, matching grpc-go-shmem shmNoWU. Receiver-side flow control
    /// is unnecessary because the SHM ring's <c>WaitForSpace</c> already
    /// blocks the sender when the ring fills.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "No-op stub kept instance-method for API stability; see method doc.")]
    internal uint AccruePeerCredit(int wireBytes)
    {
        _ = wireBytes;
        return 0;
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
    public Task SendRequestHeadersAsync(string method, string authority, Metadata? metadata = null, DateTime? deadline = null, bool coalesceWithHalfClose = false)
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

        // Single-stream-mode inline-write fast path. When the connection
        // negotiated single-stream mode and only one stream is active,
        // bypass the WriterLoop queue and write Headers directly to the
        // ring under TryPauseWriterLoop.
        //
        // This is critical for correctness, not just perf: client unary
        // sends Headers, then (fire-and-forget) writes the body Message
        // via WriteInlineDirectMultiFrame which is also a TryPauseWriterLoop
        // inline write. If Headers went through the queue while Message
        // went inline, the two write paths race against each other on
        // the SPSC ring and produce a "Headers not delivered before
        // Message" failure mode (~1/15 stress runs on Intel Linux).
        // Routing Headers through the same TryPause path serialises the
        // sends through `_inlineWriterActive` CAS; both writes go to the
        // ring in caller-thread order, no race.
        //
        // Falls back to the queued path when:
        //   * not in single-stream mode (multi-stream pipelining wants
        //     Headers in the WriterLoop's batch), or
        //   * TryPauseWriterLoop fails (WriterLoop busy or another inline
        //     writer holds the CAS); the queued path is correct (single
        //     writer = WriterLoop) and Just Slower.
        if (_connection.SingleStreamMode && _connection.ActiveStreamCount <= 1)
        {
            var writer = _connection.FrameWriter;
            if (writer != null && writer.TryPauseWriterLoop())
            {
                try
                {
                    if (coalesceWithHalfClose)
                    {
                        // Wake-coalescing (unary): suppress the per-frame
                        // SignalData for Headers; SendHalfCloseAsync will
                        // close the batch and fire a single SignalData
                        // covering Headers+Message+HalfClose. Only safe
                        // when caller guarantees HalfClose follows shortly
                        // (i.e., unary request with known body length).
                        writer.BeginInlineBatch();
                        Interlocked.Increment(ref s_coalesceOpened);
                        var batchOpened = true;
                        try
                        {
                            writer.WriteInlineFrame(FrameType.Headers, StreamId,
                                HeadersFlags.Initial, payload.AsSpan(0, payloadLength), default);
                            Volatile.Write(ref _pendingInlineBatch, 1);
                            batchOpened = false; // ownership transferred to HalfClose / Dispose
                        }
                        finally
                        {
                            if (batchOpened) writer.EndInlineBatch();
                        }
                    }
                    else
                    {
                        writer.WriteInlineFrame(FrameType.Headers, StreamId,
                            HeadersFlags.Initial, payload.AsSpan(0, payloadLength), default);
                    }
                    return Task.CompletedTask;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(payload);
                    writer.ResumeWriterLoop();
                }
            }
        }

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

    /// <summary>Sets the grpc-encoding for response compression.
    /// Will be automatically included in response headers.</summary>
    internal void SetResponseEncoding(string encoding)
    {
        _responseEncoding = encoding;
    }

    private Metadata? InjectResponseEncoding(Metadata? metadata)
    {
        if (_responseEncoding == null) return metadata;
        metadata ??= new Metadata();
        metadata.Add("grpc-encoding", _responseEncoding);
        return metadata;
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

        metadata = InjectResponseEncoding(metadata);

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
    /// Writes response headers directly to the ring via the given writer.
    /// Used by singleStreamMode inline write paths (TryPause/ExecuteInline).
    /// </summary>
    internal void SendResponseHeadersInline(ShmFrameWriter writer, Metadata? metadata = null)
    {
        if (_responseHeaders != null) return;
        metadata = InjectResponseEncoding(metadata);
        _responseHeaders = new HeadersV1 { Version = 1, HeaderType = 1, Metadata = ConvertMetadata(metadata) };
        var (payload, payloadLength) = _responseHeaders.Encode();
        try
        {
            writer.WriteInlineFrame(FrameType.Headers, StreamId, HeadersFlags.Initial,
                payload.AsSpan(0, payloadLength), default);
        }
        finally { ArrayPool<byte>.Shared.Return(payload); }
    }

    private bool HasSentInitialHeaders()
    {
        return IsClientStream ? _requestHeaders != null : _responseHeaders != null;
    }

    private void ThrowIfCannotSendMessage()
    {
        if (_cancelled)
            throw new InvalidOperationException("Cannot send after cancel");
        if (!HasSentInitialHeaders())
            throw new InvalidOperationException("Cannot send message before headers");
        if (Volatile.Read(ref _halfCloseSent) != 0)
            throw new InvalidOperationException("Cannot send after half-close");
    }

    private void ThrowIfCannotSendTrailers()
    {
        if (_cancelled)
            throw new InvalidOperationException("Cannot send after cancel");
        if (_trailers != null)
            throw new InvalidOperationException("Cannot send trailers after trailers");
    }

    /// <summary>
    /// Sends a message payload as raw bytes — does NOT wrap the gRPC
    /// 5-byte LPM (length-prefixed-message) header. Use this overload
    /// when the caller already owns the framed wire form (e.g. relaying
    /// a pre-framed payload). For typical gRPC use cases prefer the
    /// <see cref="SendMessageAsync(Google.Protobuf.IMessage, CancellationToken)"/>
    /// overload, which is zero-allocation by construction.
    /// </summary>
    public Task SendMessageAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfCannotSendMessage();

        // No per-stream flow control: the ring's WaitForSpace provides
        // back-pressure via the SPSC ring buffer.
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
                finally
                {
                    _sendLock.Release();
                }
                return Task.CompletedTask;
            }
            // Lock contended — fall through to normal SendFrameAsync
        }
        return SendFrameAsync(FrameType.Message, 0, data, ct);
    }

    /// <summary>
    /// Sends a message with an implicit half-close in one frame (EndStream flag).
    /// Eliminates the separate HalfClose frame, reducing a unary RPC from 3 to 2
    /// client-side frames and saving one ring write + signal round-trip.
    /// </summary>
    public Task SendMessageAndHalfCloseAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfCannotSendMessage();

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
                    Volatile.Write(ref _halfCloseSent, 1);
                    return Task.CompletedTask;
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
                Volatile.Write(ref _halfCloseSent, 1);
                return Task.CompletedTask;
            }
            return SendMessageAndHalfCloseCompleteAsync(task);
    }

    private async Task SendMessageAndHalfCloseCompleteAsync(Task sendTask)
    {
        await sendTask.ConfigureAwait(false);
        Volatile.Write(ref _halfCloseSent, 1);
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
            ThrowIfCannotSendMessage();
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(pooledBuffer);
            throw;
        }

        // No per-stream flow control: ring WaitForSpace provides back-pressure.
        var ct = cancellationToken.CanBeCanceled ? cancellationToken : _disposeCts.Token;
        return SendFrameZeroCopyAsync(FrameType.Message, 0, data, pooledBuffer, ct);
    }

    /// <summary>
    /// Sends a gRPC message: the protobuf body is serialized DIRECTLY
    /// into a pooled ring-sized buffer (no intermediate
    /// <c>ToByteArray()</c> allocation) and wrapped with the 5-byte
    /// gRPC LPM header inline (<c>[compFlag=0(1)][len(4 BE)][body]</c>).
    /// The pooled buffer is returned to <see cref="ArrayPool{T}"/> after
    /// the ring write completes.
    ///
    /// <para>This is the recommended path for hand-written
    /// server/client implementations that use <see cref="ShmGrpcStream"/>
    /// directly (e.g. via <c>ShmGrpcServer</c>). Kestrel-hosted gRPC
    /// services already get equivalent zero-allocation framing through
    /// <c>SerializationContext.GetBufferWriter()</c> + the SHM
    /// <c>PipeWriter</c> adapter; they should NOT call this method.</para>
    /// </summary>
    public Task SendMessageAsync(Google.Protobuf.IMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var size = message.CalculateSize();
        var totalLen = 5 + size;
        var buf = ArrayPool<byte>.Shared.Rent(totalLen);
        try
        {
            buf[0] = 0; // uncompressed (gRPC LPM compression flag)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                buf.AsSpan(1, 4), (uint)size);
            if (size > 0)
            {
                message.WriteTo(buf.AsSpan(5, size));
            }
            // SendMessageZeroCopyAsync owns `buf` and returns it to the pool
            // after the ring write completes. We must NOT return it here.
            return SendMessageZeroCopyAsync(buf.AsMemory(0, totalLen), buf, cancellationToken);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buf);
            throw;
        }
    }

    /// <summary>
    /// Strips the 5-byte gRPC LPM header from a wire-format MESSAGE body
    /// (<c>[compFlag(1)][len(4 BE)][body]</c>) and returns the body slice.
    /// Throws on malformed or compressed (compFlag != 0) blobs; callers
    /// that need to handle compression should peek the flag byte first.
    ///
    /// Counterpart to <see cref="SendMessageWithLpmZeroCopyAsync"/> for
    /// hand-written servers/clients reading raw frames out of
    /// <see cref="ReceiveMessagesAsync"/>.
    /// </summary>
    public static ReadOnlySpan<byte> UnwrapLpm(ReadOnlySpan<byte> framed)
    {
        if (framed.Length < 5)
            throw new ArgumentException($"LPM blob too short: {framed.Length} bytes", nameof(framed));
        if (framed[0] != 0)
            throw new InvalidDataException(
                $"Compressed gRPC LPM not supported (compFlag=0x{framed[0]:X2}); set grpc-encoding=identity or handle compression at the application layer.");
        var len = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(framed.Slice(1, 4));
        if (5 + len > framed.Length)
            throw new InvalidDataException(
                $"LPM declares {len} bytes but only {framed.Length - 5} are available.");
        return framed.Slice(5, len);
    }

    /// <summary>
    /// Writes trailers directly to the ring via the given (paused) writer.
    /// Caller MUST hold the WriterLoop pause. No queue, no async, no signal race.
    /// </summary>
    internal void SendTrailersInline(ShmFrameWriter writer, StatusCode statusCode, string? statusMessage = null, Metadata? metadata = null)
    {
        _trailers = new TrailersV1
        {
            Version = 1,
            GrpcStatusCode = statusCode,
            GrpcStatusMessage = statusMessage,
            Metadata = ConvertMetadata(metadata)
        };

        var (payload, payloadLength) = _trailers.Encode();
        try
        {
            writer.WriteInlineFrame(FrameType.Trailers, StreamId, TrailersFlags.EndStream,
                payload.AsSpan(0, payloadLength), default);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
        }
        Volatile.Write(ref _halfCloseSent, 1);
    }

    /// <summary>
    /// Sends trailers and closes the stream (server-side).
    /// </summary>
    public async Task SendTrailersAsync(StatusCode statusCode, string? statusMessage = null, Metadata? metadata = null)
    {
        ThrowIfDisposed();
        if (IsClientStream)
            throw new InvalidOperationException("Only server can send trailers");
        ThrowIfCannotSendTrailers();

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
        Volatile.Write(ref _halfCloseSent, 1);
    }

    /// <summary>
    /// Signals that no more messages will be sent from this side.
    /// </summary>
    public Task SendHalfCloseAsync()
    {
        ThrowIfDisposed();
        // Atomic: ensure only one HalfClose is ever sent.
        if (Interlocked.CompareExchange(ref _halfCloseSent, 1, 0) != 0)
            return Task.CompletedTask;

        // In singleStreamMode, write HalfClose inline to avoid queue overhead.
        // Always use TryPause here (never ExecuteInline) because:
        // 1. HalfClose is a zero-payload frame — ring write is ~100ns.
        // 2. TryPause spin is bounded: WriterLoop checks _paused every Phase 2
        //    iteration (~30ns), so pause completes within a few µs.
        // 3. ExecuteInline would allocate a lambda closure + two kernel signals,
        //    adding ~2-5µs overhead per unary call that dominates small payloads.
        if (_connection.SingleStreamMode && _connection.ActiveStreamCount <= 1)
        {
            var writer = _connection.FrameWriter;
            if (writer != null && writer.TryPauseWriterLoop())
            {
                try
                {
                    FrameProtocol.WriteHalfClose(_connection.TxRing, StreamId, default);
                    // Wake-coalescing close: if SendRequestHeadersAsync
                    // opened an inline batch (unary path), close it now
                    // so the single coalesced SignalData fires.
                    if (Interlocked.Exchange(ref _pendingInlineBatch, 0) == 1)
                    {
                        writer.EndInlineBatch();
                        Interlocked.Increment(ref s_coalesceClosed);
                    }
                }
                finally
                {
                    writer.ResumeWriterLoop();
                }
                return Task.CompletedTask;
            }
        }

        var task = SendFrameAsync(FrameType.HalfClose, 0, Array.Empty<byte>());
        if (task.IsCompletedSuccessfully)
        {
            return Task.CompletedTask;
        }
        return SendHalfCloseSlowAsync(task);
    }

    private async Task SendHalfCloseSlowAsync(Task sendTask)
    {
        await sendTask.ConfigureAwait(false);
        Volatile.Write(ref _halfCloseSent, 1);
    }

    /// <summary>
    /// Cancels the stream.
    /// </summary>
    public async Task CancelAsync()
    {
        ThrowIfDisposed();
        if (_cancelled) return;
        _cancelled = true;
        CancelCancellationToken();

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
            if (s_diagHopTiming)
            {
                var push = Volatile.Read(ref _lastHopPushTicks);
                if (push != 0)
                {
                    var hopTicks = System.Diagnostics.Stopwatch.GetTimestamp() - push;
                    if (hopTicks >= 0)
                    {
                        Interlocked.Add(ref s_hopFastTicksTotal, hopTicks);
                        Interlocked.Increment(ref s_hopFastCount);
                    }
                }
            }
            return Task.FromResult<InboundFrame?>(frame);
        }

        // Slow path: need to wait for a frame.
        return ReceiveFrameSlowAsync(cancellationToken);
    }

    /// <summary>
    /// Synchronous variant of <see cref="ReceiveFrameAsync"/>. Used by
    /// <see cref="LazyChainRos"/>'s pull callback inside protobuf's
    /// synchronous <c>MergeFrom(ros)</c> parse loop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns the next queued frame immediately if one is buffered.
    /// Otherwise blocks the calling thread on the inbound frames channel
    /// until a frame arrives, the stream is disposed, or
    /// <paramref name="cancellationToken"/> fires.
    /// </para>
    /// <para>
    /// Sync-over-async safety: the underlying <c>Channel&lt;InboundFrame&gt;</c>
    /// uses <c>ManualResetValueTaskSourceCore</c> internally with no
    /// SynchronizationContext capture; awaiting it via
    /// <c>GetAwaiter().GetResult()</c> blocks the calling thread on a
    /// kernel signal that the producer (the per-connection
    /// <c>FrameReaderLoopAsync</c> running on its own dedicated task)
    /// fires asynchronously. Cannot self-deadlock because consumer and
    /// producer are on different threads.
    /// </para>
    /// </remarks>
    public InboundFrame? ReceiveFrameSync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_inboundFrames.Reader.TryRead(out var frame))
        {
            if (s_diagHopTiming)
            {
                var push = Volatile.Read(ref _lastHopPushTicks);
                if (push != 0)
                {
                    var hopTicks = System.Diagnostics.Stopwatch.GetTimestamp() - push;
                    if (hopTicks >= 0)
                    {
                        Interlocked.Add(ref s_hopFastTicksTotal, hopTicks);
                        Interlocked.Increment(ref s_hopFastCount);
                    }
                }
            }
            return frame;
        }

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
            // ValueTask<bool>: if synchronously completed, read directly; else
            // block on the underlying Task.
            var waitTask = _inboundFrames.Reader.WaitToReadAsync(ct);
            bool hasMore = waitTask.IsCompleted
                ? waitTask.Result
                : waitTask.AsTask().GetAwaiter().GetResult();

            if (hasMore && _inboundFrames.Reader.TryRead(out frame))
            {
                if (s_diagHopTiming)
                {
                    var push = Volatile.Read(ref _lastHopPushTicks);
                    if (push != 0)
                    {
                        var hopTicks = System.Diagnostics.Stopwatch.GetTimestamp() - push;
                        if (hopTicks >= 0)
                        {
                            Interlocked.Add(ref s_hopTicksTotal, hopTicks);
                            Interlocked.Increment(ref s_hopCount);
                        }
                    }
                }
                return frame;
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
                    if (s_diagHopTiming)
                    {
                        var push = Volatile.Read(ref _lastHopPushTicks);
                        if (push != 0)
                        {
                            var hopTicks = System.Diagnostics.Stopwatch.GetTimestamp() - push;
                            if (hopTicks >= 0)
                            {
                                Interlocked.Add(ref s_hopTicksTotal, hopTicks);
                                Interlocked.Increment(ref s_hopCount);
                            }
                        }
                    }
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
        {
            var sendEx = _sendFailure;
            throw sendEx != null
                ? new InvalidOperationException("Request body send failed", sendEx)
                : new InvalidOperationException("Stream closed before receiving headers");
        }

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
            {
                var sendEx2 = _sendFailure;
                throw sendEx2 != null
                    ? new InvalidOperationException("Request body send failed", sendEx2)
                    : new InvalidOperationException("Stream closed before receiving headers");
            }

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
            if (frame == null)
            {
                // If the send side failed, surface it instead of silently
                // treating a truncated response as normal EOF.
                var sendEx = _sendFailure;
                if (sendEx != null)
                    throw new InvalidOperationException("Request body send failed during streaming", sendEx);
                yield break;
            }

            var f = frame.Value;
            switch (f.Type)
            {
                case FrameType.Message:
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
                var sendEx = _sendFailure;
                if (sendEx != null)
                    throw new InvalidOperationException("Request body send failed during streaming", sendEx);
                return (default, default, true);
            }

            var f = frame.Value;
            switch (f.Type)
            {
                case FrameType.Message:
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
                        // Last fragment of multi-fragment message.
                        // Strip the 5-byte gRPC LPM header so the consumer
                        // receives just the message body — same convention
                        // as ReceiveMessageBuffersAsync.
                        messageAccumulator.Write(f.Memory.Span);
                        f.ReturnToPool();
                        var assembled = messageAccumulator.ToArray();
                        messageAccumulator.SetLength(0);
                        var endStream1 = (f.Flags & MessageFlags.EndStream) != 0;
                        if (endStream1) _halfCloseReceived = true;
                        return (assembled.AsMemory(5), default, endStream1);
                    }

                    // Single-frame message: return zero-copy view sliced past
                    // the 5-byte LPM header. Caller must hold onto 'f' and
                    // pass it back as previousFrame on the next call so the
                    // pooled buffer can be released.
                    if ((f.Flags & MessageFlags.EndStream) != 0)
                    {
                        _halfCloseReceived = true;
                        return (f.Memory.Slice(5), f, true);
                    }
                    return (f.Memory.Slice(5), f, false);

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
                    var sendEx = _sendFailure;
                    if (sendEx != null)
                        throw new InvalidOperationException("Request body send failed during streaming", sendEx);
                    yield break;
                }

                var f = frame.Value;
                switch (f.Type)
                {
                    case FrameType.Message:
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
                            // Strip the 5-byte gRPC LPM header so the consumer
                            // receives just the message body.
                            previousFrame.ReturnToPool();
                            var assembled = messageAccumulator.ToArray();
                            yield return assembled.AsMemory(5);
                            messageAccumulator.SetLength(0);
                            if ((f.Flags & MessageFlags.EndStream) != 0) { _halfCloseReceived = true; yield break; }
                            break;
                        }

                        // Return the PREVIOUS payload now that the consumer
                        // has advanced past it.
                        previousFrame.ReturnToPool();

                        previousFrame = f;
                        // Strip the 5-byte gRPC LPM header — the H2 wire carries
                        // each MESSAGE as `[compFlag(1)][len(4)][body]`; the
                        // consumer (e.g. <see cref="ShmControlResponseContent"/>'s
                        // SerializeToStreamAsync) re-frames around the body.
                        yield return f.Memory.Slice(5);
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
        // Diag: stamp the moment we're about to push to the inbound
        // channel. ReceiveFrameSlowAsync / ReceiveFrameSync read this
        // back and accumulate the gap = "in-process reader→user hop".
        if (s_diagHopTiming)
        {
            Volatile.Write(ref _lastHopPushTicks, System.Diagnostics.Stopwatch.GetTimestamp());
        }
        var ownsFrame = true;
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || _cancelled)
            {
                frame.ReturnToPool();
                ownsFrame = false;
                return;
            }

            switch (frame.Type)
            {
                case FrameType.Cancel:
                    _cancelled = true;
                    CancelCancellationToken();
                    frame.ReturnToPool();
                    ownsFrame = false;
                    _inboundFrames.Writer.TryComplete();
                    _connection.RemoveStream(StreamId);
                    break;

                case FrameType.HalfClose:
                    _halfCloseReceived = true;
                    if (_inboundFrames.Writer.TryWrite(frame))
                    {
                        ownsFrame = false;
                    }
                    else
                    {
                        frame.ReturnToPool();
                        ownsFrame = false;
                    }
                    break;

                case FrameType.Trailers:
                    _halfCloseReceived = true;
                    if (_inboundFrames.Writer.TryWrite(frame))
                    {
                        ownsFrame = false;
                    }
                    else
                    {
                        frame.ReturnToPool();
                        ownsFrame = false;
                    }
                    _inboundFrames.Writer.TryComplete();
                    // Auto-remove from connection to prevent accumulation when
                    // callers don't dispose the stream (e.g., undisposed AsyncUnaryCall).
                    // No more frames will arrive after TRAILERS.
                    _connection.RemoveStream(StreamId);
                    break;

                default:
                    if (_inboundFrames.Writer.TryWrite(frame))
                    {
                        ownsFrame = false;
                    }
                    else
                    {
                        frame.ReturnToPool();
                        ownsFrame = false;
                    }
                    break;
            }
        }
        catch
        {
            if (ownsFrame)
            {
                frame.ReturnToPool();
            }
            throw;
        }
    }

    /// <summary>
    /// Tries to dequeue a frame from the inbound channel without waiting.
    /// Used by IDirectMessageReader sync fast path.
    /// </summary>
    internal bool TryReceiveFrame(out InboundFrame frame)
    {
        return _inboundFrames.Reader.TryRead(out frame);
    }

    /// <summary>Waits until a frame is available in the channel.</summary>
    internal ValueTask<bool> WaitForFrameAsync(CancellationToken cancellationToken)
    {
        return _inboundFrames.Reader.WaitToReadAsync(cancellationToken);
    }

    /// <summary>Exposes the dispose cancellation token for direct readers.</summary>
    internal CancellationToken DisposeCancellationToken => _disposeCts.Token;

    /// <summary>
    /// Completes the inbound frame channel so that any pending
    /// ReceiveFrameAsync/WaitToReadAsync returns immediately.
    /// Safe to call even if already completed or disposed.
    /// </summary>
    internal void CompleteInbound()
    {
        _inboundFrames.Writer.TryComplete();
    }

    /// <summary>Marks half-close as received from the remote side.</summary>
    internal void MarkHalfCloseReceived()
    {
        _halfCloseReceived = true;
    }

    /// <summary>Sets trailers from a Trailers frame.</summary>
    internal void SetTrailers(InboundFrame frame)
    {
        _trailers = TrailersV1.Decode(frame.Memory.Span);
    }

    internal static void OnWindowUpdate(uint increment)
    {
        // No-op: per-stream flow control is disabled.
        // Ring WaitForSpace provides back-pressure.
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

    private void CancelCancellationToken()
    {
        try
        {
            _cancellationCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Safety: if a wake-coalescing batch was opened in
        // SendRequestHeadersAsync but never closed (because the request
        // was cancelled before HalfClose ran), close it now so the
        // ring's _batchWriteDepth doesn't leak.
        if (Interlocked.Exchange(ref _pendingInlineBatch, 0) == 1)
        {
            try { _connection.FrameWriter?.EndInlineBatch(); }
            catch { /* best effort */ }
        }

        _disposeCts.Cancel();
        CancelCancellationToken();
        _inboundFrames.Writer.TryComplete();

        // Drain any remaining queued frames to return pooled buffers.
        while (_inboundFrames.Reader.TryRead(out var frame))
        {
            frame.ReturnToPool();
        }

        _connection.RemoveStream(StreamId);
        _sendLock.Dispose();
        _cancellationCts.Dispose();
        _disposeCts.Dispose();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
