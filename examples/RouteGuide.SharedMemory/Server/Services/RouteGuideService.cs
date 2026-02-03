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

using System.Diagnostics;
using RouteGuide;
using Grpc.Core;

namespace Server.Services;

/// <summary>
/// RouteGuide service implementation demonstrating all four RPC types.
/// Mirrors the Go grpc-go-shmem route_guide example.
/// </summary>
public class RouteGuideService : RouteGuide.RouteGuide.RouteGuideBase
{
    private readonly List<Feature> _features;
    private readonly Dictionary<string, List<RouteNote>> _routeNotes = new();
    private readonly object _lock = new();

    public RouteGuideService()
    {
        _features = LoadFeatures();
    }

    /// <summary>
    /// Unary RPC: Get a feature at a given point.
    /// </summary>
    public override Task<Feature> GetFeature(Point request, ServerCallContext context)
    {
        Console.WriteLine($"GetFeature: ({request.Latitude}, {request.Longitude})");
        
        foreach (var feature in _features)
        {
            if (feature.Location.Latitude == request.Latitude &&
                feature.Location.Longitude == request.Longitude)
            {
                return Task.FromResult(feature);
            }
        }

        // No feature found - return an unnamed feature at the given point
        return Task.FromResult(new Feature
        {
            Name = "",
            Location = request
        });
    }

    /// <summary>
    /// Server streaming RPC: List features within a rectangle.
    /// </summary>
    public override async Task ListFeatures(Rectangle request, IServerStreamWriter<Feature> responseStream, ServerCallContext context)
    {
        Console.WriteLine($"ListFeatures: ({request.Lo.Latitude}, {request.Lo.Longitude}) to ({request.Hi.Latitude}, {request.Hi.Longitude})");
        
        foreach (var feature in _features)
        {
            if (context.CancellationToken.IsCancellationRequested)
                break;

            if (InRange(feature.Location, request))
            {
                await responseStream.WriteAsync(feature);
            }
        }
    }

    /// <summary>
    /// Client streaming RPC: Record a route and return a summary.
    /// </summary>
    public override async Task<RouteSummary> RecordRoute(IAsyncStreamReader<Point> requestStream, ServerCallContext context)
    {
        Console.WriteLine("RecordRoute: Starting to receive points");
        
        var pointCount = 0;
        var featureCount = 0;
        var distance = 0;
        Point? prevPoint = null;
        var startTime = Stopwatch.GetTimestamp();

        await foreach (var point in requestStream.ReadAllAsync(context.CancellationToken))
        {
            pointCount++;

            // Check if there's a feature at this point
            foreach (var feature in _features)
            {
                if (feature.Location.Latitude == point.Latitude &&
                    feature.Location.Longitude == point.Longitude &&
                    !string.IsNullOrEmpty(feature.Name))
                {
                    featureCount++;
                    break;
                }
            }

            // Calculate distance
            if (prevPoint != null)
            {
                distance += CalcDistance(prevPoint, point);
            }
            prevPoint = point;
        }

        var elapsedTime = (int)Stopwatch.GetElapsedTime(startTime).TotalSeconds;
        Console.WriteLine($"RecordRoute: Finished, {pointCount} points, {featureCount} features, {distance}m");

        return new RouteSummary
        {
            PointCount = pointCount,
            FeatureCount = featureCount,
            Distance = distance,
            ElapsedTime = elapsedTime
        };
    }

    /// <summary>
    /// Bidirectional streaming RPC: Chat about route notes.
    /// </summary>
    public override async Task RouteChat(IAsyncStreamReader<RouteNote> requestStream, IServerStreamWriter<RouteNote> responseStream, ServerCallContext context)
    {
        Console.WriteLine("RouteChat: Starting bidirectional stream");

        await foreach (var note in requestStream.ReadAllAsync(context.CancellationToken))
        {
            var key = SerializePoint(note.Location);

            List<RouteNote> prevNotes;
            lock (_lock)
            {
                if (!_routeNotes.TryGetValue(key, out var notes))
                {
                    notes = new List<RouteNote>();
                    _routeNotes[key] = notes;
                }
                notes.Add(note);
                prevNotes = notes.ToList(); // Copy to avoid lock during write
            }

            // Send all notes at this location (including the one just added)
            foreach (var prevNote in prevNotes)
            {
                await responseStream.WriteAsync(prevNote);
            }
        }

        Console.WriteLine("RouteChat: Stream completed");
    }

