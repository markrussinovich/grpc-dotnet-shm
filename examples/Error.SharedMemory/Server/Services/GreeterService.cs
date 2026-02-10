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
using Greet;
using Grpc.Core;
using Grpc.Net.SharedMemory;

namespace Server.Services;

/// <summary>
/// Greeter service with validation that demonstrates rich error details over shared memory.
/// </summary>
public class GreeterService
{
    /// <summary>
    /// Handles an incoming SayHello request with validation.
    /// </summary>
    public Task<byte[]> SayHelloAsync(ShmGrpcStream stream, byte[] requestData)
    {
        var request = HelloRequest.Parser.ParseFrom(requestData);

        Console.WriteLine($"Received greeting request with name: '{request.Name}'");

        // Validate the request
        GrpcValidation.ArgumentNotNullOrEmpty(request.Name);

        var response = new HelloReply
        {
            Message = $"Hello {request.Name}!"
        };

        return Task.FromResult(response.ToByteArray());
    }

    /// <summary>
    /// Routes an incoming gRPC method call to the appropriate handler.
    /// </summary>
    public Task<byte[]> HandleMethodAsync(ShmGrpcStream stream, string method, byte[] requestData)
    {
        return method switch
        {
            "/greet.Greeter/SayHello" => SayHelloAsync(stream, requestData),
            _ => throw new RpcException(new Status(StatusCode.Unimplemented, $"Method {method} is not implemented"))
        };
    }
}
