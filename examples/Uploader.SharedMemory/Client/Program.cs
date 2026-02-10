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

using Google.Protobuf;
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;
using Upload;

Console.WriteLine("File Uploader - Shared Memory Transport");
Console.WriteLine("========================================");
Console.WriteLine();

const string SegmentName = "uploader_shm_example";
const int ChunkSize = 32 * 1024; // 32 KB

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

    var client = new Uploader.UploaderClient(channel);

    Console.WriteLine("Starting upload call");
    var call = client.UploadFile();

    // Send file metadata first
    Console.WriteLine("Sending file metadata");
    await call.RequestStream.WriteAsync(new UploadFileRequest
    {
        Metadata = new FileMetadata
        {
            FileName = "sample.txt"
        }
    });

    // Stream file content
    var buffer = new byte[ChunkSize];
    await using var readStream = File.OpenRead("sample.txt");
    long totalBytesSent = 0;

    while (true)
    {
        var count = await readStream.ReadAsync(buffer);
        if (count == 0)
        {
            break;
        }

        Console.WriteLine($"Sending file data chunk of length {count}");
        await call.RequestStream.WriteAsync(new UploadFileRequest
        {
            Data = UnsafeByteOperations.UnsafeWrap(buffer.AsMemory(0, count))
        });
        totalBytesSent += count;
    }

    Console.WriteLine("Completing request stream");
    await call.RequestStream.CompleteAsync();

    var response = await call;
    Console.WriteLine();
    Console.WriteLine($"Upload complete. Total bytes sent: {totalBytesSent}");
    Console.WriteLine($"Upload ID: {response.Id}");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Make sure the server is running first:");
    Console.WriteLine("  cd examples/Uploader.SharedMemory/Server");
    Console.WriteLine("  dotnet run");
}

Console.WriteLine();
Console.WriteLine("Press any key to exit...");
Console.ReadKey();
