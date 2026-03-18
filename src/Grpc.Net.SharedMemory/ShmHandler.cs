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
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using Grpc.Core;
using Grpc.Net.SharedMemory.Compression;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// An HttpMessageHandler that routes gRPC requests over shared memory.
/// Use with GrpcChannel.ForAddress() by setting GrpcChannelOptions.HttpHandler.
/// </summary>
/// <example>
/// <code>
/// var handler = new ShmHandler("my_grpc_segment");
/// var channel = GrpcChannel.ForAddress("shm://localhost", new GrpcChannelOptions
/// {
///     HttpHandler = handler
/// });
/// var client = new Greeter.GreeterClient(channel);
/// </code>
/// </example>
[SupportedOSPlatform("windows")]
public sealed class ShmHandler : HttpMessageHandler
{
    private readonly string _segmentName;
    private readonly ShmConnection _connection;
    private readonly SemaphoreSlim _connectionLock;
    private readonly ShmRetryPolicy? _retryPolicy;
    private readonly ShmRetryThrottling? _retryThrottling;
    private bool _disposed;

    /// <summary>
    /// Creates a new ShmHandler that connects to the specified shared memory segment.
    /// </summary>
    /// <param name="segmentName">The name of the shared memory segment to connect to.</param>
    /// <param name="compressionOptions">Optional compression options.</param>
    /// <param name="retryPolicy">Optional retry policy for transient failures.</param>
    /// <param name="retryThrottling">Optional retry throttling to prevent overwhelming the server.</param>
    public ShmHandler(string segmentName, ShmCompressionOptions? compressionOptions = null,
        ShmRetryPolicy? retryPolicy = null, ShmRetryThrottling? retryThrottling = null)
    {
        _segmentName = segmentName ?? throw new ArgumentNullException(nameof(segmentName));
        _connection = ShmConnection.ConnectAsClient(segmentName);
        _connectionLock = new SemaphoreSlim(1, 1);
        _retryPolicy = retryPolicy;
        _retryThrottling = retryThrottling;
    }

    /// <summary>
    /// Gets the segment name this handler is connected to.
    /// </summary>
    public string SegmentName => _segmentName;

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Without retry policy, execute single attempt directly
        if (_retryPolicy == null)
        {
            return await SendSingleAttemptAsync(request, null, cancellationToken);
        }

        // Buffer request content for potential retry replay
        byte[]? requestBody = null;
        if (request.Content != null)
        {
            requestBody = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        }

        var retryState = new ShmRetryState(_retryPolicy, _retryThrottling);

