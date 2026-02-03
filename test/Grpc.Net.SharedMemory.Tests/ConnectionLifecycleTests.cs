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

using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Tests for connection lifecycle management.
/// Tests creation, disposal, GoAway, and draining scenarios.
/// </summary>
[TestFixture]
public class ConnectionLifecycleTests
{
    [Test]
    [Platform("Win")]
    public void CreateAsServer_ReturnsValidConnection()
    {
        var segmentName = $"lifecycle_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        
        Assert.That(server, Is.Not.Null);
        Assert.That(server.Name, Is.EqualTo(segmentName));
        Assert.That(server.IsClient, Is.False);
        Assert.That(server.IsClosed, Is.False);
    }

    [Test]
    [Platform("Win")]
    public void ConnectAsClient_ReturnsValidConnection()
    {
        var segmentName = $"lifecycle_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        Assert.That(client, Is.Not.Null);
        Assert.That(client.Name, Is.EqualTo(segmentName));
        Assert.That(client.IsClient, Is.True);
        Assert.That(client.IsClosed, Is.False);
    }

    [Test]
    [Platform("Win")]
    public void Dispose_ClosesConnection()
    {
        var segmentName = $"lifecycle_test_{Guid.NewGuid():N}";
        
        var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        server.Dispose();
        
        Assert.That(server.IsClosed, Is.True);
    }

    [Test]
    [Platform("Win")]
    public async Task DisposeAsync_ClosesConnection()
    {
        var segmentName = $"lifecycle_test_{Guid.NewGuid():N}";
        
        var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        await server.DisposeAsync();
        
        Assert.That(server.IsClosed, Is.True);
    }

    [Test]
    [Platform("Win")]
    public void Dispose_MultipleTimes_IsIdempotent()
    {
        var segmentName = $"lifecycle_test_{Guid.NewGuid():N}";
        
        var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        
        // Should not throw
        server.Dispose();
        server.Dispose();
        server.Dispose();
        
        Assert.That(server.IsClosed, Is.True);
    }

    [Test]
    [Platform("Win")]
    public void SendGoAway_SetsClosedFlag()
    {
        var segmentName = $"lifecycle_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        server.SendGoAway("test shutdown");
        
        Assert.That(server.IsClosed, Is.True);
    }

    [Test]
    [Platform("Win")]
    public void Drain_SetsIsDrainingFlag()
    {
        var segmentName = $"lifecycle_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        
        server.Drain();
        
        Assert.That(server.IsDraining, Is.True);
        Assert.That(server.IsClosed, Is.True); // Drain sends GoAway
    }

    [Test]
    [Platform("Win")]
    public void CreateStream_AfterGoAway_Throws()
    {
        var segmentName = $"lifecycle_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        client.SendGoAway("closing");
        
        Assert.Throws<InvalidOperationException>(() =>
        {
            client.CreateStream();
        });
    }

    [Test]
    [Platform("Win")]
    public void CreateStream_AfterDispose_Throws()
    {
        var segmentName = $"lifecycle_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        var client = ShmConnection.ConnectAsClient(segmentName);
        client.Dispose();
        
        Assert.Throws<ObjectDisposedException>(() =>
        {
            client.CreateStream();
        });
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public async Task GoAwayReceived_RaisesEvent()
    {
        var segmentName = $"lifecycle_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        var eventReceived = new TaskCompletionSource<GoAwayEventArgs>();
        client.GoAwayReceived += (sender, args) => eventReceived.TrySetResult(args);
        
        // Server sends GoAway
        server.SendGoAway("test");
        
        // Wait for client to receive it
        var args = await eventReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        
        Assert.That(args.Message, Is.EqualTo("test"));
    }

    [Test]
    [Platform("Win")]
    public void ConnectionSendQuota_InitialValue()
    {
        var segmentName = $"lifecycle_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        
        Assert.That(server.ConnectionSendQuota, Is.EqualTo(ShmConstants.InitialWindowSize));
    }

    [Test]
    [Platform("Win")]
    public void BdpEstimator_IsInitialized()
    {
        var segmentName = $"lifecycle_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        
        Assert.That(server.BdpEstimator, Is.Not.Null);
        Assert.That(server.BdpEstimator.CurrentBdp, Is.EqualTo(ShmConstants.InitialWindowSize));
    }

    [Test]
    [Platform("Win")]
    public void MultipleServerConnections_IndependentSegments()
    {
        var segment1 = $"lifecycle_test_1_{Guid.NewGuid():N}";
        var segment2 = $"lifecycle_test_2_{Guid.NewGuid():N}";
        
        using var server1 = ShmConnection.CreateAsServer(segment1, 4096, 10);
        using var server2 = ShmConnection.CreateAsServer(segment2, 4096, 10);
        
        Assert.That(server1.Name, Is.Not.EqualTo(server2.Name));
        
        // Dispose one, other should be unaffected
        server1.Dispose();
        
        Assert.That(server1.IsClosed, Is.True);
        Assert.That(server2.IsClosed, Is.False);
    }
}
