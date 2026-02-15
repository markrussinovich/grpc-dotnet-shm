using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;
using RouteGuide;

const string SegmentName = "routeguide_shm_example";

Console.WriteLine("RouteGuide Shared Memory Client");
Console.WriteLine();

using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
{
    HttpHandler = new ShmControlHandler(SegmentName),
    DisposeHttpClient = true
});

var client = new RouteGuide.RouteGuide.RouteGuideClient(channel);

// 1. Unary RPC: GetFeature
Console.WriteLine("=== GetFeature (Unary RPC) ===");
var feature1 = await client.GetFeatureAsync(new Point { Latitude = 409146138, Longitude = -746188906 });
Console.WriteLine($"Feature: name: \"{feature1.Name}\", point:({feature1.Location.Latitude}, {feature1.Location.Longitude})");

var feature2 = await client.GetFeatureAsync(new Point { Latitude = 0, Longitude = 0 });
Console.WriteLine($"Feature: name: \"{feature2.Name}\", point:({feature2.Location.Latitude}, {feature2.Location.Longitude})");
Console.WriteLine();

// 2. Server Streaming RPC: ListFeatures
Console.WriteLine("=== ListFeatures (Server Streaming RPC) ===");
Console.WriteLine("Looking for features between 40, -75 and 42, -73");
using var listCall = client.ListFeatures(new Rectangle
{
    Lo = new Point { Latitude = 400000000, Longitude = -750000000 },
    Hi = new Point { Latitude = 420000000, Longitude = -730000000 }
});
var count = 0;
await foreach (var f in listCall.ResponseStream.ReadAllAsync())
{
    Console.WriteLine($"  Feature: \"{f.Name}\" at ({f.Location.Latitude}, {f.Location.Longitude})");
    count++;
}
Console.WriteLine($"Listed {count} features");
Console.WriteLine();

// 3. Client Streaming RPC: RecordRoute
Console.WriteLine("=== RecordRoute (Client Streaming RPC) ===");
var random = new Random(42);
var pointCount = random.Next(5, 15);
Console.WriteLine($"Traversing {pointCount} points.");
using var recordCall = client.RecordRoute();
for (int i = 0; i < pointCount; i++)
{
    var point = new Point
    {
        Latitude = random.Next(400000000, 420000000),
        Longitude = random.Next(-750000000, -730000000)
    };
    await recordCall.RequestStream.WriteAsync(point);
}
await recordCall.RequestStream.CompleteAsync();
var summary = await recordCall;
Console.WriteLine($"Route summary: point_count:{summary.PointCount} feature_count:{summary.FeatureCount} distance:{summary.Distance} elapsed_time:{summary.ElapsedTime}");
Console.WriteLine();

// 4. Bidirectional Streaming RPC: RouteChat
Console.WriteLine("=== RouteChat (Bidirectional Streaming RPC) ===");
var notes = new[]
{
    new RouteNote { Location = new Point { Latitude = 0, Longitude = 1 }, Message = "First message" },
    new RouteNote { Location = new Point { Latitude = 0, Longitude = 2 }, Message = "Second message" },
    new RouteNote { Location = new Point { Latitude = 0, Longitude = 3 }, Message = "Third message" },
    new RouteNote { Location = new Point { Latitude = 0, Longitude = 1 }, Message = "Fourth message" },
    new RouteNote { Location = new Point { Latitude = 0, Longitude = 2 }, Message = "Fifth message" },
    new RouteNote { Location = new Point { Latitude = 0, Longitude = 3 }, Message = "Sixth message" },
};

using var chatCall = client.RouteChat();
var readTask = Task.Run(async () =>
{
    await foreach (var note in chatCall.ResponseStream.ReadAllAsync())
    {
        Console.WriteLine($"Got message \"{note.Message}\" at point({note.Location.Latitude}, {note.Location.Longitude})");
    }
});

foreach (var note in notes)
{
    await chatCall.RequestStream.WriteAsync(note);
    await Task.Delay(100);
}
await chatCall.RequestStream.CompleteAsync();
await readTask;

Console.WriteLine();
Console.WriteLine("All examples completed successfully!");
Console.WriteLine("Press any key to exit...");
Console.ReadKey();
