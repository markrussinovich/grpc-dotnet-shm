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
/// Per-stream inbound flow control bookkeeping for the receiver side
/// of a SHM transport. Tracks how many DATA bytes the peer has sent
/// against the per-stream window we advertised, paces stream-level
/// <c>WINDOW_UPDATE</c> emission, and supports the SHM-specific
/// stream-level pre-credit at LPM-header parse time.
/// </summary>
/// <remarks>
/// <para>
/// Direct port of grpc-go-shmem <c>internal/transport/flowcontrol.go</c>
/// type <c>inFlow</c>. Wire semantics match RFC 7540 §5.2 + §6.9. The
/// SHM-specific deviation is the trigger location for stream-level
/// pre-credit: stock HTTP/2 emits pre-credit when the application
/// requests a read of <c>N</c> bytes (<c>maybeAdjust</c>); the SHM
/// codec aggregates DATA frames into a complete LPM before delivery
/// to the application, so the receiver fires <see cref="MaybeAdjustAdditive"/>
/// at LPM-header parse time instead. The wire effect is identical to
/// stock HTTP/2 pre-credit; only the trigger location moves earlier.
/// </para>
/// <para>
/// <b>Field invariant</b>: total receive capacity within current
/// enforcement bounds is <c>limit + delta - (pendingData + pendingUpdate)</c>.
/// <see cref="OnData"/> trips <c>FLOW_CONTROL_ERROR</c> when this goes
/// negative (RFC 7540 §6.9.1, §5.2.2).
/// </para>
/// <para>
/// All mutations are <c>lock</c>-protected to match the Go reference
/// implementation. The lock is fine-grained (one per stream) and held
/// only across the bookkeeping update; the WU emission itself is
/// performed by the caller outside the lock.
/// </para>
/// </remarks>
internal sealed class InFlow
{
    /// <summary>
    /// Maximum window size (HTTP/2 31-bit ceiling per RFC 7540 §6.9.1).
    /// Equal to <see cref="int.MaxValue"/>.
    /// </summary>
    public const uint MaxWindowSize = int.MaxValue;

    private readonly object _lock = new();

    /// <summary>Baseline inbound limit advertised via SETTINGS_INITIAL_WINDOW_SIZE.</summary>
    private uint _limit;

    /// <summary>Bytes received in DATA frames but not yet consumed by the application.</summary>
    private uint _pendingData;

    /// <summary>Bytes consumed by the application but not yet acknowledged in a WU.</summary>
    private uint _pendingUpdate;

    /// <summary>
    /// Extra pre-credit emitted by <see cref="MaybeAdjustAdditive"/>
    /// when an inbound LPM exceeds the baseline limit. Drained by
    /// <see cref="OnRead"/> as the application consumes bytes.
    /// </summary>
    private uint _delta;

    /// <summary>
    /// Creates a new per-stream inbound FC tracker with the supplied
    /// initial window limit (in bytes).
    /// </summary>
    public InFlow(uint initialLimit)
    {
        _limit = initialLimit;
    }

    /// <summary>Gets the baseline limit (for diagnostics; not part of hot path).</summary>
    public uint Limit
    {
        get { lock (_lock) { return _limit; } }
    }

    /// <summary>Gets the current pre-credit delta (for diagnostics).</summary>
    public uint Delta
    {
        get { lock (_lock) { return _delta; } }
    }

    /// <summary>Gets pendingData (for diagnostics).</summary>
    public uint PendingData
    {
        get { lock (_lock) { return _pendingData; } }
    }

    /// <summary>
    /// Updates the baseline limit (e.g. on SETTINGS change). Assumes
    /// <paramref name="n"/> is greater than the previous limit; behavior
    /// on downward change is unspecified (matches Go semantics).
    /// </summary>
    public void NewLimit(uint n)
    {
        lock (_lock) { _limit = n; }
    }

