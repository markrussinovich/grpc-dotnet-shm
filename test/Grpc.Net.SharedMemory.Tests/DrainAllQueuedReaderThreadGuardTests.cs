#region Copyright notice and license

// Copyright 2026 The gRPC Authors
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

using System.Reflection;
using NUnit.Framework;

namespace Grpc.Net.SharedMemory.Tests;

/// <summary>
/// Regression tests for the 2026-06-01 ship-blocker discovered in PR review:
/// <c>ShmFrameWriter.DrainAllQueued</c> can be invoked from the user's
/// inline-receive continuation on the SHM frame-reader Thread (via
/// <c>WriteInlineDirectMultiFrame</c>'s pre-drain), and previously would
/// call <c>FrameProtocol.WriteMessage</c> on queued entries which can park
/// on <c>ShmGrpcStream.ReserveSendQuotaOrBlock</c>. Parking on the reader
/// Thread = guaranteed deadlock because only the reader Thread can deliver
/// the peer's <c>WINDOW_UPDATE</c> that would release us.
///
/// Round-2 refinement: the fix only defers <see cref="FrameType.Message"/>
/// entries for FOREIGN streams (StreamId != currentStreamId). Same-stream
/// Messages and non-Message entries (Headers, Trailers, HalfClose, Reset)
/// must still drain on the reader Thread to preserve HTTP/2 same-stream
/// ordering.
///
/// These tests exercise the contract via reflection because the only
/// public path that triggers it requires a multi-stream + opt-in-inline-cont
/// + saturated-stream-B setup that would be brittle to construct
/// deterministically.
/// </summary>
[TestFixture]
public class DrainAllQueuedReaderThreadGuardTests
{
    [Test]
    public void DrainAllQueued_OnReaderThread_DefersForeignStreamMessage()
    {
        var name = $"test_drain_guard_foreign_{Guid.NewGuid():N}";
        using var connection = ShmConnection.CreateAsServer(name,
            ringCapacity: 64 * 1024, maxStreams: 4);
        var writer = connection.FrameWriter;
        Assert.That(writer, Is.Not.Null);

        Assert.That(writer!.TryPauseWriterLoop(), Is.True,
            "Test setup: must be able to pause the writer loop.");
        try
        {
            var (queue, queueType, countProp) = GetPrivateQueue(writer);

            // Seed a Message entry for a FOREIGN stream (id 99 — neither
            // the writer's nor the caller's current stream).
            EnqueueFrameEntry(queue, queueType, FrameType.Message, streamId: 99u);

            Assert.That((int)countProp.GetValue(queue)!, Is.EqualTo(1),
                "Test setup: seeded entry is present.");

            var drainMethod = GetDrainMethod();

            // Invoke DrainAllQueued with currentStreamId=42 INSIDE an
            // Enter scope. The guard MUST defer the foreign-stream
            // Message back to the queue.
            var task = Task.Run(() =>
            {
                using (ShmReaderThreadContext.Enter())
                {
                    drainMethod.Invoke(writer, new object[] { 42u });
                }
            });
            Assert.That(task.Wait(TimeSpan.FromSeconds(5)), Is.True,
                "DrainAllQueued on reader Thread MUST return promptly.");

            Assert.That((int)countProp.GetValue(queue)!, Is.EqualTo(1),
                "Foreign-stream Message MUST be deferred on reader Thread " +
                "to prevent the cross-stream WINDOW_UPDATE deadlock. " +
                "Regression: if this fails, the guard is dispatching foreign " +
                "Messages directly, restoring the deadlock class.");

            queueType.GetMethod("Clear")?.Invoke(queue, null);
        }
        finally
        {
            writer.ResumeWriterLoop();
        }
    }

    [Test]
    public void DrainAllQueued_OnReaderThread_DrainsNonMessageEntries()
    {
        // Non-Message entries (Headers/Trailers/HalfClose/Reset) MUST
        // always drain on the reader Thread regardless of stream id —
        // they carry no per-stream DATA quota debit, and the pre-existing
        // "queued frames before inline write" ordering invariant requires
        // it. The round-2 refinement specifically restored this drain
        // (the round-1 broad return was over-zealous and would have left
        // a stream's own queued HEADERS / TRAILERS stranded).
        var name = $"test_drain_guard_nonmsg_{Guid.NewGuid():N}";
        using var connection = ShmConnection.CreateAsServer(name,
            ringCapacity: 64 * 1024, maxStreams: 4);
        var writer = connection.FrameWriter;
        Assert.That(writer, Is.Not.Null);

        Assert.That(writer!.TryPauseWriterLoop(), Is.True);
        try
        {
            var (queue, queueType, countProp) = GetPrivateQueue(writer);

            // Seed a HalfClose entry. HalfClose is a 0-length non-Message
            // frame so WriteFrameEntryToRing just writes a 9-byte H2
            // frame header — fits trivially in the 64KiB ring.
            EnqueueFrameEntry(queue, queueType, FrameType.HalfClose, streamId: 99u);
            Assert.That((int)countProp.GetValue(queue)!, Is.EqualTo(1));

            var drainMethod = GetDrainMethod();

            var task = Task.Run(() =>
            {
                using (ShmReaderThreadContext.Enter())
                {
                    drainMethod.Invoke(writer, new object[] { 42u });
                }
            });
            Assert.That(task.Wait(TimeSpan.FromSeconds(5)), Is.True);

            Assert.That((int)countProp.GetValue(queue)!, Is.EqualTo(0),
                "Non-Message entries (HalfClose here) MUST drain on reader " +
                "Thread to preserve HTTP/2 same-stream ordering. " +
                "Regression: if this fails, the guard is over-deferring " +
                "(round-1 broad-return regression — see PR review round 2).");
        }
        finally
        {
            writer.ResumeWriterLoop();
        }
    }

