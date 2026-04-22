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
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// End-to-end integration tests for <see cref="ShmConnectionPool"/> using
/// <see cref="ShmControlListener"/> + <see cref="ShmControlHandler"/>.
/// </summary>
[TestFixture]
[Platform("Win")]
[NonParallelizable]
public class ShmConnectionPoolE2ETests
{
    [Test]
    [CancelAfter(15000)]
    public async Task E2E_SingleConnDefault_UnaryWorks()
    {
        var segmentName = $"pool_e2e_{Guid.NewGuid():N}";
        using var listener = new ShmControlListener(segmentName, ringCapacity: 4096, maxStreams: 100);

        // Start server.
        var serverTask = Task.Run(async () =>
        {
            await foreach (var connection in listener.AcceptConnectionsAsync())
            {
                _ = HandleConnectionAsync(connection);
            }
        });

        // Create handler with default options (pool enabled).
        using var handler = new ShmControlHandler(segmentName);
        var pool = handler.Pool;

        // Send a unary "call" — send headers + message + half-close, receive headers + message + trailers.
        await PerformUnaryCallAsync(handler);

        Assert.That(pool!.ConnectionCount, Is.EqualTo(1));
        Assert.That(pool.TotalConnectionsCreated, Is.EqualTo(1));
    }

    [Test]
    [CancelAfter(30000)]
    public async Task E2E_MultiConn_ConcurrentUnary_AllSucceed()
    {
        var segmentName = $"pool_e2e_{Guid.NewGuid():N}";
        using var listener = new ShmControlListener(segmentName, ringCapacity: 65536, maxStreams: 5);

        // Start server.
        var serverCts = new CancellationTokenSource();
        var serverTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var connection in listener.AcceptConnectionsAsync(serverCts.Token))
                {
                    _ = HandleConnectionAsync(connection);
                }
            }
            catch (OperationCanceledException) { }
        });

        // Create handler with pool enabled.
        var options = new ShmClientTransportOptions
        {
            EnableMultipleConnections = true,
            RingCapacity = 65536,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };
        using var handler = new ShmControlHandler(segmentName, options);
        var pool = handler.Pool;

        // Fire 20 concurrent unary calls — with maxStreams=5 this should create multiple connections.
        var tasks = Enumerable.Range(0, 20).Select(_ =>
            Task.Run(() => PerformUnaryCallAsync(handler))
        ).ToArray();

        await Task.WhenAll(tasks);

        // Should have created multiple connections since maxStreams=5 < 20 concurrent.
        Assert.That(pool!.ConnectionCount, Is.GreaterThan(1),
            "Pool should have scaled up under concurrent load");
        Assert.That(pool.TotalConnectionsCreated, Is.GreaterThan(1));

        serverCts.Cancel();
    }

    [Test]
    [CancelAfter(15000)]
    public async Task E2E_MultiConn_ServerSees_MultipleConnections()
    {
        var segmentName = $"pool_e2e_{Guid.NewGuid():N}";
        using var listener = new ShmControlListener(segmentName, ringCapacity: 65536, maxStreams: 2);
        var connectionsAccepted = 0;

        var serverCts = new CancellationTokenSource();
        var serverTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var connection in listener.AcceptConnectionsAsync(serverCts.Token))
                {
                    Interlocked.Increment(ref connectionsAccepted);
                    _ = HandleConnectionAsync(connection);
                }
            }
            catch (OperationCanceledException) { }
        });

        var options = new ShmClientTransportOptions
        {
            EnableMultipleConnections = true,
            RingCapacity = 65536,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        using var handler = new ShmControlHandler(segmentName, options);

        // Fill connection 1 (maxStreams=2), force connection 2.
        var t1 = Task.Run(() => PerformUnaryCallAsync(handler));
        var t2 = Task.Run(() => PerformUnaryCallAsync(handler));
        var t3 = Task.Run(() => PerformUnaryCallAsync(handler));
        await Task.WhenAll(t1, t2, t3);

        // Server should have accepted > 1 CONNECT handshake.
        Assert.That(connectionsAccepted, Is.GreaterThan(1));

        serverCts.Cancel();
    }

    #region RingCapacity Negotiation

    [Test]
    [CancelAfter(15000)]
    public async Task E2E_RingCapacity_ClientPreference_UsedWhenSmallerThanServer()
    {
        // Server allows up to 65536, client prefers 4096 → negotiated = 4096
        var segmentName = $"pool_e2e_{Guid.NewGuid():N}";
        const ulong serverRing = 65536;
        const ulong clientRing = 4096;

        using var listener = new ShmControlListener(segmentName, ringCapacity: serverRing, maxStreams: 100);
        ShmConnection? serverSideConn = null;

        var serverCts = new CancellationTokenSource();
        var serverTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var connection in listener.AcceptConnectionsAsync(serverCts.Token))
                {
                    serverSideConn = connection;
                    _ = HandleConnectionAsync(connection);
                }
            }
            catch (OperationCanceledException) { }
        });

        var options = new ShmClientTransportOptions
        {
            EnableMultipleConnections = false,
            RingCapacity = clientRing,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        using var handler = new ShmControlHandler(segmentName, options);

        await PerformUnaryCallAsync(handler);

        // Verify the data connection uses the client's smaller ring capacity.
        Assert.That(serverSideConn, Is.Not.Null);
        Assert.That(serverSideConn!.TxRing.Capacity, Is.EqualTo(clientRing));
        Assert.That(serverSideConn!.RxRing.Capacity, Is.EqualTo(clientRing));

        serverCts.Cancel();
    }

    [Test]
    [CancelAfter(15000)]
    public async Task E2E_RingCapacity_ClientLargerThanServer_CappedByServer()
    {
        // Server allows 4096, client wants 65536 → negotiated = 4096
        var segmentName = $"pool_e2e_{Guid.NewGuid():N}";
        const ulong serverRing = 4096;
        const ulong clientRing = 65536;

        using var listener = new ShmControlListener(segmentName, ringCapacity: serverRing, maxStreams: 100);
        ShmConnection? serverSideConn = null;

        var serverCts = new CancellationTokenSource();
        var serverTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var connection in listener.AcceptConnectionsAsync(serverCts.Token))
                {
                    serverSideConn = connection;
                    _ = HandleConnectionAsync(connection);
                }
            }
            catch (OperationCanceledException) { }
        });

        var options = new ShmClientTransportOptions
        {
            EnableMultipleConnections = false,
            RingCapacity = clientRing,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        using var handler = new ShmControlHandler(segmentName, options);

        await PerformUnaryCallAsync(handler);

        // Verify the data connection uses the server's smaller ring capacity.
        Assert.That(serverSideConn, Is.Not.Null);
        Assert.That(serverSideConn!.TxRing.Capacity, Is.EqualTo(serverRing));

        serverCts.Cancel();
    }

    [Test]
    [CancelAfter(15000)]
    public async Task E2E_RingCapacity_ClientZero_UsesServerDefault()
    {
        // Client sends 0 (no preference) → server uses its default (8192)
        var segmentName = $"pool_e2e_{Guid.NewGuid():N}";
        const ulong serverRing = 8192;

        using var listener = new ShmControlListener(segmentName, ringCapacity: serverRing, maxStreams: 100);
        ShmConnection? serverSideConn = null;

        var serverCts = new CancellationTokenSource();
        var serverTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var connection in listener.AcceptConnectionsAsync(serverCts.Token))
                {
                    serverSideConn = connection;
                    _ = HandleConnectionAsync(connection);
                }
            }
            catch (OperationCanceledException) { }
        });

        var options = new ShmClientTransportOptions
        {
            EnableMultipleConnections = false,
            RingCapacity = 0,  // No preference
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        using var handler = new ShmControlHandler(segmentName, options);

        await PerformUnaryCallAsync(handler);

        // Verify the data connection uses the server's default ring capacity.
        Assert.That(serverSideConn, Is.Not.Null);
        Assert.That(serverSideConn!.TxRing.Capacity, Is.EqualTo(serverRing));

        serverCts.Cancel();
    }

    #endregion

    #region Helpers

    private static async Task PerformUnaryCallAsync(ShmControlHandler handler)
    {
        // Simulate what GrpcChannel.SendAsync does: create an HttpRequestMessage
        // with gRPC content and send it through the handler.
        var requestPayload = Encoding.UTF8.GetBytes("test request");
        var grpcFrame = new byte[5 + requestPayload.Length];
        grpcFrame[0] = 0; // not compressed
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            grpcFrame.AsSpan(1), (uint)requestPayload.Length);
        requestPayload.CopyTo(grpcFrame.AsSpan(5));

        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/test.Service/Method")
        {
            Version = new Version(2, 0),
            Content = new ByteArrayContent(grpcFrame)
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc");

        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        using var response = await invoker.SendAsync(request, CancellationToken.None);

        // Read the response body to trigger trailers.
        var body = await response.Content.ReadAsByteArrayAsync();
    }

    private static async Task HandleConnectionAsync(ShmConnection connection)
    {
        try
        {
            await foreach (var stream in connection.AcceptStreamsAsync())
            {
                _ = HandleStreamAsync(stream);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    private static async Task HandleStreamAsync(ShmGrpcStream stream)
    {
        try
        {
            // Read request message.
            await foreach (var msg in stream.ReceiveMessagesAsync())
            {
                break; // unary — one message
            }

            // Send response headers.
            await stream.SendResponseHeadersAsync();

            // Send response message.
            var responseData = Encoding.UTF8.GetBytes("test response");
            await stream.SendMessageAsync(responseData);

            // Send trailers.
            await stream.SendTrailersAsync(StatusCode.OK, "OK");
        }
        catch (Exception) { }
        finally
        {
            stream.Dispose();
        }
    }

    #endregion
}
