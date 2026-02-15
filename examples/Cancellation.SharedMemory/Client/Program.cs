using Echo;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;

const string SegmentName = "cancellation_shm_example";

Console.WriteLine("Cancellation Example - Shared Memory Client");
Console.WriteLine();

using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
{
    HttpHandler = new ShmControlHandler(SegmentName),
    DisposeHttpClient = true
});

var client = new Echo.Echo.EchoClient(channel);

// Start a bidirectional streaming call
Console.WriteLine("Starting bidirectional streaming call...");
using var cts = new CancellationTokenSource();
using var call = client.BidirectionalStreamingEcho(cancellationToken: cts.Token);

try
{
    // Send a few messages
    for (int i = 1; i <= 3; i++)
    {
        var message = $"message {i}";
        Console.WriteLine($"Sending: {message}");
        await call.RequestStream.WriteAsync(new EchoRequest { Message = message });

        if (await call.ResponseStream.MoveNext(cts.Token))
        {
            Console.WriteLine($"Received: {call.ResponseStream.Current.Message}");
        }
    }

    // Cancel the stream after sending 3 messages
    Console.WriteLine("cancelling context");
    cts.Cancel();

    // Try to read after cancellation - should throw
    try
    {
        await call.ResponseStream.MoveNext(cts.Token);
    }
    catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
    {
        Console.WriteLine($"Caught expected cancellation: {ex.Status}");
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Stream was cancelled as expected");
}

Console.WriteLine();
Console.WriteLine("Cancellation example completed!");
Console.WriteLine("Press any key to exit...");
Console.ReadKey();
