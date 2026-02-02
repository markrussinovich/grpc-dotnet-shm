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

#if WINDOWS

using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Grpc.Net.SharedMemory.Synchronization;

/// <summary>
/// Windows implementation of ring synchronization using named events.
/// Uses auto-reset events for cross-process signaling.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsRingSync : IRingSync
{
    private readonly SafeWaitHandle _dataEvent;
    private readonly SafeWaitHandle _spaceEvent;
    private readonly SafeWaitHandle _contigEvent;
    private readonly EventWaitHandle _dataWaitHandle;
    private readonly EventWaitHandle _spaceWaitHandle;
    private readonly EventWaitHandle _contigWaitHandle;
    private bool _disposed;

    public WindowsRingSync(string segmentName, string ringId, bool isServer)
    {
        var dataEventName = $"Global\\grpc_shm_{segmentName}_{ringId}_data";
        var spaceEventName = $"Global\\grpc_shm_{segmentName}_{ringId}_space";
        var contigEventName = $"Global\\grpc_shm_{segmentName}_{ringId}_contig";

        if (isServer)
        {
            // Server creates the events
            _dataWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, dataEventName);
            _spaceWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, spaceEventName);
            _contigWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, contigEventName);
        }
        else
        {
            // Client opens existing events
            _dataWaitHandle = EventWaitHandle.OpenExisting(dataEventName);
            _spaceWaitHandle = EventWaitHandle.OpenExisting(spaceEventName);
            _contigWaitHandle = EventWaitHandle.OpenExisting(contigEventName);
        }

        _dataEvent = _dataWaitHandle.SafeWaitHandle;
        _spaceEvent = _spaceWaitHandle.SafeWaitHandle;
        _contigEvent = _contigWaitHandle.SafeWaitHandle;
    }

    public bool WaitForData(uint expectedSeq, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        return WaitForEvent(_dataWaitHandle, timeout, cancellationToken);
    }

    public bool WaitForSpace(uint expectedSeq, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        return WaitForEvent(_spaceWaitHandle, timeout, cancellationToken);
    }

    public bool WaitForContig(uint expectedSeq, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        return WaitForEvent(_contigWaitHandle, timeout, cancellationToken);
    }

    private static bool WaitForEvent(EventWaitHandle handle, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var timeoutMs = timeout.HasValue ? (int)timeout.Value.TotalMilliseconds : Timeout.Infinite;

        if (cancellationToken.CanBeCanceled)
        {
            // Wait with cancellation support
            var waitHandles = new WaitHandle[] { handle, cancellationToken.WaitHandle };
            var result = WaitHandle.WaitAny(waitHandles, timeoutMs);
            
            // result == 0 means the event was signaled
            // result == 1 means cancellation was requested
            // result == WaitHandle.WaitTimeout means timeout
            return result == 0;
        }
        else
        {
            return handle.WaitOne(timeoutMs);
        }
    }

    public void SignalData()
    {
        _dataWaitHandle.Set();
    }

    public void SignalSpace()
    {
        _spaceWaitHandle.Set();
    }

    public void SignalContig()
    {
        _contigWaitHandle.Set();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _dataWaitHandle.Dispose();
        _spaceWaitHandle.Dispose();
        _contigWaitHandle.Dispose();
    }
}

#endif
