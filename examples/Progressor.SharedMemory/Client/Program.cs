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

using Client.ResponseProgress;
using Google.Protobuf.WellKnownTypes;
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;
using Progress;

Console.WriteLine("Progressor Client - Shared Memory Progress Reporting");
Console.WriteLine("=====================================================");
Console.WriteLine();

const string SegmentName = "progressor_shm_example";

Console.WriteLine($"Connecting via shared memory segment: {SegmentName}");
Console.WriteLine();

try
{
    using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
    {
        HttpHandler = new ShmControlHandler(SegmentName),
        DisposeHttpClient = true
    });

    var client = new Progressor.ProgressorClient(channel);

    Console.WriteLine("Running history operation with progress reporting...");
    Console.WriteLine();

    // Create progress reporter that updates console
    var progress = new Progress<int>(percent =>
    {
        // Draw progress bar
        Console.Write("\r[");
        var filled = percent / 5;
        Console.Write(new string('█', filled));
        Console.Write(new string('░', 20 - filled));
        Console.Write($"] {percent}%  ");
    });

    var result = await ServerStreamingCallWithProgress(client, progress);

    Console.WriteLine();
    Console.WriteLine();
    Console.WriteLine("Operation complete! Results:");
    Console.WriteLine("----------------------------");

    foreach (var item in result.Items)
    {
        Console.WriteLine($"  • {item}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Make sure the server is running first:");
    Console.WriteLine("  cd examples/Progressor.SharedMemory/Server");
    Console.WriteLine("  dotnet run");
}

Console.WriteLine();
Console.WriteLine("Shutting down");
Console.WriteLine("Press any key to exit...");
Console.ReadKey();

static ResponseProgress<HistoryResult, int> ServerStreamingCallWithProgress(
    Progressor.ProgressorClient client,
    IProgress<int> progress)
{
    var call = client.RunHistory(new Empty());
    return GrpcProgress.Create(call.ResponseStream, progress);
}
