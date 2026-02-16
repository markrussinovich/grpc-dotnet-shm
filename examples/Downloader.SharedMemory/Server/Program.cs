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
using Grpc.Net.SharedMemory;
using Download;
using Server.Services;

const string SegmentName = "downloader_shm_example";

Console.WriteLine("File Downloader - Shared Memory Server");
Console.WriteLine("=======================================");
Console.WriteLine($"Segment name: {SegmentName}");

// Create the downloader service
var downloaderService = new DownloaderService();

// Create SHM gRPC server (canonical WS3 hosting surface)
await using var server = new ShmGrpcServer(SegmentName, ringCapacity: 1024 * 1024, maxStreams: 100);
server.MapServerStreaming<DownloadFileRequest, DownloadFileResponse>(
    "/download.Downloader/DownloadFile", downloaderService.DownloadFileAsync);

Console.WriteLine("Server listening on shared memory segment: " + SegmentName);
Console.WriteLine("Press Ctrl+C to stop the server.");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    await server.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Server shutting down...");
}

Console.WriteLine("Server stopped.");
