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
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Parses protobuf messages using <see cref="ArrayPool{T}"/> for large
/// <c>bytes</c> fields, avoiding Large Object Heap (LOH) allocations.
///
/// .NET allocates <c>byte[]</c> ≥ 85 000 bytes on the LOH, which is only
/// collected during Gen2 GC (full, stop-the-world). Protobuf's standard
/// <c>ReadBytes()</c> always does <c>new byte[size]</c>, so any message
/// with a bytes field ≥ 85 KB triggers frequent Gen2 collections.
///
/// This parser scans the protobuf wire format, rents from ArrayPool for
/// large bytes fields, and wraps them via <see cref="UnsafeByteOperations"/>.
/// All other fields are accumulated and parsed via standard <c>MergeFrom</c>.
///
/// <para><b>Lifetime contract</b>: pooled arrays tracked in the returned
/// list must be returned to <see cref="ArrayPool{T}.Shared"/> after the
/// message is consumed. Holding references to <c>ByteString.Memory</c>
/// past that point is undefined behavior.</para>
/// </summary>
internal static class PooledProtoParser
{
    /// <summary>.NET LOH threshold — byte[] at or above this go to LOH.</summary>
    internal const int LohThreshold = 85_000;

    /// <summary>
    /// Messages above this size skip PooledParser entirely and use standard
    /// MergeFrom. Set higher than <see cref="_maxPooledArraySize"/> because
    /// a message can contain a poolable bytes field plus overhead (tags,
    /// wrapper message, other small fields). Default: 2x maxPooledArraySize.
    /// </summary>
    internal static int MaxPooledSize => (int)Math.Min((long)_maxPooledArraySize * 2, int.MaxValue);

    /// <summary>
    /// Max size for individual pooled byte[] from ArrayPool. Larger fields
    /// fall through to standard MergeFrom (new byte[]) to avoid bloating
    /// ArrayPool buckets with oversized arrays that are rarely reused.
    /// Configurable via <see cref="MaxPooledArraySizeOverride"/>.
    /// Default: 2MB.
    /// </summary>
    private static int _maxPooledArraySize = 2 * 1024 * 1024;

    /// <summary>
    /// Sets the max pooled array size. Call before any parsing occurs.
    /// Must be a power of 2 for efficient ArrayPool bucket alignment.
    /// </summary>
    internal static int MaxPooledArraySizeOverride
    {
        get => _maxPooledArraySize;
        set => _maxPooledArraySize = RoundUpToPowerOf2(Math.Max(LohThreshold, value));
    }

    /// <summary>
    /// Parses <paramref name="data"/> into a new <typeparamref name="T"/>,
    /// using ArrayPool for <c>bytes</c> fields ≥ <see cref="LohThreshold"/>.
    /// </summary>
    /// <param name="data">Serialized protobuf bytes.</param>
    /// <returns>The parsed message.</returns>
    public static T ParseFrom<T>(ReadOnlySpan<byte> data)
        where T : IMessage<T>, new()
    {
        // Small message — no LOH risk, use standard fast path.
        // Very large message — scanner overhead outweighs LOH savings.
        if (data.Length < LohThreshold || data.Length > MaxPooledSize)
        {
            var small = new T();
            small.MergeFrom(data);
            return small;
        }

        var msg = new T();
        ParseInto(data, msg);
        return msg;
    }

