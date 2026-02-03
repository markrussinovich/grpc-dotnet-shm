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

namespace Grpc.Net.SharedMemory.Health;

/// <summary>
/// Health check service for shared memory transport.
/// Modeled on grpc-go health package.
/// </summary>
public interface IShmHealthService
{
    /// <summary>
    /// Gets the health status of a service.
    /// </summary>
    /// <param name="serviceName">The service name (empty string for server health).</param>
    /// <returns>The serving status.</returns>
    ShmServingStatus Check(string serviceName);

    /// <summary>
    /// Watches the health status of a service.
    /// </summary>
    /// <param name="serviceName">The service name to watch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An async enumerable of health status updates.</returns>
    IAsyncEnumerable<ShmServingStatus> Watch(string serviceName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Serving status for health checks.
/// </summary>
public enum ShmServingStatus
{
    /// <summary>
    /// Unknown status.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Service is serving.
    /// </summary>
    Serving = 1,

    /// <summary>
    /// Service is not serving.
    /// </summary>
    NotServing = 2,

    /// <summary>
    /// Service exists but status is unknown.
    /// </summary>
    ServiceUnknown = 3
}

/// <summary>
/// Default implementation of health service for shared memory transport.
/// Modeled on grpc-go health.Server.
/// </summary>
public sealed class ShmHealthService : IShmHealthService
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ShmServingStatus> _statusMap = new();
    private readonly Dictionary<string, List<Action<ShmServingStatus>>> _watchers = new();
    private bool _shutdown;

    /// <summary>
    /// Creates a new health service.
    /// </summary>
    public ShmHealthService()
    {
        // Default to serving for the empty string (server health)
        _statusMap[""] = ShmServingStatus.Serving;
    }

    /// <summary>
    /// Sets the serving status for a service.
    /// </summary>
    /// <param name="serviceName">The service name (empty string for server health).</param>
    /// <param name="status">The serving status.</param>
    public void SetServingStatus(string serviceName, ShmServingStatus status)
    {
        List<Action<ShmServingStatus>>? watchers = null;

        lock (_lock)
        {
            if (_shutdown)
            {
                return;
            }

            _statusMap[serviceName] = status;

            if (_watchers.TryGetValue(serviceName, out var w))
            {
                watchers = w.ToList();
            }
        }

        // Notify watchers outside the lock
        if (watchers != null)
        {
            foreach (var watcher in watchers)
            {
                try
                {
                    watcher(status);
                }
                catch
                {
                    // Ignore watcher errors
                }
            }
        }
    }

    /// <summary>
    /// Shuts down the health service, setting all services to NotServing.
    /// </summary>
    public void Shutdown()
    {
        List<(string, List<Action<ShmServingStatus>>)>? watchers = null;

        lock (_lock)
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;

            // Set all services to NotServing
            watchers = new();
            foreach (var serviceName in _statusMap.Keys.ToList())
            {
                _statusMap[serviceName] = ShmServingStatus.NotServing;

                if (_watchers.TryGetValue(serviceName, out var w))
                {
                    watchers.Add((serviceName, w.ToList()));
                }
            }
        }

        // Notify watchers outside the lock
        if (watchers != null)
        {
            foreach (var (_, ws) in watchers)
            {
                foreach (var watcher in ws)
                {
                    try
                    {
                        watcher(ShmServingStatus.NotServing);
                    }
                    catch
                    {
                        // Ignore watcher errors
                    }
                }
            }
        }
    }

    /// <summary>
    /// Resumes the health service after a shutdown.
    /// </summary>
    public void Resume()
    {
        lock (_lock)
        {
            _shutdown = false;
        }
    }

    /// <inheritdoc/>
    public ShmServingStatus Check(string serviceName)
    {
        lock (_lock)
        {
            if (_statusMap.TryGetValue(serviceName, out var status))
            {
                return status;
            }
            return ShmServingStatus.ServiceUnknown;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ShmServingStatus> Watch(
        string serviceName,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<ShmServingStatus>();

        Action<ShmServingStatus> watcher = status =>
        {
            channel.Writer.TryWrite(status);
        };

        // Get initial status and register watcher
        ShmServingStatus initialStatus;
        lock (_lock)
        {
            if (_statusMap.TryGetValue(serviceName, out initialStatus))
            {
                yield return initialStatus;
            }
            else
            {
                yield return ShmServingStatus.ServiceUnknown;
            }

            if (!_watchers.TryGetValue(serviceName, out var watchers))
            {
                watchers = new List<Action<ShmServingStatus>>();
                _watchers[serviceName] = watchers;
            }
            watchers.Add(watcher);
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var status = await channel.Reader.ReadAsync(cancellationToken);
                yield return status;
            }
        }
        finally
        {
            // Unregister watcher
            lock (_lock)
            {
                if (_watchers.TryGetValue(serviceName, out var watchers))
                {
                    watchers.Remove(watcher);
                    if (watchers.Count == 0)
                    {
                        _watchers.Remove(serviceName);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Gets all registered service names.
    /// </summary>
    public IReadOnlyCollection<string> GetServices()
    {
        lock (_lock)
        {
            return _statusMap.Keys.ToList();
        }
    }
}

/// <summary>
/// Health check options for shared memory clients.
/// Modeled on grpc-go health check config.
/// </summary>
public sealed class ShmHealthCheckOptions
{
    /// <summary>
    /// Gets or sets the service name to check. Default is empty string (server health).
    /// </summary>
    public string ServiceName { get; set; } = "";

    /// <summary>
    /// Gets or sets the health check timeout.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the health check interval.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets whether health checks are enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the number of consecutive failures before marking unhealthy.
    /// </summary>
    public int FailureThreshold { get; set; } = 3;
}

/// <summary>
/// Client-side health check state.
/// </summary>
public sealed class ShmHealthCheckState
{
    private readonly ShmHealthCheckOptions _options;
    private int _consecutiveFailures;
    private ShmServingStatus _lastStatus = ShmServingStatus.Unknown;
    private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;

    /// <summary>
    /// Creates a new health check state.
    /// </summary>
    public ShmHealthCheckState(ShmHealthCheckOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Gets the last known status.
    /// </summary>
    public ShmServingStatus LastStatus => _lastStatus;

    /// <summary>
    /// Gets the time of the last check.
    /// </summary>
    public DateTimeOffset LastCheck => _lastCheck;

    /// <summary>
    /// Gets whether the service is considered healthy.
    /// </summary>
    public bool IsHealthy => _lastStatus == ShmServingStatus.Serving;

    /// <summary>
    /// Gets the number of consecutive failures.
    /// </summary>
    public int ConsecutiveFailures => _consecutiveFailures;

    /// <summary>
    /// Records a health check result.
    /// </summary>
    /// <param name="status">The health status.</param>
    public void RecordCheck(ShmServingStatus status)
    {
        _lastCheck = DateTimeOffset.UtcNow;
        _lastStatus = status;

        if (status == ShmServingStatus.Serving)
        {
            _consecutiveFailures = 0;
        }
        else
        {
            _consecutiveFailures++;
        }
    }

    /// <summary>
    /// Checks if a health check is due.
    /// </summary>
    public bool IsCheckDue()
    {
        if (!_options.Enabled)
        {
            return false;
        }

        return DateTimeOffset.UtcNow - _lastCheck >= _options.Interval;
    }

    /// <summary>
    /// Checks if the service should be marked unhealthy due to failures.
    /// </summary>
    public bool ShouldMarkUnhealthy()
    {
        return _consecutiveFailures >= _options.FailureThreshold;
    }
}
