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

using Grpc.Net.SharedMemory.LoadBalancing;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

[TestFixture]
public class ShmLoadBalancingTests
{
    [Test]
    public void EndpointCapability_Create_SetsProperties()
    {
        // Act
        var capability = ShmEndpointCapability.Create("test_segment", preferred: true, isLocal: true);

        // Assert
        Assert.That(capability.Enabled, Is.True);
        Assert.That(capability.SegmentName, Is.EqualTo("test_segment"));
        Assert.That(capability.Preferred, Is.True);
        Assert.That(capability.IsLocal, Is.True);
    }

    [Test]
    public void EndpointCapability_Disabled_HasEnabledFalse()
    {
        // Act
        var capability = ShmEndpointCapability.Disabled;

        // Assert
        Assert.That(capability.Enabled, Is.False);
    }

    [Test]
    public void EndpointAddress_IsShmAddress_DetectsScheme()
    {
        // Arrange
        var shmAddr = new ShmEndpointAddress { Address = "shm://test_segment" };
        var tcpAddr = new ShmEndpointAddress { Address = "localhost:50051" };

        // Assert
        Assert.That(shmAddr.IsShmAddress, Is.True);
        Assert.That(tcpAddr.IsShmAddress, Is.False);
    }

    [Test]
    public void EndpointAddress_GetSegmentName_ExtractsFromScheme()
    {
        // Arrange
        var addr = new ShmEndpointAddress { Address = "shm://my_segment" };

        // Act
        var segment = addr.GetSegmentName();

        // Assert
        Assert.That(segment, Is.EqualTo("my_segment"));
    }

    [Test]
    public void EndpointAddress_GetSegmentName_FallsBackToCapability()
    {
        // Arrange
        var addr = new ShmEndpointAddress 
        { 
            Address = "localhost:50051",
            ShmCapability = ShmEndpointCapability.Create("cap_segment")
        };

        // Act
        var segment = addr.GetSegmentName();

        // Assert
        Assert.That(segment, Is.EqualTo("cap_segment"));
    }

    [Test]
    public void ServicePolicy_Auto_UsesShmWhenAvailable()
    {
        // Arrange
        var policy = new ShmServicePolicy { Policy = ShmServicePolicy.Auto };

        // Assert
        Assert.That(policy.ShouldUseShm(true), Is.True);
        Assert.That(policy.ShouldUseShm(false), Is.False);
    }

    [Test]
    public void ServicePolicy_Disabled_NeverUsesShm()
    {
        // Arrange
        var policy = new ShmServicePolicy { Policy = ShmServicePolicy.Disabled };

        // Assert
        Assert.That(policy.ShouldUseShm(true), Is.False);
        Assert.That(policy.ShouldUseShm(false), Is.False);
    }

    [Test]
    public void ServicePolicy_Required_UsesShmWhenAvailable()
    {
        // Arrange
        var policy = new ShmServicePolicy { Policy = ShmServicePolicy.Required };

        // Assert
        Assert.That(policy.ShouldUseShm(true), Is.True);
        Assert.That(policy.ShouldUseShm(false), Is.False);
    }

    [Test]
    public void ServicePolicy_Validate_ThrowsOnInvalidPolicy()
    {
        // Arrange
        var policy = new ShmServicePolicy { Policy = "invalid" };

        // Assert
        Assert.Throws<InvalidOperationException>(() => policy.Validate());
    }

    [Test]
    public void TransportSelector_SelectsShm_ForShmCapableEndpoint()
    {
        // Arrange
        var selector = new ShmTransportSelector(new ShmServicePolicy { Policy = ShmServicePolicy.Auto });
        var endpoint = new ShmEndpointAddress 
        { 
            Address = "localhost:50051",
            ShmCapability = ShmEndpointCapability.Create("test")
        };

        // Act
        var result = selector.SelectTransport(endpoint);

        // Assert
        Assert.That(result, Is.EqualTo(ShmTransportPreference.PreferShm));
    }

