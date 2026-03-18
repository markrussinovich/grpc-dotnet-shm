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
using Grpc.Net.SharedMemory.LoadBalancing;
using Grpc.Net.SharedMemory.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that tries shared memory transport first,
/// and falls back to a TCP <see cref="HttpMessageHandler"/> when the SHM path is
/// unavailable. Mirrors the Go <c>dialShmWithFallback</c> pattern.
/// </summary>
/// <example>
/// <code>
/// // Channel that prefers SHM but falls back to TCP:
/// using var handler = new ShmFallbackHandler("segment",
///     tcpFallbackAddress: "https://localhost:5001");
/// using var channel = GrpcChannel.ForAddress("shm://localhost",
///     new GrpcChannelOptions { HttpHandler = handler });
/// </code>
/// </example>
public sealed class ShmFallbackHandler : HttpMessageHandler
{
    private readonly string _segmentName;
    private readonly string _tcpFallbackAddress;
    private readonly ShmServicePolicy _policy;
    private readonly ILogger _logger;

    private ShmHandler? _shmHandler;
    private HttpMessageHandler? _tcpHandler;

    private volatile bool _shmFailed;
    private volatile bool _disposed;

    // Counters for observability
    private long _shmAttempts;
    private long _shmSuccesses;
    private long _tcpFallbacks;

