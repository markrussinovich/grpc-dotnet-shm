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

#if LINUX

using System.Collections.Concurrent;

namespace Grpc.Net.SharedMemory.Synchronization;

/// <summary>
/// Process-global stash that lets a same-process opener claim the
/// peer-side eventfd pair that the creator allocated during
/// <see cref="Segment.Create"/>. Cross-process openers cannot reach
/// this map and instead obtain duplicates via SCM_RIGHTS over the
/// per-segment Unix domain socket (see
/// <see cref="LinuxFdPassServer"/> / <see cref="LinuxFdPassClient"/>).
/// </summary>
/// <remarks>
/// Mirrors Go's <c>stashShmDataSegWakerForOpener</c> /
/// <c>claimShmDataSegWakerForOpener</c> /
/// <c>dropShmDataSegWakerStash</c> in
/// <c>internal/transport/shm_segment.go</c>.
/// </remarks>
internal static partial class EventfdRegistry
{
    private static readonly ConcurrentDictionary<string, OpenerStash> s_stash =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Allocates a fresh pair of eventfds and stashes the opener-side
    /// view for the matching same-process <see cref="Segment.Open"/>.
    /// </summary>
    /// <param name="segmentName">Key for the stash entry.</param>
    /// <returns>The creator-side waker (caller takes ownership and
    /// closes on segment dispose), and the raw <see cref="OpenerStash"/>
    /// (so the caller can serve it to cross-process openers via
    /// SCM_RIGHTS).</returns>
    public static (LinuxDataSegWaker creatorWaker, OpenerStash stash)
        AllocateAndStash(string segmentName)
    {
        ArgumentException.ThrowIfNullOrEmpty(segmentName);

        var efd1 = LinuxDataSegWaker.CreateEventfd();
        int efd2;
        try
        {
            efd2 = LinuxDataSegWaker.CreateEventfd();
        }
        catch
        {
            CloseFd(efd1);
            throw;
        }

        // Creator: reads efd1 (opener writes here to wake creator);
        //          writes to efd2 (opener reads here).
        var creator = new LinuxDataSegWaker(
            myReadFd: efd1, peerReadFd: efd2, ownsFds: true);

        // Opener-side stash view: reads efd2, writes efd1. Does NOT
        // own the fds (creator does).
        var stash = new OpenerStash
        {
            SegmentName = segmentName,
            CreatorReadFd = efd1,
            OpenerReadFd = efd2,
        };

        if (!s_stash.TryAdd(segmentName, stash))
        {
            creator.Dispose();
            throw new InvalidOperationException(
                $"Eventfd stash already present for segment '{segmentName}'.");
        }
        return (creator, stash);
    }

    /// <summary>
    /// Returns the opener-side waker for <paramref name="segmentName"/>
    /// without closing the descriptors (creator owns them). Removes
    /// the stash entry so a second Open call would have to fall back
    /// to SCM_RIGHTS / futex.
    /// </summary>
    public static LinuxDataSegWaker? TryClaimOpener(string segmentName)
    {
        if (!s_stash.TryRemove(segmentName, out var s)) return null;
        return new LinuxDataSegWaker(
            myReadFd: s.OpenerReadFd,
            peerReadFd: s.CreatorReadFd,
            ownsFds: false);
    }

    /// <summary>
    /// Removes any stash entry for <paramref name="segmentName"/>
    /// without closing the descriptors. Called by the creator from
    /// <see cref="Segment.Dispose"/> AFTER the data plane has been
    /// shut down so a late same-process opener does not pick up
    /// fds that are about to be closed.
    /// </summary>
    public static void Drop(string segmentName)
    {
        s_stash.TryRemove(segmentName, out _);
    }

    private static void CloseFd(int fd)
    {
        if (fd < 0) return;
        _ = LibcClose(fd);
    }

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "close")]
    private static partial int LibcClose(int fd);
}

/// <summary>
/// View of the creator-side eventfd pair as seen by an opener. The
/// integers are raw kernel descriptors owned by the creator; openers
/// reference them without taking ownership.
/// </summary>
internal sealed class OpenerStash
{
    public string SegmentName { get; init; } = "";

    /// <summary>
    /// Creator's recv fd. The opener writes here to wake the creator,
    /// i.e. this is the opener's peerReadFd.
    /// </summary>
    public int CreatorReadFd { get; init; }

    /// <summary>
    /// Opener's recv fd. The opener reads here to be parked / woken
    /// by the creator, i.e. this is the opener's myReadFd.
    /// </summary>
    public int OpenerReadFd { get; init; }
}

#endif
