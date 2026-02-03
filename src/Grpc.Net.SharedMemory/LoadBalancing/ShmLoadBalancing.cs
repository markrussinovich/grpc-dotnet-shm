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

namespace Grpc.Net.SharedMemory.LoadBalancing;

/// <summary>
/// Transport preference for load balancing decisions.
/// Modeled on grpc-go TransportPreference.
/// </summary>
public enum ShmTransportPreference
{
    /// <summary>
    /// No preference, use any available transport.
    /// </summary>
    Default = 0,

    /// <summary>
    /// Prefer shared memory if available.
    /// </summary>
    PreferShm = 1,

    /// <summary>
    /// Require shared memory, fail if not available.
    /// </summary>
    RequireShm = 2,

    /// <summary>
    /// Prefer network transport (TCP/HTTP2).
    /// </summary>
    PreferNetwork = 3
}

/// <summary>
/// Endpoint capability information for load balancing.
/// Modeled on grpc-go ShmCapability.
/// </summary>
public sealed class ShmEndpointCapability
{
    /// <summary>
    /// Gets or sets whether shared memory is enabled for this endpoint.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the segment name for shared memory transport.
    /// </summary>
    public string? SegmentName { get; set; }

    /// <summary>
    /// Gets or sets whether this endpoint is preferred for shared memory.
    /// </summary>
    public bool Preferred { get; set; }

    /// <summary>
    /// Gets or sets whether this endpoint is local (same machine).
    /// </summary>
    public bool IsLocal { get; set; }

    /// <summary>
    /// Creates a new endpoint capability.
    /// </summary>
    public static ShmEndpointCapability Create(string segmentName, bool preferred = true, bool isLocal = true)
    {
        return new ShmEndpointCapability
        {
            Enabled = true,
            SegmentName = segmentName,
            Preferred = preferred,
            IsLocal = isLocal
        };
    }

    /// <summary>
    /// Creates a disabled endpoint capability.
    /// </summary>
    public static ShmEndpointCapability Disabled => new() { Enabled = false };
}

/// <summary>
/// Endpoint address with shared memory capability information.
/// </summary>
public sealed class ShmEndpointAddress
{
    /// <summary>
    /// Gets or sets the address (e.g., "localhost:50051" or "shm://segment").
    /// </summary>
    public required string Address { get; set; }

    /// <summary>
    /// Gets or sets the shared memory capability.
    /// </summary>
    public ShmEndpointCapability? ShmCapability { get; set; }

    /// <summary>
    /// Gets or sets custom attributes for the endpoint.
    /// </summary>
    public IDictionary<string, object> Attributes { get; set; } = new Dictionary<string, object>();

    /// <summary>
    /// Gets whether this is a shared memory address.
    /// </summary>
    public bool IsShmAddress => Address.StartsWith("shm://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the segment name if this is a shared memory address.
    /// </summary>
    public string? GetSegmentName()
    {
        if (IsShmAddress)
        {
            return Address.Substring(6); // Skip "shm://"
        }
        return ShmCapability?.SegmentName;
    }
}

/// <summary>
/// Service policy for shared memory transport selection.
/// Modeled on grpc-go ShmServiceConfig.
/// </summary>
public sealed class ShmServicePolicy
{
    /// <summary>
    /// Auto policy: use shared memory if available, fallback to network.
    /// </summary>
    public const string Auto = "auto";

    /// <summary>
    /// Preferred policy: prefer shared memory, fallback allowed.
    /// </summary>
    public const string PreferredPolicy = "preferred";

    /// <summary>
    /// Required policy: require shared memory, fail if not available.
    /// </summary>
    public const string Required = "required";

    /// <summary>
    /// Disabled policy: never use shared memory.
    /// </summary>
    public const string Disabled = "disabled";

    /// <summary>
    /// Gets or sets the policy name.
    /// </summary>
    public string Policy { get; set; } = Auto;

    /// <summary>
    /// Gets or sets whether fallback to network transport is allowed.
    /// </summary>
    public bool AllowFallback { get; set; } = true;

    /// <summary>
    /// Determines if shared memory should be used for an endpoint.
    /// </summary>
    /// <param name="hasCapability">Whether the endpoint has shared memory capability.</param>
    /// <returns>True if shared memory should be used.</returns>
    public bool ShouldUseShm(bool hasCapability)
    {
        return Policy switch
        {
            Disabled => false,
            Required => hasCapability, // Will fail later if not capable
            _ => hasCapability // Auto and Preferred use shm if available
        };
    }