        while (true)
        {
            retryState.IncrementAttempt();
            var response = await SendSingleAttemptAsync(request, requestBody, cancellationToken);

            if (retryState.IsCommitted)
            {
                return response;
            }

            // For retry to work, we must read the full response to get trailers.
            // Buffer the content so we can either return it or discard it on retry.
            var contentBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            // Check grpc-status from trailing headers (populated after reading content)
            if (response.TrailingHeaders.TryGetValues("grpc-status", out var statusValues))
            {
                var statusStr = statusValues.FirstOrDefault();
                if (int.TryParse(statusStr, out var statusInt))
                {
                    var grpcStatus = (Grpc.Core.StatusCode)statusInt;
                    if (grpcStatus == Grpc.Core.StatusCode.OK)
                    {
                        retryState.RecordSuccess();
                        // Re-wrap the already-read content
                        response.Content = new ByteArrayContent(contentBytes);
                        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
                        return response;
                    }

                    if (retryState.ShouldRetry(grpcStatus, out var backoff))
                    {
                        response.Dispose();
                        await Task.Delay(backoff, cancellationToken);
                        continue;
                    }
                }
            }

            // Cannot retry — return response with buffered content
            response.Content = new ByteArrayContent(contentBytes);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
            return response;
        }
    }

    private async Task<HttpResponseMessage> SendSingleAttemptAsync(
        HttpRequestMessage request, byte[]? requestBody, CancellationToken cancellationToken)
    {
        // Create a new stream for this request
        var stream = _connection.CreateStream();

        try
        {
            // Extract gRPC metadata
            var method = request.RequestUri?.AbsolutePath ?? "/";
            var authority = request.RequestUri?.Authority ?? "localhost";
            var metadata = ExtractMetadata(request.Headers);
            var deadline = ExtractDeadline(request.Headers);

            // Send request headers
            await stream.SendRequestHeadersAsync(method, authority, metadata, deadline);

            // Send request body if present
            if (requestBody != null)
            {
                await SendMessagesAsync(stream, requestBody, cancellationToken);
            }
            else if (request.Content != null)
            {
                var bodyStream = await request.Content.ReadAsStreamAsync(cancellationToken);
                await SendMessagesAsync(stream, bodyStream, cancellationToken);
            }

            // Signal end of request
            await stream.SendHalfCloseAsync();

            // Wait for response headers
            var responseHeaders = await stream.ReceiveResponseHeadersAsync(cancellationToken);

            // Create response with streaming content
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ShmResponseContent(stream, null!),
                Version = new Version(2, 0)
            };
            ((ShmResponseContent)response.Content).SetTrailingHeaders(response.TrailingHeaders);

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
            await stream.CancelAsync();
            throw;
        }
    }

    private static async Task SendMessagesAsync(ShmGrpcStream stream, Stream bodyStream, CancellationToken cancellationToken)
    {
        // gRPC message format: [compressed:1][length:4][data:length]
        var headerBuffer = new byte[5];

        while (true)
        {
            // Read message header
            var headerBytesRead = await ReadExactlyAsync(bodyStream, headerBuffer, cancellationToken);
            if (headerBytesRead == 0) break; // End of stream
            if (headerBytesRead < 5) throw new InvalidDataException("Incomplete gRPC message header");

            var compressed = headerBuffer[0] != 0;
            var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(headerBuffer.AsSpan(1));

            // Note: 'compressed' flag here is from the upstream gRPC channel's HTTP-level compression.
            // SHM transport handles its own compression in ShmGrpcStream.SendMessageAsync.
            // For now, pass data through as-is; if the upstream already compressed it and
            // the SHM stream also compresses, the data will be double-compressed which is
            // suboptimal but not incorrect. Typically SHM compression replaces HTTP-level compression.

            // Read message body into a pooled buffer to avoid per-message heap allocation
            var messageBuffer = ArrayPool<byte>.Shared.Rent((int)length);
            try
            {
                var messageBytesRead = await ReadExactlyAsync(bodyStream, messageBuffer.AsMemory(0, (int)length), cancellationToken);
                if (messageBytesRead < length)
                    throw new InvalidDataException("Incomplete gRPC message body");
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(messageBuffer);
                throw;
            }

            // Zero-copy: ownership of messageBuffer transfers to the writer thread
            // which returns it to the pool after the ring write completes.
            await stream.SendMessageZeroCopyAsync(
                messageBuffer.AsMemory(0, (int)length),
                messageBuffer,
                cancellationToken);
        }
    }

    private static async Task SendMessagesAsync(ShmGrpcStream stream, byte[] bodyBuffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < bodyBuffer.Length)
        {
            var remaining = bodyBuffer.Length - offset;
            if (remaining < 5)
            {
                throw new InvalidDataException("Incomplete gRPC message header");
            }

            var header = bodyBuffer.AsSpan(offset, 5);
            var compressed = header[0] != 0;
            var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[1..5]);
            offset += 5;

            // Preserve existing behavior: pass payload bytes through as-is even
            // if the gRPC frame marks them as compressed.
            _ = compressed;

            if ((long)offset + length > bodyBuffer.Length)
            {
                throw new InvalidDataException("Incomplete gRPC message body");
            }

            await stream.SendMessageAsync(bodyBuffer.AsMemory(offset, (int)length), cancellationToken);
            offset += (int)length;
        }
    }

    private static async Task<int> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        return await ReadExactlyAsync(stream, buffer.AsMemory(), cancellationToken);
    }

    private static async Task<int> ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer.Slice(totalRead), cancellationToken);
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

        try
        {
            duration = unit switch
            {
                'H' => TimeSpan.FromHours(value),
                'M' => TimeSpan.FromMinutes(value),
                'S' => TimeSpan.FromSeconds(value),
                'm' => TimeSpan.FromMilliseconds(value),
                'u' => TimeSpan.FromMicroseconds(value),
                'n' => TimeSpan.FromTicks(value / 100),
                _ => TimeSpan.Zero
            };
        }
        catch (OverflowException)
        {
            return false;
        }

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
                _connection.Dispose();
                _connectionLock.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// HttpContent implementation that reads response messages from a ShmGrpcStream.
/// </summary>
internal sealed class ShmResponseContent : HttpContent
{
    private readonly ShmGrpcStream _stream;
    private HttpHeaders? _trailingHeaders;

    public ShmResponseContent(ShmGrpcStream stream, HttpHeaders? trailingHeaders)
    {
        _stream = stream;
        _trailingHeaders = trailingHeaders;
        Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
    }

    internal void SetTrailingHeaders(HttpHeaders trailingHeaders)
    {
        _trailingHeaders = trailingHeaders;
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        await SerializeToStreamAsync(stream, context, CancellationToken.None);
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        var header = new byte[5];
        header[0] = 0;

        // Write gRPC-format messages to the output stream
        await foreach (var message in _stream.ReceiveMessageBuffersAsync(cancellationToken))
        {
            // gRPC message format: [compressed:1][length:4][data]
            // Not compressed (SHM-level compression is transparent)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1), (uint)message.Length);

            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(message, cancellationToken);
        }

        // Surface gRPC trailers as HTTP trailing headers.
        // This is critical for grpc-dotnet's retry/hedging to inspect grpc-status.
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
                    var values = kv.Values.Select(v => v is byte[] bytes
                        ? Convert.ToBase64String(bytes)
                        : v?.ToString() ?? "");
                    _trailingHeaders.TryAddWithoutValidation(kv.Key, values);
                }
            }
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
