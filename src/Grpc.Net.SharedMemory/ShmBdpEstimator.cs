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

namespace Grpc.Net.SharedMemory;

/// <summary>
/// BDP (Bandwidth-Delay Product) estimator for the shared memory transport.
/// Mirrors the bdpEstimator from HTTP/2 but adapted for shared memory.
/// 
/// RFC A73 Phase 5: Flow Control Alignment
/// The shmem transport shares flow control settings with HTTP/2 configuration,
/// using the same initial window sizes and dynamic BDP estimation algorithm.
/// 
/// Key constants (shared with HTTP/2):
/// - alpha = 0.9: Smoothing factor for RTT estimation
/// - beta = 0.66: Threshold for BDP increase trigger
/// - gamma = 2: Multiplicative factor for BDP growth
/// - bdpLimit = 1 GiB: Maximum BDP window
/// </summary>
public sealed class ShmBdpEstimator
{
    /// <summary>
    /// Maximum BDP limit (1 GiB), matching InitialWindowSize.
    /// For shared memory the ring buffer provides natural backpressure,
    /// so the BDP estimator quickly settles at this limit.
    /// </summary>
    public const uint BdpLimit = 1024u * 1024 * 1024;

    /// <summary>
    /// Smoothing factor for RTT estimation (exponential moving average).
    /// </summary>
    private const double Alpha = 0.9;

    /// <summary>
    /// Threshold for BDP increase. If sample >= beta * bdp, consider increasing.
    /// </summary>
    private const double Beta = 0.66;

    /// <summary>
    /// Multiplicative factor for BDP growth.
    /// </summary>
    private const double Gamma = 2.0;

    /// <summary>
    /// BDP ping data for identification.
    /// </summary>
    public static readonly byte[] BdpPingData = { 2, 4, 16, 16, 9, 14, 7, 7 };

    private readonly object _lock = new();
    private readonly Action<uint>? _updateFlowControl;

    // Fast path fields - accessed atomically without lock
    private int _settled; // 1 when BDP estimation is complete
    private uint _sampleAtomic;
    private int _isSentAtomic; // 1 when a BDP ping is outstanding

    // Slow path fields - protected by mutex
    private uint _bdp;
    private double _bwMax;
    private DateTime _sentAt;
    private ulong _sampleCount;
    private double _rtt;

    /// <summary>
    /// Gets the current BDP estimate in bytes.
    /// </summary>
    public uint CurrentBdp
    {
        get
        {
            lock (_lock)
            {
                return _bdp;
            }
        }
    }

    /// <summary>
    /// Gets whether BDP estimation has settled at the maximum.
    /// </summary>
    public bool IsSettled => Volatile.Read(ref _settled) != 0;

    /// <summary>
    /// Creates a new BDP estimator.
    /// </summary>
    /// <param name="initialWindow">The initial window size.</param>
    /// <param name="updateFlowControl">Callback when BDP estimate changes.</param>
    public ShmBdpEstimator(uint initialWindow, Action<uint>? updateFlowControl = null)
    {
        _bdp = initialWindow;
        _updateFlowControl = updateFlowControl;
    }

    /// <summary>
    /// Adds bytes to the current sample. Returns true if a BDP ping should be sent.
    /// This is the hot path - optimized to avoid mutex in common cases.
    /// </summary>
    /// <param name="n">Number of bytes received.</param>
    /// <returns>True if a BDP ping should be sent.</returns>
    public bool Add(uint n)
    {
        // Fast path: if already settled at bdpLimit, nothing to do
        if (Volatile.Read(ref _settled) != 0)
        {
            return false;
        }

        // Fast path: if a ping is already sent, just accumulate sample atomically
        if (Volatile.Read(ref _isSentAtomic) != 0)
        {
            Interlocked.Add(ref _sampleAtomic, n);
            return false;
        }

        // Slow path: need to initiate a new measurement cycle
        lock (_lock)
        {
            // Double-check after acquiring lock
            if (_bdp == BdpLimit)
            {
                Volatile.Write(ref _settled, 1);
                return false;
            }

            // Check again if another thread already set isSent
            if (Volatile.Read(ref _isSentAtomic) != 0)
            {
                Interlocked.Add(ref _sampleAtomic, n);
                return false;
            }

            // Start new measurement
            Volatile.Write(ref _isSentAtomic, 1);
            Volatile.Write(ref _sampleAtomic, n);
            _sentAt = DateTime.MinValue;
            _sampleCount++;
            return true;
        }
    }

    /// <summary>
    /// Records the time when a BDP ping is sent.
    /// </summary>
    public void Timesnap()
    {
        lock (_lock)
        {
            _sentAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Updates the BDP estimate when a BDP ping ack is received.
    /// </summary>
    public void Calculate()
    {
        lock (_lock)
        {
            if (_sentAt == DateTime.MinValue)
            {
                return;
            }

            var rttSample = (DateTime.UtcNow - _sentAt).TotalSeconds;

            // Bootstrap RTT with an average of first 10 samples.
            if (_sampleCount < 10)
            {
                _rtt += (rttSample - _rtt) / _sampleCount;
            }
            else
            {
                // Exponential moving average for subsequent samples.
                _rtt += (rttSample - _rtt) * Alpha;
            }

            // Read and reset atomic sample
            var sample = Interlocked.Exchange(ref _sampleAtomic, 0);
            Volatile.Write(ref _isSentAtomic, 0);

            // Avoid division by zero
            if (_rtt <= 0)
            {
                return;
            }

            // The sample is at most 1.5x the real BDP on a saturated connection.
            var bwCurrent = sample / (_rtt * 1.5);
            if (bwCurrent > _bwMax)
            {
                _bwMax = bwCurrent;
            }

            // Update BDP if the sample suggests higher capacity.
            if (sample >= Beta * _bdp && Math.Abs(bwCurrent - _bwMax) < 0.001 && _bdp != BdpLimit)
            {
                _bdp = (uint)(Gamma * sample);
                if (_bdp > BdpLimit)
                {
                    _bdp = BdpLimit;
                    Volatile.Write(ref _settled, 1);
                }

                var bdp = _bdp;
                // Call callback outside lock to avoid deadlock
                Task.Run(() => _updateFlowControl?.Invoke(bdp));
            }
        }
    }
}
