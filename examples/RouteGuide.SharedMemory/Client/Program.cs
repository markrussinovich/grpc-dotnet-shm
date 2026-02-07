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

using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;
using RouteGuide;

const string SegmentName = "routeguide_shm";

Console.WriteLine("RouteGuide Shared Memory Client");
Console.WriteLine($"Connecting to shm://{SegmentName}");
Console.WriteLine();

// Create a gRPC channel using the SHM transport handler.
// This is the ONLY difference from a TCP client — the HttpHandler.
// Everything else (stubs, streaming, metadata) is identical.
using var handler = new ShmHandler(SegmentName);
using var channel = GrpcChannel.ForAddress("shm://localhost", new GrpcChannelOptions
{
    HttpHandler = handler
});
var client = new RouteGuide.RouteGuide.RouteGuideClient(channel);

Console.WriteLine("Connected to server");
Console.WriteLine();

// ============================================================
// 1. Unary RPC: GetFeature
// ============================================================
Console.WriteLine("=== GetFeature (Unary RPC) ===");

await PrintFeature(client, new Point { Latitude = 409146138, Longitude = -746188906 });
await PrintFeature(client, new Point { Latitude = 0, Longitude = 0 });

Console.WriteLine();

// ============================================================
// 2. Server Streaming RPC: ListFeatures
// ============================================================
Console.WriteLine("=== ListFeatures (Server Streaming RPC) ===");
Console.WriteLine("Looking for features between 40, -75 and 42, -73");

await PrintFeatures(client, new Rectangle
{
    Lo = new Point { Latitude = 400000000, Longitude = -750000000 },
    Hi = new Point { Latitude = 420000000, Longitude = -730000000 }
});

Console.WriteLine();

// ============================================================
// 3. Client Streaming RPC: RecordRoute
// ============================================================
Console.WriteLine("=== RecordRoute (Client Streaming RPC) ===");

await RunRecordRoute(client);

Console.WriteLine();

// ============================================================
// 4. Bidirectional Streaming RPC: RouteChat
// ============================================================
Console.WriteLine("=== RouteChat (Bidirectional Streaming RPC) ===");

await RunRouteChat(client);

Console.WriteLine();
Console.WriteLine("All examples completed successfully!");

// ============================================================
// Helper Functions — identical to what a TCP client would use
// ============================================================

async Task PrintFeature(RouteGuide.RouteGuide.RouteGuideClient c, Point point)
{
    var feature = await c.GetFeatureAsync(point);
    if (string.IsNullOrEmpty(feature.Name))
    {
        Console.WriteLine($"Feature: name: \"\", point:({feature.Location.Latitude}, {feature.Location.Longitude})");
    }
    else
    {
        Console.WriteLine($"Feature: name: \"{feature.Name}\", point:({feature.Location.Latitude}, {feature.Location.Longitude})");
    }
}

async Task PrintFeatures(RouteGuide.RouteGuide.RouteGuideClient c, Rectangle rect)
{
    var count = 0;
    using var call = c.ListFeatures(rect);
    await foreach (var feature in call.ResponseStream.ReadAllAsync())
    {
        Console.WriteLine($"  Feature: \"{feature.Name}\" at ({feature.Location.Latitude}, {feature.Location.Longitude})");
        count++;
    }
    Console.WriteLine($"Listed {count} features");
}

async Task RunRecordRoute(RouteGuide.RouteGuide.RouteGuideClient c)
{
    var random = new Random(42);
    var pointCount = random.Next(5, 15);
    Console.WriteLine($"Traversing {pointCount} points.");

    using var call = c.RecordRoute();

    for (int i = 0; i < pointCount; i++)
    {
        var point = new Point
        {
            Latitude = random.Next(400000000, 420000000),
            Longitude = random.Next(-750000000, -730000000)
        };
        await call.RequestStream.WriteAsync(point);
    }
    await call.RequestStream.CompleteAsync();

    var summary = await call.ResponseAsync;
    Console.WriteLine($"Route summary: point_count:{summary.PointCount} feature_count:{summary.FeatureCount} distance:{summary.Distance} elapsed_time:{summary.ElapsedTime}");
}

async Task RunRouteChat(RouteGuide.RouteGuide.RouteGuideClient c)
{
    var notes = new[]
    {
        new RouteNote { Location = new Point { Latitude = 0, Longitude = 1 }, Message = "First message" },
        new RouteNote { Location = new Point { Latitude = 0, Longitude = 2 }, Message = "Second message" },
        new RouteNote { Location = new Point { Latitude = 0, Longitude = 3 }, Message = "Third message" },
        new RouteNote { Location = new Point { Latitude = 0, Longitude = 1 }, Message = "Fourth message" },
        new RouteNote { Location = new Point { Latitude = 0, Longitude = 2 }, Message = "Fifth message" },
        new RouteNote { Location = new Point { Latitude = 0, Longitude = 3 }, Message = "Sixth message" },
    };

    using var call = c.RouteChat();

    // Start receiving in background
    var receiveTask = Task.Run(async () =>
    {
        await foreach (var note in call.ResponseStream.ReadAllAsync())
        {
            Console.WriteLine($"Got message \"{note.Message}\" at point({note.Location.Latitude}, {note.Location.Longitude})");
        }
    });

    // Send notes
    foreach (var note in notes)
    {
        await call.RequestStream.WriteAsync(note);
        await Task.Delay(100);
    }
    await call.RequestStream.CompleteAsync();

    await receiveTask;
}
