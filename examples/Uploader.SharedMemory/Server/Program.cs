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
using Grpc.Core;
using Grpc.Net.SharedMemory;
using Server.Services;
using Upload;

const string SegmentName = "uploader_shm_example";

Console.WriteLine("File Uploader - Shared Memory Server");
Console.WriteLine("=====================================");
Console.WriteLine($"Segment name: {SegmentName}");

// Create uploads directory
var uploadsPath = Path.Combine(Environment.CurrentDirectory, "uploads");
Directory.CreateDirectory(uploadsPath);

// Create the uploader service
var uploaderService = new UploaderService(uploadsPath);

// Create the shared memory listener using ShmControlListener for grpc-go-shmem compatibility
using var listener = new ShmControlListener(SegmentName, ringCapacity: 1024 * 1024, maxStreams: 100);
Console.WriteLine("Server listening on shared memory segment: " + SegmentName);
Console.WriteLine($"Uploads will be saved to: {uploadsPath}");
Console.WriteLine("Press Ctrl+C to stop the server.");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    await foreach (var connection in listener.AcceptConnectionsAsync(cts.Token))
    {
        Console.WriteLine($"New connection accepted: {connection.Name}");

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var stream in connection.AcceptStreamsAsync(cts.Token))
                {
                    try
                    {
                        var headers = stream.RequestHeaders;
                        if (headers?.Method is { } method)
                        {
                            Console.WriteLine($"Received request for method: {method}");

                            if (method == "/upload.Uploader/UploadFile")
                            {
                                await stream.SendResponseHeadersAsync();
                                var uploadId = await uploaderService.UploadFileAsync(stream, cts.Token);
                                
                                // Send response
                                var response = new Upload.UploadFileResponse { Id = uploadId };
                                await stream.SendMessageAsync(response.ToByteArray());
                                await stream.SendTrailersAsync(StatusCode.OK);
                            }
                            else
                            {
                                throw new RpcException(new Status(StatusCode.Unimplemented, $"Method {method} is not implemented"));
                            }
                        }
                    }
                    catch (RpcException ex)
                    {
                        Console.WriteLine($"RPC error: {ex.Status.StatusCode} - {ex.Status.Detail}");
                        await stream.SendTrailersAsync(ex.Status.StatusCode, ex.Status.Detail);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error: {ex.Message}");
                        await stream.SendTrailersAsync(StatusCode.Internal, ex.Message);
                    }
                }
            }
            catch (OperationCanceledException) { }
        });
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Server shutting down...");
}

Console.WriteLine("Server stopped.");