    /// <summary>
    /// SHM-specific additive pre-credit for the codec-driven path
    /// (<c>OnMessageStart</c> at LPM-header parse time). Returns the
    /// additional credit (bytes) the caller must emit as a stream-level
    /// <c>WINDOW_UPDATE</c>, or 0 if existing capacity already admits
    /// the LPM.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The "additive" wording is critical: stock <c>maybeAdjust</c> SETs
    /// <c>delta = n</c>, which loses outstanding pre-credit when two
    /// large LPMs pipeline on one stream and the application has not
    /// yet drained the first from the recvBuffer. Pre-credit hook fires
    /// per LPM at parse time, so multiple in-flight LPMs each issue a
    /// pre-credit request; SET would silently drop the first's debt and
    /// onData would falsely trip <c>FLOW_CONTROL_ERROR</c> on the
    /// second LPM's incoming DATA bytes.
    /// </para>
    /// <para>
    /// Cap: <c>limit + delta</c> never exceeds <see cref="MaxWindowSize"/>
    /// (HTTP/2 31-bit ceiling). Returns 0 when the cap is hit even though
    /// the LPM is not fully covered; the caller must treat this as a
    /// stream error and refuse the message (RFC 7540 §6.9.1).
    /// </para>
    /// </remarks>
    public uint MaybeAdjustAdditive(uint n)
    {
        // Clamp request to int32 to avoid signed-overflow arithmetic.
        if (n > int.MaxValue)
        {
            n = int.MaxValue;
        }

        lock (_lock)
        {
            // avail = remaining receive capacity inside current bounds.
            long avail = (long)_limit + _delta - _pendingData - _pendingUpdate;
            long need = (long)n - avail;
            if (need <= 0)
            {
                return 0;
            }

            // Cap so limit + delta does not exceed the HTTP/2 31-bit window.
            long headroom = (long)MaxWindowSize - _limit - _delta;
            if (need > headroom)
            {
                need = headroom;
            }
            if (need <= 0)
            {
                return 0;
            }

            _delta += (uint)need;
            return (uint)need;
        }
    }

    /// <summary>
    /// Records inbound DATA bytes received on the stream. Returns
    /// <see langword="null"/> if the receive is within window; returns
    /// an error message string if it exceeds <c>limit + delta</c>, which
    /// the caller MUST translate into a <c>RST_STREAM</c> with
    /// <c>FLOW_CONTROL_ERROR</c> per RFC 7540 §5.2.2.
    /// </summary>
    /// <param name="n">Number of DATA payload bytes (excluding H2 frame header).</param>
    /// <returns>
    /// <see langword="null"/> on success; a diagnostic message string
    /// describing the over-window receive if <c>FLOW_CONTROL_ERROR</c>
    /// should be raised.
    /// </returns>
    /// <remarks>
    /// Arithmetic is performed in <c>ulong</c> against the canonical
    /// <c>limit + delta</c> cap (max <c>2 * MaxWindowSize ~= 4 GiB</c>)
    /// so a hostile peer cannot drive the <c>uint</c> <c>_pendingData</c>
    /// past the wrap boundary and silently mask a quota violation.
    /// On violation, <c>_pendingData</c> is saturated at
    /// <see cref="uint.MaxValue"/> so subsequent receives also trip
    /// the check until the caller tears the stream down.
    /// </remarks>
    public string? OnData(uint n)
    {
        lock (_lock)
        {
            ulong newPending = (ulong)_pendingData + n;
            ulong total = newPending + _pendingUpdate;
            ulong cap = (ulong)_limit + _delta;
            if (total > cap)
            {
                // Saturate so a peer that keeps sending after the first
                // FLOW_CONTROL_ERROR cannot wrap _pendingData back below
                // the limit and silently re-pass this check.
                _pendingData = newPending > uint.MaxValue ? uint.MaxValue : (uint)newPending;
                return $"received {total}-bytes data exceeding the limit {cap} bytes";
            }
            _pendingData = (uint)newPending;
            return null;
        }
    }

    /// <summary>
    /// Records that the application has consumed <paramref name="n"/>
    /// bytes from a stream. Returns the stream-level <c>WINDOW_UPDATE</c>
    /// increment to emit, or 0 if below the drip threshold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Drains <c>delta</c> first (refunding any pre-credit emitted by
    /// <see cref="MaybeAdjustAdditive"/>) before accumulating toward
    /// <c>pendingUpdate</c>. Once <c>pendingUpdate &gt;= limit / 4</c>,
    /// flushes the accumulator and returns its value (caller emits the
    /// stream-level WU bypassing the per-frame batching of conn-level
    /// drip).
    /// </para>
    /// <para>
    /// The <c>limit/4</c> threshold matches the stock HTTP/2 cadence
    /// (per RFC 7540 §6.9.2 and consistent with the gRFC SHM v3.4+
    /// requirement that stream-level drip follow stock H2 semantics).
    /// </para>
    /// </remarks>
    public uint OnRead(uint n)
    {
        lock (_lock)
        {
            if (_pendingData == 0)
            {
                return 0;
            }
            // Caller-contract guard: in normal use n <= _pendingData
            // (the caller only reads what was previously OnData'd). Clamp
            // defensively so an off-by-one in the codec cannot underflow
            // _pendingData into a near-uint.MaxValue value, which would
            // make OnData appear over-quota forever after.
            if (n > _pendingData)
            {
                n = _pendingData;
            }
            _pendingData -= n;
            if (n > _delta)
            {
                n -= _delta;
                _delta = 0;
            }
            else
            {
                _delta -= n;
                n = 0;
            }
            _pendingUpdate += n;
            if (_pendingUpdate >= _limit / 4)
            {
                var wu = _pendingUpdate;
                _pendingUpdate = 0;
                return wu;
            }
            return 0;
        }
    }
}
