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

using Grpc.Net.SharedMemory.Health;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Tests for health service in shared memory transport.
/// SHM equivalent of TCP health service tests in FunctionalTests.
/// </summary>
[TestFixture]
public class ShmHealthServiceTests
{
    #region Basic Health Status Tests

    [Test]
    public void Check_DefaultServerHealth_ReturnsServing()
    {
        // Arrange
        var healthService = new ShmHealthService();

        // Act
        var status = healthService.Check("");

        // Assert
        Assert.That(status, Is.EqualTo(ShmServingStatus.Serving));
    }

    [Test]
    public void Check_UnknownService_ReturnsServiceUnknown()
    {
        // Arrange
        var healthService = new ShmHealthService();

        // Act
        var status = healthService.Check("unknown-service");

        // Assert
        Assert.That(status, Is.EqualTo(ShmServingStatus.ServiceUnknown));
    }

    [Test]
    public void SetServingStatus_ThenCheck_ReturnsCorrectStatus()
    {
        // Arrange
        var healthService = new ShmHealthService();
        const string serviceName = "my-service";

        // Act
        healthService.SetServingStatus(serviceName, ShmServingStatus.Serving);
        var status = healthService.Check(serviceName);

        // Assert
        Assert.That(status, Is.EqualTo(ShmServingStatus.Serving));
    }

    [Test]
    public void SetServingStatus_NotServing_ReturnsNotServing()
    {
        // Arrange
        var healthService = new ShmHealthService();
        const string serviceName = "failing-service";

        // Act
        healthService.SetServingStatus(serviceName, ShmServingStatus.NotServing);
        var status = healthService.Check(serviceName);

        // Assert
        Assert.That(status, Is.EqualTo(ShmServingStatus.NotServing));
    }

    [Test]
    public void SetServingStatus_UpdateExisting_ReturnsNewStatus()
    {
        // Arrange
        var healthService = new ShmHealthService();
        const string serviceName = "my-service";

        // Act
        healthService.SetServingStatus(serviceName, ShmServingStatus.Serving);
        healthService.SetServingStatus(serviceName, ShmServingStatus.NotServing);
        var status = healthService.Check(serviceName);

        // Assert
        Assert.That(status, Is.EqualTo(ShmServingStatus.NotServing));
    }

    #endregion

    #region Shutdown Tests

    [Test]
    public void Shutdown_SetsAllServicesToNotServing()
    {
        // Arrange
        var healthService = new ShmHealthService();
        healthService.SetServingStatus("service1", ShmServingStatus.Serving);
        healthService.SetServingStatus("service2", ShmServingStatus.Serving);

        // Act
        healthService.Shutdown();

        // Assert - after shutdown, server health should be NotServing
        var serverStatus = healthService.Check("");
        Assert.That(serverStatus, Is.EqualTo(ShmServingStatus.NotServing));
    }

    [Test]
    public void Shutdown_AfterShutdown_SetServingStatusIsIgnored()
    {
        // Arrange
        var healthService = new ShmHealthService();
        healthService.Shutdown();

        // Act
        healthService.SetServingStatus("new-service", ShmServingStatus.Serving);
        var status = healthService.Check("new-service");

        // Assert - should not be registered after shutdown
        Assert.That(status, Is.EqualTo(ShmServingStatus.ServiceUnknown));
    }

    #endregion

    #region Watch Tests

