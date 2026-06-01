#region Copyright notice and license

// Copyright 2026 The gRPC Authors
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
using System.Diagnostics;
using Grpc.Core;
using Grpc.Net.SharedMemory;
using Grpc.Net.SharedMemory.Wire;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests.Wire;

/// <summary>
/// Round-7 PR-B planning profile. NOT a regression test — these are
/// <c>[Explicit]</c> manual micro-benchmarks that measure the cost of
/// the header serialization path (HeadersV1 ↔ bytes ↔ HeadersV1 ↔ HPACK
/// round-trip) per simulated Unary RPC. Used to decide whether the
/// PR-B object-passthrough refactor is worth its risk.
/// <para>
/// Run manually via:
/// <c>dotnet test ... --filter "FullyQualifiedName~HeaderPathProfile"</c>
/// </para>
/// </summary>
[TestFixture]
[Category("Profile")]
public class HeaderPathProfileTests
{
    private const int Iterations = 100_000;

    private static HeadersV1 BuildClientHeaders() => new()
    {
        HeaderType = 0,
        Method = "/grpc.test.BenchmarkService/UnaryCall",
        Authority = "localhost",
    };

    private static HeadersV1 BuildServerHeaders() => new()
    {
        HeaderType = 1,
    };

    private static TrailersV1 BuildOkTrailers() => new()
    {
        GrpcStatusCode = StatusCode.OK,
        GrpcStatusMessage = null,
        Metadata = Array.Empty<MetadataKV>(),
    };

    private static (long elapsedMs, long allocBytes) MeasureLoop(Action body)
    {
        // Warm-up: 5 % of iteration count to JIT the body.
        var warm = Math.Max(1, Iterations / 20);
        for (var i = 0; i < warm; i++) body();

        // Forced full GC before timing so all generations are quiescent.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);

        var allocStart = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < Iterations; i++) body();
        sw.Stop();
        var allocEnd = GC.GetAllocatedBytesForCurrentThread();