    private static bool InRange(Point point, Rectangle rect)
    {
        var left = Math.Min(rect.Lo.Longitude, rect.Hi.Longitude);
        var right = Math.Max(rect.Lo.Longitude, rect.Hi.Longitude);
        var top = Math.Max(rect.Lo.Latitude, rect.Hi.Latitude);
        var bottom = Math.Min(rect.Lo.Latitude, rect.Hi.Latitude);

        return point.Longitude >= left &&
               point.Longitude <= right &&
               point.Latitude >= bottom &&
               point.Latitude <= top;
    }

    private static int CalcDistance(Point p1, Point p2)
    {
        const double R = 6371000; // Earth radius in meters
        var lat1 = ToRadians(p1.Latitude / 1e7);
        var lat2 = ToRadians(p2.Latitude / 1e7);
        var dLat = ToRadians((p2.Latitude - p1.Latitude) / 1e7);
        var dLon = ToRadians((p2.Longitude - p1.Longitude) / 1e7);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1) * Math.Cos(lat2) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return (int)(R * c);
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;

    private static string SerializePoint(Point point) => $"{point.Latitude},{point.Longitude}";

    private static List<Feature> LoadFeatures()
    {
        // Sample features matching the Go example
        return new List<Feature>
        {
            new Feature { Name = "Patriots Path, Mendham, NJ 07945, USA", Location = new Point { Latitude = 409146138, Longitude = -746188906 } },
            new Feature { Name = "101 New Jersey 10, Whippany, NJ 07981, USA", Location = new Point { Latitude = 408122808, Longitude = -743999179 } },
            new Feature { Name = "U.S. 6, Shohola, PA 18458, USA", Location = new Point { Latitude = 413628156, Longitude = -749015468 } },
            new Feature { Name = "5 Conners Road, Kingston, NY 12401, USA", Location = new Point { Latitude = 419999544, Longitude = -740371136 } },
            new Feature { Name = "Mid Hudson Psychiatric Center, New Hampton, NY 10958, USA", Location = new Point { Latitude = 414008389, Longitude = -743951297 } },
            new Feature { Name = "", Location = new Point { Latitude = 419611318, Longitude = -746524769 } },
            new Feature { Name = "287 Flugertown Road, Livingston Manor, NY 12758, USA", Location = new Point { Latitude = 406411633, Longitude = -741722051 } },
            new Feature { Name = "4001 Tremley Point Road, Linden, NJ 07036, USA", Location = new Point { Latitude = 406109563, Longitude = -742186778 } },
            new Feature { Name = "352 South Mountain Road, Wallkill, NY 12589, USA", Location = new Point { Latitude = 416802456, Longitude = -742370183 } },
            new Feature { Name = "Bailey Turn Road, Harriman, NY 10926, USA", Location = new Point { Latitude = 412950425, Longitude = -741077389 } },
            new Feature { Name = "193-199 Wawayanda Road, Hewitt, NJ 07421, USA", Location = new Point { Latitude = 412144655, Longitude = -743949739 } },
            new Feature { Name = "406-496 Ward Avenue, Pine Bush, NY 12566, USA", Location = new Point { Latitude = 415736605, Longitude = -742847522 } },
            new Feature { Name = "", Location = new Point { Latitude = 416851321, Longitude = -742674555 } },
            new Feature { Name = "3387 Richmond Terrace, Staten Island, NY 10303, USA", Location = new Point { Latitude = 406411633, Longitude = -741722051 } },
            new Feature { Name = "261 Van Sickle Road, Goshen, NY 10924, USA", Location = new Point { Latitude = 413069058, Longitude = -744597778 } },
        };
    }
}
