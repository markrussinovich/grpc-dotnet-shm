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
using Mail;

Console.WriteLine("Mailer Client - Shared Memory Bidirectional Streaming");
Console.WriteLine("======================================================");
Console.WriteLine();

var mailboxName = GetMailboxName(args);
const string SegmentName = "mailer_shm_example";

Console.WriteLine($"Creating client to mailbox '{mailboxName}'");
Console.WriteLine($"Connecting via shared memory segment: {SegmentName}");
Console.WriteLine();

try
{
    using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
    {
        HttpHandler = new ShmControlHandler(SegmentName),
        DisposeHttpClient = true
    });

    var client = new Mailer.MailerClient(channel);

    Console.WriteLine("Client created via shared memory transport");
    Console.WriteLine("Press escape to disconnect. Press any other key to forward mail.");

    using var call = client.Mailbox(headers: new Metadata { new Metadata.Entry("mailbox-name", mailboxName) });

    // Read responses in background task (bidirectional streaming)
    var responseTask = Task.Run(async () =>
    {
        await foreach (var message in call.ResponseStream.ReadAllAsync())
        {
            Console.ForegroundColor = message.Reason == MailboxMessage.Types.Reason.Received
                ? ConsoleColor.White
                : ConsoleColor.Green;
            Console.WriteLine();
            Console.WriteLine(message.Reason == MailboxMessage.Types.Reason.Received
                ? "Mail received"
                : "Mail forwarded");
            Console.WriteLine($"New mail: {message.New}, Forwarded mail: {message.Forwarded}");
            Console.ResetColor();
        }
    });

    // Send requests based on user input
    while (true)
    {
        var result = Console.ReadKey(intercept: true);
        if (result.Key == ConsoleKey.Escape)
        {
            break;
        }

        // Forward mail via shared memory bidirectional stream
        await call.RequestStream.WriteAsync(new ForwardMailMessage());
        Console.WriteLine("Forward request sent via shared memory...");
    }

    Console.WriteLine("Disconnecting...");
    await call.RequestStream.CompleteAsync();
    await responseTask;
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine();
    Console.WriteLine("Make sure the server is running first:");
    Console.WriteLine("  cd examples/Mailer.SharedMemory/Server");
    Console.WriteLine("  dotnet run");
}

Console.WriteLine("Disconnected. Press any key to exit.");
Console.ReadKey();

static string GetMailboxName(string[] args)
{
    if (args.Length < 1)
    {
        Console.WriteLine("No mailbox name provided. Using default name. Usage: dotnet run <name>.");
        return "DefaultMailbox";
    }

    return args[0];
}
