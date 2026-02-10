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
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;
using Race;

Console.WriteLine("Racer Client - Shared Memory Concurrent Streams");
Console.WriteLine("================================================");
Console.WriteLine();

const string SegmentName = "racer_shm_example";
var raceDuration = TimeSpan.FromSeconds(30);

Console.WriteLine($"Connecting via shared memory segment: {SegmentName}");
Console.WriteLine($"Race duration: {raceDuration.TotalSeconds} seconds");
Console.WriteLine("Press any key to start race...");
Console.ReadKey();

try
{
    using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
    {
        HttpHandler = new ShmControlHandler(SegmentName),
        DisposeHttpClient = true
    });

    var client = new Racer.RacerClient(channel);

    await BidirectionalStreamingRace(client, raceDuration);
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Make sure the server is running first:");
    Console.WriteLine("  cd examples/Racer.SharedMemory/Server");
    Console.WriteLine("  dotnet run");
}

Console.WriteLine("Finished");
Console.WriteLine("Press any key to exit...");
Console.ReadKey();

static async Task BidirectionalStreamingRace(Racer.RacerClient client, TimeSpan raceDuration)
{
    var headers = new Metadata { new Metadata.Entry("race-duration", raceDuration.ToString()) };

    Console.WriteLine();
    Console.WriteLine("Ready, set, go!");
    Console.WriteLine("Racing over shared memory transport for maximum throughput...");
    Console.WriteLine();

    using var call = client.ReadySetGo(new CallOptions(headers));
    var complete = false;

    // Read incoming messages in a background task (concurrent stream)
    RaceMessage? lastMessageReceived = null;
    var readTask = Task.Run(async () =>
    {
        await foreach (var message in call.ResponseStream.ReadAllAsync())
        {
            lastMessageReceived = message;
        }
    });

    // Write outgoing messages until timer is complete (concurrent stream)
    var sw = Stopwatch.StartNew();
    var sent = 0;

    // Report progress in real-time
    var reportTask = Task.Run(async () =>
    {
        while (!complete)
        {
            Console.WriteLine($"Messages sent: {sent:n0}");
            Console.WriteLine($"Messages received: {lastMessageReceived?.Count ?? 0:n0}");
            Console.WriteLine($"Elapsed: {sw.Elapsed.TotalSeconds:F1}s");

            await Task.Delay(TimeSpan.FromSeconds(1));

            if (!complete)
            {
                Console.SetCursorPosition(0, Console.CursorTop - 3);
            }
        }
    });

    // Send as fast as possible over shared memory
    while (sw.Elapsed < raceDuration)
    {
        await call.RequestStream.WriteAsync(new RaceMessage { Count = ++sent });
    }

    // Finish call and report results
    await call.RequestStream.CompleteAsync();
    await readTask;

    complete = true;
    await reportTask;

    Console.WriteLine();
    Console.WriteLine("=== RACE RESULTS ===");
    Console.WriteLine($"Total messages sent: {sent:n0}");
    Console.WriteLine($"Total messages received: {lastMessageReceived?.Count ?? 0:n0}");
    Console.WriteLine($"Messages/second (send): {sent / raceDuration.TotalSeconds:n0}");
    Console.WriteLine($"Messages/second (receive): {(lastMessageReceived?.Count ?? 0) / raceDuration.TotalSeconds:n0}");
    Console.WriteLine();
    Console.WriteLine("Note: Shared memory eliminates network overhead for maximum throughput!");
}