        return (sw.ElapsedMilliseconds, allocEnd - allocStart);
    }

    /// <summary>
    /// Models the WRITE path that one Unary RPC sends across the wire:
    /// 1 REQUEST HEADERS (client→server), 1 RESPONSE HEADERS, 1 TRAILERS.
    /// Each goes through:
    ///   <c>HeadersV1.Encode()</c> (upper layer)
    ///   → <c>Http2Codec.Write.DecodeHeadersV1()</c> (re-parses to HeadersV1)
    ///   → <c>HpackHeadersAdapter.EncodeHeaders()</c> (final wire bytes).
    /// </summary>
    [Test, Explicit("Profile only — run via filter")]
    public void Profile_WriteSide_HeaderChain_CurrentBaseline()
    {
        var clientHeaders = BuildClientHeaders();
        var serverHeaders = BuildServerHeaders();
        var trailers = BuildOkTrailers();

        var (ms, bytes) = MeasureLoop(() =>
        {
            // REQ HEADERS (client side)
            var (cBuf, cLen) = clientHeaders.Encode();
            var clientRoundTrip = HeadersV1.Decode(cBuf.AsSpan(0, cLen));
            ArrayPool<byte>.Shared.Return(cBuf);
            var (cHpack, _) = HpackHeadersAdapter.EncodeHeaders(clientRoundTrip);
            ArrayPool<byte>.Shared.Return(cHpack);

            // RESP HEADERS (server side)
            var (sBuf, sLen) = serverHeaders.Encode();
            var serverRoundTrip = HeadersV1.Decode(sBuf.AsSpan(0, sLen));
            ArrayPool<byte>.Shared.Return(sBuf);
            var (sHpack, _) = HpackHeadersAdapter.EncodeHeaders(serverRoundTrip);
            ArrayPool<byte>.Shared.Return(sHpack);

            // TRAILERS (server side)
            var (tBuf, tLen) = trailers.Encode();
            var trailersRoundTrip = TrailersV1.Decode(tBuf.AsSpan(0, tLen));
            ArrayPool<byte>.Shared.Return(tBuf);
            var (tHpack, _) = HpackHeadersAdapter.EncodeTrailers(trailersRoundTrip);
            ArrayPool<byte>.Shared.Return(tHpack);
        });

        Report("Write-Baseline", ms, bytes);
    }

    /// <summary>
    /// Simulates the PR-B WRITE path where <see cref="HeadersV1"/>/<see cref="TrailersV1"/>
    /// is enqueued as an object straight through to the H2 codec, eliminating the
    /// <c>HeadersV1.Encode → DecodeHeadersV1</c> round-trip.
    /// </summary>
    [Test, Explicit("Profile only — run via filter")]
    public void Profile_WriteSide_HeaderChain_PRB_ObjectPassthrough()
    {
        var clientHeaders = BuildClientHeaders();
        var serverHeaders = BuildServerHeaders();
        var trailers = BuildOkTrailers();

        var (ms, bytes) = MeasureLoop(() =>
        {
            var (cHpack, _) = HpackHeadersAdapter.EncodeHeaders(clientHeaders);
            ArrayPool<byte>.Shared.Return(cHpack);
            var (sHpack, _) = HpackHeadersAdapter.EncodeHeaders(serverHeaders);
            ArrayPool<byte>.Shared.Return(sHpack);
            var (tHpack, _) = HpackHeadersAdapter.EncodeTrailers(trailers);
            ArrayPool<byte>.Shared.Return(tHpack);
        });

        Report("Write-PRB-ObjPassthrough", ms, bytes);
    }

    /// <summary>
    /// Models the READ path that one Unary RPC consumes from the wire:
    /// server reads REQ HEADERS; client reads RESP HEADERS + TRAILERS.
    /// Each goes through:
    ///   <c>HpackHeadersAdapter.DecodeHeaders()</c> (HPACK → HeadersV1)
    ///   → <c>headersV1.Encode()</c> (Http2Codec.Read.EmitDecodedHeaders re-serializes
    ///      to custom-binary bytes so the existing frame queue can carry it)
    ///   → <c>HeadersV1.Decode()</c> (upper layer parses bytes back).
    /// </summary>
    [Test, Explicit("Profile only — run via filter")]
    public void Profile_ReadSide_HeaderChain_CurrentBaseline()
    {
        var clientHpack = EncodeHpack(BuildClientHeaders());
        var serverHpack = EncodeHpack(BuildServerHeaders());
        var trailersHpack = EncodeHpackTrailers(BuildOkTrailers());

        var (ms, bytes) = MeasureLoop(() =>
        {
            // REQ HEADERS off wire (server side)
            var v1 = HpackHeadersAdapter.DecodeHeaders(clientHpack);
            var (rebuf, rebufLen) = v1.Encode();
            var parsed = HeadersV1.Decode(rebuf.AsSpan(0, rebufLen));
            ArrayPool<byte>.Shared.Return(rebuf);

            // RESP HEADERS off wire (client side)
            var sV1 = HpackHeadersAdapter.DecodeHeaders(serverHpack);
            var (srebuf, srebufLen) = sV1.Encode();
            var sParsed = HeadersV1.Decode(srebuf.AsSpan(0, srebufLen));
            ArrayPool<byte>.Shared.Return(srebuf);

            // TRAILERS off wire (client side)
            var tV1 = HpackHeadersAdapter.DecodeTrailers(trailersHpack);
            var (trebuf, trebufLen) = tV1.Encode();
            var tParsed = TrailersV1.Decode(trebuf.AsSpan(0, trebufLen));
            ArrayPool<byte>.Shared.Return(trebuf);
        });

        Report("Read-Baseline", ms, bytes);
    }

    /// <summary>
    /// Simulates the PR-B READ path: codec attaches the already-decoded
    /// <see cref="HeadersV1"/>/<see cref="TrailersV1"/> to the inbound frame
    /// and the upper layer consumes it directly. No second Encode/Decode.
    /// </summary>
    [Test, Explicit("Profile only — run via filter")]
    public void Profile_ReadSide_HeaderChain_PRB_ObjectPassthrough()
    {
        var clientHpack = EncodeHpack(BuildClientHeaders());
        var serverHpack = EncodeHpack(BuildServerHeaders());
        var trailersHpack = EncodeHpackTrailers(BuildOkTrailers());

        var (ms, bytes) = MeasureLoop(() =>
        {
            var v1 = HpackHeadersAdapter.DecodeHeaders(clientHpack);
            var sV1 = HpackHeadersAdapter.DecodeHeaders(serverHpack);
            var tV1 = HpackHeadersAdapter.DecodeTrailers(trailersHpack);
        });

        Report("Read-PRB-ObjPassthrough", ms, bytes);
    }

    private static byte[] EncodeHpack(HeadersV1 h)
    {
        var (buf, len) = HpackHeadersAdapter.EncodeHeaders(h);
        try { return buf.AsSpan(0, len).ToArray(); }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    private static byte[] EncodeHpackTrailers(TrailersV1 t)
    {
        var (buf, len) = HpackHeadersAdapter.EncodeTrailers(t);
        try { return buf.AsSpan(0, len).ToArray(); }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    private static void Report(string label, long ms, long bytes)
    {
        var nsPerOp = (double)ms * 1_000_000 / Iterations;
        var bytesPerOp = (double)bytes / Iterations;
        TestContext.Out.WriteLine(
            $"[PROFILE {label}] {Iterations:N0} iter | {ms} ms total | " +
            $"{nsPerOp:F1} ns/op | {bytesPerOp:F1} B/op | {bytes:N0} B total");
    }
}
