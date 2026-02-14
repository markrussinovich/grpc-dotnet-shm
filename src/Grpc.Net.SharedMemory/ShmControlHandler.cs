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

using System.Net;
using System.Net.Http.Headers;
using Grpc.Core;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// An HttpMessageHandler that routes gRPC requests over shared memory
/// using the grpc-go-shmem compatible control segment protocol.
/// Use with GrpcChannel.ForAddress() by setting GrpcChannelOptions.HttpHandler.
/// </summary>
/// <example>
/// <code>
/// var handler = new ShmControlHandler("my_grpc_segment");
/// var channel = GrpcChannel.ForAddress("shm://localhost", new GrpcChannelOptions
/// {
///     HttpHandler = handler
/// });
/// var client = new Greeter.GreeterClient(channel);
/// </code>
/// </example>
public sealed class ShmControlHandler : HttpMessageHandler
{
    private readonly string _baseName;
    private ShmConnection? _connection;
    private readonly SemaphoreSlim _connectionLock;
    private readonly TimeSpan _connectTimeout;
    private bool _disposed;

    /// <summary>
    /// Creates a new ShmControlHandler that connects to the specified shared memory segment
    /// using the grpc-go-shmem control segment protocol.
    /// </summary>
    /// <param name="baseName">The base name of the shared memory segment (without _ctl suffix).</param>
    /// <param name="connectTimeout">Timeout for connection establishment (default: 30s).</param>
    public ShmControlHandler(string baseName, TimeSpan? connectTimeout = null)
    {
        _baseName = baseName ?? throw new ArgumentNullException(nameof(baseName));
        _connectionLock = new SemaphoreSlim(1, 1);
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Gets the base segment name this handler connects to.
    /// </summary>
    public string BaseName => _baseName;

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Ensure we have a connection
        var connection = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        // Create a new stream for this request
        var stream = connection.CreateStream();

        try
        {
            // Extract gRPC metadata
            var method = request.RequestUri?.AbsolutePath ?? "/";
            var authority = request.RequestUri?.Authority ?? "localhost";
            var metadata = ExtractMetadata(request.Headers);
            var deadline = ExtractDeadline(request.Headers);

            // Send request headers
            await stream.SendRequestHeadersAsync(method, authority, metadata, deadline).ConfigureAwait(false);

            // Send request body via a write-through stream that forwards each
            // gRPC frame directly to SHM.  CopyToAsync must run in the background
            // because PushStreamContent.SerializeToStreamAsync awaits CompleteTcs
            // for streaming calls (it blocks until the client closes the request
            // stream).  A single Task.Run + direct writes replaces the previous
            // Pipe + 2×Task.Run approach, eliminating resource accumulation.
            if (request.Content != null)
            {
                var writeStream = new ShmGrpcRequestStream(stream);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await request.Content.CopyToAsync(writeStream, cancellationToken).ConfigureAwait(false);
                        await stream.SendHalfCloseAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { /* Normal cancellation */ }
                    catch (ObjectDisposedException) { /* Call disposed */ }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"ShmControlHandler body send error: {ex.Message}");
                    }
                }, cancellationToken);
            }
            else
            {
                await stream.SendHalfCloseAsync().ConfigureAwait(false);
            }

            // Wait for response headers (server sends these before processing messages)
            var responseHeaders = await stream.ReceiveResponseHeadersAsync(cancellationToken).ConfigureAwait(false);

            // Create response with streaming content
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ShmControlResponseContent(stream),
                Version = new Version(2, 0)
            };
            ((ShmControlResponseContent)response.Content).SetTrailingHeaders(response.TrailingHeaders);

            // Add response headers
            if (responseHeaders.Metadata != null)
            {
                foreach (var kv in responseHeaders.Metadata)
                {
                    var values = kv.Key.EndsWith("-bin", StringComparison.OrdinalIgnoreCase)
                        ? kv.Values.Select(Convert.ToBase64String)
                        : kv.Values.Select(v => System.Text.Encoding.UTF8.GetString(v));
                    response.Headers.TryAddWithoutValidation(kv.Key, values);
                }
            }

            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await stream.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<ShmConnection> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_connection != null && !_connection.IsClosed)
        {
            return _connection;
        }

        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection != null && !_connection.IsClosed)
            {
                return _connection;
            }

            // Close any existing broken connection
            _connection?.Dispose();

            // Connect via control segment protocol
            _connection = await ConnectViaControlSegmentAsync(cancellationToken).ConfigureAwait(false);
            return _connection;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task<ShmConnection> ConnectViaControlSegmentAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(_connectTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var ct = linkedCts.Token;

        // Open the control segment
        var ctlName = _baseName + ShmConstants.ControlSegmentSuffix;
        Segment ctlSegment;
        try
        {
            ctlSegment = Segment.Open(ctlName);
        }
        catch (FileNotFoundException)
        {
            throw new InvalidOperationException($"Server not listening on segment '{_baseName}'. Control segment '{ctlName}' not found.");
        }

        try
        {
            // Wait for server to be ready
            await ctlSegment.WaitForServerAsync(ct).ConfigureAwait(false);

            // Control rings: Ring A is client→server (we write), Ring B is server→client (we read)
            var ctlTx = ctlSegment.RingA;
            var ctlRx = ctlSegment.RingB;

            // Send CONNECT request
            await WriteControlFrameAsync(ctlTx, FrameType.Connect, ControlWire.EncodeConnectRequest(), ct).ConfigureAwait(false);

            // Read response
            var (responseHeader, responsePayload) = await ReadControlFrameAsync(ctlRx, ct).ConfigureAwait(false);

            switch (responseHeader.Type)
            {
                case FrameType.Accept:
                    var dataSegmentName = ControlWire.DecodeConnectResponse(responsePayload.Span);

                    // Open the data segment
                    var dataSegment = Segment.Open(dataSegmentName);
                    await dataSegment.WaitForServerAsync(ct).ConfigureAwait(false);

                    // Signal that client has mapped the segment
                    dataSegment.SetClientReady(true);

                    // Create and return the connection
                    return ShmConnection.FromClientSegment(dataSegmentName, dataSegment);

                case FrameType.Reject:
                    var message = ControlWire.DecodeConnectReject(responsePayload.Span);
                    throw new InvalidOperationException($"Connection rejected by server: {message}");

                default:
                    throw new InvalidOperationException($"Unexpected response frame type: {responseHeader.Type}");
            }
        }
        finally
        {
            ctlSegment.Dispose();
        }
    }

    private static Task WriteControlFrameAsync(ShmRing ring, FrameType type, byte[] payload, CancellationToken ct)
    {
        var header = new FrameHeader
        {
            Length = (uint)payload.Length,
            StreamId = 0,
            Type = type,
            Flags = 0
        };

        var headerBytes = header.ToBytes();
        // Write header and payload (ring.Write blocks until space is available)
        ring.Write(headerBytes, ct);
        if (payload.Length > 0)
        {
            ring.Write(payload, ct);
        }

        return Task.CompletedTask;
    }

    private static Task<(FrameHeader header, Memory<byte> payload)> ReadControlFrameAsync(ShmRing ring, CancellationToken ct)
    {
        // Read frame header
        var headerBuffer = new byte[ShmConstants.FrameHeaderSize];
        ReadExact(ring, headerBuffer, ct);

        var header = FrameHeader.Parse(headerBuffer);

        // Read payload if any
        Memory<byte> payload = Memory<byte>.Empty;
        if (header.Length > 0)
        {
            var payloadBuffer = new byte[header.Length];
            ReadExact(ring, payloadBuffer, ct);
            payload = payloadBuffer;
        }

        return Task.FromResult((header, payload));
    }

    private static void ReadExact(ShmRing ring, Span<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            read += ring.Read(buffer[read..], ct);
        }
    }

    private static Metadata? ExtractMetadata(HttpRequestHeaders headers)
    {
        var metadata = new Metadata();

        foreach (var header in headers)
        {
            // Skip pseudo-headers and standard HTTP headers
            if (header.Key.StartsWith(':') ||
                header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in header.Value)
            {
                if (header.Key.EndsWith("-bin", StringComparison.OrdinalIgnoreCase))
                {
                    // Binary metadata
                    metadata.Add(new Metadata.Entry(header.Key, Convert.FromBase64String(value)));
                }
                else
                {
                    metadata.Add(new Metadata.Entry(header.Key, value));
                }
            }
        }

        return metadata.Count > 0 ? metadata : null;
    }

    private static DateTime? ExtractDeadline(HttpRequestHeaders headers)
    {
        if (headers.TryGetValues("grpc-timeout", out var values))
        {
            var timeout = values.FirstOrDefault();
            if (!string.IsNullOrEmpty(timeout))
            {
                // Parse timeout format: <value><unit> where unit is H/M/S/m/u/n
                if (TryParseGrpcTimeout(timeout, out var duration))
                {
                    return DateTime.UtcNow + duration;
                }
            }
        }
        return null;
    }

    private static bool TryParseGrpcTimeout(string timeout, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrEmpty(timeout) || timeout.Length < 2)
            return false;

        var unit = timeout[^1];
        if (!long.TryParse(timeout[..^1], out var value))
            return false;

        duration = unit switch
        {
            'H' => TimeSpan.FromHours(value),
            'M' => TimeSpan.FromMinutes(value),
            'S' => TimeSpan.FromSeconds(value),
            'm' => TimeSpan.FromMilliseconds(value),
            'u' => TimeSpan.FromMicroseconds(value),
            'n' => TimeSpan.FromTicks(value / 100), // nanoseconds
            _ => TimeSpan.Zero
        };

        return duration > TimeSpan.Zero;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                _connection?.Dispose();
                _connectionLock.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Write-through stream that forwards each gRPC-framed message written by
