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

            // Send request body if present
            if (request.Content != null)
            {
                var bodyStream = await request.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await SendMessagesAsync(stream, bodyStream, cancellationToken).ConfigureAwait(false);
            }

            // Signal end of request
            await stream.SendHalfCloseAsync().ConfigureAwait(false);

            // Wait for response headers
            var responseHeaders = await stream.ReceiveResponseHeadersAsync(cancellationToken).ConfigureAwait(false);

            // Create response with streaming content
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ShmControlResponseContent(stream),
                Version = new Version(2, 0)
            };

            // Add response headers
            if (responseHeaders.Metadata != null)
            {
                foreach (var kv in responseHeaders.Metadata)
                {
                    var values = kv.Values.Select(v => v is byte[] bytes
                        ? Convert.ToBase64String(bytes)
                        : v?.ToString() ?? "");
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

    private static async Task WriteControlFrameAsync(ShmRing ring, FrameType type, byte[] payload, CancellationToken ct)
    {
        var header = new FrameHeader
        {
            Length = (uint)payload.Length,
            StreamId = 0,
            Type = type,
            Flags = 0
        };

        var headerBytes = header.ToBytes();
        var totalLength = headerBytes.Length + payload.Length;

        // Wait for space
        while (!ring.CanWrite(totalLength))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1, ct).ConfigureAwait(false);
        }

        // Write header and payload
        ring.Write(headerBytes, ct);
        if (payload.Length > 0)
        {
            ring.Write(payload, ct);
        }
    }

    private static async Task<(FrameHeader header, Memory<byte> payload)> ReadControlFrameAsync(ShmRing ring, CancellationToken ct)
    {
        // Wait for header data
        while (!ring.TryPeek(ShmConstants.FrameHeaderSize, out _))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1, ct).ConfigureAwait(false);
        }

        // Read frame header
        var headerBuffer = new byte[ShmConstants.FrameHeaderSize];
        if (!ring.TryRead(headerBuffer))
        {
            throw new InvalidOperationException("Failed to read frame header");
        }

        var header = FrameHeader.Parse(headerBuffer);

        // Read payload if any
        Memory<byte> payload = Memory<byte>.Empty;
        if (header.Length > 0)
        {
            while (!ring.TryPeek((int)header.Length, out _))
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(1, ct).ConfigureAwait(false);
            }

            var payloadBuffer = new byte[header.Length];
            if (!ring.TryRead(payloadBuffer))
            {
                throw new InvalidOperationException("Failed to read frame payload");
            }
            payload = payloadBuffer;
        }

        return (header, payload);
    }

    private static async Task SendMessagesAsync(ShmGrpcStream stream, Stream bodyStream, CancellationToken cancellationToken)
    {
        // gRPC message format: [compressed:1][length:4][data:length]
        var headerBuffer = new byte[5];

        while (true)
        {
            // Read message header
            var headerBytesRead = await ReadExactlyAsync(bodyStream, headerBuffer, cancellationToken).ConfigureAwait(false);
            if (headerBytesRead == 0) break; // End of stream
            if (headerBytesRead < 5) throw new InvalidDataException("Incomplete gRPC message header");

            var compressed = headerBuffer[0] != 0;
            var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(headerBuffer.AsSpan(1));

            if (compressed)
            {
                throw new NotSupportedException("Compression not yet supported");
            }

            // Read message body
            var messageBuffer = new byte[length];
            var messageBytesRead = await ReadExactlyAsync(bodyStream, messageBuffer, cancellationToken).ConfigureAwait(false);
            if (messageBytesRead < length) throw new InvalidDataException("Incomplete gRPC message body");

            await stream.SendMessageAsync(messageBuffer, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<int> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead), cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0) return totalRead;
            totalRead += bytesRead;
        }
        return totalRead;
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
/// HttpContent implementation that reads response messages from a ShmGrpcStream.
/// </summary>
internal sealed class ShmControlResponseContent : HttpContent
{
    private readonly ShmGrpcStream _stream;

    public ShmControlResponseContent(ShmGrpcStream stream)
    {
        _stream = stream;
        Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        // Write gRPC-format messages to the output stream
        await foreach (var message in _stream.ReceiveMessagesAsync(cancellationToken))
        {
            // gRPC message format: [compressed:1][length:4][data]
            var header = new byte[5];
            header[0] = 0; // Not compressed
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1), (uint)message.Length);

            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false; // Streaming content, length unknown
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
