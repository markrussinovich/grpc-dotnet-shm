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

[TestFixture]
public class RingBufferTests
{
    private const int TestCapacity = 1024; // 1 KB for testing

    [Test]
    public void Constructor_ValidParameters_CreatesRing()
    {
        // Arrange
        var memory = new byte[ShmConstants.RingHeaderSize + TestCapacity];

        // Act
        using var ring = new ShmRing(memory, 0, TestCapacity);

        // Assert
        Assert.That(ring.Capacity, Is.EqualTo((ulong)TestCapacity));
        Assert.That(ring.IsClosed, Is.False);
    }

    [Test]
    public void Constructor_NonPowerOfTwo_ThrowsArgumentException()
    {
        // Arrange
        var memory = new byte[ShmConstants.RingHeaderSize + 1000];

        // Act & Assert
        Assert.Throws<ArgumentException>(() => new ShmRing(memory, 0, 1000));
    }

    [Test]
    public void Constructor_MemoryTooSmall_ThrowsArgumentException()
    {
        // Arrange
        var memory = new byte[100]; // Too small

        // Act & Assert
        Assert.Throws<ArgumentException>(() => new ShmRing(memory, 0, TestCapacity));
    }

    [Test]
    public void Write_SimpleData_Succeeds()
    {
        // Arrange
        var memory = new byte[ShmConstants.RingHeaderSize + TestCapacity];
        using var ring = new ShmRing(memory, 0, TestCapacity);
        var data = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        ring.Write(data, CancellationToken.None);

        // Assert
        var state = ring.GetState();
        Assert.That(state.Used, Is.EqualTo(5UL));
    }

    [Test]
    public void Write_EmptyData_NoOp()
    {
        // Arrange
        var memory = new byte[ShmConstants.RingHeaderSize + TestCapacity];
        using var ring = new ShmRing(memory, 0, TestCapacity);

        // Act
        ring.Write(ReadOnlySpan<byte>.Empty, CancellationToken.None);

        // Assert
        var state = ring.GetState();
        Assert.That(state.Used, Is.EqualTo(0UL));
    }

    [Test]
    public void Write_DataExceedsCapacity_ThrowsArgumentException()
    {
        // Arrange
        var memory = new byte[ShmConstants.RingHeaderSize + TestCapacity];
        using var ring = new ShmRing(memory, 0, TestCapacity);
        var data = new byte[TestCapacity + 1];

        // Act & Assert
        Assert.Throws<ArgumentException>(() => ring.Write(data, CancellationToken.None));
    }

    [Test]
    public void Read_AfterWrite_ReturnsData()
    {
        // Arrange
        var memory = new byte[ShmConstants.RingHeaderSize + TestCapacity];
        using var ring = new ShmRing(memory, 0, TestCapacity);
        var writeData = new byte[] { 1, 2, 3, 4, 5 };
        ring.Write(writeData, CancellationToken.None);

        // Act
        var readBuffer = new byte[10];
        var bytesRead = ring.Read(readBuffer, CancellationToken.None);

        // Assert
        Assert.That(bytesRead, Is.EqualTo(5));
        Assert.That(readBuffer[..5], Is.EqualTo(writeData));
    }

    [Test]
    public void Read_PartialRead_ReturnsAvailableData()
    {
        // Arrange
        var memory = new byte[ShmConstants.RingHeaderSize + TestCapacity];
        using var ring = new ShmRing(memory, 0, TestCapacity);
        var writeData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        ring.Write(writeData, CancellationToken.None);

        // Act - read only 3 bytes
        var readBuffer = new byte[3];
        var bytesRead = ring.Read(readBuffer, CancellationToken.None);

        // Assert
        Assert.That(bytesRead, Is.EqualTo(3));
        Assert.That(readBuffer, Is.EqualTo(new byte[] { 1, 2, 3 }));

        // Read remaining
        var remaining = new byte[10];
        bytesRead = ring.Read(remaining, CancellationToken.None);
        Assert.That(bytesRead, Is.EqualTo(7));
        Assert.That(remaining[..7], Is.EqualTo(new byte[] { 4, 5, 6, 7, 8, 9, 10 }));
    }

