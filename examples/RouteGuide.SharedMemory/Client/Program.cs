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

using System.Net;
using Grpc.Net.SharedMemory;
using RouteGuide;

const string SegmentName = "routeguide_shm";

Console.WriteLine("RouteGuide Shared Memory Client");
Console.WriteLine($"Connecting to shm://{SegmentName}");
Console.WriteLine();

// Connect to the shared memory server
// This mirrors the Go: grpc.NewClient("shm://routeguide_shm", grpc.WithShmTransport())
using var connection = ShmConnection.ConnectAsClient(SegmentName);

Console.WriteLine("Connected to server");
Console.WriteLine();

// ============================================================
// 1. Unary RPC: GetFeature
// ============================================================
Console.WriteLine("=== GetFeature (Unary RPC) ===");

// Looking for a valid feature
await PrintFeature(connection, new Point { Latitude = 409146138, Longitude = -746188906 });

// Feature missing
await PrintFeature(connection, new Point { Latitude = 0, Longitude = 0 });

Console.WriteLine();

// ============================================================
// 2. Server Streaming RPC: ListFeatures
// ============================================================
Console.WriteLine("=== ListFeatures (Server Streaming RPC) ===");
Console.WriteLine("Looking for features between 40, -75 and 42, -73");

await PrintFeatures(connection, new Rectangle
{
    Lo = new Point { Latitude = 400000000, Longitude = -750000000 },
    Hi = new Point { Latitude = 420000000, Longitude = -730000000 }
});

Console.WriteLine();

// ============================================================
// 3. Client Streaming RPC: RecordRoute
// ============================================================
Console.WriteLine("=== RecordRoute (Client Streaming RPC) ===");

await RunRecordRoute(connection);

Console.WriteLine();

// ============================================================
// 4. Bidirectional Streaming RPC: RouteChat
// ============================================================
Console.WriteLine("=== RouteChat (Bidirectional Streaming RPC) ===");

await RunRouteChat(connection);

Console.WriteLine();
Console.WriteLine("All examples completed successfully!");

// ============================================================
// Helper Functions
// ============================================================

async Task PrintFeature(ShmConnection conn, Point point)
{
    var stream = conn.CreateStream();
    try
    {
        await stream.SendRequestHeadersAsync("/routeguide.RouteGuide/GetFeature", SegmentName);

        // Send point as request
        var requestBytes = point.ToByteArray();
        var framedRequest = FrameMessage(requestBytes);
        await stream.SendMessageAsync(framedRequest);
        await stream.SendTrailersAsync(Grpc.Core.StatusCode.OK); // Half-close

        // Receive response headers
        await stream.ReceiveFrameAsync();

        // Receive feature
        var frame = await stream.ReceiveFrameAsync();
        if (frame?.Type == FrameType.Data)
        {
            var feature = Feature.Parser.ParseFrom(frame.Value.Payload.AsSpan(5));
            if (string.IsNullOrEmpty(feature.Name))
            {
                Console.WriteLine($"Feature: name: \"\", point:({feature.Location.Latitude}, {feature.Location.Longitude})");
            }
            else
            {
                Console.WriteLine($"Feature: name: \"{feature.Name}\", point:({feature.Location.Latitude}, {feature.Location.Longitude})");
            }
        }
    }
    finally
    {
        stream.Dispose();
    }
}

async Task PrintFeatures(ShmConnection conn, Rectangle rect)
{
    var stream = conn.CreateStream();
    try
    {
        await stream.SendRequestHeadersAsync("/routeguide.RouteGuide/ListFeatures", SegmentName);

        // Send rectangle as request
        var requestBytes = rect.ToByteArray();
        await stream.SendMessageAsync(FrameMessage(requestBytes));
        await stream.SendTrailersAsync(Grpc.Core.StatusCode.OK);

        // Receive response headers
        await stream.ReceiveFrameAsync();

        // Receive stream of features
        var count = 0;
        while (true)
        {
            var frame = await stream.ReceiveFrameAsync();
            if (frame == null || frame.Value.Type == FrameType.Trailers)
                break;

            if (frame.Value.Type == FrameType.Data)
            {
                var feature = Feature.Parser.ParseFrom(frame.Value.Payload.AsSpan(5));
                Console.WriteLine($"  Feature: \"{feature.Name}\" at ({feature.Location.Latitude}, {feature.Location.Longitude})");
                count++;
            }
        }
        Console.WriteLine($"Listed {count} features");
    }
    finally
    {
        stream.Dispose();
    }
}

