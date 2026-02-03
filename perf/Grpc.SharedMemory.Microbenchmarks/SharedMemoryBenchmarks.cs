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

using BenchmarkDotNet.Attributes;
using Grpc.Net.SharedMemory;

namespace Grpc.SharedMemory.Microbenchmarks;

/// <summary>
/// Benchmarks for ring buffer write/read operations.
/// Measures raw transport overhead without network stack.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class RingBufferBenchmarks
{
    private Segment _segment = null!;
    private byte[] _smallPayload = null!;
    private byte[] _mediumPayload = null!;
    private byte[] _largePayload = null!;
    private byte[] _readBuffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Use unique segment name per run to avoid conflicts
        var segmentName = $"bench_{Guid.NewGuid():N}";
        _segment = Segment.Create(segmentName, 64 * 1024 * 1024); // 64MB ring

        // Pre-allocate payloads
        _smallPayload = new byte[64];           // Typical metadata size
        _mediumPayload = new byte[4096];        // 4KB - typical message
        _largePayload = new byte[1024 * 1024];  // 1MB - large message

        // Fill with data
        Random.Shared.NextBytes(_smallPayload);
        Random.Shared.NextBytes(_mediumPayload);
        Random.Shared.NextBytes(_largePayload);

        _readBuffer = new byte[1024 * 1024];
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _segment?.Dispose();
    }

    [Benchmark(Description = "Write 64B")]
    public void Write_64B()
    {
        _segment.RingA.Write(_smallPayload);
        // Reset for next iteration
        _segment.RingA.Read(_readBuffer.AsSpan(0, _smallPayload.Length));
    }

    [Benchmark(Description = "Write 4KB")]
    public void Write_4KB()
    {
        _segment.RingA.Write(_mediumPayload);
        _segment.RingA.Read(_readBuffer.AsSpan(0, _mediumPayload.Length));
    }

    [Benchmark(Description = "Write 1MB")]
    public void Write_1MB()
    {
        _segment.RingA.Write(_largePayload);
        _segment.RingA.Read(_readBuffer.AsSpan(0, _largePayload.Length));
    }
}

/// <summary>
/// Benchmarks for frame protocol operations (headers + payload).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class FrameProtocolBenchmarks
{
    private Segment _segment = null!;
    private byte[] _messagePayload = null!;
    private HeadersV1 _headers = null!;
    private TrailersV1 _trailers = null!;
    private byte[] _readBuffer = null!;
    private byte[] _frameHeaderBuffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        var segmentName = $"bench_frame_{Guid.NewGuid():N}";
        _segment = Segment.Create(segmentName, 64 * 1024 * 1024);

        _messagePayload = new byte[4096];
        Random.Shared.NextBytes(_messagePayload);

        _headers = new HeadersV1
        {
            Version = 1,
            HeaderType = 0,
            Method = "/grpc.test.BenchmarkService/UnaryCall",
            Authority = "localhost",
            DeadlineUnixNano = 0,
            Metadata = Array.Empty<MetadataKV>()
        };

        _trailers = new TrailersV1
        {
            Version = 1,
            GrpcStatusCode = Grpc.Core.StatusCode.OK,
            GrpcStatusMessage = "",
            Metadata = Array.Empty<MetadataKV>()
        };

        _readBuffer = new byte[64 * 1024];
        _frameHeaderBuffer = new byte[16];
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _segment?.Dispose();
    }

    [Benchmark(Description = "Headers encode")]
    public byte[] HeadersEncode()
    {
        return _headers.Encode();
    }

    [Benchmark(Description = "Trailers encode")]
    public byte[] TrailersEncode()
    {
        return _trailers.Encode();
    }

    [Benchmark(Description = "FrameHeader encode")]
    public void FrameHeaderEncode()
    {
        var header = new FrameHeader
        {
            Length = 4096,
            StreamId = 1,
            Type = FrameType.Message,
            Flags = 0
        };
        header.EncodeTo(_frameHeaderBuffer);
    }

    [Benchmark(Description = "FrameHeader decode")]
    public FrameHeader FrameHeaderDecode()
    {
        // Pre-populated buffer
        return FrameHeader.DecodeFrom(_frameHeaderBuffer);
    }

    [Benchmark(Description = "Full request frame (headers + 4KB msg)")]
    public void FullRequestFrame()
    {
        // Write headers frame
        var headersPayload = _headers.Encode();
        var headersFrame = new FrameHeader
        {
            Length = (uint)headersPayload.Length,
            StreamId = 1,
            Type = FrameType.Headers,
            Flags = HeadersFlags.Initial
        };
        headersFrame.EncodeTo(_frameHeaderBuffer);
        _segment.RingA.Write(_frameHeaderBuffer);
        _segment.RingA.Write(headersPayload);

        // Write message frame
        var msgFrame = new FrameHeader
        {
            Length = (uint)_messagePayload.Length,
            StreamId = 1,
            Type = FrameType.Message,
            Flags = 0  // No flags for final message
        };
        msgFrame.EncodeTo(_frameHeaderBuffer);
        _segment.RingA.Write(_frameHeaderBuffer);
        _segment.RingA.Write(_messagePayload);

        // Drain for next iteration
        _segment.RingA.Read(_readBuffer.AsSpan(0, 16 + headersPayload.Length + 16 + _messagePayload.Length));
    }
}

