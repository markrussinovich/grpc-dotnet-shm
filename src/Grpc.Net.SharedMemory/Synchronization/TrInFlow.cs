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

namespace Grpc.Net.SharedMemory.Synchronization;

/// <summary>
/// Connection-level inbound flow control bookkeeping for the receiver
/// side of a SHM transport. Tracks total DATA bytes received across
/// all streams against the conn-level window we advertised, and paces
/// connection-level <c>WINDOW_UPDATE</c> drip emission.
/// </summary>
/// <remarks>
/// <para>
/// Direct port of grpc-go-shmem <c>internal/transport/flowcontrol.go</c>
/// type <c>trInFlow</c>, after the post-PR simplification (commit
/// <c>4ae85302e</c> "drop conn pre-credit") that removed the
/// <c>delta</c>/<c>settleDebt</c>/<c>maybeAdjust</c> fields. The
/// rationale: the SHM receiver continuously drains DATA bytes from
/// the ring as each frame arrives (advancing <c>ReadIdx</c>),
/// independent of the application's read pace. Conn-level
/// drip-on-receive is therefore sufficient — no pre-credit needed.
/// </para>
/// <para>
/// Wire semantics match RFC 7540 §5.2 + §6.9. The receiver-driven
/// drip cadence at <c>limit/4</c> is preserved for stock HTTP/2
/// interop. SHM may emit at a SHM-tuned threshold instead (e.g.
/// 8 MiB; tracked outside this class via the transport's own
/// <c>wuThreshold</c>); this class returns the stock <c>limit/4</c>
/// signal which callers may ignore in favor of their own cadence.
/// </para>
/// <para>
/// All mutations are <c>lock</c>-protected; <c>EffectiveWindowSize</c>
/// is exposed via <see cref="Interlocked.Read"/> on a separate
/// <c>long</c> field for lock-free observation by the BDP estimator
/// path (matches Go's <c>atomic.LoadUint32</c> on
/// <c>effectiveWindowSize</c>).
/// </para>
/// </remarks>
internal sealed class TrInFlow
{
    private readonly object _lock = new();

    /// <summary>Baseline conn-level inbound limit.</summary>
    private uint _limit;

    /// <summary>Bytes received across all streams but not yet acknowledged.</summary>
    private uint _unacked;

    /// <summary>
    /// Lock-free snapshot of the current effective window size
    /// (<c>limit - unacked</c>); updated on every mutation. Read by
    /// the BDP estimator from arbitrary threads.
    /// </summary>
    private long _effectiveWindowSize;

    /// <summary>
    /// Creates a new conn-level inbound FC tracker with the supplied
    /// initial limit (in bytes).
    /// </summary>
    public TrInFlow(uint initialLimit)
    {
        _limit = initialLimit;
        Interlocked.Exchange(ref _effectiveWindowSize, initialLimit);
    }

    /// <summary>Gets the baseline limit (for diagnostics).</summary>
    public uint Limit
    {
        get { lock (_lock) { return _limit; } }
    }

    /// <summary>Gets unacked bytes (for diagnostics).</summary>
    public uint Unacked
    {
        get { lock (_lock) { return _unacked; } }
    }

    /// <summary>
    /// Lock-free read of current effective window size
    /// (<c>limit - unacked</c>). May be slightly stale relative to a
    /// concurrent <see cref="OnData"/> call. Used by the BDP estimator.
    /// </summary>
    public uint EffectiveWindowSize => (uint)Interlocked.Read(ref _effectiveWindowSize);

    /// <summary>
    /// Updates the baseline conn-level limit (e.g. on BDP-driven
    /// window growth). Returns the delta that must be advertised to
    /// the peer as a <c>WINDOW_UPDATE</c>.
    /// </summary>
    public uint NewLimit(uint n)
    {
        lock (_lock)
        {
            var d = n - _limit;
            _limit = n;
            UpdateEffectiveWindowSizeLocked();
            return d;
        }
    }

    /// <summary>
    /// Called when an inbound DATA frame is parsed. Accumulates
    /// <paramref name="n"/> bytes against the conn-level limit and
    /// returns the conn-level <c>WINDOW_UPDATE</c> drip increment to
    /// emit if <c>unacked &gt;= limit / 4</c>, or 0 otherwise.
    /// </summary>
    /// <remarks>
    /// Matches stock HTTP/2 conn-level drip cadence (RFC 7540 §6.9.2).
    /// SHM callers may choose to ignore this return value and drive
    /// WU emission off a separate SHM-tuned threshold; <see cref="Reset"/>
    /// can be invoked then to flush the accumulator and prevent
    /// double-counting.
    /// </remarks>
    public uint OnData(uint n)
    {
        lock (_lock)
        {
            _unacked += n;
            UpdateEffectiveWindowSizeLocked();
            if (_unacked < _limit / 4)
            {
                return 0;
            }
            return ResetLocked();
        }
    }

    /// <summary>
    /// Flushes the accumulated unacked counter and returns the value
    /// (in bytes). The caller is expected to emit a conn-level
    /// <c>WINDOW_UPDATE</c> with this increment. Idempotent: a second
    /// call without intervening <see cref="OnData"/> returns 0.
    /// </summary>
    public uint Reset()
    {
        lock (_lock)
        {
            return ResetLocked();
        }
    }

    private uint ResetLocked()
    {
        var u = _unacked;
        _unacked = 0;
        UpdateEffectiveWindowSizeLocked();
        return u;
    }

    private void UpdateEffectiveWindowSizeLocked()
    {
        var eff = (long)_limit - _unacked;
        if (eff < 0) eff = 0;
        Interlocked.Exchange(ref _effectiveWindowSize, eff);
    }
}
