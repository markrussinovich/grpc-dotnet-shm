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

using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Grpc.Net.SharedMemory.Synchronization;

/// <summary>
/// Linux SCM_RIGHTS-based file-descriptor handoff for the per-data-
/// segment eventfd waker.
/// </summary>
/// <remarks>
/// <para>
/// Wire-compatible with Go's <c>shm_fdpass_linux.go</c>:
/// </para>
/// <list type="bullet">
///   <item><description>
///     Per-segment Unix-domain socket at
///     <c>&lt;segment_path&gt;.fds.sock</c>.
///   </description></item>
///   <item><description>
///     Server sends a fixed 4-byte token (<c>"FDS\n"</c>) plus the
///     two eventfd file descriptors via SCM_RIGHTS in a single
///     sendmsg call, then closes the accepted connection.
///   </description></item>
///   <item><description>
///     Socket is chmod 0600 immediately after bind; every accept
///     additionally verifies SO_PEERCRED matches the server's UID.
///   </description></item>
///   <item><description>
///     Opener fast-fails on missing socket (creator hasn't enabled
///     the waker) so handshake doesn't stall.
///   </description></item>
/// </list>
/// </remarks>
internal static partial class LinuxFdPass
{
    /// <summary>4-byte handshake token. Wire compatible with Go.</summary>
    private static ReadOnlySpan<byte> HandshakeToken => "FDS\n"u8;

    /// <summary>
    /// Returns the per-segment fd-pass socket path for
    /// <paramref name="segmentFilePath"/>.
    /// </summary>
    internal static string SocketPathFor(string segmentFilePath)
        => segmentFilePath + ".fds.sock";
}

/// <summary>
/// Server side of the SCM_RIGHTS fd-pass protocol. Bound by the
/// creator after <see cref="EventfdRegistry.AllocateAndStash"/>;
/// accept loop runs until <see cref="Stop"/> is invoked from
/// <see cref="Segment.Dispose"/>.
/// </summary>
internal sealed partial class LinuxFdPassServer : IDisposable
{
    private readonly string _sockPath;
    private readonly int _creatorReadFd;
    private readonly int _openerReadFd;
    private readonly CancellationTokenSource _cts = new();
    private Socket? _listener;
    private Thread? _acceptThread;
    private int _disposed;
    // In-flight worker tracking: every accepted client is wrapped in a
    // Task whose reference we keep here, so Dispose can wait for
    // outstanding SendTokenAndFds calls to finish before the segment
    // tears down the eventfds we are passing. Without this the accept
    // thread alone is not enough — Accept can return, the worker queues,
    // we close the listener (accept thread joins), and Dispose proceeds
    // to free the eventfds while a worker is still inside sendmsg.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> _activeWorkers = new();

    private LinuxFdPassServer(string sockPath, int creatorReadFd, int openerReadFd)
    {
        _sockPath = sockPath;
        _creatorReadFd = creatorReadFd;
        _openerReadFd = openerReadFd;
    }

    /// <summary>
    /// Binds the per-segment Unix socket and starts the accept loop.
    /// </summary>
    /// <param name="segmentFilePath">Backing-file path of the segment
    /// (e.g., <c>/dev/shm/grpc_shm_NAME</c>). Socket lives at
    /// <c>segmentFilePath + ".fds.sock"</c>.</param>
    /// <param name="stash">Opener-side fd pair to serve over
    /// SCM_RIGHTS.</param>
    /// <returns>The running server, or <c>null</c> on failure (caller
    /// continues without cross-process eventfd support).</returns>
    public static LinuxFdPassServer? Start(string segmentFilePath, OpenerStash stash)
    {
        // Hard gate: the sendmsg/recvmsg msghdr layout in
        // LinuxFdPassWire is hand-rolled for 64-bit glibc. On 32-bit
        // Linux the struct offsets differ and we'd corrupt the kernel's
        // view. Refuse to start the server; opener will fall through
        // to the futex wake path.
        if (!LinuxFdPassWire.IsSupported)
        {
            return null;
        }
        var sockPath = LinuxFdPass.SocketPathFor(segmentFilePath);
        // Best-effort unlink in case a prior server crashed mid-life.
        try { System.IO.File.Delete(sockPath); } catch { }

        Socket? listener = null;
        try
        {
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(sockPath));
            listener.Listen(8);

            // Tighten socket permissions to 0600 so only the segment
            // owner can dial in. Default /dev/shm umask would otherwise
            // create a world-accessible socket — any local UID could
            // receive the eventfd duplicates and flood our wake counter.
            if (chmod(sockPath, 0x180 /* 0600 */) != 0)
            {
                throw new System.IO.IOException(
                    $"fdpass: chmod {sockPath} 0600 failed (errno={Marshal.GetLastWin32Error()})");
            }
        }
        catch
        {
            listener?.Dispose();
            try { System.IO.File.Delete(sockPath); } catch { }
            return null;
        }