/// <summary>
/// Latency benchmarks comparing ring buffer to memory copy baseline.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class LatencyBenchmarks
{
    private Segment _segment = null!;
    private byte[] _payload = null!;
    private byte[] _readBuffer = null!;
    private byte[] _memoryBuffer = null!;

    [Params(64, 1024, 4096, 16384)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var segmentName = $"bench_latency_{Guid.NewGuid():N}";
        _segment = Segment.Create(segmentName, 64 * 1024 * 1024);

        _payload = new byte[PayloadSize];
        Random.Shared.NextBytes(_payload);
        _readBuffer = new byte[PayloadSize];
        _memoryBuffer = new byte[64 * 1024 * 1024];
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _segment?.Dispose();
    }

    [Benchmark(Baseline = true, Description = "Memory.Copy baseline")]
    public void MemoryCopyBaseline()
    {
        // Simulates minimal overhead - just memcpy
        _payload.AsSpan().CopyTo(_memoryBuffer.AsSpan(0, PayloadSize));
        _memoryBuffer.AsSpan(0, PayloadSize).CopyTo(_readBuffer);
    }

    [Benchmark(Description = "Ring buffer round-trip")]
    public void RingBufferRoundTrip()
    {
        _segment.RingA.Write(_payload);
        _segment.RingA.Read(_readBuffer.AsSpan(0, PayloadSize));
    }
}

/// <summary>
/// Throughput benchmarks measuring sustained message rate.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class ThroughputBenchmarks
{
    private Segment _segment = null!;
    private byte[] _payload = null!;
    private byte[] _readBuffer = null!;

    [Params(100, 1000, 10000)]
    public int MessageCount { get; set; }

    [Params(64, 4096)]
    public int MessageSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var segmentName = $"bench_throughput_{Guid.NewGuid():N}";
        _segment = Segment.Create(segmentName, 64 * 1024 * 1024);

        _payload = new byte[MessageSize];
        Random.Shared.NextBytes(_payload);
        _readBuffer = new byte[MessageSize];
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _segment?.Dispose();
    }

    [Benchmark(Description = "Batch write+read")]
    public void BatchWriteRead()
    {
        for (int i = 0; i < MessageCount; i++)
        {
            _segment.RingA.Write(_payload);
        }

        for (int i = 0; i < MessageCount; i++)
        {
            _segment.RingA.Read(_readBuffer.AsSpan(0, MessageSize));
        }
    }

    [Benchmark(Description = "Interleaved write+read")]
    public void InterleavedWriteRead()
    {
        for (int i = 0; i < MessageCount; i++)
        {
            _segment.RingA.Write(_payload);
            _segment.RingA.Read(_readBuffer.AsSpan(0, MessageSize));
        }
    }
}
