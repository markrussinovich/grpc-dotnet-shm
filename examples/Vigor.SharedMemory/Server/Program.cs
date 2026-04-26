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

using Grpc.Health.V1;
using Grpc.AspNetCore.Server.SharedMemory;
using Microsoft.Extensions.Diagnostics.HealthChecks;

const string SegmentName = "vigor_shm_example";

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGrpc();
builder.Services.AddGrpcHealthChecks()
    .AddAsyncCheck("", () =>
    {
        var result = Random.Shared.Next() % 5 == 0
            ? HealthCheckResult.Unhealthy()
            : HealthCheckResult.Healthy();

        return Task.FromResult(result);
    }, Array.Empty<string>());
builder.Services.Configure<HealthCheckPublisherOptions>(options =>
{
    options.Delay = TimeSpan.Zero;
    options.Period = TimeSpan.FromSeconds(5);
});
builder.WebHost.UseSharedMemory(SegmentName, options =>
{
    options.RingCapacity = 1024 * 1024;
    options.MaxStreams = 100;
});

var app = builder.Build();
app.MapGrpcHealthChecksService();

app.Run();
