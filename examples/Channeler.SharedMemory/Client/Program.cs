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

using System.Text;
using DataChannel;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;

Console.WriteLine("Channeler Client - Shared Memory Multi-Threaded");
Console.WriteLine("================================================");
Console.WriteLine();

const string SegmentName = "channeler_shm_example";

Console.WriteLine($"Connecting via shared memory segment: {SegmentName}");
Console.WriteLine();

try
{
    using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
    {
        HttpHandler = new ShmControlHandler(SegmentName),
        DisposeHttpClient = true
    });

    var client = new DataChanneler.DataChannelerClient(channel);

    // Run multiple operations concurrently on different threads
    Console.WriteLine("Starting multi-threaded operations over shared memory...");
    Console.WriteLine();

    var tasks = new List<Task>
    {
        Task.Run(() => UploadDataAsync(client, "Thread-1", 1)),
        Task.Run(() => UploadDataAsync(client, "Thread-2", 2)),
        Task.Run(() => UploadDataAsync(client, "Thread-3", 3)),
        Task.Run(() => DownloadResultsAsync(client, "Thread-4")),
        Task.Run(() => DownloadResultsAsync(client, "Thread-5"))
    };

    await Task.WhenAll(tasks);

    Console.WriteLine();
    Console.WriteLine("All multi-threaded operations completed!");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Make sure the server is running first:");
    Console.WriteLine("  cd examples/Channeler.SharedMemory/Server");
    Console.WriteLine("  dotnet run");
}

Console.WriteLine("Shutting down");
Console.WriteLine("Press any key to exit...");
Console.ReadKey();

static async Task UploadDataAsync(DataChanneler.DataChannelerClient client, string threadName, int dataMultiplier)
{
    Console.WriteLine($"[{threadName}] Starting upload...");

    var call = client.UploadData();

    // Create test data with varying sizes per thread
    var testData = Encoding.UTF8.GetBytes($"Data from {threadName}: " + new string('X', 100 * dataMultiplier));
    var dataChunks = testData.Chunk(10);

    foreach (var chunk in dataChunks)
    {
        await call.RequestStream.WriteAsync(new DataRequest { Value = ByteString.CopyFrom(chunk) });
        await Task.Delay(10); // Simulate processing time
    }

    await call.RequestStream.CompleteAsync();

    var result = await call;
    Console.WriteLine($"[{threadName}] Upload complete: {result.BytesProcessed} bytes processed");
}

static async Task DownloadResultsAsync(DataChanneler.DataChannelerClient client, string threadName)
{
    Console.WriteLine($"[{threadName}] Starting download...");

    var testData = Encoding.UTF8.GetBytes($"Request from {threadName}");
    var call = client.DownloadResults(new DataRequest { Value = ByteString.CopyFrom(testData) });

    var totalBytes = 0;
    await foreach (var result in call.ResponseStream.ReadAllAsync())
    {
        totalBytes += result.BytesProcessed;
    }

    Console.WriteLine($"[{threadName}] Download complete: {totalBytes} total bytes received");
}
