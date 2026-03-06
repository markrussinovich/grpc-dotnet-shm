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

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Batched frame writer modelled after Kestrel's <c>Http2FrameWriter</c>.
/// Small payloads are defensively copied into pooled buffers at enqueue time.
/// Large payloads can be enqueued zero-copy; the pooled buffer is returned
/// after the data has been written to the ring buffer.
/// </summary>
internal sealed class ShmFrameWriter : IDisposable
{
    private struct FrameEntry
    {
        public FrameType Type;
        public uint StreamId;
        public byte Flags;
        public int Length;
        public ReadOnlyMemory<byte> Payload; // payload data (both paths)
        public byte[]? ReturnToPool;          // buffer to return to ArrayPool after ring write
    }

    private readonly ShmRing _ring;
    private readonly Channel<FrameEntry> _channel;
    private readonly Task _writerTask;
    private readonly CancellationTokenSource _cts;
    private readonly CancellationToken _ct;
    private bool _disposed;

    public ShmFrameWriter(ShmRing ring, CancellationTokenSource cts)
    {
        _ring = ring;
        _cts = cts;
        _ct = cts.Token;
        _channel = Channel.CreateUnbounded<FrameEntry>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _writerTask = Task.Factory.StartNew(
            WriterLoopAsync, _ct,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// Enqueues a frame by defensively copying the payload into a pooled buffer.
    /// The copy is written to the ring and the buffer returned to the pool by
    /// the dedicated writer thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">The writer has been completed (disposed).</exception>
    public void Enqueue(FrameType type, uint streamId, byte flags, ReadOnlySpan<byte> payload)
    {
        var len = payload.Length;
        byte[]? buf = null;
        ReadOnlyMemory<byte> mem = default;
        if (len > 0)
        {
            buf = ArrayPool<byte>.Shared.Rent(len);
            payload.CopyTo(buf);
            mem = buf.AsMemory(0, len);
        }

        if (!_channel.Writer.TryWrite(new FrameEntry
        {
            Type = type, StreamId = streamId, Flags = flags,
            Length = len, Payload = mem, ReturnToPool = buf
        }))
        {
            // Channel is completed (Dispose was called). Return the buffer and
            // surface the failure so callers don't silently lose frames.
            if (buf != null)
                ArrayPool<byte>.Shared.Return(buf);
            throw new InvalidOperationException("Frame writer has been disposed.");
        }
    }

    /// <summary>
    /// Enqueues a frame without copying the payload. The caller's
    /// <paramref name="pooledBuffer"/> is returned to <see cref="ArrayPool{T}"/>
    /// after the data has been written to the ring buffer.
    /// Pass <c>null</c> if the payload does not need to be returned.
    /// </summary>
    /// <remarks>
    /// On failure the caller retains ownership of <paramref name="pooledBuffer"/>;
    /// this method does NOT return it to the pool.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The writer has been completed (disposed).</exception>
    public void EnqueueZeroCopy(FrameType type, uint streamId, byte flags,
        ReadOnlyMemory<byte> payload, byte[]? pooledBuffer)
    {
        if (!_channel.Writer.TryWrite(new FrameEntry
        {
            Type = type, StreamId = streamId, Flags = flags,
            Length = payload.Length, Payload = payload,
            ReturnToPool = pooledBuffer
        }))
        {
            // Channel is completed — caller still owns the buffer.
            throw new InvalidOperationException("Frame writer has been disposed.");
        }
    }

    private async Task WriterLoopAsync()
    {
        const int maxBatch = 512;
        var batch = new FrameEntry[maxBatch];

        try
        {
            var reader = _channel.Reader;

            while (!_ct.IsCancellationRequested)
            {
                // Phase 1: immediate read
                if (reader.TryRead(out batch[0]))
                {
                    var count = 1;
                    while (count < maxBatch && reader.TryRead(out batch[count]))
                        count++;
                    FlushBatch(batch, count);
                    continue;
                }

                // Phase 2: adaptive spin
                var spinWait = new SpinWait();
                var found = false;
                while (!spinWait.NextSpinWillYield)
                {
                    spinWait.SpinOnce(sleep1Threshold: -1);
                    if (reader.TryRead(out batch[0]))
                    {
                        var count = 1;
                        while (count < maxBatch && reader.TryRead(out batch[count]))
                            count++;
                        FlushBatch(batch, count);
                        found = true;
                        break;
                    }
                }

                if (found) continue;

                // Phase 3: async wait
                if (!await reader.WaitToReadAsync(_ct).ConfigureAwait(false))
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (RingClosedException) { }
    }

    private void FlushBatch(FrameEntry[] batch, int count)
    {
        try
        {
            // No lock needed: the writer thread is the sole consumer of the
            // Channel (SingleReader=true) and all ring writes go through it,
            // so there is no concurrent access to the ring.
            // Importantly, NOT holding a lock here means that if
            // FrameProtocol.WriteMessage blocks waiting for ring space
            // (ReserveWrite), we do not prevent other enqueue operations
            // from completing — avoiding a deadlock where WindowUpdate
            // frames cannot be enqueued while the ring is full.
            for (var i = 0; i < count; i++)
            {
                ref var entry = ref batch[i];
                var payload = entry.Payload.Span;

                if (entry.Type == FrameType.Message)
                {
                    var isLast = (entry.Flags & MessageFlags.More) == 0;
                    FrameProtocol.WriteMessage(_ring, entry.StreamId, payload, isLast, _ct);
                }
                else
                {
                    var header = new FrameHeader(entry.Type, entry.StreamId, (uint)entry.Length, entry.Flags);
                    FrameProtocol.WriteFrame(_ring, header, payload, _ct);
                }
            }
        }
        finally
        {
            // Always return pooled buffers, even if a ring write threw.
            for (var i = 0; i < count; i++)
            {
                if (batch[i].ReturnToPool != null)
                    ArrayPool<byte>.Shared.Return(batch[i].ReturnToPool!);
                batch[i] = default;
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;

            // 1. Stop accepting new entries.
            _channel.Writer.TryComplete();

            // 2. Give the writer thread a chance to flush remaining entries.
            var writerDone = false;
            try
            {
                writerDone = _writerTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                writerDone = true; // task faulted — it's done
            }

            // 3. If the writer is still blocked (e.g. ring full), cancel to
            //    unblock it, then wait again for it to actually exit.
            if (!writerDone)
            {
                _cts.Cancel();
                try
                {
                    _writerTask.Wait(TimeSpan.FromSeconds(2));
                }
                catch (AggregateException)
                {
                    // Expected — OperationCanceledException / RingClosedException.
                }
            }

            // 4. Writer task is now guaranteed to have exited.
            //    Drain any remaining entries so pooled buffers are returned.
            //    This is safe because the channel was created with
            //    SingleReader=true and the sole reader (writer task) has exited.
            while (_channel.Reader.TryRead(out var entry))
            {
                if (entry.ReturnToPool != null)
                    ArrayPool<byte>.Shared.Return(entry.ReturnToPool);
            }
        }
    }
}
