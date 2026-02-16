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

namespace Grpc.Net.SharedMemory.Synchronization;

internal sealed class MissingRingSync : IRingSync
{
    private readonly InvalidOperationException _exception;

    public MissingRingSync(string segmentName, string ringId, Exception innerException)
    {
        _exception = new InvalidOperationException(
            $"Synchronization primitives are unavailable for segment '{segmentName}' ring '{ringId}'. " +
            "This segment was opened with allowMissingSyncPrimitives=true and cannot perform blocking ring synchronization.",
            innerException);
    }

    public bool WaitForData(uint expectedSeq, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        throw _exception;
    }

    public bool WaitForSpace(uint expectedSeq, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        throw _exception;
    }

    public bool WaitForContig(uint expectedSeq, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        throw _exception;
    }

    public void SignalData()
    {
    }

    public void SignalSpace()
    {
    }

    public void SignalContig()
    {
    }

    public void Dispose()
    {
    }
}