    [Test]
    public void DrainAllQueued_OffReaderThread_DrainsForeignStreamMessage()
    {
        // Companion test: off-reader-Thread, the guard does NOT trigger,
        // so a foreign-stream Message must drain normally (regression
        // check that we didn't accidentally make the deferral
        // unconditional).
        var name = $"test_drain_off_reader_{Guid.NewGuid():N}";
        using var connection = ShmConnection.CreateAsServer(name,
            ringCapacity: 64 * 1024, maxStreams: 4);
        var writer = connection.FrameWriter;
        Assert.That(writer, Is.Not.Null);

        Assert.That(writer!.TryPauseWriterLoop(), Is.True);
        try
        {
            var (queue, queueType, countProp) = GetPrivateQueue(writer);
            EnqueueFrameEntry(queue, queueType, FrameType.Message, streamId: 99u);
            Assert.That((int)countProp.GetValue(queue)!, Is.EqualTo(1));

            var drainMethod = GetDrainMethod();
            var task = Task.Run(() =>
            {
                Assert.That(ShmReaderThreadContext.IsOnReaderThread, Is.False);
                drainMethod.Invoke(writer, new object[] { 42u });
            });
            Assert.That(task.Wait(TimeSpan.FromSeconds(5)), Is.True);

            Assert.That((int)countProp.GetValue(queue)!, Is.EqualTo(0),
                "Off reader Thread, DrainAllQueued must drain ALL queued " +
                "entries (including foreign-stream Messages) — the guard " +
                "must NOT trigger when IsOnReaderThread is false.");
        }
        finally
        {
            writer.ResumeWriterLoop();
        }
    }

    // ----- Reflection helpers -----

    private static (object queue, Type queueType, PropertyInfo countProp)
        GetPrivateQueue(ShmFrameWriter writer)
    {
        var queueField = typeof(ShmFrameWriter).GetField("_queue",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(queueField, Is.Not.Null, "_queue field must exist");
        var queue = queueField!.GetValue(writer)!;
        var queueType = queue.GetType();
        var countProp = queueType.GetProperty("Count");
        Assert.That(countProp, Is.Not.Null, "ConcurrentQueue<T>.Count must exist");
        return (queue, queueType, countProp!);
    }

    private static MethodInfo GetDrainMethod()
    {
        var m = typeof(ShmFrameWriter).GetMethod("DrainAllQueued",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(m, Is.Not.Null,
            "DrainAllQueued method must exist (current signature: " +
            "private void DrainAllQueued(uint currentStreamId = 0))");
        return m!;
    }

    /// <summary>
    /// Seeds a frame entry with the given type + stream id. Empty
    /// payload so the down-stream Write* call only emits a 9-byte
    /// H2 frame header (fits trivially in 64KiB test rings).
    /// </summary>
    private static void EnqueueFrameEntry(object queue, Type queueType,
        FrameType type, uint streamId)
    {
        var frameEntryType = typeof(ShmFrameWriter).GetNestedType("FrameEntry",
            BindingFlags.NonPublic);
        Assert.That(frameEntryType, Is.Not.Null, "FrameEntry type must exist");

        var entry = Activator.CreateInstance(frameEntryType!)!;
        SetField(entry, frameEntryType!, "Type", type);
        SetField(entry, frameEntryType!, "StreamId", streamId);
        SetField(entry, frameEntryType!, "Flags", (byte)0);
        SetField(entry, frameEntryType!, "Length", 0);
        SetField(entry, frameEntryType!, "Payload", ReadOnlyMemory<byte>.Empty);

        var enqueueMethod = queueType.GetMethod("Enqueue", new[] { frameEntryType! });
        Assert.That(enqueueMethod, Is.Not.Null, "Enqueue method must exist on queue");
        enqueueMethod!.Invoke(queue, new[] { entry });
    }

    private static void SetField(object boxedStruct, Type type,
        string fieldName, object value)
    {
        var f = type.GetField(fieldName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.That(f, Is.Not.Null, $"FrameEntry.{fieldName} field must exist");
        f!.SetValue(boxedStruct, value);
    }
}
