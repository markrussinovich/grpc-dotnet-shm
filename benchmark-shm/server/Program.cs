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

using System.Globalization;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.SharedMemory;
using Grpc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace BenchmarkServer;

public class BenchmarkServiceImpl : BenchmarkService.BenchmarkServiceBase
{
    // Pre-allocated response payloads for common sizes
    private static readonly Dictionary<int, byte[]> PreallocatedPayloads = new();
    private static readonly object PayloadLock = new();

    public static byte[] GetOrCreatePayload(int size)
    {
        if (!PreallocatedPayloads.TryGetValue(size, out var payload))
        {
            lock (PayloadLock)
            {
                if (!PreallocatedPayloads.TryGetValue(size, out payload))
                {
                    payload = new byte[size];
                    PreallocatedPayloads[size] = payload;
                }
            }
        }
        return payload;
    }

    public override Task<SimpleResponse> UnaryCall(SimpleRequest request, ServerCallContext context)
    {
        var responseSize = request.ResponseSize;
        var payload = GetOrCreatePayload(responseSize);
        
        return Task.FromResult(new SimpleResponse
        {
            Payload = new Payload
            {
                Type = request.ResponseType,
                Body = ByteString.CopyFrom(payload)
            }
        });
    }

    public override async Task StreamingCall(
        IAsyncStreamReader<SimpleRequest> requestStream,
        IServerStreamWriter<SimpleResponse> responseStream,
        ServerCallContext context)
    {
        await foreach (var request in requestStream.ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
        {
            var responseSize = request.ResponseSize;
            var payload = GetOrCreatePayload(responseSize);
            
            await responseStream.WriteAsync(new SimpleResponse
            {
                Payload = new Payload
                {
                    Type = request.ResponseType,
                    Body = ByteString.CopyFrom(payload)
                }
            }).ConfigureAwait(false);
        }
    }
}

public class Program
{
    public static async Task Main(string[] args)
    {
        var tcpPort = 50051;
        var shmSegmentName = "grpc_bench";
        bool tcpOnly = false;
        bool shmOnly = false;

        // Parse command line args
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length)
            {
                tcpPort = int.Parse(args[++i], CultureInfo.InvariantCulture);
            }
            else if (args[i] == "--shm-segment" && i + 1 < args.Length)
            {
                shmSegmentName = args[++i];
            }
            else if (args[i] == "--tcp-only")
            {
                tcpOnly = true;
            }
            else if (args[i] == "--shm-only")
            {
                shmOnly = true;
            }
        }

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var tasks = new List<Task>();

        // Start TCP server using ASP.NET Core/Kestrel
        if (!shmOnly)
        {
            tasks.Add(StartTcpServerAsync(tcpPort, cts.Token));
        }

        // Start SHM server using ShmControlListener
        if (!tcpOnly)
        {
            tasks.Add(StartShmServerAsync(shmSegmentName, cts.Token));
        }

        Console.WriteLine("Benchmark server started");
        Console.WriteLine("Press Ctrl+C to stop...");

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Server stopped.");
        }
    }

    private static async Task StartTcpServerAsync(int port, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        
        builder.Services.AddGrpc();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(port, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http2;
            });
        });

        var app = builder.Build();
        app.MapGrpcService<BenchmarkServiceImpl>();

        Console.WriteLine($"TCP endpoint: http://0.0.0.0:{port}");
        
        await app.RunAsync(ct).ConfigureAwait(false);
    }

    private static async Task StartShmServerAsync(string segmentName, CancellationToken ct)
    {
        using var listener = new ShmControlListener(segmentName, ringCapacity: 2 * 1024 * 1024, maxStreams: 100);
        Console.WriteLine($"SHM endpoint: shm://{segmentName}");

        await foreach (var connection in listener.AcceptConnectionsAsync(ct).ConfigureAwait(false))
        {
            _ = HandleConnectionAsync(connection, ct);
        }
    }

    private static async Task HandleConnectionAsync(ShmConnection connection, CancellationToken ct)
    {
        try
        {
            await foreach (var stream in connection.AcceptStreamsAsync(ct).ConfigureAwait(false))
            {
                _ = HandleStreamAsync(stream, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Connection error: {ex.Message}");
        }
    }

    private static async Task HandleStreamAsync(ShmGrpcStream stream, CancellationToken ct)
    {
        var method = stream.RequestHeaders?.Method ?? "";
        
        try
        {
            if (method == "/grpc.testing.BenchmarkService/UnaryCall")
            {
                await HandleUnaryCallAsync(stream, ct).ConfigureAwait(false);
            }
            else if (method == "/grpc.testing.BenchmarkService/StreamingCall")
            {
                await HandleStreamingCallAsync(stream, ct).ConfigureAwait(false);
            }
            else
            {
                // Unknown method - send not implemented
                await stream.SendTrailersAsync(StatusCode.Unimplemented, $"Method not found: {method}").ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Stream error: {ex.Message}");
        }
    }

    private static async Task HandleUnaryCallAsync(ShmGrpcStream stream, CancellationToken ct)
    {
        // Read first message
        ReadOnlyMemory<byte> requestData = default;
        await foreach (var message in stream.ReceiveMessagesAsync(ct).ConfigureAwait(false))
        {
            requestData = message;
            break; // Only expect one message for unary
        }

        if (requestData.IsEmpty)
        {
            return;
        }

        var request = SimpleRequest.Parser.ParseFrom(requestData.Span);
        var responseSize = request.ResponseSize;
        var payload = BenchmarkServiceImpl.GetOrCreatePayload(responseSize);

        var response = new SimpleResponse
        {
            Payload = new Payload
            {
                Type = request.ResponseType,
                Body = ByteString.CopyFrom(payload)
            }
        };

        // Send response headers
        await stream.SendResponseHeadersAsync().ConfigureAwait(false);

        // Send response message
        await stream.SendMessageAsync(response.ToByteArray(), ct).ConfigureAwait(false);

        // Send trailers - signal this is the end (also removes stream from connection)
        await stream.SendTrailersAsync(StatusCode.OK).ConfigureAwait(false);
    }

    private static async Task HandleStreamingCallAsync(ShmGrpcStream stream, CancellationToken ct)
    {
        // Send response headers first
        await stream.SendResponseHeadersAsync().ConfigureAwait(false);

        // Read and respond to each message
        await foreach (var requestData in stream.ReceiveMessagesAsync(ct).ConfigureAwait(false))
        {
            var request = SimpleRequest.Parser.ParseFrom(requestData.Span);
            var responseSize = request.ResponseSize;
            var payload = BenchmarkServiceImpl.GetOrCreatePayload(responseSize);

            var response = new SimpleResponse
            {
                Payload = new Payload
                {
                    Type = request.ResponseType,
                    Body = ByteString.CopyFrom(payload)
                }
            };

            await stream.SendMessageAsync(response.ToByteArray(), ct).ConfigureAwait(false);
        }

        // Send trailers
        await stream.SendTrailersAsync(StatusCode.OK).ConfigureAwait(false);
    }
}
