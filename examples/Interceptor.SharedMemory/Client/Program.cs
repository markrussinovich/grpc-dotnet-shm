using Echo;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;

const string SegmentName = "interceptor_shm_example";
const string FallbackToken = "some-secret-token";

Console.WriteLine("Interceptor Example - Shared Memory Client");
Console.WriteLine();

using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
{
    HttpHandler = new ShmControlHandler(SegmentName),
    DisposeHttpClient = true
});

// Wrap the channel with a client-side interceptor
var invoker = channel.Intercept(new ClientLoggerInterceptor());
var client = new Echo.Echo.EchoClient(invoker);

// Unary echo with interceptor
Console.WriteLine("--- Unary call ---");
var headers = new Metadata { { "authorization", $"Bearer {FallbackToken}" } };
var reply = await client.UnaryEchoAsync(
    new EchoRequest { Message = "hello world" },
    headers: headers);
Console.WriteLine($"UnaryEcho: {reply.Message}");

// Bidirectional streaming echo with interceptor
Console.WriteLine();
Console.WriteLine("--- Bidirectional streaming call ---");
using var call = client.BidirectionalStreamingEcho(headers: headers);
for (int i = 1; i <= 5; i++)
{
    await call.RequestStream.WriteAsync(new EchoRequest { Message = $"Request {i}" });
    if (await call.ResponseStream.MoveNext(CancellationToken.None))
    {
        Console.WriteLine($"BidiEcho: {call.ResponseStream.Current.Message}");
    }
}
await call.RequestStream.CompleteAsync();

Console.WriteLine();
Console.WriteLine("Interceptor example completed!");
Console.WriteLine("Press any key to exit...");
Console.ReadKey();

/// <summary>
/// Client-side interceptor that logs calls and adds authorization metadata.
/// </summary>
public class ClientLoggerInterceptor : Interceptor
{
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        Console.WriteLine($"LOG: Starting call {context.Method.FullName}");
        var call = continuation(request, context);
        Console.WriteLine($"LOG: Call started");
        return call;
    }

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        Console.WriteLine($"LOG: Starting streaming call {context.Method.FullName}");
        var call = continuation(context);
        Console.WriteLine($"LOG: Streaming call started");
        return call;
    }
}
