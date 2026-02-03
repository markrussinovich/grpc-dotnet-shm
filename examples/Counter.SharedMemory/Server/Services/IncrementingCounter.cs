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

namespace Server.Services;

/// <summary>
/// Simple counter that can be incremented.
/// </summary>
public class IncrementingCounter
{
    private int _count;

    public void Increment(int amount)
    {
        Interlocked.Add(ref _count, amount);
    }

    public int Count => _count;
}
