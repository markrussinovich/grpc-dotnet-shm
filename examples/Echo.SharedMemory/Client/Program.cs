using Echo;
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;

const string SegmentName = "echo_shm_example";

Console.WriteLine("Echo Example - Shared Memory Client");
Console.WriteLine();

using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
{
    HttpHandler = new ShmControlHandler(SegmentName),
    DisposeHttpClient = true
});

var client = new Echo.Echo.EchoClient(channel);

// Unary echo
var reply = await client.UnaryEchoAsync(new EchoRequest { Message = "Hello SHM!" });
Console.WriteLine($"UnaryEcho: {reply.Message}");

// Bidirectional streaming echo
Console.WriteLine();
Console.WriteLine("Starting bidirectional streaming...");
using var call = client.BidirectionalStreamingEcho();
for (int i = 1; i <= 5; i++)
{
    await call.RequestStream.WriteAsync(new EchoRequest { Message = $"Message {i}" });
    if (await call.ResponseStream.MoveNext(CancellationToken.None))
    {
        Console.WriteLine($"BidiEcho: {call.ResponseStream.Current.Message}");
    }
}
await call.RequestStream.CompleteAsync();

Console.WriteLine();
Console.WriteLine("Echo example completed!");
Console.WriteLine("Press any key to exit...");
Console.ReadKey();