    [Test]
    public void WriteRead_WrapAround_HandlesCorrectly()
    {
        // Arrange - use small ring to force wrap-around
        const int smallCapacity = 128;
        var memory = new byte[ShmConstants.RingHeaderSize + smallCapacity];
        using var ring = new ShmRing(memory, 0, smallCapacity);

        // Fill ring partially
        var data1 = new byte[100];
        for (var i = 0; i < data1.Length; i++) data1[i] = (byte)i;
        ring.Write(data1, CancellationToken.None);

        // Read most of it (moves read index forward, freeing space)
        var readBuffer = new byte[80];
        var bytesRead = ring.Read(readBuffer, CancellationToken.None);
        Assert.That(bytesRead, Is.EqualTo(80));
        for (var i = 0; i < 80; i++)
        {
            Assert.That(readBuffer[i], Is.EqualTo((byte)i), $"First read mismatch at position {i}");
        }

        // Write more data that wraps around (write position is at 100, capacity 128)
        // So first 28 bytes go at positions 100-127, remaining 32 bytes wrap to positions 0-31
        var data2 = new byte[60];
        for (var i = 0; i < data2.Length; i++) data2[i] = (byte)(200 + i);
        ring.Write(data2, CancellationToken.None);

        // Now we have: 20 remaining from first write (positions 80-99) + 60 from second = 80 bytes total
        // Read all remaining data
        var finalBuffer = new byte[100];
        var bytesRead1 = ring.Read(finalBuffer, CancellationToken.None);

        // Should get remaining 20 bytes from first write + 60 from second = 80 bytes total
        Assert.That(bytesRead1, Is.EqualTo(80));
        
        // First 20 bytes are from the first write (bytes 80-99)
        for (var i = 0; i < 20; i++)
        {
            Assert.That(finalBuffer[i], Is.EqualTo((byte)(80 + i)), $"Remaining first write mismatch at position {i}");
        }

        // Next 60 bytes are from second write
        for (var i = 0; i < 60; i++)
        {
            Assert.That(finalBuffer[20 + i], Is.EqualTo((byte)(200 + i)), $"Second write mismatch at position {i}");
        }
    }

    [Test]
    public void Close_PreventsNewWrites()
    {
        // Arrange
        var memory = new byte[ShmConstants.RingHeaderSize + TestCapacity];
        using var ring = new ShmRing(memory, 0, TestCapacity);

        // Act
        ring.Close();

        // Assert
        Assert.That(ring.IsClosed, Is.True);
        Assert.Throws<RingClosedException>(() => ring.Write(new byte[] { 1, 2, 3 }, CancellationToken.None));
    }

    [Test]
    public void Close_AllowsDrainingRemainingData()
    {
        // Arrange
        var memory = new byte[ShmConstants.RingHeaderSize + TestCapacity];
        using var ring = new ShmRing(memory, 0, TestCapacity);
        var data = new byte[] { 1, 2, 3, 4, 5 };
        ring.Write(data, CancellationToken.None);

        // Act
        ring.Close();

        // Assert - should still be able to read remaining data
        var buffer = new byte[10];
        var bytesRead = ring.Read(buffer, CancellationToken.None);
        Assert.That(bytesRead, Is.EqualTo(5));
        Assert.That(buffer[..5], Is.EqualTo(data));

        // Now should throw on empty closed ring
        Assert.Throws<RingClosedException>(() => ring.Read(buffer, CancellationToken.None));
    }

    [Test]
    public void ReserveWrite_BasicOperation_Succeeds()
    {
        // Arrange
        var memory = new byte[ShmConstants.RingHeaderSize + TestCapacity];
        using var ring = new ShmRing(memory, 0, TestCapacity);

        // Act
        var reservation = ring.ReserveWrite(16, CancellationToken.None);

        // Assert
        Assert.That(reservation.Length, Is.EqualTo(16));
        Assert.That(reservation.First.Length, Is.EqualTo(16));
        Assert.That(reservation.Second.Length, Is.EqualTo(0));
    }

    [Test]
    public void ReserveWrite_WrapAround_ReturnsTwoSlices()
    {
        // Arrange - use small ring
        const int smallCapacity = 128;
        var memory = new byte[ShmConstants.RingHeaderSize + smallCapacity];
        using var ring = new ShmRing(memory, 0, smallCapacity);

        // Fill and drain to move write position near end
        var fillData = new byte[100];
        ring.Write(fillData, CancellationToken.None);
        var readBuf = new byte[100];
        ring.Read(readBuf, CancellationToken.None);

        // Act - reserve data that wraps around
        var reservation = ring.ReserveWrite(50, CancellationToken.None);

        // Assert
        Assert.That(reservation.Length, Is.EqualTo(50));
        // First slice goes to end of buffer (28 bytes)
        Assert.That(reservation.First.Length, Is.EqualTo(28));
        // Second slice at beginning (22 bytes)
        Assert.That(reservation.Second.Length, Is.EqualTo(22));
    }

