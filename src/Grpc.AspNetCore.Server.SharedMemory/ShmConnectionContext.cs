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

using System.IO.Pipelines;
using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Grpc.Net.SharedMemory;

namespace Grpc.AspNetCore.Server.SharedMemory;

/// <summary>
/// A <see cref="ConnectionContext"/> that wraps a shared memory connection.
/// Exposes the shared memory ring buffers as an <see cref="IDuplexPipe"/> transport
/// so Kestrel can run HTTP/2 over it.
/// </summary>
internal sealed class ShmConnectionContext : ConnectionContext
{
    private readonly ShmStream _shmStream;
    private readonly CancellationTokenSource _connectionClosedCts;

    public ShmConnectionContext(string connectionId, ShmStream shmStream, EndPoint localEndPoint)
    {
        ConnectionId = connectionId;
        _shmStream = shmStream;
        _connectionClosedCts = new CancellationTokenSource();
        ConnectionClosed = _connectionClosedCts.Token;
        LocalEndPoint = localEndPoint;
        Features = new FeatureCollection();
        Items = new Dictionary<object, object?>();

        // Wrap the bidirectional ShmStream into IDuplexPipe for Kestrel
        var reader = PipeReader.Create(_shmStream, new StreamPipeReaderOptions(leaveOpen: true));
        var writer = PipeWriter.Create(_shmStream, new StreamPipeWriterOptions(leaveOpen: true));
        Transport = new DuplexPipe(reader, writer);
    }

    /// <inheritdoc/>
    public override string ConnectionId { get; set; }

    /// <inheritdoc/>
    public override IFeatureCollection Features { get; }

    /// <inheritdoc/>
    public override IDictionary<object, object?> Items { get; set; }

    /// <inheritdoc/>
    public override IDuplexPipe Transport { get; set; }

    /// <inheritdoc/>
    public override CancellationToken ConnectionClosed { get; set; }

    /// <inheritdoc/>
    public override EndPoint? LocalEndPoint { get; set; }

    /// <inheritdoc/>
    public override EndPoint? RemoteEndPoint { get; set; }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        _connectionClosedCts.Cancel();

        if (Transport is DuplexPipe duplexPipe)
        {
            await duplexPipe.Input.CompleteAsync().ConfigureAwait(false);
            await duplexPipe.Output.CompleteAsync().ConfigureAwait(false);
        }

        await _shmStream.DisposeAsync().ConfigureAwait(false);
        _connectionClosedCts.Dispose();
    }

    /// <summary>
    /// Simple IDuplexPipe implementation wrapping a PipeReader and PipeWriter.
    /// </summary>
    private sealed class DuplexPipe : IDuplexPipe
    {
        public DuplexPipe(PipeReader input, PipeWriter output)
        {
            Input = input;
            Output = output;
        }

        public PipeReader Input { get; }
        public PipeWriter Output { get; }
    }
}