/// grpc-dotnet directly to ShmGrpcStream.SendMessageAsync — no intermediate
/// Pipe or buffer copy.  grpc-dotnet always writes each gRPC message as a
/// single WriteAsync call: [compressed:1][length:4][data].
/// </summary>
internal sealed class ShmGrpcRequestStream : Stream
{
    private readonly ShmGrpcStream _shmStream;

    public ShmGrpcRequestStream(ShmGrpcStream shmStream)
    {
        _shmStream = shmStream;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length < 5) return; // ignore empty/partial writes

        // Parse gRPC frame header
        var span = buffer.Span;
        var compressed = span[0] != 0;
        if (compressed) throw new NotSupportedException("Compression not yet supported");

        var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(span.Slice(1));

        // Pass message body directly as a memory slice — no copy.
        // The previous .ToArray() allocated a new byte[] (LOH at ≥85KB) on every send.
        await _shmStream.SendMessageAsync(buffer.Slice(5, length), cancellationToken).ConfigureAwait(false);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Use WriteAsync.");

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// HttpContent implementation that reads response messages from a ShmGrpcStream.
/// grpc-dotnet calls ReadAsStreamAsync (→ CreateContentReadStreamAsync) to get a
/// stream it can incrementally read gRPC-framed messages from.  We return a
/// lightweight wrapper that reads from ShmGrpcStream.ReceiveMessageBuffersAsync()
/// directly on the caller's thread — no Pipe, no Task.Run, no resource
/// accumulation across thousands of calls.
/// </summary>
internal sealed class ShmControlResponseContent : HttpContent
{
    private readonly ShmGrpcStream _stream;
    private HttpHeaders? _trailingHeaders;

