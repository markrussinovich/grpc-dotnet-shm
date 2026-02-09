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

using Count;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Server.Services;

/// <summary>
/// Counter service implementation for shared memory transport.
/// </summary>
public class CounterService : Counter.CounterBase
{
    private readonly IncrementingCounter _counter;

    public CounterService(IncrementingCounter counter)
    {
        _counter = counter;
    }

    public override Task<CounterReply> IncrementCount(Empty request, ServerCallContext context)
    {
        _counter.Increment(1);
        Console.WriteLine($"  IncrementCount: count is now {_counter.Count}");

        return Task.FromResult(new CounterReply { Count = _counter.Count });
    }

    public override async Task<CounterReply> AccumulateCount(IAsyncStreamReader<CounterRequest> requestStream, ServerCallContext context)
    {
        await foreach (var message in requestStream.ReadAllAsync())
        {
            Console.WriteLine($"  AccumulateCount: received {message.Count}");
            _counter.Increment(message.Count);
        }

        Console.WriteLine($"  AccumulateCount: final count is {_counter.Count}");
        return new CounterReply { Count = _counter.Count };
    }

    public override async Task Countdown(Empty request, IServerStreamWriter<CounterReply> responseStream, ServerCallContext context)
    {
        Console.WriteLine($"  Countdown: starting from {_counter.Count}");

        for (var i = _counter.Count; i >= 0; i--)
        {
            await responseStream.WriteAsync(new CounterReply { Count = i });
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        Console.WriteLine("  Countdown: complete");
    }
}
