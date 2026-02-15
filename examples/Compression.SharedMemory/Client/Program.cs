using Echo;
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;

const string SegmentName = "compression_shm_example";

Console.WriteLine("Compression Example - Shared Memory Client");
Console.WriteLine();
Console.WriteLine("Note: Shared memory transport doesn't need compression since");
Console.WriteLine("data is transferred via direct memory copy without network overhead.");
Console.WriteLine("This example demonstrates a standard gRPC echo over SHM.");
Console.WriteLine();

using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
{
    HttpHandler = new ShmControlHandler(SegmentName),
    DisposeHttpClient = true
});

var client = new Echo.Echo.EchoClient(channel);

// Send messages of various sizes to demonstrate throughput
foreach (var size in new[] { 10, 100, 1_000, 10_000 })
{
    var message = new string('A', size);
    var reply = await client.UnaryEchoAsync(new EchoRequest { Message = message });
    Console.WriteLine($"Sent {size} bytes, received {reply.Message.Length} bytes");
}

Console.WriteLine();
Console.WriteLine("Compression example completed!");
Console.WriteLine("Press any key to exit...");
Console.ReadKey();
