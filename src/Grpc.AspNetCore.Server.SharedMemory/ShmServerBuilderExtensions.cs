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

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Grpc.Net.SharedMemory;

namespace Grpc.AspNetCore.Server.SharedMemory;

/// <summary>
/// Extension methods for configuring the shared memory transport on Kestrel.
/// </summary>
/// <example>
/// <code>
/// var builder = WebApplication.CreateBuilder(args);
/// builder.Services.AddGrpc();
/// builder.WebHost.UseSharedMemory("my-service", options =>
/// {
///     options.RingCapacity = 1024 * 1024;
///     options.MaxStreams = 100;
/// });
///
/// var app = builder.Build();
/// app.MapGrpcService&lt;GreeterService&gt;();
/// app.Run();
/// </code>
/// </example>
public static class ShmServerBuilderExtensions
{
    /// <summary>
    /// Configures Kestrel to listen on a shared memory transport.
    /// This enables ASP.NET Core gRPC services to be served over shared memory
    /// using the standard <c>MapGrpcService&lt;T&gt;()</c> pattern.
    /// </summary>
    /// <param name="webHostBuilder">The web host builder.</param>
    /// <param name="segmentName">The shared memory segment name clients will connect to.</param>
    /// <param name="configure">Optional action to configure transport options.</param>
    /// <returns>The web host builder for chaining.</returns>
    /// <remarks>
    /// Equivalent to Go's <c>grpc.NewServer().Serve(shmListener)</c> — the transport
    /// changes but the gRPC service code is identical.
    /// </remarks>
    public static IWebHostBuilder UseSharedMemory(
        this IWebHostBuilder webHostBuilder,
        string segmentName,
        Action<ShmTransportOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(webHostBuilder);
        ArgumentNullException.ThrowIfNullOrEmpty(segmentName);

        var options = new ShmTransportOptions();
        configure?.Invoke(options);

        webHostBuilder.ConfigureKestrel(kestrelOptions =>
        {
            kestrelOptions.Listen(new ShmEndPoint(segmentName), listenOptions =>
            {
                // Use HTTP/2 prior-knowledge mode (h2c) — no TLS needed for local IPC.
                // Kestrel's HTTP/2 engine runs over the ShmStream byte transport.
                listenOptions.Protocols = HttpProtocols.Http2;
            });
        });

        webHostBuilder.ConfigureServices(services =>
        {
            services.AddSingleton(options);
            services.AddSingleton<IConnectionListenerFactory>(
                sp => new ShmConnectionListenerFactory(
                    sp.GetRequiredService<ShmTransportOptions>()));
        });

        return webHostBuilder;
    }
}
