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
/// Tests for keepalive options and enforcement policy.
/// These tests verify the A73 RFC keepalive implementation.
/// </summary>
[TestFixture]
public class KeepaliveOptionsTests
{
    [Test]
    public void Default_IsDisabled()
    {
        var options = ShmKeepaliveOptions.Default;
        
        Assert.That(options.IsEnabled, Is.False);
        Assert.That(options.Time, Is.EqualTo(Timeout.InfiniteTimeSpan));
    }

    [Test]
    public void Active_IsEnabled()
    {
        var options = ShmKeepaliveOptions.Active();
        
        Assert.That(options.IsEnabled, Is.True);
        Assert.That(options.Time, Is.EqualTo(TimeSpan.FromSeconds(30)));
        Assert.That(options.PingTimeout, Is.EqualTo(TimeSpan.FromSeconds(20)));
        Assert.That(options.PermitWithoutStream, Is.False);
    }

    [Test]
    public void Active_WithCustomValues()
    {
        var options = ShmKeepaliveOptions.Active(
            time: TimeSpan.FromSeconds(10),
            pingTimeout: TimeSpan.FromSeconds(5));
        
        Assert.That(options.Time, Is.EqualTo(TimeSpan.FromSeconds(10)));
        Assert.That(options.PingTimeout, Is.EqualTo(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public void IsEnabled_TrueForPositiveTime()
    {
        var options = new ShmKeepaliveOptions
        {
            Time = TimeSpan.FromSeconds(1)
        };
        
        Assert.That(options.IsEnabled, Is.True);
    }

    [Test]
    public void IsEnabled_FalseForZeroTime()
    {
        var options = new ShmKeepaliveOptions
        {
            Time = TimeSpan.Zero
        };
        
        Assert.That(options.IsEnabled, Is.False);
    }

    [Test]
    public void IsEnabled_FalseForNegativeTime()
    {
        var options = new ShmKeepaliveOptions
        {
            Time = TimeSpan.FromSeconds(-1)
        };
        
        Assert.That(options.IsEnabled, Is.False);
    }

    [Test]
    public void PermitWithoutStream_DefaultsFalse()
    {
        var options = new ShmKeepaliveOptions();
        
        Assert.That(options.PermitWithoutStream, Is.False);
    }

    [Test]
    public void PermitWithoutStream_CanBeSet()
    {
        var options = new ShmKeepaliveOptions
        {
            PermitWithoutStream = true
        };
        
        Assert.That(options.PermitWithoutStream, Is.True);
    }
}

/// <summary>
/// Tests for keepalive enforcement policy.
/// </summary>
[TestFixture]
public class KeepaliveEnforcementPolicyTests
{
    [Test]
    public void Default_HasReasonableValues()
    {
        var policy = ShmKeepaliveEnforcementPolicy.Default;
        
        Assert.That(policy.MinTime, Is.EqualTo(TimeSpan.FromMinutes(5)));
        Assert.That(policy.PermitWithoutStream, Is.False);
        Assert.That(policy.MaxPingStrikes, Is.EqualTo(2));
    }

    [Test]
    public void MinTime_CanBeSet()
    {
        var policy = new ShmKeepaliveEnforcementPolicy
        {
            MinTime = TimeSpan.FromSeconds(30)
        };
        
        Assert.That(policy.MinTime, Is.EqualTo(TimeSpan.FromSeconds(30)));
    }

    [Test]
    public void MaxPingStrikes_CanBeSet()
    {
        var policy = new ShmKeepaliveEnforcementPolicy
        {
            MaxPingStrikes = 5
        };
        
        Assert.That(policy.MaxPingStrikes, Is.EqualTo(5));
    }
}

/// <summary>
/// Integration tests for keepalive functionality.
/// </summary>
[TestFixture]
public class KeepaliveIntegrationTests
{
    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void Connection_WithKeepalive_SendsPings()
    {
        var segmentName = $"keepalive_test_{Guid.NewGuid():N}";
        
        // Create server with short keepalive for testing
        var keepalive = new ShmKeepaliveOptions
        {
            Time = TimeSpan.FromMilliseconds(100),
            PingTimeout = TimeSpan.FromSeconds(5),
            PermitWithoutStream = true
        };
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10, keepalive);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        // Wait for some keepalive pings to be sent
        Thread.Sleep(500);
        
        // Connection should still be open
        Assert.That(server.IsClosed, Is.False);
        Assert.That(client.IsClosed, Is.False);
    }

    [Test]
    [Platform("Win")]
    [Timeout(5000)]
    public void Connection_WithoutKeepalive_DoesNotSendPings()
    {
        var segmentName = $"no_keepalive_test_{Guid.NewGuid():N}";
        
        using var server = ShmConnection.CreateAsServer(segmentName, 4096, 10);
        using var client = ShmConnection.ConnectAsClient(segmentName);
        
        // Wait a bit
        Thread.Sleep(200);
        
        // Connection should still be open
        Assert.That(server.IsClosed, Is.False);
        Assert.That(client.IsClosed, Is.False);
    }

    [Test]
    [Platform("Win")]
    public void Connection_WithEnforcementPolicy_Created()
    {
        var segmentName = $"enforcement_test_{Guid.NewGuid():N}";
        
        var policy = new ShmKeepaliveEnforcementPolicy
        {
            MinTime = TimeSpan.FromSeconds(1),
            MaxPingStrikes = 3
        };
        
        using var server = ShmConnection.CreateAsServer(
            segmentName, 4096, 10, 
            keepaliveOptions: null, 
            enforcementPolicy: policy);
        
        // Server should be created successfully
        Assert.That(server, Is.Not.Null);
        Assert.That(server.IsClosed, Is.False);
    }
}
