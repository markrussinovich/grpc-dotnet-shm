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

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Frame payload wrapper that either owns a pooled buffer or a ring reservation.
/// Call <see cref="Release"/> to return buffers or commit the ring read.
/// </summary>
public readonly struct FramePayload
{
    public static readonly FramePayload Empty = new(ReadOnlyMemory<byte>.Empty, null, default, false);

    private readonly byte[]? _pooledBuffer;
    private readonly ReadReservation _reservation;
    private readonly bool _hasReservation;

    public ReadOnlyMemory<byte> Memory { get; }

    public int Length => Memory.Length;

    private FramePayload(ReadOnlyMemory<byte> memory, byte[]? pooledBuffer, ReadReservation reservation, bool hasReservation)
    {
        Memory = memory;
        _pooledBuffer = pooledBuffer;
        _reservation = reservation;
        _hasReservation = hasReservation;
    }

    public static FramePayload FromPooled(byte[] buffer, int length)
    {
        return new FramePayload(buffer.AsMemory(0, length), buffer, default, false);
    }

    public static FramePayload FromReservation(ReadReservation reservation, int length)
    {
        var memory = reservation.First;
        if (memory.Length != length)
        {
            memory = memory.Slice(0, length);
        }

        return new FramePayload(memory, null, reservation, true);
    }

    public void Release()
    {
        if (_hasReservation)
        {
            _reservation.Ring?.ReleaseBorrow(Length);
            return;
        }

        if (_pooledBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(_pooledBuffer);
        }
    }
}