    public ShmControlResponseContent(ShmGrpcStream stream, Task? bodySendTask = null)
    {
        _stream = stream;
        Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
    }

    internal void SetTrailingHeaders(HttpHeaders trailingHeaders)
    {
        _trailingHeaders = trailingHeaders;
    }

    protected override Task<Stream> CreateContentReadStreamAsync()
    {
        return Task.FromResult<Stream>(new ShmGrpcResponseStream(_stream, this));
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        var header = new byte[5];
        header[0] = 0;

        await foreach (var message in _stream.ReceiveMessageBuffersAsync(cancellationToken))
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1), (uint)message.Length);
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }

        ApplyTrailers();
    }

    internal void ApplyTrailers()
    {
        if (_stream.Trailers != null && _trailingHeaders != null)
        {
            var trailers = _stream.Trailers;
            _trailingHeaders.TryAddWithoutValidation("grpc-status", ((int)trailers.GrpcStatusCode).ToString());
            if (!string.IsNullOrEmpty(trailers.GrpcStatusMessage))
            {
                _trailingHeaders.TryAddWithoutValidation("grpc-message", Uri.EscapeDataString(trailers.GrpcStatusMessage));
            }
            if (trailers.Metadata != null)
            {
                foreach (var kv in trailers.Metadata)
                {
                    var values = kv.Key.EndsWith("-bin", StringComparison.OrdinalIgnoreCase)
                        ? kv.Values.Select(Convert.ToBase64String)
                        : kv.Values.Select(v => System.Text.Encoding.UTF8.GetString(v));
                    _trailingHeaders.TryAddWithoutValidation(kv.Key, values);
                }
            }
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stream.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// A read-only stream that yields gRPC-framed messages from a ShmGrpcStream
/// on the caller's thread (no background pump, no Pipe).  Each ReadAsync call
/// writes the gRPC 5-byte header + message data directly into the caller's
/// buffer — zero intermediate allocations on the hot path.
///
/// Previous implementation allocated <c>new byte[5 + message.Length]</c> per
/// message, causing LOH allocations (and Gen2 GC pressure) at ≥85 KB payloads.
/// </summary>
internal sealed class ShmGrpcResponseStream : Stream
{
    private readonly IAsyncEnumerator<ReadOnlyMemory<byte>> _enumerator;
    private readonly ShmControlResponseContent _content;
    // Current message being served (raw payload from SHM ring, pooled buffer).
    private ReadOnlyMemory<byte> _message;
    private int _messageLength;
    // How many bytes of the *logical* gRPC frame (5-byte header + message) have been served.
    private int _frameOffset;
    private bool _hasMessage;
    private bool _completed;

    public ShmGrpcResponseStream(ShmGrpcStream shmStream, ShmControlResponseContent content)
    {
        _enumerator = shmStream.ReceiveMessageBuffersAsync(CancellationToken.None).GetAsyncEnumerator();
        _content = content;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;
        if (_completed) return 0;

        // If we're mid-message, continue serving it.
        if (_hasMessage && _frameOffset < 5 + _messageLength)
        {
            return ServeCurrentMessage(buffer.Span);
        }

        // Advance to the next message (previous pooled buffer is returned by the enumerator).
        if (!await _enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            _completed = true;
            _content.ApplyTrailers();
            return 0;
        }

        _message = _enumerator.Current;
        _messageLength = _message.Length;
        _frameOffset = 0;
        _hasMessage = true;

        return ServeCurrentMessage(buffer.Span);
    }

    /// <summary>
    /// Writes portions of the logical gRPC frame [compressed:1][length:4][data]
    /// directly into <paramref name="dest"/> without any intermediate allocation.
    /// </summary>
    private int ServeCurrentMessage(Span<byte> dest)
    {
        var totalFrameLen = 5 + _messageLength;
        int written = 0;

        // --- Serve the 5-byte gRPC header ---
        if (_frameOffset < 5)
        {
            Span<byte> hdr = stackalloc byte[5];
            hdr[0] = 0; // not compressed
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(hdr.Slice(1), (uint)_messageLength);

            int hdrStart = _frameOffset;
            int hdrRemaining = 5 - hdrStart;
            int hdrToCopy = Math.Min(hdrRemaining, dest.Length);
            hdr.Slice(hdrStart, hdrToCopy).CopyTo(dest);
            written += hdrToCopy;
            _frameOffset += hdrToCopy;

            if (written >= dest.Length)
                return written;
        }

        // --- Serve message data ---
        if (_frameOffset >= 5 && _frameOffset < totalFrameLen)
        {
            int msgStart = _frameOffset - 5;
            int msgRemaining = _messageLength - msgStart;
            int toCopy = Math.Min(msgRemaining, dest.Length - written);
            _message.Span.Slice(msgStart, toCopy).CopyTo(dest.Slice(written));
            written += toCopy;
            _frameOffset += toCopy;
        }

        return written;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Use ReadAsync.");

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }
}