    [Test]
    public void TransportSelector_SelectsNetwork_ForNonShmEndpoint()
    {
        // Arrange
        var selector = new ShmTransportSelector(new ShmServicePolicy { Policy = ShmServicePolicy.Auto });
        var endpoint = new ShmEndpointAddress { Address = "localhost:50051" };

        // Act
        var result = selector.SelectTransport(endpoint);

        // Assert
        Assert.That(result, Is.EqualTo(ShmTransportPreference.PreferNetwork));
    }

    [Test]
    public void TransportSelector_RequiresShm_ForRequiredPolicy()
    {
        // Arrange
        var selector = new ShmTransportSelector(new ShmServicePolicy { Policy = ShmServicePolicy.Required });
        var endpoint = new ShmEndpointAddress 
        { 
            Address = "shm://test",
            ShmCapability = ShmEndpointCapability.Create("test")
        };

        // Act
        var result = selector.SelectTransport(endpoint);

        // Assert
        Assert.That(result, Is.EqualTo(ShmTransportPreference.RequireShm));
    }
}

[TestFixture]
public class ShmPreferPickerTests
{
    [Test]
    public void Pick_PrefersShmEndpoints()
    {
        // Arrange
        var endpoints = new List<ShmEndpointAddress>
        {
            new() { Address = "localhost:50051" },
            new() { Address = "shm://preferred", ShmCapability = ShmEndpointCapability.Create("preferred") },
            new() { Address = "localhost:50052" }
        };
        var picker = new ShmPreferPicker(endpoints);

        // Act
        var result = picker.Pick(new ShmPickInfo { FullMethod = "/test/Method" });

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Address!.Address, Does.Contain("preferred"));
    }

    [Test]
    public void Pick_FallsBackToNetwork_WhenNoShm()
    {
        // Arrange
        var endpoints = new List<ShmEndpointAddress>
        {
            new() { Address = "localhost:50051" },
            new() { Address = "localhost:50052" }
        };
        var picker = new ShmPreferPicker(endpoints);

        // Act
        var result = picker.Pick(new ShmPickInfo { FullMethod = "/test/Method" });

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Address!.Address, Does.Contain("localhost"));
    }

    [Test]
    public void Pick_FailsWhenNoEndpoints()
    {
        // Arrange
        var picker = new ShmPreferPicker(Array.Empty<ShmEndpointAddress>());

        // Act
        var result = picker.Pick(new ShmPickInfo { FullMethod = "/test/Method" });

        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Error, Is.Not.Null);
    }

    [Test]
    public void Pick_RoundRobins_AcrossShmEndpoints()
    {
        // Arrange
        var endpoints = new List<ShmEndpointAddress>
        {
            new() { Address = "shm://segment1", ShmCapability = ShmEndpointCapability.Create("segment1") },
            new() { Address = "shm://segment2", ShmCapability = ShmEndpointCapability.Create("segment2") }
        };
        var picker = new ShmPreferPicker(endpoints);
        var info = new ShmPickInfo { FullMethod = "/test/Method" };

        // Act
        var addresses = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            var result = picker.Pick(info);
            addresses.Add(result.Address!.Address);
        }

        // Assert - should alternate between segments
        Assert.That(addresses, Does.Contain("shm://segment1"));
        Assert.That(addresses, Does.Contain("shm://segment2"));
    }
}

[TestFixture]
public class ShmPickResultTests
{
    [Test]
    public void Success_CreatesSuccessfulResult()
    {
        // Arrange
        var address = new ShmEndpointAddress { Address = "shm://test" };

        // Act
        var result = ShmPickResult.Success(address);

        // Assert
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Address, Is.SameAs(address));
        Assert.That(result.Error, Is.Null);
        Assert.That(result.Queue, Is.False);
    }

    [Test]
    public void Fail_CreatesFailedResult()
    {
        // Arrange
        var error = new InvalidOperationException("Test error");

        // Act
        var result = ShmPickResult.Fail(error);

        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Address, Is.Null);
        Assert.That(result.Error, Is.SameAs(error));
    }

    [Test]
    public void Queued_CreatesQueuedResult()
    {
        // Act
        var result = ShmPickResult.Queued;

        // Assert
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Queue, Is.True);
    }
}
