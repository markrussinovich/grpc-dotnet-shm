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

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Round-8 PR-C1 regression tests for the two pool-leak bugs the
/// dual-agent review surfaced (cf. the Go-side pool-leak findings the
/// user emphasized in the same review cycle):
///
/// <list type="number">
///   <item><c>ShmGrpcRequestStream</c> created in <c>ShmControlHandler.SendOnStreamAsync</c>
///     was never disposed, so any RPC that took the multi-fragment
///     <c>WriteAsync</c> path (custom HttpContent, marshaller bypass)
///     leaked the rented <c>_bodyBuf</c> back to GC instead of returning
///     it to <see cref="System.Buffers.ArrayPool{T}.Shared"/>.</item>
///   <item><c>ShmFrameWriter._deferred</c> entries parked by per-stream
///     send-quota exhaustion were not drained in <c>Dispose</c>, leaving
///     pooled <c>ReturnToPool</c> buffers stranded and any
///     <c>EnqueueZeroCopyAndWait</c> waiter blocked forever.</item>
/// </list>
/// </summary>
[TestFixture]
public class PoolLeakRegressionTests
{
    [Test]
    [Platform("Win")]
    [CancelAfter(20000)]
    public async Task SendBody_DisposesRequestStream_LiveCountReturnsToBaseline()
    {
        // Round-8 PR-C1 #1: every ShmGrpcRequestStream constructed inside
        // SendBodyAsync MUST be matched by a Dispose so _bodyBuf returns
        // to ArrayPool. The fix wraps the stream in `using` (ownership
        // moved from caller into SendBodyAsync). Without the fix this
        // test would observe LiveInstanceCount > baseline after 20
        // back-to-back unary RPCs and fail the closing assert.
        var segmentName = $"leak_reqstream_{Guid.NewGuid():N}";

        await using var server = new ShmGrpcServer(segmentName, singleStreamMode: true);
        server.MapUnary<BytesValue, BytesValue>(
            "/test.Echo/Unary",
            (req, _) => Task.FromResult(new BytesValue { Value = req.Value }));

        using var serverCts = new CancellationTokenSource();
        var serverTask = Task.Run(() => server.RunAsync(serverCts.Token));
        await WaitForServerAsync(segmentName);

        try
        {
            using var handler = new ShmControlHandler(segmentName, new ShmClientTransportOptions
            {
                EnableMultipleConnections = false,
                SingleStreamMode = true,
                ConnectTimeout = TimeSpan.FromSeconds(5),
            });

            using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
            {
                HttpHandler = handler,
                DisposeHttpClient = false,
            });

            var marshaller = Marshallers.Create<BytesValue>(
                req => req.ToByteArray(),
                bytes => BytesValue.Parser.ParseFrom(bytes));
            var method = new Method<BytesValue, BytesValue>(
                MethodType.Unary, "test.Echo", "Unary", marshaller, marshaller);
            var invoker = channel.CreateCallInvoker();

            var body = new byte[1024];
            new Random(42).NextBytes(body);
            var req = new BytesValue { Value = Google.Protobuf.ByteString.CopyFrom(body) };

            // Warm-up: amortise first-call setup (connection / control plane
            // also constructs some short-lived streams).
            _ = await invoker.AsyncUnaryCall(method, host: null, new CallOptions(), req);

            var baseline = ShmGrpcRequestStream.LiveInstanceCount;

            // Drive enough RPCs to make any per-RPC leak observable. With
            // the fix, every RPC's SendBodyAsync disposes its writeStream;
            // LiveInstanceCount stays at baseline (or transiently +1 while
            // a call is in flight, but always returns to baseline once the
            // call completes and SendBodyAsync's `using` runs).
            const int RpcCount = 20;
            for (var i = 0; i < RpcCount; i++)
            {
                var resp = await invoker.AsyncUnaryCall(method, host: null, new CallOptions(), req);
                Assert.That(resp.Value.Length, Is.EqualTo(1024));
            }

            // SendBodyAsync is fire-and-forget from the caller's view; the
            // dispose runs inside SendBodyAsync's `using` after CopyToAsync
            // returns. Give it a moment to settle and GC any pending state.
            await Task.Delay(200);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.That(ShmGrpcRequestStream.LiveInstanceCount, Is.EqualTo(baseline),
                "Every ShmGrpcRequestStream constructed by SendBodyAsync must be " +
                "disposed before SendBodyAsync returns. A non-zero delta means the " +
                "`using` in SendBodyAsync was removed or the call path stopped " +
                "going through SendBodyAsync entirely.");
        }
        finally
        {
            serverCts.Cancel();
            try { await serverTask; } catch { /* ignore */ }
        }
    }

    private static async Task WaitForServerAsync(string segmentName)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var _ = Segment.OpenControlSegment(segmentName);
                return;
            }
            catch (FileNotFoundException)
            {
                await Task.Delay(10);
            }
        }
        Assert.Fail($"Server did not create control segment for '{segmentName}'.");
    }
}
