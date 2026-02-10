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
using Grpc.Net.SharedMemory;
using Upload;

namespace Server.Services;

/// <summary>
/// Uploader service that receives streamed file content over shared memory.
/// </summary>
public class UploaderService
{
    private readonly string _uploadsPath;

    public UploaderService(string uploadsPath)
    {
        _uploadsPath = uploadsPath;
    }

    /// <summary>
    /// Receives a file upload from the client via streaming.
    /// </summary>
    public async Task<string> UploadFileAsync(ShmGrpcStream stream, CancellationToken cancellationToken)
    {
        var uploadId = Path.GetRandomFileName();
        var uploadPath = Path.Combine(_uploadsPath, uploadId);
        Directory.CreateDirectory(uploadPath);

        Console.WriteLine($"Starting upload: {uploadId}");

        await using var writeStream = File.Create(Path.Combine(uploadPath, "data.bin"));
        long totalBytesReceived = 0;

        // Read messages from the client stream using ReceiveFrameAsync
        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = await stream.ReceiveFrameAsync(cancellationToken);
            if (frame == null)
            {
                break; // End of stream
            }

            var (frameType, payload) = frame.Value;
            
            // Only process MESSAGE frames (DATA frames in HTTP/2 terminology)
            if (frameType != FrameType.Message || payload.Length == 0)
            {
                // HALF_CLOSE or other frame types indicate end of client stream
                if (frameType == FrameType.HalfClose)
                {
                    break;
                }
                continue;
            }

            var message = UploadFileRequest.Parser.ParseFrom(payload);

            if (message.Metadata != null)
            {
                Console.WriteLine($"Received metadata: {message.Metadata.FileName}");
                await File.WriteAllTextAsync(
                    Path.Combine(uploadPath, "metadata.json"), 
                    message.Metadata.ToString(),
                    cancellationToken);
            }

            if (message.Data != null && !message.Data.IsEmpty)
            {
                Console.WriteLine($"Received data chunk of {message.Data.Length} bytes");
                await writeStream.WriteAsync(message.Data.Memory, cancellationToken);
                totalBytesReceived += message.Data.Length;
            }
        }

        Console.WriteLine($"Upload complete: {uploadId}, total bytes: {totalBytesReceived}");
        Console.WriteLine($"Files saved to: {uploadPath}");

        return uploadId;
    }
}
