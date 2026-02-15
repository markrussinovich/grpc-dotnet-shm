using Echo;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;

const string SegmentName = "deadline_shm_example";

Console.WriteLine("Deadline Example - Shared Memory Client");
Console.WriteLine();

using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
{
    HttpHandler = new ShmControlHandler(SegmentName),
    DisposeHttpClient = true
});

var client = new Echo.Echo.EchoClient(channel);

// Test 1: Normal request with generous deadline - should succeed
await UnaryCallWithDeadline(client, 1, "world", TimeSpan.FromSeconds(5), StatusCode.OK);

// Test 2: Request that triggers server-side delay with short deadline - should fail
await UnaryCallWithDeadline(client, 2, "delay", TimeSpan.FromSeconds(1), StatusCode.DeadlineExceeded);

Console.WriteLine();
Console.WriteLine("All deadline tests completed!");
Console.WriteLine("Press any key to exit...");
Console.ReadKey();

static async Task UnaryCallWithDeadline(Echo.Echo.EchoClient client, int id, string message, TimeSpan deadline, StatusCode expected)
{
    try
    {
        var reply = await client.UnaryEchoAsync(
            new EchoRequest { Message = message },
            deadline: DateTime.UtcNow.Add(deadline));
        Console.WriteLine($"request {id}: wanted = {expected}, got = OK, response = \"{reply.Message}\"");
    }
    catch (RpcException ex)
    {
        Console.WriteLine($"request {id}: wanted = {expected}, got = {ex.StatusCode}");
    }
}