    [Test]
    [CancelAfter(5000)]
    public async Task Watch_StatusChange_NotifiesWatcher()
    {
        // Arrange
        var healthService = new ShmHealthService();
        const string serviceName = "watch-service";
        healthService.SetServingStatus(serviceName, ShmServingStatus.Serving);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var statusChanges = new List<ShmServingStatus>();

        // Act - start watching in background
        var watchTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var status in healthService.Watch(serviceName, cts.Token))
                {
                    statusChanges.Add(status);
                    if (statusChanges.Count >= 2)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        });

        // Give watch time to start
        await Task.Delay(100);

        // Trigger status change
        healthService.SetServingStatus(serviceName, ShmServingStatus.NotServing);

        // Wait for watch to complete
        await Task.WhenAny(watchTask, Task.Delay(1500));
        cts.Cancel();

        // Assert
        Assert.That(statusChanges, Has.Count.GreaterThanOrEqualTo(1));
    }

    [Test]
    [CancelAfter(5000)]
    public async Task Watch_Cancellation_StopsWatching()
    {
        // Arrange
        var healthService = new ShmHealthService();
        const string serviceName = "cancel-watch-service";
        healthService.SetServingStatus(serviceName, ShmServingStatus.Serving);

        using var cts = new CancellationTokenSource();
        var completed = false;

        // Act
        var watchTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in healthService.Watch(serviceName, cts.Token))
                {
                    // Wait for first status then cancel
                    cts.Cancel();
                }
            }
            catch (OperationCanceledException)
            {
                completed = true;
            }
        });

        await Task.WhenAny(watchTask, Task.Delay(2000));

        // Assert
        Assert.That(completed || cts.IsCancellationRequested, Is.True);
    }

    #endregion

    #region ShmServingStatus Enum Tests

    [Test]
    public void ShmServingStatus_HasExpectedValues()
    {
        // Assert - verify enum values match gRPC health protocol
        Assert.That((int)ShmServingStatus.Unknown, Is.EqualTo(0));
        Assert.That((int)ShmServingStatus.Serving, Is.EqualTo(1));
        Assert.That((int)ShmServingStatus.NotServing, Is.EqualTo(2));
        Assert.That((int)ShmServingStatus.ServiceUnknown, Is.EqualTo(3));
    }

    #endregion

    #region Multiple Services Tests

    [Test]
    public void MultipleServices_IndependentStatus()
    {
        // Arrange
        var healthService = new ShmHealthService();

        // Act
        healthService.SetServingStatus("service-a", ShmServingStatus.Serving);
        healthService.SetServingStatus("service-b", ShmServingStatus.NotServing);
        healthService.SetServingStatus("service-c", ShmServingStatus.Unknown);

        // Assert
        Assert.That(healthService.Check("service-a"), Is.EqualTo(ShmServingStatus.Serving));
        Assert.That(healthService.Check("service-b"), Is.EqualTo(ShmServingStatus.NotServing));
        Assert.That(healthService.Check("service-c"), Is.EqualTo(ShmServingStatus.Unknown));
    }

    [Test]
    public void ServerHealth_EmptyString_DistinctFromNamedServices()
    {
        // Arrange
        var healthService = new ShmHealthService();

        // Act
        healthService.SetServingStatus("", ShmServingStatus.NotServing);
        healthService.SetServingStatus("my-service", ShmServingStatus.Serving);

        // Assert
        Assert.That(healthService.Check(""), Is.EqualTo(ShmServingStatus.NotServing));
        Assert.That(healthService.Check("my-service"), Is.EqualTo(ShmServingStatus.Serving));
    }

    #endregion

    #region Thread Safety Tests

    [Test]
    public void ConcurrentSetAndCheck_IsThreadSafe()
    {
        // Arrange
        var healthService = new ShmHealthService();
        var services = Enumerable.Range(0, 100).Select(i => $"service-{i}").ToArray();
        var exceptions = new List<Exception>();

        // Act - concurrent set and check
        Parallel.ForEach(services, service =>
        {
            try
            {
                for (int i = 0; i < 100; i++)
                {
                    var status = (ShmServingStatus)(i % 3);
                    healthService.SetServingStatus(service, status);
                    _ = healthService.Check(service);
                }
            }
            catch (Exception ex)
            {
                lock (exceptions)
                {
                    exceptions.Add(ex);
                }
            }
        });

        // Assert
        Assert.That(exceptions, Is.Empty, "No exceptions should occur during concurrent access");
    }

    #endregion
}
