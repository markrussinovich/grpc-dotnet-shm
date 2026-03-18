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

namespace Grpc.Net.SharedMemory;

/// <summary>
/// A stream implementation that wraps bidirectional shared memory ring buffers.
/// Used to provide a standard Stream interface for gRPC transport integration.
/// </summary>
public sealed class ShmStream : Stream
{
    private readonly ShmRing _readRing;
    private readonly ShmRing _writeRing;
    private readonly CancellationTokenSource _disposeCts;
    private readonly Segment? _ownedSegment;
    private int _disposed;
    private int _inflightOps; // Tracks in-flight Task.Run operations

    /// <summary>
    /// Creates a new ShmStream with separate read and write ring buffers.
    /// </summary>
    /// <param name="readRing">Ring buffer for reading (receiving data).</param>
    /// <param name="writeRing">Ring buffer for writing (sending data).</param>
    /// <param name="ownedSegment">Optional segment to dispose when the stream is disposed.</param>
    public ShmStream(ShmRing readRing, ShmRing writeRing, Segment? ownedSegment = null)
    {
        _readRing = readRing ?? throw new ArgumentNullException(nameof(readRing));
        _writeRing = writeRing ?? throw new ArgumentNullException(nameof(writeRing));
        _ownedSegment = ownedSegment;
        _disposeCts = new CancellationTokenSource();
    }

    /// <inheritdoc/>
    public override bool CanRead => Volatile.Read(ref _disposed) == 0;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => Volatile.Read(ref _disposed) == 0;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override void Flush()
    {
        // Ring buffer writes are immediately visible, no buffering
    }

    /// <inheritdoc/>
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ThrowIfDisposed();
        return _readRing.Read(buffer.AsSpan(offset, count), _disposeCts.Token);
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        return _readRing.Read(buffer, _disposeCts.Token);
    }

    /// <inheritdoc/>
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _inflightOps);
        try
        {
            ThrowIfDisposed();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
            return await Task.Run(() => _readRing.Read(buffer.AsSpan(offset, count), linkedCts.Token), linkedCts.Token);
        }
        finally
        {
            Interlocked.Decrement(ref _inflightOps);
        }
    }

    /// <inheritdoc/>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _inflightOps);
        try
        {
            ThrowIfDisposed();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
            return await Task.Run(() => _readRing.Read(buffer.Span, linkedCts.Token), linkedCts.Token);
        }
        finally
        {
            Interlocked.Decrement(ref _inflightOps);
        }
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ThrowIfDisposed();
        _writeRing.Write(buffer.AsSpan(offset, count), _disposeCts.Token);
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfDisposed();
        _writeRing.Write(buffer, _disposeCts.Token);
    }

    /// <inheritdoc/>
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _inflightOps);
        try
        {
            ThrowIfDisposed();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
            await Task.Run(() => _writeRing.Write(buffer.AsSpan(offset, count), linkedCts.Token), linkedCts.Token);
        }
        finally
        {
            Interlocked.Decrement(ref _inflightOps);
        }
    }

    /// <inheritdoc/>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _inflightOps);
        try
        {
            ThrowIfDisposed();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
            await Task.Run(() => _writeRing.Write(buffer.Span, linkedCts.Token), linkedCts.Token);
        }
        finally
        {
            Interlocked.Decrement(ref _inflightOps);
        }
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && disposing)
        {
            _disposeCts.Cancel();

            // Wait for in-flight Task.Run operations to exit before
            // unmapping segment memory.  SpinWait is acceptable here
            // because the ring operations check the CancellationToken
            // frequently and exit quickly after cancel.
            var spin = new SpinWait();
            while (Volatile.Read(ref _inflightOps) > 0)
            {
                spin.SpinOnce();
            }

            _disposeCts.Dispose();
            _ownedSegment?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _disposeCts.Cancel();

            // Wait for in-flight Task.Run operations to exit.
            while (Volatile.Read(ref _inflightOps) > 0)
            {
                await Task.Delay(1).ConfigureAwait(false);
            }

            _disposeCts.Dispose();
            _ownedSegment?.Dispose();
        }

        await base.DisposeAsync();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