    [Test]
    public void CommitWrite_PublishesData()
    {
        // Arrange
        var memory = new byte[ShmConstants.RingHeaderSize + TestCapacity];
        using var ring = new ShmRing(memory, 0, TestCapacity);

        var reservation = ring.ReserveWrite(16, CancellationToken.None);
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        data.CopyTo(reservation.First);

        // Act
        ring.CommitWrite(reservation, 16);

        // Assert
        var state = ring.GetState();
        Assert.That(state.Used, Is.EqualTo(16UL));

        // Read and verify
        var buffer = new byte[16];
        ring.Read(buffer, CancellationToken.None);
        Assert.That(buffer, Is.EqualTo(data));
    }

    [Test]
    public void ReserveRead_BasicOperation_Succeeds()
    {
        // Arrange
        var memory = new byte[ShmConstants.RingHeaderSize + TestCapacity];
        using var ring = new ShmRing(memory, 0, TestCapacity);
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        ring.Write(data, CancellationToken.None);

        // Act
        var reservation = ring.ReserveRead(8, CancellationToken.None);

        // Assert
        Assert.That(reservation.Length, Is.EqualTo(8));
        Assert.That(reservation.First.ToArray(), Is.EqualTo(data));
    }

    [Test]
    public void CommitRead_FreesSpace()
    {
        // Arrange
        var memory = new byte[ShmConstants.RingHeaderSize + TestCapacity];
        using var ring = new ShmRing(memory, 0, TestCapacity);
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        ring.Write(data, CancellationToken.None);

        var reservation = ring.ReserveRead(8, CancellationToken.None);

        // Act
        ring.CommitRead(reservation, 8);

        // Assert
        var state = ring.GetState();
        Assert.That(state.Used, Is.EqualTo(0UL));
    }

    [Test]
    public void ConcurrentWriteRead_SingleProducerSingleConsumer_Works()
    {
        // Arrange
        var memory = new byte[ShmConstants.RingHeaderSize + TestCapacity];
        using var ring = new ShmRing(memory, 0, TestCapacity);
        const int messageCount = 100;
        const int messageSize = 8;
        var receivedMessages = new List<byte[]>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act - run producer and consumer concurrently
        var producerTask = Task.Run(() =>
        {
            for (var i = 0; i < messageCount; i++)
            {
                var data = new byte[messageSize];
                for (var j = 0; j < messageSize; j++)
                {
                    data[j] = (byte)(i + j);
                }
                ring.Write(data, cts.Token);
            }
        }, cts.Token);

        var consumerTask = Task.Run(() =>
        {
            for (var i = 0; i < messageCount; i++)
            {
                var buffer = new byte[messageSize];
                var bytesRead = ring.Read(buffer, cts.Token);
                Assert.That(bytesRead, Is.EqualTo(messageSize));
                receivedMessages.Add(buffer.ToArray());
            }
        }, cts.Token);

        Task.WaitAll(producerTask, consumerTask);

        // Assert
        Assert.That(receivedMessages.Count, Is.EqualTo(messageCount));
        for (var i = 0; i < messageCount; i++)
        {
            for (var j = 0; j < messageSize; j++)
            {
                Assert.That(receivedMessages[i][j], Is.EqualTo((byte)(i + j)));
            }
        }
    }

    [Test]
    public void Cancellation_ThrowsOperationCanceledException()
    {
        // Arrange
        var memory = new byte[ShmConstants.RingHeaderSize + TestCapacity];
        using var ring = new ShmRing(memory, 0, TestCapacity);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        Assert.Throws<OperationCanceledException>(() =>
            ring.Read(new byte[10], cts.Token));
    }

    [Test]
    public void GetState_ReturnsCorrectValues()
    {
        // Arrange
        var memory = new byte[ShmConstants.RingHeaderSize + TestCapacity];
        using var ring = new ShmRing(memory, 0, TestCapacity);
        ring.Write(new byte[100], CancellationToken.None);
        ring.Read(new byte[30], CancellationToken.None);

        // Act
        var state = ring.GetState();

        // Assert
        Assert.That(state.Capacity, Is.EqualTo((ulong)TestCapacity));
        Assert.That(state.WriteIdx, Is.EqualTo(100UL));
        Assert.That(state.ReadIdx, Is.EqualTo(30UL));
        Assert.That(state.Used, Is.EqualTo(70UL));
        Assert.That(state.Available, Is.EqualTo((ulong)TestCapacity - 70));
        Assert.That(state.Closed, Is.False);
    }
}