        var srv = new LinuxFdPassServer(sockPath, stash.CreatorReadFd, stash.OpenerReadFd)
        {
            _listener = listener,
        };
        srv._acceptThread = new Thread(srv.AcceptLoop)
        {
            IsBackground = true,
            Name = $"shm-fdpass-{stash.SegmentName}",
        };
        srv._acceptThread.Start();
        return srv;
    }

    /// <summary>
    /// Signals the accept thread to exit and unlinks the socket file.
    /// Closing the listener triggers an <c>EBADF</c> in <c>Accept</c>
    /// so the thread observes the cancel and returns.
    /// </summary>
    public void Stop() => Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cts.Cancel(); } catch { }
        try { _listener?.Dispose(); } catch { }
        try { System.IO.File.Delete(_sockPath); } catch { }
        // Join the accept thread so any in-flight Accept / sendmsg
        // completes (or aborts with EBADF after _listener.Dispose) before
        // the segment teardown frees the eventfds we are passing.
        // Without this join, a concurrent dialer could race the segment
        // dispose, observe partial SCM_RIGHTS data, and either silently
        // miss the FDs or crash the host on race-y free.
        var t = _acceptThread;
        _acceptThread = null;
        if (t != null && t.IsAlive)
        {
            // 500 ms is generous; Accept returns immediately once the
            // listener is closed.
            t.Join(500);
        }
        // Wait for in-flight workers — they are still inside SendTokenAndFds
        // and would dereference the about-to-be-closed eventfds.
        // Each worker has a 5 sec SO_SNDTIMEO so the bound is well-defined.
        try
        {
            var pending = _activeWorkers.Keys.ToArray();
            if (pending.Length > 0)
            {
                Task.WaitAll(pending, TimeSpan.FromSeconds(6));
            }
        }
        catch { /* best-effort drain */ }
        _cts.Dispose();
    }

    private void AcceptLoop()
    {
        var ownerUid = (uint)getuid();
        while (!_cts.IsCancellationRequested)
        {
            Socket conn;
            try
            {
                conn = _listener!.Accept();
            }
            catch
            {
                // Listener disposed (Dispose path) or transient error.
                return;
            }

            // Hand off to a worker Task so a slow opener doesn't stall
            // other concurrent opens. We use a TaskCompletionSource so the
            // Task reference is published into _activeWorkers BEFORE the
            // worker body has a chance to run-and-remove (otherwise a fast
            // worker can complete its TryRemove against an empty dict and
            // leave a stale entry once the late TryAdd lands).
            //
            // We do NOT pass _cts.Token to StartNew: if Dispose cancels
            // the CTS between Accept and StartNew, a cancelled task body
            // never runs and the accepted socket would leak its fd. The
            // worker body itself observes Dispose via the Worker.Run
            // try/finally which always disposes _conn.
            var worker = new Worker(conn, _creatorReadFd, _openerReadFd, ownerUid);
            var tcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _activeWorkers.TryAdd(tcs.Task, 0);
            ThreadPool.UnsafeQueueUserWorkItem(static s =>
                {
                    var (w, t, dict) = s;
                    try { w.Run(); }
                    finally
                    {
                        dict.TryRemove(t.Task, out _);
                        t.TrySetResult(true);
                    }
                },
                (worker, tcs, _activeWorkers),
                preferLocal: false);
        }
    }

    private sealed class Worker
    {
        private readonly Socket _conn;
        private readonly int _fd0;
        private readonly int _fd1;
        private readonly uint _ownerUid;

        public Worker(Socket conn, int fd0, int fd1, uint ownerUid)
        {
            _conn = conn;
            _fd0 = fd0;
            _fd1 = fd1;
            _ownerUid = ownerUid;
        }

        public void Run()
        {
            try
            {
                var rawFd = (int)_conn.SafeHandle.DangerousGetHandle().ToInt64();

                // Verify SO_PEERCRED.uid matches our UID (second-line
                // defence after chmod 0600).
                if (!CheckPeerUid(rawFd, _ownerUid))
                {
                    return;
                }

                // 5 sec write timeout via SO_SNDTIMEO so a stuck opener
                // doesn't pin the worker thread.
                _conn.SendTimeout = 5000;

                LinuxFdPassWire.SendTokenAndFds(rawFd, _fd0, _fd1);
            }
            catch
            {
                // Best-effort: any failure means this opener will fall
                // through to its own retry / futex path.
            }
            finally
            {
                try { _conn.Dispose(); } catch { }
            }
        }

        private static unsafe bool CheckPeerUid(int sockFd, uint expectedUid)
        {
            Ucred ucred;
            uint len = (uint)sizeof(Ucred);
            // SOL_SOCKET = 1, SO_PEERCRED = 17 on Linux.
            var rc = getsockopt(sockFd, 1, 17, &ucred, &len);
            if (rc != 0 || len != (uint)sizeof(Ucred)) return false;
            return ucred.uid == expectedUid;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Ucred
    {
        public int pid;
        public uint uid;
        public uint gid;
    }

    [LibraryImport("libc", EntryPoint = "chmod", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int chmod(string path, uint mode);

    [LibraryImport("libc", EntryPoint = "getuid", SetLastError = true)]
    private static partial int getuid();

    [LibraryImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static unsafe partial int getsockopt(int sockfd, int level, int optname, Ucred* optval, uint* optlen);
}

/// <summary>
/// Client side of the SCM_RIGHTS fd-pass protocol. Used by
/// <see cref="Segment.Open"/> when the same-process stash claim misses
/// — i.e., the creator lives in a different process.
/// </summary>
internal static partial class LinuxFdPassClient
{
    /// <summary>
    /// Dials the per-segment fd-pass socket and receives the two
    /// eventfd descriptors via SCM_RIGHTS. The fd ordering matches
    /// the creator-side send order:
    /// <list type="bullet">
    ///   <item><description><c>fds[0]</c> = creator's recv fd
    ///   (opener writes here to wake creator → this is the opener's
    ///   <c>PeerReadFd</c>).</description></item>
    ///   <item><description><c>fds[1]</c> = opener's recv fd
    ///   (opener reads here to be woken → this is the opener's
    ///   <c>MyReadFd</c>).</description></item>
    /// </list>
    /// </summary>
    /// <param name="segmentFilePath">Backing-file path of the segment.
    /// Socket is derived as <c>segmentFilePath + ".fds.sock"</c>.</param>
    /// <returns>Two fds, in the (peer, my) ordering above. Returns
    /// <c>null</c> if the socket file is absent (fast-fail; opener
    /// falls back to futex) or if any subsequent step fails.</returns>
    public static int[]? TryReceive(string segmentFilePath)
    {
        // Same 64-bit gate as the server side — on 32-bit Linux we
        // cannot safely use the hand-rolled msghdr layout.
        if (!LinuxFdPassWire.IsSupported)
        {
            return null;
        }
        var sockPath = LinuxFdPass.SocketPathFor(segmentFilePath);
        if (!System.IO.File.Exists(sockPath))
        {
            // Creator either disabled the waker, hasn't bound yet, or
            // already torn down. Fast-fail; caller continues with the
            // futex / events fallback.
            return null;
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        Socket? conn = null;
        try
        {
            conn = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            // Brief retry: the server may have just bound but not yet
            // reached Accept when we arrive (same-process race).
            while (true)
            {
                try
                {
                    conn.Connect(new UnixDomainSocketEndPoint(sockPath));
                    break;
                }
                catch (SocketException)
                {
                    if (DateTime.UtcNow >= deadline) return null;
                    Thread.Sleep(5);
                }
            }

            conn.ReceiveTimeout = 5000;
            var rawFd = (int)conn.SafeHandle.DangerousGetHandle().ToInt64();
            return LinuxFdPassWire.RecvTokenAndFds(rawFd);
        }
        catch
        {
            return null;
        }
        finally
        {
            conn?.Dispose();
        }
    }
}

/// <summary>
/// Raw sendmsg/recvmsg wire codec for the
/// "<c>"FDS\n"</c> + 2 fds via SCM_RIGHTS" exchange. Glibc-compatible
/// struct layout on 64-bit Linux (x86_64, aarch64). 32-bit Linux is
/// NOT supported by this hand-rolled binary layout (struct offsets
/// differ); callers must check <see cref="IsSupported"/> first.
/// </summary>
internal static partial class LinuxFdPassWire
{
    /// <summary>
    /// Returns <c>true</c> when the current process can use the
    /// SCM_RIGHTS wire codec safely (64-bit pointer + size_t). On 32-bit
    /// Linux (e.g. armhf, i686) the hand-rolled <c>struct msghdr</c>
    /// offsets do not match libc; the caller must fall back to the
    /// futex wake path.
    /// </summary>
    public static bool IsSupported => IntPtr.Size == 8;

    // Layouts (Linux glibc, 64-bit):
    //   struct iovec { void *iov_base; size_t iov_len; }            -> 16 B
    //   struct msghdr {
    //       void *msg_name;       size_t msg_namelen;               (16 B with 4 B pad)
    //       struct iovec *msg_iov; size_t msg_iovlen;                (16 B)
    //       void *msg_control;    size_t msg_controllen;             (16 B)
    //       int msg_flags;                                            (4 B + 4 pad)
    //   }                                                            -> 56 B total
    //   struct cmsghdr {
    //       size_t cmsg_len;       (8 B)
    //       int cmsg_level;        (4 B)
    //       int cmsg_type;         (4 B)
    //   }                                                            -> 16 B header
    //
    // CMSG_ALIGN to sizeof(size_t) = 8.

    private const int SOL_SOCKET = 1;
    private const int SCM_RIGHTS = 1;
    // MSG_CMSG_CLOEXEC: Linux-specific recvmsg flag — kernel marks
    // every received SCM_RIGHTS fd with O_CLOEXEC atomically, so the
    // fds cannot leak into a forked child between recvmsg() and our
    // own fcntl(F_SETFD) call. Defined in <sys/socket.h>.
    private const int MSG_CMSG_CLOEXEC = 0x40000000;
    private const int CmsgHeaderSize = 16;
    private const int CmsgPayload2Fds = 8; // 2 * sizeof(int)
    private const int CmsgSpace2Fds = CmsgHeaderSize + CmsgPayload2Fds; // already 8-aligned

    private const int MsgHdrSize = 56;
    private const int MsgHdrOffControl = 32;
    private const int MsgHdrOffControlLen = 40;

    [StructLayout(LayoutKind.Sequential)]
    private struct Iovec
    {
        public IntPtr base_;
        public UIntPtr len;
    }

    /// <summary>
    /// Sends the 4-byte handshake token plus two file descriptors via
    /// SCM_RIGHTS on the connected Unix socket <paramref name="sockFd"/>.
    /// Throws on partial writes / kernel errors.
    /// </summary>
    public static unsafe void SendTokenAndFds(int sockFd, int fd0, int fd1)
    {
        // Token payload: 4 bytes ("FDS\n").
        Span<byte> token = stackalloc byte[4] { (byte)'F', (byte)'D', (byte)'S', (byte)'\n' };

        // Control buffer: cmsghdr (16) + 2 fds (8).
        Span<byte> ctl = stackalloc byte[CmsgSpace2Fds];
        ctl.Clear();

        fixed (byte* pToken = token)
        fixed (byte* pCtl = ctl)
        {
            var iov = new Iovec { base_ = (IntPtr)pToken, len = (UIntPtr)token.Length };

            // cmsg_len = sizeof(cmsghdr) + payload = 16 + 8 = 24
            *(ulong*)(pCtl + 0) = (ulong)(CmsgHeaderSize + CmsgPayload2Fds);
            *(int*)(pCtl + 8) = SOL_SOCKET;
            *(int*)(pCtl + 12) = SCM_RIGHTS;
            *(int*)(pCtl + 16) = fd0;
            *(int*)(pCtl + 20) = fd1;

            Span<byte> msghdr = stackalloc byte[MsgHdrSize];
            msghdr.Clear();
            fixed (byte* pHdr = msghdr)
            {
                // msg_iov + msg_iovlen at offset 16.
                *(IntPtr*)(pHdr + 16) = (IntPtr)(&iov);
                *(ulong*)(pHdr + 24) = 1;
                // msg_control + msg_controllen at offset 32 / 40.
                *(IntPtr*)(pHdr + MsgHdrOffControl) = (IntPtr)pCtl;
                *(ulong*)(pHdr + MsgHdrOffControlLen) = (ulong)CmsgSpace2Fds;

                long sent;
                do { sent = sendmsg(sockFd, pHdr, 0); }
                while (sent == -1 && Marshal.GetLastWin32Error() == EINTR);
                if (sent != token.Length)
                {
                    throw new System.IO.IOException(
                        $"fdpass: sendmsg returned {sent} (errno={Marshal.GetLastWin32Error()})");
                }
            }
        }
    }

    /// <summary>
    /// Reads the 4-byte token and two file descriptors via SCM_RIGHTS
    /// on the connected Unix socket <paramref name="sockFd"/>. Returns
    /// the (peer, my) fd pair in the same order the server sent them.
    /// </summary>
    public static unsafe int[] RecvTokenAndFds(int sockFd)
    {
        Span<byte> token = stackalloc byte[4];
        // Allow a little extra control space in case the kernel adds
        // padding.
        Span<byte> ctl = stackalloc byte[64];
        ctl.Clear();

        fixed (byte* pToken = token)
        fixed (byte* pCtl = ctl)
        {
            var iov = new Iovec { base_ = (IntPtr)pToken, len = (UIntPtr)token.Length };

            Span<byte> msghdr = stackalloc byte[MsgHdrSize];
            msghdr.Clear();
            fixed (byte* pHdr = msghdr)
            {
                *(IntPtr*)(pHdr + 16) = (IntPtr)(&iov);
                *(ulong*)(pHdr + 24) = 1;
                *(IntPtr*)(pHdr + MsgHdrOffControl) = (IntPtr)pCtl;
                *(ulong*)(pHdr + MsgHdrOffControlLen) = (ulong)ctl.Length;

                long n;
                // MSG_CMSG_CLOEXEC = 0x40000000 (Linux): kernel sets the
                // O_CLOEXEC flag on every received fd atomically, so the
                // fds we get back cannot leak into a child spawned by
                // another thread between recvmsg and our own fcntl call.
                do { n = recvmsg(sockFd, pHdr, MSG_CMSG_CLOEXEC); }
                while (n == -1 && Marshal.GetLastWin32Error() == EINTR);
                if (n != token.Length)
                {
                    throw new System.IO.IOException(
                        $"fdpass: recvmsg returned {n} (errno={Marshal.GetLastWin32Error()})");
                }

                if (token[0] != (byte)'F' || token[1] != (byte)'D'
                    || token[2] != (byte)'S' || token[3] != (byte)'\n')
                {
                    throw new System.IO.IOException(
                        $"fdpass: bad handshake token: 0x{token[0]:X2}{token[1]:X2}{token[2]:X2}{token[3]:X2}");
                }

                // Walk the control buffer for our SCM_RIGHTS cmsg.
                var ctlLen = (ulong)(*(ulong*)(pHdr + MsgHdrOffControlLen));
                if (ctlLen < CmsgHeaderSize)
                {
                    throw new System.IO.IOException("fdpass: no control message");
                }
                var cLen = *(ulong*)(pCtl + 0);
                var cLevel = *(int*)(pCtl + 8);
                var cType = *(int*)(pCtl + 12);
                if (cLevel != SOL_SOCKET || cType != SCM_RIGHTS)
                {
                    throw new System.IO.IOException(
                        $"fdpass: unexpected cmsg level={cLevel} type={cType}");
                }
                var payloadLen = (long)cLen - CmsgHeaderSize;
                if (payloadLen < 4 || payloadLen % 4 != 0)
                {
                    throw new System.IO.IOException(
                        $"fdpass: bad payload len {payloadLen}");
                }
                var nFds = (int)(payloadLen / 4);
                if (nFds != 2)
                {
                    // Close any fds we received before erroring out so
                    // we don't leak them into the caller's process.
                    for (var i = 0; i < nFds; i++)
                    {
                        var fd = *(int*)(pCtl + CmsgHeaderSize + i * 4);
                        if (fd >= 0) _ = close(fd);
                    }
                    throw new System.IO.IOException(
                        $"fdpass: expected 2 fds, got {nFds}");
                }
                return new[]
                {
                    *(int*)(pCtl + CmsgHeaderSize + 0),
                    *(int*)(pCtl + CmsgHeaderSize + 4),
                };
            }
        }
    }

    private const int EINTR = 4;

    [LibraryImport("libc", EntryPoint = "sendmsg", SetLastError = true)]
    private static unsafe partial long sendmsg(int sockfd, byte* msg, int flags);

    [LibraryImport("libc", EntryPoint = "recvmsg", SetLastError = true)]
    private static unsafe partial long recvmsg(int sockfd, byte* msg, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int close(int fd);
}

#endif
