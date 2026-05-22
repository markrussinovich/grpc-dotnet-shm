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
using Grpc.Net.SharedMemory.Compression;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Transport types for parameterized tests.
/// Matches grpc-go-shmem's listTestEnv() pattern.
/// </summary>
public enum TransportKind
{
    /// <summary>
    /// Shared memory transport via ShmConnection.
    /// </summary>
    Shm,

    /// <summary>
    /// TCP/HTTP2 transport via Kestrel (requires P6: server-side integration).
    /// </summary>
    Tcp
}

/// <summary>
/// Base class for transport-parameterized tests.
/// Provides shared memory connection setup/teardown that works on both Windows and Linux.
/// This enables Go-style test parameterization: running the same tests over different transports.
/// </summary>
/// <remarks>
/// Equivalent to grpc-go-shmem's listTestEnv() which runs all TCP tests over shared memory too.
/// Tests inheriting from this class will automatically run on both Windows and Linux.
///
/// Usage with parameterization:
/// <code>
/// [TestFixture(TransportKind.Shm)]
/// // [TestFixture(TransportKind.Tcp)] // Uncomment when P6 (server integration) lands
/// public class MyTests : TransportTestBase
/// {
///     public MyTests(TransportKind transport) : base(transport) { }
/// }
/// </code>
/// </remarks>
public abstract class TransportTestBase
{
    private readonly List<IDisposable> _disposables = new();

    /// <summary>
    /// The transport being used for this test fixture instance.
    /// </summary>
    protected TransportKind Transport { get; }

    /// <summary>
    /// Creates a new TransportTestBase with SHM transport (default for non-parameterized use).
    /// </summary>
    protected TransportTestBase() : this(TransportKind.Shm) { }

    /// <summary>
    /// Creates a new TransportTestBase with the specified transport.
    /// </summary>
    protected TransportTestBase(TransportKind transport)
    {
        Transport = transport;
    }

    /// <summary>
    /// Creates a server/client connection pair for testing.
    /// Uses /dev/shm on Linux and temp directory on Windows.
    /// </summary>
    /// <param name="ringCapacity">Ring buffer capacity (must be power of 2).</param>
    /// <param name="maxStreams">Maximum concurrent streams.</param>
    /// <param name="compressionOptions">Optional compression options for both server and client.</param>
    /// <returns>A tuple of (server, client) connections.</returns>
    protected (ShmConnection Server, ShmConnection Client) CreateConnectionPair(
        ulong ringCapacity = 4096,
        uint maxStreams = 100)
    {
        if (Transport == TransportKind.Tcp)
        {
            throw new NotSupportedException(
                "TCP transport requires P6 (server-side ASP.NET Core integration). " +
                "Use TransportKind.Shm or implement IConnectionListenerFactory first.");
        }

        var segmentName = $"test_{Guid.NewGuid():N}";
        var server = ShmConnection.CreateAsServer(segmentName, ringCapacity, maxStreams);
        _disposables.Add(server);

        var client = ShmConnection.ConnectAsClient(segmentName);
        _disposables.Add(client);

        return (server, client);
    }

    /// <summary>
    /// Creates a server/client connection pair and returns the segment name for advanced tests.
    /// </summary>
    protected (ShmConnection Server, ShmConnection Client, string SegmentName) CreateConnectionPairWithName(
        ulong ringCapacity = 4096,
        uint maxStreams = 100)
    {
        if (Transport == TransportKind.Tcp)
        {
            throw new NotSupportedException(
                "TCP transport requires P6 (server-side ASP.NET Core integration).");
        }

        var segmentName = $"test_{Guid.NewGuid():N}";
        var server = ShmConnection.CreateAsServer(segmentName, ringCapacity, maxStreams);
        _disposables.Add(server);

        var client = ShmConnection.ConnectAsClient(segmentName);
        _disposables.Add(client);

        return (server, client, segmentName);
    }

    /// <summary>
    /// Creates a single server connection (for tests that manage client connection separately).
    /// </summary>
    protected (ShmConnection Server, string SegmentName) CreateServer(
        ulong ringCapacity = 4096,
        uint maxStreams = 100)
    {
        if (Transport == TransportKind.Tcp)
        {
            throw new NotSupportedException(
                "TCP transport requires P6 (server-side ASP.NET Core integration).");
        }

        var segmentName = $"test_{Guid.NewGuid():N}";
        var server = ShmConnection.CreateAsServer(segmentName, ringCapacity, maxStreams);
        _disposables.Add(server);
        return (server, segmentName);
    }

    /// <summary>
    /// Tracks a disposable resource for cleanup after the test.
    /// </summary>
    protected T Track<T>(T disposable) where T : IDisposable
    {
        _disposables.Add(disposable);
        return disposable;
    }

    [TearDown]
    public void CleanupConnections()
    {
        // Dispose in reverse order (client before server)
        for (int i = _disposables.Count - 1; i >= 0; i--)
        {
            try
            {
                _disposables[i].Dispose();
            }
            catch
            {
                // Best effort cleanup
            }
        }
        _disposables.Clear();
    }
}
