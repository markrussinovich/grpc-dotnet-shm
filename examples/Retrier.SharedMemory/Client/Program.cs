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
using Grpc.Net.Client;
using Grpc.Net.SharedMemory;
using Retry;

Console.WriteLine("Retrier Client - Shared Memory Transport");
Console.WriteLine("=========================================");
Console.WriteLine();

const string SegmentName = "retrier_shm_example";

Console.WriteLine($"Connecting to shared memory segment: {SegmentName}");
Console.WriteLine("(Make sure the server is running first!)");
Console.WriteLine();

try
{
    // Configure retry policy for shared memory transport
    var retryPolicy = new ShmRetryPolicy
    {
        MaxAttempts = 5,
        InitialBackoff = TimeSpan.FromMilliseconds(500),
        MaxBackoff = TimeSpan.FromSeconds(5),
        BackoffMultiplier = 2.0,
        RetryableStatusCodes = new HashSet<StatusCode> { StatusCode.Unavailable }
    };

    // Create channel using shared memory handler
    using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
    {
        HttpHandler = new ShmHttpHandler(SegmentName),
        DisposeHttpClient = true
    });

    var client = new Retrier.RetrierClient(channel);

    Console.WriteLine("Delivering packages with retry policy:");
    Console.WriteLine($"  MaxAttempts: {retryPolicy.MaxAttempts}");
    Console.WriteLine($"  InitialBackoff: {retryPolicy.InitialBackoff.TotalMilliseconds}ms");
    Console.WriteLine($"  BackoffMultiplier: {retryPolicy.BackoffMultiplier}");
    Console.WriteLine();

    await DeliverPackagesWithRetry(client, retryPolicy);
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Make sure the server is running first:");
    Console.WriteLine("  cd examples/Retrier.SharedMemory/Server");
    Console.WriteLine("  dotnet run");
}

Console.WriteLine();
Console.WriteLine("Shutting down");
Console.WriteLine("Press any key to exit...");
Console.ReadKey();

static async Task DeliverPackagesWithRetry(Retrier.RetrierClient client, ShmRetryPolicy retryPolicy)
{
    var products = new[]
    {
        "Secrets of Silicon Valley",
        "The Busy Executive's Database Guide",
        "Emotional Security: A New Algorithm",
        "Prolonged Data Deprivation: Four Case Studies",
        "Cooking with Computers: Surreptitious Balance Sheets",
        "Silicon Valley Gastronomic Treats",
        "Sushi, Anyone?",
        "Fifty Years in Buckingham Palace Kitchens",
        "But Is It User Friendly?",
        "You Can Combat Computer Stress!"
    };

    Console.WriteLine("Delivering packages...");
    foreach (var product in products)
    {
        var delivered = false;
        var attempt = 0;

        while (!delivered && attempt < retryPolicy.MaxAttempts)
        {
            attempt++;
            try
            {
                var package = new Package { Name = product };
                var call = client.DeliverPackageAsync(package);
                var response = await call;

                // Success
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write(response.Message);
                Console.ResetColor();

                // Show retry count from headers
                var headers = await call.ResponseHeadersAsync;
                var previousAttempts = headers.GetValue("grpc-previous-rpc-attempts");
                if (previousAttempts != null)
                {
                    Console.Write($" (retry count: {previousAttempts})");
                }
                else if (attempt > 1)
                {
                    Console.Write($" (client retries: {attempt - 1})");
                }
                Console.WriteLine();

                delivered = true;
            }
            catch (RpcException ex) when (retryPolicy.ShouldRetry(ex.StatusCode) && attempt < retryPolicy.MaxAttempts)
            {
                // Retry with backoff
                var backoff = retryPolicy.CalculateBackoff(attempt);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  Attempt {attempt} failed ({ex.StatusCode}), retrying in {backoff.TotalMilliseconds:F0}ms...");
                Console.ResetColor();
                await Task.Delay(backoff);
            }
            catch (RpcException ex)
            {
                // Final failure
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"- {product} (failed after {attempt} attempts: {ex.Status.Detail})");
                Console.ResetColor();
                delivered = true; // Stop retrying
            }
        }

        await Task.Delay(TimeSpan.FromMilliseconds(200));
    }
}
