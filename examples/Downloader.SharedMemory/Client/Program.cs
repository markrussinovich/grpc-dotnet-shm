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

using Download;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;

Console.WriteLine("File Downloader - Shared Memory Transport");
Console.WriteLine("==========================================");
Console.WriteLine();

const string SegmentName = "downloader_shm_example";

Console.WriteLine($"Connecting to shared memory segment: {SegmentName}");
Console.WriteLine("(Make sure the server is running first!)");
Console.WriteLine();

try
{
    using var handler = new ShmHandler(SegmentName);
    using var channel = GrpcChannel.ForAddress("shm://localhost", new GrpcChannelOptions
    {
        HttpHandler = handler
    });

    var client = new Downloader.DownloaderClient(channel);

    var downloadsPath = Path.Combine(Environment.CurrentDirectory, "downloads");
    var downloadId = Path.GetRandomFileName();
    var downloadIdPath = Path.Combine(downloadsPath, downloadId);
    Directory.CreateDirectory(downloadIdPath);

    Console.WriteLine("Starting download call");

    using var call = client.DownloadFile(new DownloadFileRequest
    {
        Id = downloadId
    });

    await using var writeStream = File.Create(Path.Combine(downloadIdPath, "data.bin"));
    long totalBytes = 0;

    await foreach (var message in call.ResponseStream.ReadAllAsync())
    {
        if (message.Metadata != null)
        {
            Console.WriteLine("Saving metadata to file");
            var metadata = message.Metadata.ToString();
            await File.WriteAllTextAsync(Path.Combine(downloadIdPath, "metadata.json"), metadata);
        }
        if (message.Data != null)
        {
            var bytes = message.Data.Memory;
            Console.WriteLine($"Received {bytes.Length} bytes");
            await writeStream.WriteAsync(bytes);
            totalBytes += bytes.Length;
        }
    }

    Console.WriteLine();
    Console.WriteLine($"Downloaded {totalBytes} bytes total");
    Console.WriteLine("Files were saved in: " + downloadIdPath);
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Make sure the server is running first:");
    Console.WriteLine("  cd examples/Downloader.SharedMemory/Server");
    Console.WriteLine("  dotnet run");
}

Console.WriteLine();
Console.WriteLine("Press any key to exit...");
Console.ReadKey();
