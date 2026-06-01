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

namespace Grpc.Net.SharedMemory;

/// <summary>
/// Thread-local marker identifying frames being processed on the SHM
/// frame-reader thread. Used by outbound send paths to detect — and hop
/// off — when the user's inline-receive continuation is about to issue a
/// flow-controlled blocking send that would otherwise park the reader
/// thread on <c>_sendQuotaWake</c> and deadlock the connection (no
/// thread to deliver the peer's <c>WINDOW_UPDATE</c> that would
/// release us).
/// </summary>
/// <remarks>
/// <para>
/// Set via <see cref="Enter"/> at the top of the reader loop
/// (<c>ShmConnection.FrameReaderLoopAsync</c>) and cleared by disposing
/// the returned <see cref="Scope"/>. The depth counter is reentrancy-safe
/// — nested <see cref="Enter"/> calls increment/decrement symmetrically
/// — although the reader loop is single-call at present.
/// </para>
/// <para>
/// Used by <c>ShmGrpcStream.SendMessageAsync</c> to pre-flight a
/// <c>Task.Yield()</c> hop off the reader thread when (a) the current
/// thread is the reader thread and (b) the upcoming write may block on
/// per-stream or per-connection send quota. Without the hop, an inline
/// MoveNext continuation that echoes a flow-controlled body would
/// silently deadlock the entire connection.
/// </para>
/// <para>
/// <b>Tripwire:</b> <c>ShmGrpcStream.ReserveSendQuotaOrBlock</c>
/// asserts <c>!IsOnReaderThread</c> in DEBUG builds immediately before
/// <c>_sendQuotaWake.Wait</c>, catching any send call site that
/// reaches the blocking path without first calling the pre-flight hop.
/// </para>
/// </remarks>
internal static class ShmReaderThreadContext
{
    [ThreadStatic]
    private static int t_depth;

    /// <summary>
    /// True when the current thread is currently inside the
    /// <see cref="Enter"/> scope of the SHM frame-reader loop. Cheap:
    /// one TLS read + branch.
    /// </summary>
    /// <remarks>
    /// Uses strict <c>&gt; 0</c> rather than <c>!= 0</c>: a future bug
    /// causing an unbalanced <see cref="Scope.Dispose"/> on a non-reader
    /// thread would otherwise leave that thread reporting "on reader
    /// thread" forever (negative count). With the strict check, a
    /// negative count reads as <see langword="false"/>, which is the
    /// safer wrong answer (a missed hop fires the DEBUG assert at
    /// <c>ShmGrpcStream.ReserveSendQuotaOrBlock</c> only on a real
    /// reader thread, not on innocent pool threads).
    /// </remarks>
    public static bool IsOnReaderThread => t_depth > 0;

    /// <summary>
    /// Marks the current thread as the SHM frame-reader thread for
    /// the lifetime of the returned <see cref="Scope"/>. Dispose the
    /// scope (typically via <c>using var _ = Enter();</c>) to clear
    /// the marker.
    /// <para>
    /// <b>Hard rule:</b> the body inside an <see cref="Enter"/> scope
    /// MUST NOT <c>await</c> anything that can resume on a different
    /// thread. The <c>t_depth</c> counter is <c>[ThreadStatic]</c>;
    /// a resume on a pool thread would <c>t_depth--</c> on a thread
    /// that never <c>t_depth++</c>'d. The lone call site at
    /// <c>ShmConnection.FrameReaderLoopAsync</c> wraps a fully
    /// synchronous <c>ProcessFrame</c>, which is the only supported
    /// usage.
    /// </para>
    /// </summary>
    public static Scope Enter()
    {
        t_depth++;
        return default;
    }

    /// <summary>
    /// Decrement-on-dispose RAII scope returned by <see cref="Enter"/>.
    /// Symmetric with the <c>t_depth++</c> in <c>Enter</c>; multiple
    /// nested scopes on the same thread compose correctly.
    /// </summary>
    public readonly struct Scope : IDisposable
    {
        public void Dispose() => t_depth--;
    }
}
