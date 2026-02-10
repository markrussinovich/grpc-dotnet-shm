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

using Echo;
using Grpc.Core;

namespace Server;

public class EchoService : Echo.Echo.EchoBase
{
    private readonly ILogger<EchoService> _logger;

    public EchoService(ILogger<EchoService> logger)
    {
        _logger = logger;
    }

    public override Task<EchoResponse> UnaryEcho(EchoRequest request, ServerCallContext context)
    {
        _logger.LogInformation("UnaryEcho: {Message}", request.Message);
        return Task.FromResult(new EchoResponse { Message = request.Message });
    }

    public override async Task BidirectionalStreamingEcho(
        IAsyncStreamReader<EchoRequest> requestStream,
        IServerStreamWriter<EchoResponse> responseStream,
        ServerCallContext context)
    {
        await foreach (var req in requestStream.ReadAllAsync())
        {
            _logger.LogInformation("BidirectionalStreamingEcho: {Message}", req.Message);
            await responseStream.WriteAsync(new EchoResponse { Message = req.Message });
        }
    }
}