    /// <summary>
    /// Creates a new fallback handler that tries SHM first, then TCP.
    /// </summary>
    /// <param name="segmentName">The shared memory segment name.</param>
    /// <param name="tcpFallbackAddress">The TCP address (e.g. "https://localhost:5001") to fall back to.</param>
    /// <param name="policy">Transport selection policy. Defaults to <see cref="ShmServicePolicy.Auto"/>.</param>
    /// <param name="tcpHandler">
    /// Optional pre-configured TCP handler. If null, a default <see cref="SocketsHttpHandler"/>
    /// with HTTP/2 will be created.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public ShmFallbackHandler(
        string segmentName,
        string tcpFallbackAddress,
        ShmServicePolicy? policy = null,
        HttpMessageHandler? tcpHandler = null,
        ILogger? logger = null)
    {
        _segmentName = segmentName ?? throw new ArgumentNullException(nameof(segmentName));
        _tcpFallbackAddress = tcpFallbackAddress ?? throw new ArgumentNullException(nameof(tcpFallbackAddress));
        _policy = policy ?? new ShmServicePolicy();
        _policy.Validate();
        _logger = logger ?? NullLogger.Instance;
        _tcpHandler = tcpHandler;
    }

    /// <summary>
    /// Gets the segment name this handler targets.
    /// </summary>
    public string SegmentName => _segmentName;

    /// <summary>
    /// Gets the TCP fallback address.
    /// </summary>
    public string TcpFallbackAddress => _tcpFallbackAddress;

    /// <summary>
    /// Gets whether the handler has fallen back to TCP.
    /// </summary>
    public bool IsFallenBack => _shmFailed;

    /// <summary>
    /// Gets the number of SHM connection attempts.
    /// </summary>
    public long ShmAttempts => Interlocked.Read(ref _shmAttempts);

    /// <summary>
    /// Gets the number of successful SHM sends.
    /// </summary>
    public long ShmSuccesses => Interlocked.Read(ref _shmSuccesses);

    /// <summary>
    /// Gets the number of TCP fallback sends.
    /// </summary>
    public long TcpFallbacks => Interlocked.Read(ref _tcpFallbacks);

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Policy: disabled → always TCP
        if (_policy.Policy == ShmServicePolicy.Disabled)
        {
            return await SendViaTcpAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // If we've already failed SHM and fallback is allowed, go straight to TCP
        if (_shmFailed && _policy.AllowFallback && _policy.Policy != ShmServicePolicy.Required)
        {
            return await SendViaTcpAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // Try SHM
        Interlocked.Increment(ref _shmAttempts);
        try
        {
            var shmHandler = EnsureShmHandler();
            var invoker = new HttpMessageInvoker(shmHandler, disposeHandler: false);
            var response = await invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _shmSuccesses);
            ShmTelemetry.RecordTransportSelected("shm", _segmentName);
            return response;
        }
        catch (Exception ex) when (IsShmTransientError(ex))
        {
            _logger.LogWarning(ex, "SHM transport to '{SegmentName}' failed", _segmentName);
            ShmTelemetry.RecordError("shm_fallback_triggered", _segmentName);

            // Policy: required → do not fallback, propagate the error
            if (_policy.Policy == ShmServicePolicy.Required || !_policy.AllowFallback)
            {
                throw;
            }

            // Mark SHM as failed for fast-path on subsequent calls
            _shmFailed = true;

            _logger.LogInformation(
                "Falling back from SHM '{SegmentName}' to TCP '{TcpAddress}'",
                _segmentName, _tcpFallbackAddress);

            return await SendViaTcpAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private ShmHandler EnsureShmHandler()
    {
        var existing = Volatile.Read(ref _shmHandler);
        if (existing != null)
        {
            return existing;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);

        var newHandler = new ShmHandler(_segmentName);
        var prev = Interlocked.CompareExchange(ref _shmHandler, newHandler, null);
        if (prev != null)
        {
            newHandler.Dispose();
            return prev;
        }

        // Re-check: if Dispose raced between our disposed check and the CAS,
        // the new handler was published into an already-disposed parent.
        if (_disposed)
        {
            Interlocked.CompareExchange(ref _shmHandler, null, newHandler);
            newHandler.Dispose();
            throw new ObjectDisposedException(nameof(ShmFallbackHandler));
        }

        return newHandler;
    }

    private async Task<HttpResponseMessage> SendViaTcpAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _tcpFallbacks);
        ShmTelemetry.RecordTransportSelected("tcp", _tcpFallbackAddress);

        var tcpHandler = EnsureTcpHandler();
        var invoker = new HttpMessageInvoker(tcpHandler, disposeHandler: false);
        return await invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private HttpMessageHandler EnsureTcpHandler()
    {
        var existing = Volatile.Read(ref _tcpHandler);
        if (existing != null)
        {
            return existing;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);

        var newHandler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true
        };
        var prev = Interlocked.CompareExchange(ref _tcpHandler, newHandler, null);
        if (prev != null)
        {
            newHandler.Dispose();
            return prev;
        }

        if (_disposed)
        {
            Interlocked.CompareExchange(ref _tcpHandler, null, newHandler);
            newHandler.Dispose();
            throw new ObjectDisposedException(nameof(ShmFallbackHandler));
        }

        return newHandler;
    }

    /// <summary>
    /// Determines whether an exception from the SHM path is a transient/connection
    /// error that warrants fallback (vs. an application-level error that should propagate).
    /// </summary>
    private static bool IsShmTransientError(Exception ex)
    {
        // Unwrap aggregate exceptions
        if (ex is AggregateException agg)
        {
            return agg.InnerExceptions.Any(IsShmTransientError);
        }

        // SHM segment not found / connection refused
        if (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }

        // General I/O errors on the shared memory segment
        if (ex is IOException)
        {
            return true;
        }

        // Platform-specific errors (e.g., UnauthorizedAccessException for SHM)
        if (ex is UnauthorizedAccessException)
        {
            return true;
        }

        // Timeout connecting to SHM
        if (ex is TimeoutException)
        {
            return true;
        }

        // ObjectDisposed on the SHM connection
        if (ex is ObjectDisposedException)
        {
            return true;
        }

        // InvalidOperation if SHM is not supported on this platform
        if (ex is InvalidOperationException && ex.Message.Contains("shared memory", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (disposing)
        {
            _shmHandler?.Dispose();
            _tcpHandler?.Dispose();
        }

        base.Dispose(disposing);
    }
}
