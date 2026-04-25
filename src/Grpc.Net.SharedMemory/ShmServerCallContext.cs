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

using System.Text;
using Grpc.Core;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// A <see cref="ServerCallContext"/> implementation for the shared memory transport.
/// Wraps an <see cref="ShmGrpcStream"/> and provides gRPC call context to service methods.
/// </summary>
internal sealed class ShmServerCallContext : ServerCallContext, IDisposable
{
    private readonly ShmGrpcStream _stream;
    private readonly HeadersV1 _requestHeaders;
    private readonly CancellationTokenSource _callCts;
    private readonly Timer? _deadlineTimer;
    private readonly Metadata _requestMetadata;
    private readonly Metadata _responseTrailers;
    private Status _status;
    private WriteOptions? _writeOptions;
    private bool _headersSent;
    private int _deadlineExceeded;
    private int _disposed;

    public ShmServerCallContext(ShmGrpcStream stream, HeadersV1 requestHeaders, CancellationToken cancellationToken)
    {
        _stream = stream;
        _requestHeaders = requestHeaders;
        _callCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stream.CancellationToken);
        _requestMetadata = ConvertFromShmMetadata(requestHeaders.Metadata);
        _responseTrailers = new Metadata();
        _status = Status.DefaultSuccess;

        if (requestHeaders.DeadlineUnixNano != 0)
        {
            var deadline = DeadlineCore;
            var dueTime = deadline - DateTime.UtcNow;
            if (dueTime <= TimeSpan.Zero)
            {
                MarkDeadlineExceeded();
            }
            else
            {
                _deadlineTimer = new Timer(static state =>
                {
                    ((ShmServerCallContext)state!).MarkDeadlineExceeded();
                }, this, dueTime, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>
    /// Ensures response headers have been sent. Called before first response message.
    /// </summary>
    internal Task EnsureResponseHeadersSentAsync()
    {
        if (_headersSent)
            return Task.CompletedTask;

        return EnsureResponseHeadersSentSlowAsync();
    }

    internal bool HeadersSent => _headersSent;
    internal void MarkHeadersSent() => _headersSent = true;

    private async Task EnsureResponseHeadersSentSlowAsync()
    {
        _headersSent = true;
        await _stream.SendResponseHeadersAsync();
    }

    protected override string MethodCore => _requestHeaders.Method ?? "";
    protected override string HostCore => _requestHeaders.Authority ?? "localhost";
    protected override string PeerCore => $"shm://{_stream.StreamId}";

    protected override DateTime DeadlineCore
    {
        get
        {
            if (_requestHeaders.DeadlineUnixNano == 0)
                return DateTime.MaxValue;
            // DeadlineUnixNano is Unix time in nanoseconds
            var unixMs = (long)(_requestHeaders.DeadlineUnixNano / 1_000_000);
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime;
        }
    }

    protected override Metadata RequestHeadersCore => _requestMetadata;
    protected override CancellationToken CancellationTokenCore => _callCts.Token;
    protected override Metadata ResponseTrailersCore => _responseTrailers;
    protected override Status StatusCore { get => _status; set => _status = value; }
    protected override WriteOptions? WriteOptionsCore { get => _writeOptions; set => _writeOptions = value; }

    protected override AuthContext AuthContextCore =>
        new AuthContext(null, new Dictionary<string, List<AuthProperty>>());

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
    {
        throw new NotSupportedException("Context propagation is not supported on shared memory transport");
    }

    protected override async Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
    {
        if (_headersSent)
            throw new InvalidOperationException("Response headers already sent");
        _headersSent = true;
        await _stream.SendResponseHeadersAsync(responseHeaders);
    }

    internal bool IsDeadlineExceeded => Volatile.Read(ref _deadlineExceeded) != 0 || DeadlineCore <= DateTime.UtcNow;

    internal void ApplyCancellationStatus()
    {
        if (_status.StatusCode != StatusCode.OK)
        {
            return;
        }

        if (IsDeadlineExceeded)
        {
            _status = new Status(StatusCode.DeadlineExceeded, "Deadline Exceeded");
        }
        else if (_stream.IsCancelled)
        {
            _status = new Status(StatusCode.Cancelled, "Call canceled by the client.");
        }
    }

    private void MarkDeadlineExceeded()
    {
        if (Interlocked.Exchange(ref _deadlineExceeded, 1) == 0)
        {
            try
            {
                _callCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _deadlineTimer?.Dispose();
        _callCts.Dispose();
    }

    /// <summary>
    /// Converts SHM metadata (MetadataKV[]) to gRPC Metadata.
    /// </summary>
    internal static Metadata ConvertFromShmMetadata(IReadOnlyList<MetadataKV> shmMetadata)
    {
        var metadata = new Metadata();
        foreach (var kv in shmMetadata)
        {
            foreach (var value in kv.Values)
            {
                if (kv.Key.EndsWith("-bin", StringComparison.OrdinalIgnoreCase))
                {
                    metadata.Add(new Metadata.Entry(kv.Key, value));
                }
                else
                {
                    metadata.Add(new Metadata.Entry(kv.Key, Encoding.UTF8.GetString(value)));
                }
            }
        }
        return metadata;
    }
}
