using Echo;
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;

const string SegmentName = "keepalive_shm_example";

Console.WriteLine("Keepalive Example - Shared Memory Client");
Console.WriteLine();
Console.WriteLine("Note: Shared memory transport uses OS-level process liveness");
Console.WriteLine("detection rather than application-level keepalive pings.");
Console.WriteLine("If the peer process exits, the connection is detected as broken.");
Console.WriteLine();

using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
{
    HttpHandler = new ShmControlHandler(SegmentName),
    DisposeHttpClient = true
});

var client = new Echo.Echo.EchoClient(channel);

// Send periodic echo pings to demonstrate connection is alive
for (int i = 1; i <= 5; i++)
{
    var reply = await client.UnaryEchoAsync(
        new EchoRequest { Message = $"keepalive ping {i}" });
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Ping {i}: {reply.Message}");
    await Task.Delay(TimeSpan.FromSeconds(2));
}

Console.WriteLine();
Console.WriteLine("Keepalive example completed!");
Console.WriteLine("Press any key to exit...");
Console.ReadKey();
