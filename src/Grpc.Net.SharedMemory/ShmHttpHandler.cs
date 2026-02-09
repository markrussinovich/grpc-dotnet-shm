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

using System.Net.Http;

namespace Grpc.Net.SharedMemory;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that routes HTTP/2 gRPC traffic over shared memory
/// using the grpc-go-shmem compatible control segment protocol.
/// The underlying transport is standard HTTP/2; only the byte stream is replaced with
/// shared memory ring buffers instead of TCP sockets.
/// </summary>
/// <remarks>
/// Use this handler with <c>GrpcChannel.ForAddress</c> to create a client that communicates
/// over shared memory while using standard gRPC service stubs:
/// <code>
/// using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
/// {
///     HttpHandler = new ShmHttpHandler("my_segment"),
///     DisposeHttpClient = true
/// });
/// var client = new Greeter.GreeterClient(channel);
/// </code>
/// </remarks>
public sealed class ShmHttpHandler : DelegatingHandler
{
    private readonly string _baseName;

    /// <summary>
    /// Creates a new <see cref="ShmHttpHandler"/> that connects via the shared memory
    /// control segment protocol.
    /// </summary>
    /// <param name="baseName">The base segment name (without <c>_ctl</c> suffix).</param>
    public ShmHttpHandler(string baseName)
    {
        _baseName = baseName ?? throw new ArgumentNullException(nameof(baseName));

        var inner = new SocketsHttpHandler();
        inner.ConnectCallback = ConnectAsync;
        InnerHandler = inner;
    }

    /// <summary>
    /// Gets the base segment name this handler connects to.
    /// </summary>
    public string BaseName => _baseName;

    private async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        // Open the control segment
        var ctlName = _baseName + ShmConstants.ControlSegmentSuffix;
        Segment ctlSegment;
        try
        {
            ctlSegment = Segment.Open(ctlName);
        }
        catch (FileNotFoundException)
        {
            throw new InvalidOperationException(
                $"Server not listening on segment '{_baseName}'. " +
                $"Control segment '{ctlName}' not found.");
        }

        try
        {
            // Wait for server to be ready
            await ctlSegment.WaitForServerAsync(cancellationToken).ConfigureAwait(false);

            // Control rings: Ring A = client→server, Ring B = server→client
            var ctlTx = ctlSegment.RingA;
            var ctlRx = ctlSegment.RingB;

            // Send CONNECT request
            WriteControlFrame(ctlTx, FrameType.Connect, ControlWire.EncodeConnectRequest());

            // Read response
            var (header, payload) = await ReadControlFrameAsync(ctlRx, cancellationToken)
                .ConfigureAwait(false);

            switch (header.Type)
            {
                case FrameType.Accept:
                    var dataSegmentName = ControlWire.DecodeConnectResponse(payload.Span);

                    // Open the data segment
                    var dataSegment = Segment.Open(dataSegmentName);
                    await dataSegment.WaitForServerAsync(cancellationToken).ConfigureAwait(false);

                    // Signal that client has mapped the segment
                    dataSegment.SetClientReady(true);

                    // Client reads from RingB (server→client), writes to RingA (client→server)
                    return new ShmStream(dataSegment.RingB, dataSegment.RingA);

                case FrameType.Reject:
                    var message = ControlWire.DecodeConnectReject(payload.Span);
                    throw new InvalidOperationException($"Connection rejected by server: {message}");

                default:
                    throw new InvalidOperationException(
                        $"Unexpected response frame type: {header.Type}");
            }
        }
        finally
        {
            // Unmap but don't close — server keeps the control segment
            ctlSegment.UnmapWithoutClose();
        }
    }

    private static void WriteControlFrame(ShmRing ring, FrameType type, byte[] payload)
    {
        var header = new FrameHeader
        {
            Length = (uint)payload.Length,
            StreamId = 0,
            Type = type,
            Flags = 0
        };

        var headerBytes = header.ToBytes();

        // Write header then payload — reader waits for full payload via TryPeek
        ring.Write(headerBytes);
        if (payload.Length > 0)
        {
            ring.Write(payload);
        }
    }

    private static async Task<(FrameHeader header, Memory<byte> payload)> ReadControlFrameAsync(
        ShmRing ring, CancellationToken ct)
    {
        // Wait for frame header data
        while (!ring.TryPeek(ShmConstants.FrameHeaderSize, out _))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1, ct).ConfigureAwait(false);
        }

        // Read frame header
        var headerBuffer = new byte[ShmConstants.FrameHeaderSize];
        if (!ring.TryRead(headerBuffer))
        {
            throw new InvalidOperationException("Failed to read control frame header");
        }

        var header = FrameHeader.Parse(headerBuffer);

        // Read payload if present
        Memory<byte> payload = Memory<byte>.Empty;
        if (header.Length > 0)
        {
            while (!ring.TryPeek((int)header.Length, out _))
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(1, ct).ConfigureAwait(false);
            }

            var payloadBuffer = new byte[header.Length];
            if (!ring.TryRead(payloadBuffer))
            {
                throw new InvalidOperationException("Failed to read control frame payload");
            }
            payload = payloadBuffer;
        }

        return (header, payload);
    }
}
