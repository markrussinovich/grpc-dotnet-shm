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
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

[TestFixture]
[Platform("Win")]
public class Http2BenchmarkPathTests
{
    [Test]
    [CancelAfter(20000)]
    public async Task UnaryCall_OverGrpcChannel_Works()
    {
        var segmentName = $"h2bench_{Guid.NewGuid():N}";

        await using var server = new ShmGrpcServer(segmentName, singleStreamMode: true);
        server.MapUnary<StringValue, StringValue>(
            "/test.Echo/Unary",
            (req, _) => Task.FromResult(new StringValue { Value = "echo:" + req.Value }));

        using var serverCts = new CancellationTokenSource();
        var serverTask = Task.Run(() => server.RunAsync(serverCts.Token));
        await WaitForServerAsync(segmentName);

        try
        {
            using var handler = new ShmControlHandler(segmentName, new ShmClientTransportOptions
            {
                EnableMultipleConnections = true,
                SingleStreamMode = true,
                ConnectTimeout = TimeSpan.FromSeconds(5),
            });

            using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
            {
                HttpHandler = handler,
                DisposeHttpClient = false,
                MaxReceiveMessageSize = 4 * 1024 * 1024,
                MaxSendMessageSize = 4 * 1024 * 1024,
            });

            var marshaller = Marshallers.Create<StringValue>(
                req => req.ToByteArray(),
                bytes => StringValue.Parser.ParseFrom(bytes));
            var method = new Method<StringValue, StringValue>(
                MethodType.Unary, "test.Echo", "Unary", marshaller, marshaller);

            var invoker = channel.CreateCallInvoker();
            var resp = await invoker.AsyncUnaryCall(method, host: null, new CallOptions(),
                new StringValue { Value = "hi" });

            Assert.That(resp.Value, Is.EqualTo("echo:hi"));
        }
        finally
        {
            serverCts.Cancel();
            try { await serverTask; } catch { }
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