    /// <summary>
    /// Validates the policy configuration.
    /// </summary>
    public void Validate()
    {
        if (!new[] { Auto, PreferredPolicy, Required, Disabled }.Contains(Policy))
        {
            throw new InvalidOperationException($"Invalid shm policy: {Policy}");
        }
    }
}

/// <summary>
/// Interface for shared memory-aware pickers.
/// Modeled on grpc-go Picker.
/// </summary>
public interface IShmPicker
{
    /// <summary>
    /// Picks an endpoint for an RPC.
    /// </summary>
    /// <param name="info">Information about the RPC.</param>
    /// <returns>The pick result.</returns>
    ShmPickResult Pick(ShmPickInfo info);
}

/// <summary>
/// Information for making a pick decision.
/// </summary>
public sealed class ShmPickInfo
{
    /// <summary>
    /// Gets or sets the full method name.
    /// </summary>
    public required string FullMethod { get; set; }

    /// <summary>
    /// Gets or sets any context data for the pick.
    /// </summary>
    public IDictionary<string, object>? Context { get; set; }
}

/// <summary>
/// Result of a pick operation.
/// </summary>
public sealed class ShmPickResult
{
    /// <summary>
    /// Gets or sets the selected endpoint address.
    /// </summary>
    public ShmEndpointAddress? Address { get; set; }

    /// <summary>
    /// Gets or sets the error if no endpoint is available.
    /// </summary>
    public Exception? Error { get; set; }

    /// <summary>
    /// Gets or sets whether to queue the RPC and try again.
    /// </summary>
    public bool Queue { get; set; }

    /// <summary>
    /// Gets whether the pick was successful.
    /// </summary>
    public bool IsSuccess => Address != null && Error == null && !Queue;

    /// <summary>
    /// Creates a successful pick result.
    /// </summary>
    public static ShmPickResult Success(ShmEndpointAddress address) => new() { Address = address };

    /// <summary>
    /// Creates a failed pick result.
    /// </summary>
    public static ShmPickResult Fail(Exception error) => new() { Error = error };

    /// <summary>
    /// Creates a queued pick result.
    /// </summary>
    public static ShmPickResult Queued => new() { Queue = true };
}

/// <summary>
/// Round-robin picker that prefers shared memory endpoints.
/// Modeled on grpc-go shmem_prefer balancer.
/// </summary>
public sealed class ShmPreferPicker : IShmPicker
{
    private readonly List<ShmEndpointAddress> _shmEndpoints;
    private readonly List<ShmEndpointAddress> _networkEndpoints;
    private int _shmIndex;
    private int _networkIndex;

    /// <summary>
    /// Creates a new shm-prefer picker.
    /// </summary>
    public ShmPreferPicker(IEnumerable<ShmEndpointAddress> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        _shmEndpoints = endpoints
            .Where(e => e.ShmCapability?.Enabled == true || e.IsShmAddress)
            .ToList();

        _networkEndpoints = endpoints
            .Where(e => e.ShmCapability?.Enabled != true && !e.IsShmAddress)
            .ToList();
    }

    /// <inheritdoc/>
    public ShmPickResult Pick(ShmPickInfo info)
    {
        // Prefer shm endpoints
        if (_shmEndpoints.Count > 0)
        {
            var index = Interlocked.Increment(ref _shmIndex) % _shmEndpoints.Count;
            return ShmPickResult.Success(_shmEndpoints[index]);
        }

        // Fall back to network endpoints
        if (_networkEndpoints.Count > 0)
        {
            var index = Interlocked.Increment(ref _networkIndex) % _networkEndpoints.Count;
            return ShmPickResult.Success(_networkEndpoints[index]);
        }

        return ShmPickResult.Fail(new InvalidOperationException("No endpoints available"));
    }
}

/// <summary>
/// Transport selector for choosing between shared memory and network transport.
/// Modeled on grpc-go TransportSelector.
/// </summary>
public sealed class ShmTransportSelector
{
    private readonly ShmServicePolicy _policy;

    /// <summary>
    /// Creates a new transport selector.
    /// </summary>
    public ShmTransportSelector(ShmServicePolicy? policy = null)
    {
        _policy = policy ?? new ShmServicePolicy();
    }

    /// <summary>
    /// Selects the transport type for an endpoint.
    /// </summary>
    /// <param name="endpoint">The endpoint address.</param>
    /// <returns>The transport preference.</returns>
    public ShmTransportPreference SelectTransport(ShmEndpointAddress endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (_policy.Policy == ShmServicePolicy.Disabled)
        {
            return ShmTransportPreference.PreferNetwork;
        }

        var hasShmCapability = endpoint.ShmCapability?.Enabled == true || endpoint.IsShmAddress;

        if (_policy.Policy == ShmServicePolicy.Required)
        {
            return hasShmCapability ? ShmTransportPreference.RequireShm : ShmTransportPreference.PreferNetwork;
        }

        if (hasShmCapability)
        {
            return ShmTransportPreference.PreferShm;
        }

        return ShmTransportPreference.PreferNetwork;
    }

    /// <summary>
    /// Checks if shared memory is available for an endpoint.
    /// </summary>
    public bool IsShmAvailable(ShmEndpointAddress endpoint)
    {
        return endpoint.ShmCapability?.Enabled == true || endpoint.IsShmAddress;
    }
}
