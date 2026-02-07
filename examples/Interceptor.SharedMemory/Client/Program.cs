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
using Echo;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;

// The ONLY transport difference from TCP is using ShmHandler instead of the default HttpHandler.
// All gRPC API usage (interceptors, streaming, metadata, etc.) remains identical.

const string SegmentName = "interceptor_shm";
const string FallbackToken = "some-secret-token";

Console.WriteLine("Interceptor Example - Shared Memory Client");
Console.WriteLine($"Connecting to shm://{SegmentName}");
Console.WriteLine();

try
{
    using var handler = new ShmHandler(SegmentName);
    using var channel = GrpcChannel.ForAddress("shm://localhost", new GrpcChannelOptions
    {
        HttpHandler = handler
    });

    // Wrap the call invoker with our logging interceptor
    var invoker = channel.Intercept(new LoggingInterceptor(FallbackToken));
    var client = new Echo.Echo.EchoClient(invoker);

    Console.WriteLine("Connected to server (with LoggingInterceptor)");
    Console.WriteLine();

    // Call unary echo through interceptor
    await CallUnaryEcho(client, "hello world");
    Console.WriteLine();

    // Call bidirectional streaming echo through interceptor
    await CallBidiStreamingEcho(client);
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Make sure the server is running first:");
    Console.WriteLine("  cd examples/Interceptor.SharedMemory/Server");
    Console.WriteLine("  dotnet run");
}

Console.WriteLine();
Console.WriteLine("Interceptor example completed!");

async Task CallUnaryEcho(Echo.Echo.EchoClient client, string message)
{
    Console.WriteLine($"Calling UnaryEcho with message: \"{message}\"");

    var response = await client.UnaryEchoAsync(new EchoRequest { Message = message });

    Console.WriteLine($"UnaryEcho response: {response.Message}");
}

async Task CallBidiStreamingEcho(Echo.Echo.EchoClient client)
{
    Console.WriteLine("Calling BidirectionalStreamingEcho");

    using var call = client.BidirectionalStreamingEcho();

    // Send 5 messages and read responses
    for (int i = 1; i <= 5; i++)
    {
        var message = $"Request {i}";
        await call.RequestStream.WriteAsync(new EchoRequest { Message = message });
        Console.WriteLine($"  Sent: {message}");

        if (await call.ResponseStream.MoveNext(CancellationToken.None))
        {
            Console.WriteLine($"  Received: {call.ResponseStream.Current.Message}");
        }
    }

    // Signal we're done sending
    await call.RequestStream.CompleteAsync();
}

/// <summary>
/// A client interceptor that logs RPC timing and injects an authorization header.
/// Works identically over shared memory or TCP — interceptors are transport-agnostic.
/// </summary>
sealed class LoggingInterceptor : Interceptor
{
    private readonly string _token;

    public LoggingInterceptor(string token)
    {
        _token = token;
    }

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        var start = DateTime.Now;
        Log("Starting unary RPC: {0}", context.Method.FullName);

        // Inject auth header
        var headers = context.Options.Headers ?? new Metadata();
        headers.Add("authorization", $"Bearer {_token}");
        var newOptions = context.Options.WithHeaders(headers);
        var newContext = new ClientInterceptorContext<TRequest, TResponse>(
            context.Method, context.Host, newOptions);

        var call = continuation(request, newContext);

        // Wrap the response to log completion
        var responseAsync = LogResponseAsync(call.ResponseAsync, context.Method.FullName, start);

        return new AsyncUnaryCall<TResponse>(
            responseAsync,
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            call.Dispose);
    }

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        Log("Starting streaming RPC: {0}", context.Method.FullName);

        // Inject auth header
        var headers = context.Options.Headers ?? new Metadata();
        headers.Add("authorization", $"Bearer {_token}");
        var newOptions = context.Options.WithHeaders(headers);
        var newContext = new ClientInterceptorContext<TRequest, TResponse>(
            context.Method, context.Host, newOptions);

        return continuation(newContext);
    }

    private static async Task<TResponse> LogResponseAsync<TResponse>(
        Task<TResponse> responseTask, string method, DateTime start)
    {
        var response = await responseTask;
        var end = DateTime.Now;
        Log("RPC: {0}, start time: {1}, end time: {2}",
            method,
            start.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            end.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
        return response;
    }

    private static void Log(string format, params object[] args)
    {
        Console.WriteLine("LOG:\t" + string.Format(CultureInfo.InvariantCulture, format, args));
    }
}
