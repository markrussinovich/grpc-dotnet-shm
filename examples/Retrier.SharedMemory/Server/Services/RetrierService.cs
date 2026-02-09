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
using Retry;

namespace Server.Services;

/// <summary>
/// Retrier service that simulates intermittent failures for retry demonstration.
/// </summary>
public class RetrierService : Retrier.RetrierBase
{
    private readonly Random _random = new();
    private const double DeliverySuccessRate = 0.5; // 50% success rate

    public override Task<Response> DeliverPackage(Package request, ServerCallContext context)
    {
        // Simulate intermittent failures to demonstrate retry behavior
        if (_random.NextDouble() > DeliverySuccessRate)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, $"Delivery failed for: {request.Name}"));
        }

        return Task.FromResult(new Response
        {
            Message = $"+ {request.Name}"
        });
    }
}