    /// <summary>
    /// Parses <paramref name="data"/> into <paramref name="msg"/> using
    /// ArrayPool for large bytes fields. Non-pooled fields are accumulated
    /// and parsed via a single <see cref="MessageExtensions.MergeFrom(IMessage, ReadOnlySpan{byte})"/>.
    /// </summary>
    internal static void ParseInto(ReadOnlySpan<byte> data, IMessage msg)
    {
        int pos = 0;

        while (pos < data.Length)
        {
            int tagStart = pos;
            uint tag = ReadVarint32(data, ref pos);
            int fieldNumber = (int)(tag >> 3);
            int wireType = (int)(tag & 7);

            switch (wireType)
            {
                case 0: // VARINT
                    SkipVarint(data, ref pos);
                    // Apply immediately in wire order via MergeFrom on this single field.
                    msg.MergeFrom(data.Slice(tagStart, pos - tagStart));
                    break;

                case 1: // FIXED64
                    pos += 8;
                    if (pos > data.Length) throw new FormatException("Truncated protobuf message");
                    msg.MergeFrom(data.Slice(tagStart, pos - tagStart));
                    break;

                case 5: // FIXED32
                    pos += 4;
                    if (pos > data.Length) throw new FormatException("Truncated protobuf message");
                    msg.MergeFrom(data.Slice(tagStart, pos - tagStart));
                    break;

                case 2: // LENGTH_DELIMITED (bytes, string, message, packed)
                {
                    int length = (int)ReadVarint32(data, ref pos);
                    if (length < 0) throw new FormatException("Truncated protobuf message");
                    int dataStart = pos;
                    pos += length;
                    if (pos > data.Length) throw new FormatException("Truncated protobuf message");

                    var fd = msg.Descriptor.FindFieldByNumber(fieldNumber);

                    if (fd is { FieldType: FieldType.Bytes, IsRepeated: false }
                        && length >= LohThreshold && length <= _maxPooledArraySize)
                    {
                        var pooled = ArrayPool<byte>.Shared.Rent(RoundUpToPowerOf2(length));
                        data.Slice(dataStart, length).CopyTo(pooled);
                        var manager = new PooledMemoryManager(pooled, length);
                        fd.Accessor.SetValue(msg,
                            UnsafeByteOperations.UnsafeWrap(manager.Memory));
                    }
                    else if (fd is { FieldType: FieldType.Bytes, IsRepeated: false }
                        && length >= LohThreshold)
                    {
                        var owned = new byte[length];
                        data.Slice(dataStart, length).CopyTo(owned);
                        fd.Accessor.SetValue(msg,
                            UnsafeByteOperations.UnsafeWrap(owned));
                    }
                    else if (fd is { FieldType: FieldType.Message, IsRepeated: false }
                             && length >= LohThreshold && length <= MaxPooledSize)
                    {
                        // Large nested message → recurse to handle big bytes inside.
                        // Respect merge semantics: if this field was already set
                        // (e.g., duplicate singular message in wire data), merge
                        // into the existing value instead of replacing it.
                        if (fd.Accessor.HasValue(msg))
                        {
                            var existing = (IMessage)fd.Accessor.GetValue(msg);
                            ParseInto(data.Slice(dataStart, length), existing);
                        }
                        else
                        {
                            var nested = (IMessage)Activator.CreateInstance(fd.MessageType.ClrType)!;
                            ParseInto(data.Slice(dataStart, length), nested);
                            fd.Accessor.SetValue(msg, nested);
                        }
                    }
                    else
                    {
                        // Small/other fields: apply immediately via MergeFrom.
                        msg.MergeFrom(data.Slice(tagStart, pos - tagStart));
                    }
                    break;
                }

                default:
                    // Unknown wire type: apply via MergeFrom (it will skip or throw).
                    msg.MergeFrom(data.Slice(tagStart, data.Length - tagStart));
                    pos = data.Length;
                    break;
            }
        }
    }

    private static uint ReadVarint32(ReadOnlySpan<byte> data, ref int pos)
    {
        uint result = 0;
        int shift = 0;
        byte b;
        do
        {
            if (pos >= data.Length)
                throw new FormatException("Truncated protobuf message");
            b = data[pos++];
            result |= (uint)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0 && shift < 35);
        return result;
    }

    private static void SkipVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        while (pos < data.Length && (data[pos++] & 0x80) != 0) { }
    }

    /// <summary>
    /// Rounds up to the next power of 2 to align with ArrayPool bucket
    /// boundaries, reducing fragmentation from varied allocation sizes.
    /// </summary>
    private static int RoundUpToPowerOf2(int value)
    {
        if (value <= 0) return 1;
        if (value > (1 << 30)) return 1 << 30;
        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return value + 1;
    }
}

/// <summary>
/// A <see cref="MemoryManager{T}"/> that wraps an ArrayPool-rented buffer
/// and returns it to the pool in its finalizer. This enables ByteString
/// fields created via <see cref="UnsafeByteOperations.UnsafeWrap"/> to
/// use pooled memory with automatic GC-driven cleanup — no manual
/// Dispose/Return needed by the consumer.
///
/// Lifetime: the pooled array stays alive as long as the ByteString
/// (which holds a ReadOnlyMemory referencing this manager) is reachable.
/// When the ByteString is collected, this manager becomes unreachable,
/// and the finalizer returns the array to ArrayPool.
/// </summary>
internal sealed class PooledMemoryManager : MemoryManager<byte>
{
    private byte[]? _array;
    private readonly int _length;

    public PooledMemoryManager(byte[] array, int length)
    {
        _array = array;
        _length = length;
    }

    public override Span<byte> GetSpan() => _array.AsSpan(0, _length);

    public override MemoryHandle Pin(int elementIndex = 0) =>
        throw new NotSupportedException("Pinning pooled memory is not supported");

    public override void Unpin() =>
        throw new NotSupportedException("Unpinning pooled memory is not supported");

    protected override void Dispose(bool disposing)
    {
        Return();
    }

    // CA2015: Finalizer on MemoryManager<T> can free memory while Span<T> is
    // live. Safe here because ByteString stores ReadOnlyMemory (not Span),
    // which holds a strong reference to this manager. The finalizer only runs
    // after all Memory references are unreachable.
#pragma warning disable CA2015
    ~PooledMemoryManager()
    {
        Return();
    }
#pragma warning restore CA2015

    private void Return()
    {
        var array = Interlocked.Exchange(ref _array, null);
        if (array != null)
            ArrayPool<byte>.Shared.Return(array);
    }
}