async Task RunRecordRoute(ShmConnection conn)
{
    var stream = conn.CreateStream();
    try
    {
        await stream.SendRequestHeadersAsync("/routeguide.RouteGuide/RecordRoute", SegmentName);

        // Generate random points
        var random = new Random(42);
        var pointCount = random.Next(5, 15);
        Console.WriteLine($"Traversing {pointCount} points.");

        for (int i = 0; i < pointCount; i++)
        {
            var point = new Point
            {
                Latitude = random.Next(400000000, 420000000),
                Longitude = random.Next(-750000000, -730000000)
            };
            await stream.SendMessageAsync(FrameMessage(point.ToByteArray()));
        }

        // Half-close to signal end of points
        await stream.SendTrailersAsync(Grpc.Core.StatusCode.OK);

        // Receive response headers
        await stream.ReceiveFrameAsync();

        // Receive summary
        var frame = await stream.ReceiveFrameAsync();
        if (frame?.Type == FrameType.Data)
        {
            var summary = RouteSummary.Parser.ParseFrom(frame.Value.Payload.AsSpan(5));
            Console.WriteLine($"Route summary: point_count:{summary.PointCount} feature_count:{summary.FeatureCount} distance:{summary.Distance} elapsed_time:{summary.ElapsedTime}");
        }
    }
    finally
    {
        stream.Dispose();
    }
}

async Task RunRouteChat(ShmConnection conn)
{
    var stream = conn.CreateStream();
    try
    {
        await stream.SendRequestHeadersAsync("/routeguide.RouteGuide/RouteChat", SegmentName);

        var notes = new[]
        {
            new RouteNote { Location = new Point { Latitude = 0, Longitude = 1 }, Message = "First message" },
            new RouteNote { Location = new Point { Latitude = 0, Longitude = 2 }, Message = "Second message" },
            new RouteNote { Location = new Point { Latitude = 0, Longitude = 3 }, Message = "Third message" },
            new RouteNote { Location = new Point { Latitude = 0, Longitude = 1 }, Message = "Fourth message" },
            new RouteNote { Location = new Point { Latitude = 0, Longitude = 2 }, Message = "Fifth message" },
            new RouteNote { Location = new Point { Latitude = 0, Longitude = 3 }, Message = "Sixth message" },
        };

        // Start a task to receive notes
        var receiveTask = Task.Run(async () =>
        {
            // Receive response headers
            await stream.ReceiveFrameAsync();

            while (true)
            {
                var frame = await stream.ReceiveFrameAsync();
                if (frame == null || frame.Value.Type == FrameType.Trailers)
                    break;

                if (frame.Value.Type == FrameType.Data)
                {
                    var note = RouteNote.Parser.ParseFrom(frame.Value.Payload.AsSpan(5));
                    Console.WriteLine($"Got message \"{note.Message}\" at point({note.Location.Latitude}, {note.Location.Longitude})");
                }
            }
        });

        // Send notes
        foreach (var note in notes)
        {
            await stream.SendMessageAsync(FrameMessage(note.ToByteArray()));
            await Task.Delay(100); // Small delay to interleave send/receive
        }

        // Half-close
        await stream.SendTrailersAsync(Grpc.Core.StatusCode.OK);

        // Wait for receive to complete
        await receiveTask;
    }
    finally
    {
        stream.Dispose();
    }
}

byte[] FrameMessage(byte[] message)
{
    var framed = new byte[5 + message.Length];
    framed[0] = 0; // Not compressed
    var lengthBytes = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(message.Length));
    Buffer.BlockCopy(lengthBytes, 0, framed, 1, 4);
    Buffer.BlockCopy(message, 0, framed, 5, message.Length);
    return framed;
}
