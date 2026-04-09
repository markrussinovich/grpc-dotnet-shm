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
/// Frame payload wrapper that owns a pooled buffer.
/// Call <see cref="Release"/> to return the buffer to the pool.
/// </summary>
public readonly struct FramePayload
{
    public static readonly FramePayload Empty = new(ReadOnlyMemory<byte>.Empty, null);

    private readonly byte[]? _pooledBuffer;

    public ReadOnlyMemory<byte> Memory { get; }

    public int Length => Memory.Length;

    private FramePayload(ReadOnlyMemory<byte> memory, byte[]? pooledBuffer)
    {
        Memory = memory;
        _pooledBuffer = pooledBuffer;
    }

    public static FramePayload FromPooled(byte[] buffer, int length)
    {
        return new FramePayload(buffer.AsMemory(0, length), buffer);
    }

    public void Release()
    {
        if (_pooledBuffer != null)
        {
            ArrayPool<byte>.Shared.Return(_pooledBuffer);
        }
    }
}
