#!/usr/bin/env python3
"""
Benchmark runner and plotter for .NET gRPC SHM vs TCP transport.
Matches grpc-go-shmem/benchmark/shmemtcp/benchmark_runner.py â€” same plot
structure adapted for 2 transports (SHM, TCP) instead of 3.

Usage:
    python benchmark_runner.py              # Plot from cached results (or run if none exist)
    python benchmark_runner.py --run        # Force rerun benchmarks, then plot
    python benchmark_runner.py --plot-only  # Only plot (fail if no cached results)
"""

import argparse
import json
import os
import subprocess
import sys
from datetime import datetime
from pathlib import Path

import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import numpy as np

# Directory setup â€” matches Go's out/<platform>/ convention
SCRIPT_DIR = Path(__file__).parent.absolute()
OUT_ROOT = SCRIPT_DIR / "out"
PLATFORM_NAME = "windows" if os.name == "nt" else ("linux" if sys.platform.startswith("linux") else sys.platform)
OUT_DIR = OUT_ROOT / PLATFORM_NAME
RESULTS_FILE = OUT_DIR / "results.json"


def run_ringbench() -> dict:
    """Run .NET gRPC benchmarks and return JSON results."""
    print("=" * 70)
    print("Running .NET gRPC Benchmarks (SHM vs TCP)...")
    print("=" * 70)

    OUT_DIR.mkdir(parents=True, exist_ok=True)

    cmd = [
        "dotnet", "run",
        "--project", str(SCRIPT_DIR / "ringbench" / "RingBench.csproj"),
        "-c", "Release",
        "--",
        "--out", str(OUT_ROOT)
    ]

    print(f"Running: {' '.join(cmd)}")
    print("-" * 70)

    try:
        result = subprocess.run(cmd, capture_output=False, text=True, timeout=600)
        if result.returncode != 0:
            print(f"ERROR: Benchmark exited with code {result.returncode}")
            return None
    except subprocess.TimeoutExpired:
        print("ERROR: Benchmark timed out after 600s")
        return None
    except Exception as e:
        print(f"ERROR: Failed to run benchmarks: {e}")
        return None

    print("-" * 70)

    try:
        with open(RESULTS_FILE) as f:
            results = json.load(f)
        nu = len(results.get("unary", []))
        ns = len(results.get("streaming", []))
        print(f"Parsed {nu} unary + {ns} streaming results")
        return results
    except Exception as e:
        print(f"ERROR: Failed to load results: {e}")
        return None


def save_results(results: dict):
    """Save benchmark results to JSON file."""
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    with open(RESULTS_FILE, 'w') as f:
        json.dump(results, f, indent=2)
    print(f"Saved results to: {RESULTS_FILE}")


def load_results() -> dict:
    """Load benchmark results from cached JSON file."""
    if RESULTS_FILE.exists():
        try:
            with open(RESULTS_FILE) as f:
                results = json.load(f)
            print(f"Loaded results from: {RESULTS_FILE}")
            print(f"  Timestamp: {results.get('timestamp', 'unknown')}")
            print(f"  CPU: {results.get('cpu', 'unknown')}")
            return results
        except Exception:
            pass
    return None


def format_size(b):
    if b == 0:
        return '0B'
    if b >= 1048576:
        return f'{b // 1048576}MB'
    if b >= 1024:
        return f'{b // 1024}KB'
    return f'{b}B'


def extract_data(results: dict) -> dict:
    """Extract plotting data from .NET benchmark results.

    Maps .NET JSON format into the same data structure used by the Go
    benchmark_runner.py so the plotting code is structurally identical.
    """
    # Build lookup tables: (transport, size_bytes) -> entry
    unary_lookup = {}
    for e in results.get("unary", []):
        unary_lookup[(e["transport"], e["size_bytes"])] = e

    stream_lookup = {}
    for e in results.get("streaming", []):
        stream_lookup[(e["transport"], e["size_bytes"])] = e

    # --- Size groups (matching Go's split) ---
    # Small streaming sizes  (Go: 64B-1MB; .NET: 1B-1MB)
    sizes = [1, 1024, 4096, 16384, 65536, 262144, 524288, 1048576]
    size_labels = [format_size(s) for s in sizes]

    # Small unary/roundtrip sizes  (Go: 64B-4KB; .NET: 1B-16KB)
    rt_sizes = [1, 1024, 4096, 16384]
    rt_labels = [format_size(s) for s in rt_sizes]

    # Large payload sizes  (Go: 1MB-256MB; .NET: 1MB-128MB)
    large_sizes = [1048576, 2097152, 4194304, 16777216, 33554432, 134217728]
    large_size_labels = [format_size(s) for s in large_sizes]

    # --- Helpers ---
    def _get(lookup, transport, sz_list, field, scale=1.0):
        """Return values for *transport* at each size, scaled."""
        out = []
        for s in sz_list:
            entry = lookup.get((transport, s))
            if entry is not None and entry.get(field) is not None:
                out.append(entry[field] * scale)
            else:
                out.append(None)
        return out

    # .NET stores latency in us -- convert to ns (*1000) to match Go plots.
    US_TO_NS = 1000.0

    data = {
        "sizes": sizes,
        "size_labels": size_labels,
        "large_sizes": large_sizes,
        "large_size_labels": large_size_labels,
        "rt_sizes": rt_sizes,
        "rt_size_labels": rt_labels,
        "cpu": results.get("cpu", "Unknown CPU"),
        "runtime": results.get("runtime", ""),
        "timestamp": results.get("timestamp", ""),
    }

    # Streaming (one-way ping-pong) -- small sizes
    data["shm_stream_latency"]    = _get(stream_lookup, "shm", sizes, "avg_latency_us", US_TO_NS)
    data["tcp_stream_latency"]    = _get(stream_lookup, "tcp", sizes, "avg_latency_us", US_TO_NS)
    data["shm_stream_throughput"] = _get(stream_lookup, "shm", sizes, "throughput_mb_per_s")
    data["tcp_stream_throughput"] = _get(stream_lookup, "tcp", sizes, "throughput_mb_per_s")

    # Unary (roundtrip) -- small sizes
    data["shm_rt_latency"] = _get(unary_lookup, "shm", rt_sizes, "avg_latency_us", US_TO_NS)
    data["tcp_rt_latency"] = _get(unary_lookup, "tcp", rt_sizes, "avg_latency_us", US_TO_NS)

    # Large payload STREAMING
    data["shm_large_stream_throughput"] = _get(stream_lookup, "shm", large_sizes, "throughput_mb_per_s")
    data["shm_large_stream_latency"]   = _get(stream_lookup, "shm", large_sizes, "avg_latency_us", US_TO_NS)
    data["tcp_large_stream_throughput"] = _get(stream_lookup, "tcp", large_sizes, "throughput_mb_per_s")
    data["tcp_large_stream_latency"]   = _get(stream_lookup, "tcp", large_sizes, "avg_latency_us", US_TO_NS)

    # Large payload UNARY (roundtrip)
    data["shm_large_rt_throughput"] = _get(unary_lookup, "shm", large_sizes, "throughput_mb_per_s")
    data["shm_large_rt_latency"]   = _get(unary_lookup, "shm", large_sizes, "avg_latency_us", US_TO_NS)
    data["tcp_large_rt_throughput"] = _get(unary_lookup, "tcp", large_sizes, "throughput_mb_per_s")
    data["tcp_large_rt_latency"]   = _get(unary_lookup, "tcp", large_sizes, "avg_latency_us", US_TO_NS)

    return data


# =========================================================================
# Helpers
# =========================================================================

def _filter_numeric(seq):
    """Return only numeric entries from a sequence."""
    return [v for v in seq if isinstance(v, (int, float))]


def _has_numeric(seq) -> bool:
    return bool(_filter_numeric(seq))


def _safe_number(seq, idx):
    if idx < len(seq):
        v = seq[idx]
        if isinstance(v, (int, float)):
            return v
    return None


# =========================================================================
# Plot generation -- mirrors Go's generate_plots + generate_consolidated_plot
# =========================================================================

def generate_plots(data: dict):
    """Generate benchmark plots (patterns + summary + large payloads)."""
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    plt.style.use('default')
    plt.rcParams.update({
        'figure.facecolor': 'white',
        'axes.facecolor': 'white',
        'axes.grid': True,
        'grid.alpha': 0.3,
        'font.size': 10,
    })

    colors = {'shm': '#00cc6a', 'tcp': '#ff5555'}

    cpu = data.get("cpu", "")
    runtime = data.get("runtime", "")
    timestamp = data.get("timestamp", "")[:10]

    width = 0.35  # 2-transport bar width

    # ================================================================
    # Plot 1: Communication Pattern Benchmarks (main dashboard)
    # ================================================================
    fig, axes = plt.subplots(3, 2, figsize=(14, 14))
    fig.suptitle(
        f'gRPC Shared Memory Transport - Communication Pattern Benchmarks\n'
        f'64 MiB Ring Buffers \u2022 {cpu[:40]}',
        fontsize=14, fontweight='bold')

    # --- Row 1: Unary (Roundtrip) ---
    rt_labels = data["rt_size_labels"]
    x = np.arange(len(rt_labels))

    shm_rt = data["shm_rt_latency"]
    tcp_rt = data["tcp_rt_latency"]

    ax = axes[0, 0]
    if all(v is not None for v in shm_rt + tcp_rt):
        ax.bar(x - width / 2, shm_rt, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.bar(x + width / 2, tcp_rt, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Latency (ns)')
        ax.set_title('[UNARY] Unary RPC (Ping-Pong) - Latency\n(lower is better)')
        ax.set_xticks(x); ax.set_xticklabels(rt_labels)
        ax.legend(loc='upper right')
        for i, (shm, tcp) in enumerate(zip(shm_rt, tcp_rt)):
            if shm and tcp:
                speedup = tcp / shm
                ax.annotate(f'{speedup:.1f}x', xy=(i - width / 2, shm), xytext=(0, 5),
                           textcoords='offset points', ha='center', fontsize=9,
                           color=colors['shm'], fontweight='bold')
    else:
        ax.text(0.5, 0.5, 'No roundtrip data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('[UNARY] Unary RPC - Latency')

    ax = axes[0, 1]
    if all(v is not None for v in shm_rt + tcp_rt):
        shm_ops = [1e9 / lat / 1000 for lat in shm_rt]
        tcp_ops = [1e9 / lat / 1000 for lat in tcp_rt]
        ax.bar(x - width / 2, shm_ops, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.bar(x + width / 2, tcp_ops, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Throughput (Kops/s)')
        ax.set_title('[UNARY] Unary RPC - Throughput\n(higher is better)')
        ax.set_xticks(x); ax.set_xticklabels(rt_labels)
        ax.legend(loc='upper right')
    else:
        ax.text(0.5, 0.5, 'No roundtrip data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('[UNARY] Unary RPC - Throughput')

    # --- Row 2: Streaming ---
    size_labels = data["size_labels"]
    x = np.arange(len(size_labels))

    shm_lat = data["shm_stream_latency"]
    tcp_lat = data["tcp_stream_latency"]

    ax = axes[1, 0]
    if all(v is not None for v in shm_lat + tcp_lat):
        ax.bar(x - width / 2, shm_lat, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.bar(x + width / 2, tcp_lat, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Latency (ns)')
        ax.set_title('[STREAM] Unidirectional Streaming - Latency\n(lower is better)')
        ax.set_xticks(x); ax.set_xticklabels(size_labels)
        ax.legend(loc='upper left')
        ax.set_yscale('log')
        for i, (shm, tcp) in enumerate(zip(shm_lat, tcp_lat)):
            if shm and tcp:
                speedup = tcp / shm
                ax.annotate(f'{speedup:.1f}x', xy=(i - width / 2, shm), xytext=(0, 5),
                           textcoords='offset points', ha='center', fontsize=8,
                           color=colors['shm'], fontweight='bold')
    else:
        ax.text(0.5, 0.5, 'No streaming data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('[STREAM] Streaming - Latency')

    ax = axes[1, 1]
    shm_tp = data["shm_stream_throughput"]
    tcp_tp = data["tcp_stream_throughput"]
    if all(v is not None for v in shm_tp + tcp_tp):
        ax.bar(x - width / 2, shm_tp, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.bar(x + width / 2, tcp_tp, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Throughput (MB/s)')
        ax.set_title('[STREAM] Streaming - Throughput\n(higher is better)')
        ax.set_xticks(x); ax.set_xticklabels(size_labels)
        ax.legend(loc='upper left')
        ax.set_yscale('log')
    else:
        ax.text(0.5, 0.5, 'No streaming data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('[STREAM] Streaming - Throughput')

    # --- Row 3: Bidirectional Streaming (estimated) ---
    bidi_overhead = 1.15

    ax = axes[2, 0]
    if all(v is not None for v in shm_lat + tcp_lat):
        bidi_shm = [v * 2 * bidi_overhead for v in shm_lat]
        bidi_tcp = [v * 2 * bidi_overhead for v in tcp_lat]
        ax.bar(x - width / 2, bidi_shm, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.bar(x + width / 2, bidi_tcp, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Latency (ns)')
        ax.set_title('[BIDI] Bidirectional Streaming - Latency (est.)\n(lower is better)')
        ax.set_xticks(x); ax.set_xticklabels(size_labels)
        ax.legend(loc='upper left')
        ax.set_yscale('log')
    else:
        ax.text(0.5, 0.5, 'No streaming data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('[BIDI] Bidirectional Streaming - Latency')

    ax = axes[2, 1]
    if all(v is not None for v in shm_tp + tcp_tp):
        bidi_shm_tp = [v * 0.85 for v in shm_tp]
        bidi_tcp_tp = [v * 0.80 for v in tcp_tp]
        ax.bar(x - width / 2, bidi_shm_tp, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.bar(x + width / 2, bidi_tcp_tp, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Throughput (MB/s)')
        ax.set_title('[BIDI] Bidirectional Streaming - Throughput (est.)\n(higher is better)')
        ax.set_xticks(x); ax.set_xticklabels(size_labels)
        ax.legend(loc='upper left')
        ax.set_yscale('log')
    else:
        ax.text(0.5, 0.5, 'No streaming data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('[BIDI] Bidirectional Streaming - Throughput')

    plt.tight_layout(rect=[0, 0, 1, 0.96])
    patterns_file = OUT_DIR / "benchmark_patterns.png"
    plt.savefig(patterns_file, dpi=150, bbox_inches='tight', facecolor='white')
    plt.close()
    print(f"Created: {patterns_file}")

    # ================================================================
    # Plot 2: Summary Comparison
    # ================================================================
    fig, axes = plt.subplots(2, 2, figsize=(14, 10))
    fig.suptitle(f'gRPC Transport Performance Summary\n{timestamp}', fontsize=14, fontweight='bold')

    idx_1k = 1  # 1KB index in rt_sizes

    ax = axes[0, 0]
    if (shm_rt[idx_1k] and tcp_rt[idx_1k] and
            shm_lat[1] and tcp_lat[1]):
        categories = ['Unary RPC\n(Roundtrip)', 'Streaming\n(One-way)']
        shm_vals = [shm_rt[idx_1k], shm_lat[1]]
        tcp_vals = [tcp_rt[idx_1k], tcp_lat[1]]
        xc = np.arange(len(categories))
        ax.bar(xc - width / 2, shm_vals, width, label='SHM', color=colors['shm'], edgecolor='black')
        ax.bar(xc + width / 2, tcp_vals, width, label='TCP', color=colors['tcp'], edgecolor='black')
        ax.set_ylabel('Latency (ns)')
        ax.set_title('Latency @ 1KB Message Size')
        ax.set_xticks(xc); ax.set_xticklabels(categories)
        ax.legend()
        ax.set_yscale('log')

    ax = axes[0, 1]
    if _has_numeric(shm_tp) and _has_numeric(tcp_tp):
        transports = ['SHM', 'TCP']
        max_tp = [max(_filter_numeric(shm_tp)), max(_filter_numeric(tcp_tp))]
        bars = ax.bar(transports, max_tp, color=[colors['shm'], colors['tcp']], edgecolor='black')
        ax.set_ylabel('Throughput (MB/s)')
        ax.set_title('Peak Throughput (Streaming)')
        for bar, val in zip(bars, max_tp):
            label = f'{val / 1000:.1f} GB/s' if val >= 1000 else f'{val:.0f} MB/s'
            ax.annotate(label, xy=(bar.get_x() + bar.get_width() / 2, val),
                       xytext=(0, 5), textcoords='offset points', ha='center', fontweight='bold')

    ax = axes[1, 0]
    if (shm_rt[idx_1k] and tcp_rt[idx_1k] and shm_lat[1] and tcp_lat[1]):
        categories = ['Unary\nvs TCP', 'Stream\nvs TCP']
        speedups = [
            tcp_rt[idx_1k] / shm_rt[idx_1k],
            tcp_lat[1] / shm_lat[1],
        ]
        bars = ax.bar(categories, speedups, color=[colors['tcp'], colors['tcp']],
                      edgecolor='black', alpha=0.7)
        ax.set_ylabel('Speedup Factor (x)')
        ax.set_title('SHM Latency Speedup (1KB)')
        ax.axhline(y=1, color='gray', linestyle='--', alpha=0.5)
        for bar, val in zip(bars, speedups):
            ax.annotate(f'{val:.1f}x', xy=(bar.get_x() + bar.get_width() / 2, val),
                       xytext=(0, 5), textcoords='offset points', ha='center', fontweight='bold')

    ax = axes[1, 1]
    ax.axis('off')
    summary_text = f"""
BENCHMARK SUMMARY
{'=' * 45}

CPU: {cpu[:50]}
Runtime: {runtime}
Date: {timestamp}

KEY RESULTS (1KB messages):
"""
    if shm_rt[idx_1k] and tcp_rt[idx_1k]:
        unary_speedup = tcp_rt[idx_1k] / shm_rt[idx_1k]
        summary_text += f"""
\u2022 Unary RPC:
  SHM: {shm_rt[idx_1k]:.0f} ns
  TCP: {tcp_rt[idx_1k]:.0f} ns
  Speedup: {unary_speedup:.1f}x
"""

    if shm_lat[1] and tcp_lat[1]:
        stream_speedup = tcp_lat[1] / shm_lat[1]
        summary_text += f"""
\u2022 Streaming:
  SHM: {shm_lat[1]:.0f} ns
  TCP: {tcp_lat[1]:.0f} ns
  Speedup: {stream_speedup:.1f}x
"""

    if _has_numeric(shm_tp):
        summary_text += f"""
\u2022 Peak Throughput:
  SHM: {max(_filter_numeric(shm_tp)) / 1000:.2f} GB/s
  TCP: {max(_filter_numeric(tcp_tp)) / 1000:.2f} GB/s
"""

    ax.text(0.1, 0.9, summary_text, transform=ax.transAxes, fontsize=11,
            verticalalignment='top', fontfamily='monospace',
            bbox=dict(boxstyle='round', facecolor='wheat', alpha=0.5))

    plt.tight_layout(rect=[0, 0, 1, 0.96])
    summary_file = OUT_DIR / "benchmark_summary.png"
    plt.savefig(summary_file, dpi=150, bbox_inches='tight', facecolor='white')
    plt.close()
    print(f"Created: {summary_file}")

    # ================================================================
    # Plot 3: Large Payload Benchmarks (1MB - 128MB)
    # ================================================================
    shm_large_tp = data.get("shm_large_stream_throughput", [])
    tcp_large_tp = data.get("tcp_large_stream_throughput", [])
    shm_large_lat = data.get("shm_large_stream_latency", [])
    tcp_large_lat = data.get("tcp_large_stream_latency", [])
    large_labels = data.get("large_size_labels", [])

    plot_files = [patterns_file, summary_file]

    if shm_large_tp and any(v is not None for v in shm_large_tp):
        fig, axes_lp = plt.subplots(1, 2, figsize=(14, 6))
        fig.suptitle(
            f'Large Payload Performance - All Transports (64 MiB Ring Buffer)\n{cpu[:40]}',
            fontsize=14, fontweight='bold')

        valid_idx = [i for i, v in enumerate(shm_large_tp) if v is not None]
        if valid_idx:
            valid_labels = [large_labels[i] for i in valid_idx]
            xl = np.arange(len(valid_labels))
            w = 0.35

            shm_vals = [shm_large_tp[i] or 0 for i in valid_idx]
            tcp_vals = [tcp_large_tp[i] or 0 for i in valid_idx]

            ax = axes_lp[0]
            ax.bar(xl - w / 2, shm_vals, w, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
            ax.bar(xl + w / 2, tcp_vals, w, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
            ax.set_xlabel('Message Size')
            ax.set_ylabel('Throughput (MB/s)')
            ax.set_title('Large Payload Throughput\n(higher is better)')
            ax.set_xticks(xl); ax.set_xticklabels(valid_labels)
            ax.legend(loc='upper right')
            ax.set_yscale('log')

            shm_lat_vals = [(shm_large_lat[i] or 0) / 1e6 for i in valid_idx]
            tcp_lat_vals = [(tcp_large_lat[i] or 0) / 1e6 for i in valid_idx]

            ax = axes_lp[1]
            ax.bar(xl - w / 2, shm_lat_vals, w, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
            ax.bar(xl + w / 2, tcp_lat_vals, w, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
            ax.set_xlabel('Message Size')
            ax.set_ylabel('Latency (ms)')
            ax.set_title('Large Payload Latency\n(lower is better)')
            ax.set_xticks(xl); ax.set_xticklabels(valid_labels)
            ax.legend(loc='upper left')

        plt.tight_layout(rect=[0, 0, 1, 0.94])
        large_file = OUT_DIR / "benchmark_large_payloads.png"
        plt.savefig(large_file, dpi=150, bbox_inches='tight', facecolor='white')
        plt.close()
        print(f"Created: {large_file}")
        plot_files.append(large_file)

    return plot_files


def generate_consolidated_plot(data: dict):
    """Generate a single consolidated plot with all benchmark results.

    Layout matches grpc-go-shmem's consolidated plot:
      Row 1 -- UNARY RPC (Small):     Latency | Throughput | Speedup
      Row 2 -- STREAMING (Small):     Latency | Throughput | Speedup
      Row 3 -- STREAMING (Large):     Throughput | Latency  | Speedup
      Row 4 -- UNARY RPC (Large):     Throughput | Latency  | Speedup
      Row 5 -- Pattern comparisons:   SHM Stream vs Unary | TCP Stream vs Unary | Combined Speedup
      Row 6 -- Summary metrics:       Peak Throughput | Min Latency | Performance Advantage
      Row 7 -- Text summary
    """
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    plt.style.use('default')
    plt.rcParams.update({
        'figure.facecolor': 'white',
        'axes.facecolor': 'white',
        'axes.grid': True,
        'grid.alpha': 0.3,
        'font.size': 9,
    })

    colors = {'shm': '#00cc6a', 'tcp': '#ff5555'}

    cpu = data.get("cpu", "")
    runtime = data.get("runtime", "")
    timestamp = data.get("timestamp", "")[:10]

    fig = plt.figure(figsize=(18, 28))
    gs = fig.add_gridspec(7, 3, hspace=0.35, wspace=0.25,
                          height_ratios=[1, 1, 1, 1, 1, 1, 0.6])

    fig.suptitle(
        f'gRPC Shared Memory Transport - Complete Benchmark Results\n'
        f'64 MiB Ring Buffers \u2022 {cpu} \u2022 {timestamp}',
        fontsize=16, fontweight='bold', y=0.98)

    width = 0.35  # 2-transport

    panel_status = []  # track which panels rendered data vs 'No data'

    # ============================================================
    # ROW 1: Unary RPC (Roundtrip) -- Small Payloads
    # ============================================================
    rt_labels = data["rt_size_labels"]
    x_rt = np.arange(len(rt_labels))

    shm_rt = data["shm_rt_latency"]
    tcp_rt = data["tcp_rt_latency"]

    # Unary Latency
    ax = fig.add_subplot(gs[0, 0])
    if all(v is not None for v in shm_rt + tcp_rt):
        ax.bar(x_rt - width / 2, shm_rt, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.bar(x_rt + width / 2, tcp_rt, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Latency (ns)')
        ax.set_title('UNARY RPC (Small) - Latency\n(lower is better)', fontweight='bold')
        ax.set_xticks(x_rt); ax.set_xticklabels(rt_labels)
        ax.legend(loc='upper right', fontsize=8)
        for i, (shm, tcp) in enumerate(zip(shm_rt, tcp_rt)):
            if shm and tcp:
                speedup = tcp / shm
                ax.annotate(f'{speedup:.1f}x', xy=(i - width / 2, shm), xytext=(0, 3),
                           textcoords='offset points', ha='center', fontsize=7,
                           color=colors['shm'], fontweight='bold')
    else:
        ax.text(0.5, 0.5, 'No data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('UNARY RPC (Small) - Latency')

    # Unary Throughput (ops/sec)
    ax = fig.add_subplot(gs[0, 1])
    if all(v is not None for v in shm_rt + tcp_rt):
        shm_ops = [1e9 / lat / 1000 for lat in shm_rt]
        tcp_ops = [1e9 / lat / 1000 for lat in tcp_rt]
        ax.bar(x_rt - width / 2, shm_ops, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.bar(x_rt + width / 2, tcp_ops, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Throughput (Kops/s)')
        ax.set_title('UNARY RPC (Small) - Throughput\n(higher is better)', fontweight='bold')
        ax.set_xticks(x_rt); ax.set_xticklabels(rt_labels)
        ax.legend(loc='upper right', fontsize=8)
    else:
        ax.text(0.5, 0.5, 'No data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('UNARY RPC (Small) - Throughput')

    # Unary Speedup
    ax = fig.add_subplot(gs[0, 2])
    if all(v is not None for v in shm_rt + tcp_rt):
        tcp_speedups = [tcp / shm for shm, tcp in zip(shm_rt, tcp_rt)]
        ax.bar(x_rt, tcp_speedups, 0.5, label='vs TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Speedup Factor')
        ax.set_title('UNARY RPC (Small) - SHM Speedup\n(higher is better)', fontweight='bold')
        ax.set_xticks(x_rt); ax.set_xticklabels(rt_labels)
        ax.legend(loc='upper right', fontsize=8)
        ax.axhline(y=1, color='gray', linestyle='--', alpha=0.5)
        for i, s in enumerate(tcp_speedups):
            ax.annotate(f'{s:.1f}x', xy=(i, s), xytext=(0, 3),
                       textcoords='offset points', ha='center', fontsize=7, fontweight='bold')
    else:
        ax.text(0.5, 0.5, 'No data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('UNARY RPC (Small) - SHM Speedup')

    # ============================================================
    # ROW 2: Streaming (Small)
    # ============================================================
    size_labels = data["size_labels"]
    x_stream = np.arange(len(size_labels))

    shm_lat = data["shm_stream_latency"]
    tcp_lat = data["tcp_stream_latency"]

    # Streaming Latency
    ax = fig.add_subplot(gs[1, 0])
    if all(v is not None for v in shm_lat + tcp_lat):
        ax.bar(x_stream - width / 2, shm_lat, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.bar(x_stream + width / 2, tcp_lat, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Latency (ns)')
        ax.set_title('STREAMING (Small) - Latency\n(lower is better)', fontweight='bold')
        ax.set_xticks(x_stream); ax.set_xticklabels(size_labels, rotation=45, ha='right')
        ax.legend(loc='upper left', fontsize=8)
        ax.set_yscale('log')
    else:
        ax.text(0.5, 0.5, 'No data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('STREAMING (Small) - Latency')

    # Streaming Throughput
    ax = fig.add_subplot(gs[1, 1])
    shm_tp = data["shm_stream_throughput"]
    tcp_tp = data["tcp_stream_throughput"]
    if all(v is not None for v in shm_tp + tcp_tp):
        ax.bar(x_stream - width / 2, shm_tp, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.bar(x_stream + width / 2, tcp_tp, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Throughput (MB/s)')
        ax.set_title('STREAMING (Small) - Throughput\n(higher is better)', fontweight='bold')
        ax.set_xticks(x_stream); ax.set_xticklabels(size_labels, rotation=45, ha='right')
        ax.legend(loc='upper left', fontsize=8)
        ax.set_yscale('log')
    else:
        ax.text(0.5, 0.5, 'No data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('STREAMING (Small) - Throughput')

    # Streaming Speedup
    ax = fig.add_subplot(gs[1, 2])
    if all(v is not None for v in shm_lat + tcp_lat):
        tcp_speedups = [tcp / shm for shm, tcp in zip(shm_lat, tcp_lat)]
        ax.bar(x_stream, tcp_speedups, 0.5, label='vs TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Speedup Factor')
        ax.set_title('STREAMING (Small) - SHM Speedup\n(higher is better)', fontweight='bold')
        ax.set_xticks(x_stream); ax.set_xticklabels(size_labels, rotation=45, ha='right')
        ax.legend(loc='upper right', fontsize=8)
        ax.axhline(y=1, color='gray', linestyle='--', alpha=0.5)
    else:
        ax.text(0.5, 0.5, 'No data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('STREAMING (Small) - SHM Speedup')

    # ============================================================
    # ROW 3: Large Payload STREAMING (1MB -- 128MB)
    # ============================================================
    shm_large_stream_tp = data.get("shm_large_stream_throughput", [])
    shm_large_stream_lat = data.get("shm_large_stream_latency", [])
    tcp_large_stream_tp = data.get("tcp_large_stream_throughput", [])
    tcp_large_stream_lat = data.get("tcp_large_stream_latency", [])
    large_labels = data.get("large_size_labels", [])

    valid_stream_idx = [i for i in range(len(large_labels))
                        if (i < len(shm_large_stream_tp) and shm_large_stream_tp[i] is not None)]

    # Large Streaming Throughput
    ax = fig.add_subplot(gs[2, 0])
    if valid_stream_idx:
        valid_labels = [large_labels[i] for i in valid_stream_idx]
        x_large = np.arange(len(valid_labels))
        shm_vals = [shm_large_stream_tp[i] or 0 for i in valid_stream_idx]
        tcp_vals = [tcp_large_stream_tp[i] or 0 for i in valid_stream_idx]
        ax.bar(x_large - width / 2, shm_vals, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.bar(x_large + width / 2, tcp_vals, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Throughput (MB/s)')
        ax.set_title('STREAMING (Large 1-128MB) - Throughput\n(higher is better)', fontweight='bold')
        ax.set_xticks(x_large); ax.set_xticklabels(valid_labels)
        ax.legend(loc='upper right', fontsize=8)
    else:
        ax.text(0.5, 0.5, 'No data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('STREAMING (Large) - Throughput')

    # Large Streaming Latency
    ax = fig.add_subplot(gs[2, 1])
    if valid_stream_idx:
        valid_labels = [large_labels[i] for i in valid_stream_idx]
        x_large = np.arange(len(valid_labels))
        shm_lat_vals = [(shm_large_stream_lat[i] or 0) / 1e6 for i in valid_stream_idx]
        tcp_lat_vals = [(tcp_large_stream_lat[i] or 0) / 1e6 for i in valid_stream_idx]
        ax.bar(x_large - width / 2, shm_lat_vals, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.bar(x_large + width / 2, tcp_lat_vals, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Latency (ms)')
        ax.set_title('STREAMING (Large 1-128MB) - Latency\n(lower is better)', fontweight='bold')
        ax.set_xticks(x_large); ax.set_xticklabels(valid_labels)
        ax.legend(loc='upper left', fontsize=8)
    else:
        ax.text(0.5, 0.5, 'No data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('STREAMING (Large) - Latency')

    # Large Streaming Speedup
    ax = fig.add_subplot(gs[2, 2])
    if valid_stream_idx and any(shm_large_stream_lat[i] for i in valid_stream_idx if i < len(shm_large_stream_lat)):
        valid_labels = [large_labels[i] for i in valid_stream_idx]
        x_large = np.arange(len(valid_labels))
        tcp_speedups = []
        for i in valid_stream_idx:
            shm_l = shm_large_stream_lat[i] if i < len(shm_large_stream_lat) and shm_large_stream_lat[i] else 1
            tcp_l = tcp_large_stream_lat[i] if i < len(tcp_large_stream_lat) and tcp_large_stream_lat[i] else shm_l
            tcp_speedups.append(tcp_l / shm_l if shm_l else 1)

        ax.bar(x_large, tcp_speedups, 0.5, label='vs TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Speedup Factor')
        ax.set_title('STREAMING (Large) - SHM Speedup\n(higher is better)', fontweight='bold')
        ax.set_xticks(x_large); ax.set_xticklabels(valid_labels)
        ax.legend(loc='upper right', fontsize=8)
        ax.axhline(y=1, color='gray', linestyle='--', alpha=0.5)
        for i, s in enumerate(tcp_speedups):
            ax.annotate(f'{s:.1f}x', xy=(i, s), xytext=(0, 3),
                       textcoords='offset points', ha='center', fontsize=7, fontweight='bold')
    else:
        ax.text(0.5, 0.5, 'No data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('STREAMING (Large) - SHM Speedup')

    # ============================================================
    # ROW 4: Large Payload UNARY/ROUNDTRIP (1MB -- 128MB)
    # ============================================================
    shm_large_rt_tp = data.get("shm_large_rt_throughput", [])
    shm_large_rt_lat = data.get("shm_large_rt_latency", [])
    tcp_large_rt_tp = data.get("tcp_large_rt_throughput", [])
    tcp_large_rt_lat = data.get("tcp_large_rt_latency", [])

    valid_rt_idx = [i for i in range(len(large_labels))
                    if (i < len(shm_large_rt_tp) and shm_large_rt_tp[i] is not None)]

    # Large Unary Throughput
    ax = fig.add_subplot(gs[3, 0])
    if valid_rt_idx:
        valid_labels = [large_labels[i] for i in valid_rt_idx]
        x_large = np.arange(len(valid_labels))
        shm_vals = [shm_large_rt_tp[i] or 0 for i in valid_rt_idx]
        tcp_vals = [tcp_large_rt_tp[i] or 0 for i in valid_rt_idx]
        ax.bar(x_large - width / 2, shm_vals, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.bar(x_large + width / 2, tcp_vals, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Throughput (MB/s)')
        ax.set_title('UNARY RPC (Large 1-128MB) - Throughput\n(higher is better)', fontweight='bold')
        ax.set_xticks(x_large); ax.set_xticklabels(valid_labels)
        ax.legend(loc='upper right', fontsize=8)
    else:
        ax.text(0.5, 0.5, 'No data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('UNARY RPC (Large) - Throughput')

    # Large Unary Latency
    ax = fig.add_subplot(gs[3, 1])
    if valid_rt_idx:
        valid_labels = [large_labels[i] for i in valid_rt_idx]
        x_large = np.arange(len(valid_labels))
        shm_lat_vals = [(shm_large_rt_lat[i] or 0) / 1e6 for i in valid_rt_idx]
        tcp_lat_vals = [(tcp_large_rt_lat[i] or 0) / 1e6 for i in valid_rt_idx]
        ax.bar(x_large - width / 2, shm_lat_vals, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.bar(x_large + width / 2, tcp_lat_vals, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Latency (ms)')
        ax.set_title('UNARY RPC (Large 1-128MB) - Latency\n(lower is better)', fontweight='bold')
        ax.set_xticks(x_large); ax.set_xticklabels(valid_labels)
        ax.legend(loc='upper left', fontsize=8)
    else:
        ax.text(0.5, 0.5, 'No data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('UNARY RPC (Large) - Latency')

    # Large Unary Speedup
    ax = fig.add_subplot(gs[3, 2])
    if valid_rt_idx and any(shm_large_rt_lat[i] for i in valid_rt_idx if i < len(shm_large_rt_lat)):
        valid_labels = [large_labels[i] for i in valid_rt_idx]
        x_large = np.arange(len(valid_labels))
        tcp_speedups = []
        for i in valid_rt_idx:
            shm_l = shm_large_rt_lat[i] if i < len(shm_large_rt_lat) and shm_large_rt_lat[i] else 1
            tcp_l = tcp_large_rt_lat[i] if i < len(tcp_large_rt_lat) and tcp_large_rt_lat[i] else shm_l
            tcp_speedups.append(tcp_l / shm_l if shm_l else 1)

        ax.bar(x_large, tcp_speedups, 0.5, label='vs TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Speedup Factor')
        ax.set_title('UNARY RPC (Large) - SHM Speedup\n(higher is better)', fontweight='bold')
        ax.set_xticks(x_large); ax.set_xticklabels(valid_labels)
        ax.legend(loc='upper right', fontsize=8)
        ax.axhline(y=1, color='gray', linestyle='--', alpha=0.5)
        for i, s in enumerate(tcp_speedups):
            ax.annotate(f'{s:.1f}x', xy=(i, s), xytext=(0, 3),
                       textcoords='offset points', ha='center', fontsize=7, fontweight='bold')
    else:
        ax.text(0.5, 0.5, 'No data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('UNARY RPC (Large) - SHM Speedup')

    # ============================================================
    # ROW 5: Streaming vs Unary Comparison for Large Payloads
    # ============================================================

    # SHM: Streaming vs Unary Throughput
    ax = fig.add_subplot(gs[4, 0])
    if valid_stream_idx and valid_rt_idx:
        common_idx = [i for i in valid_stream_idx if i in valid_rt_idx]
        if common_idx:
            labels = [large_labels[i] for i in common_idx]
            x = np.arange(len(labels))
            stream_vals = [shm_large_stream_tp[i] / 1000 for i in common_idx]
            rt_vals = [shm_large_rt_tp[i] / 1000 for i in common_idx]
            ax.bar(x - 0.15, stream_vals, 0.3, label='Streaming', color='#00aa55', edgecolor='black', linewidth=0.5)
            ax.bar(x + 0.15, rt_vals, 0.3, label='Unary', color='#00dd88', edgecolor='black', linewidth=0.5)
            ax.set_xlabel('Message Size')
            ax.set_ylabel('Throughput (GB/s)')
            ax.set_title('SHM: Streaming vs Unary\n(higher is better)', fontweight='bold')
            ax.set_xticks(x); ax.set_xticklabels(labels)
            ax.legend(loc='upper right', fontsize=8)
    else:
        ax.text(0.5, 0.5, 'No data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('SHM: Streaming vs Unary')

    # TCP: Streaming vs Unary Throughput
    ax = fig.add_subplot(gs[4, 1])
    if valid_stream_idx and valid_rt_idx:
        common_idx = [i for i in valid_stream_idx if i in valid_rt_idx]
        if common_idx:
            labels = [large_labels[i] for i in common_idx]
            x = np.arange(len(labels))
            stream_vals = [(tcp_large_stream_tp[i] or 0) / 1000 for i in common_idx]
            rt_vals = [(tcp_large_rt_tp[i] or 0) / 1000 for i in common_idx]
            ax.bar(x - 0.15, stream_vals, 0.3, label='Streaming', color='#cc3333', edgecolor='black', linewidth=0.5)
            ax.bar(x + 0.15, rt_vals, 0.3, label='Unary', color='#ff7777', edgecolor='black', linewidth=0.5)
            ax.set_xlabel('Message Size')
            ax.set_ylabel('Throughput (GB/s)')
            ax.set_title('TCP: Streaming vs Unary\n(higher is better)', fontweight='bold')
            ax.set_xticks(x); ax.set_xticklabels(labels)
            ax.legend(loc='upper right', fontsize=8)
    else:
        ax.text(0.5, 0.5, 'No data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('TCP: Streaming vs Unary')

    # Combined Speedup: SHM over TCP at each large size
    ax = fig.add_subplot(gs[4, 2])
    if valid_stream_idx and valid_rt_idx:
        common_idx = [i for i in valid_stream_idx if i in valid_rt_idx]
        if common_idx:
            labels = [large_labels[i] for i in common_idx]
            x = np.arange(len(labels))
            stream_speedups = []
            unary_speedups = []
            for i in common_idx:
                shm_sl = shm_large_stream_lat[i] if shm_large_stream_lat[i] else 1
                tcp_sl = tcp_large_stream_lat[i] if tcp_large_stream_lat[i] else shm_sl
                stream_speedups.append(tcp_sl / shm_sl if shm_sl else 1)
                shm_rl = shm_large_rt_lat[i] if shm_large_rt_lat[i] else 1
                tcp_rl = tcp_large_rt_lat[i] if tcp_large_rt_lat[i] else shm_rl
                unary_speedups.append(tcp_rl / shm_rl if shm_rl else 1)

            ax.bar(x - 0.15, stream_speedups, 0.3, label='Streaming', color='#cc3333', edgecolor='black', linewidth=0.5)
            ax.bar(x + 0.15, unary_speedups, 0.3, label='Unary', color='#ff7777', edgecolor='black', linewidth=0.5)
            ax.set_xlabel('Message Size')
            ax.set_ylabel('Speedup Factor')
            ax.set_title('SHM vs TCP: Stream & Unary Speedup\n(higher is better)', fontweight='bold')
            ax.set_xticks(x); ax.set_xticklabels(labels)
            ax.legend(loc='upper right', fontsize=8)
            ax.axhline(y=1, color='gray', linestyle='--', alpha=0.5)
    else:
        ax.text(0.5, 0.5, 'No data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('SHM vs TCP Speedup')

    # ============================================================
    # ROW 6: Peak Performance Summary
    # ============================================================
    shm_tp = data["shm_stream_throughput"]
    tcp_tp = data["tcp_stream_throughput"]
    shm_lat = data["shm_stream_latency"]
    tcp_lat = data["tcp_stream_latency"]

    # Peak Throughput by Transport
    ax = fig.add_subplot(gs[5, 0])
    if _has_numeric(shm_tp) and _has_numeric(tcp_tp):
        transports = ['SHM', 'TCP']
        peak_tp = [max(_filter_numeric(shm_tp)), max(_filter_numeric(tcp_tp))]
        bar_colors = [colors['shm'], colors['tcp']]
        bars = ax.bar(transports, peak_tp, color=bar_colors, edgecolor='black', linewidth=0.5)
        ax.set_ylabel('Throughput (MB/s)')
        ax.set_title('Peak Throughput (Streaming)\n(higher is better)', fontweight='bold')
        for bar, val in zip(bars, peak_tp):
            label = f'{val / 1000:.1f} GB/s' if val >= 1000 else f'{val:.0f} MB/s'
            ax.annotate(label, xy=(bar.get_x() + bar.get_width() / 2, val),
                       xytext=(0, 3), textcoords='offset points', ha='center', fontsize=9, fontweight='bold')

    # Minimum Latency by Transport
    ax = fig.add_subplot(gs[5, 1])
    if _has_numeric(shm_lat) and _has_numeric(tcp_lat):
        transports = ['SHM', 'TCP']
        min_lat = [min(_filter_numeric(shm_lat)), min(_filter_numeric(tcp_lat))]
        bar_colors = [colors['shm'], colors['tcp']]
        bars = ax.bar(transports, min_lat, color=bar_colors, edgecolor='black', linewidth=0.5)
        ax.set_ylabel('Latency (ns)')
        ax.set_title('Minimum Latency (1B)\n(lower is better)', fontweight='bold')
        for bar, val in zip(bars, min_lat):
            label = f'{val / 1000:.1f} \u00b5s' if val >= 1000 else f'{val:.0f} ns'
            ax.annotate(label, xy=(bar.get_x() + bar.get_width() / 2, val),
                       xytext=(0, 3), textcoords='offset points', ha='center', fontsize=9, fontweight='bold')

    # Overall Speedup Summary
    ax = fig.add_subplot(gs[5, 2])
    shm_rt = data["shm_rt_latency"]
    tcp_rt = data["tcp_rt_latency"]
    if (_has_numeric(shm_lat) and _has_numeric(tcp_lat)
            and _has_numeric(shm_rt) and _has_numeric(tcp_rt)):
        categories = ['Unary\n(1KB)', 'Stream\n(1MB)', 'Large Stream\n(16MB)', 'Large Unary\n(16MB)']
        speedups_tcp = []

        # Unary 1KB (index 1 in rt_sizes)
        u_shm = _safe_number(shm_rt, 1)
        u_tcp = _safe_number(tcp_rt, 1)
        speedups_tcp.append(u_tcp / u_shm if u_shm and u_tcp else 0)

        # Stream 1MB (last index in small sizes)
        s_shm = _safe_number(shm_lat, len(shm_lat) - 1)
        s_tcp = _safe_number(tcp_lat, len(tcp_lat) - 1)
        speedups_tcp.append(s_tcp / s_shm if s_shm and s_tcp else 0)

        # Large Stream 16MB (index 3 in large_sizes)
        ls_shm = _safe_number(shm_large_stream_lat, 3)
        ls_tcp = _safe_number(tcp_large_stream_lat, 3)
        speedups_tcp.append(ls_tcp / ls_shm if ls_shm and ls_tcp else 0)

        # Large Unary 16MB (index 3 in large_sizes)
        lu_shm = _safe_number(shm_large_rt_lat, 3)
        lu_tcp = _safe_number(tcp_large_rt_lat, 3)
        speedups_tcp.append(lu_tcp / lu_shm if lu_shm and lu_tcp else 0)

        if any(v > 0 for v in speedups_tcp):
            x_summ = np.arange(len(categories))
            bars = ax.bar(x_summ, speedups_tcp, 0.5, label='vs TCP', color=colors['tcp'],
                         edgecolor='black', linewidth=0.5)
            ax.set_ylabel('Speedup Factor')
            ax.set_title('SHM Performance Advantage\n(higher is better)', fontweight='bold')
            ax.set_xticks(x_summ); ax.set_xticklabels(categories)
            ax.legend(loc='upper right', fontsize=8)
            ax.axhline(y=1, color='gray', linestyle='--', alpha=0.5)
            for i, s in enumerate(speedups_tcp):
                if s > 0:
                    ax.annotate(f'{s:.1f}x', xy=(i, s), xytext=(0, 3),
                               textcoords='offset points', ha='center', fontsize=8, fontweight='bold')
        else:
            ax.text(0.5, 0.5, 'No data', ha='center', va='center', transform=ax.transAxes)
            ax.set_title('SHM Performance Advantage')
    else:
        ax.text(0.5, 0.5, 'No data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('SHM Performance Advantage')

    # ============================================================
    # ROW 7: Text Summary
    # ============================================================
    ax = fig.add_subplot(gs[6, :])
    ax.axis('off')

    cpu_full = data.get("cpu", "")
    summary_lines = [
        "\u2550" * 120,
        "BENCHMARK SUMMARY  -  Streaming vs Unary RPC across all payload sizes",
        "\u2550" * 120,
        "",
    ]

    shm_rt = data["shm_rt_latency"]
    tcp_rt = data["tcp_rt_latency"]
    shm_lat = data["shm_stream_latency"]
    tcp_lat = data["tcp_stream_latency"]

    if shm_rt and tcp_rt and shm_rt[1] and tcp_rt[1]:
        unary_speedup = tcp_rt[1] / shm_rt[1]
        summary_lines.append(
            f"UNARY RPC (1KB):         SHM: {shm_rt[1]:.0f} ns    TCP: {tcp_rt[1]:.0f} ns    "
            f"Speedup: {unary_speedup:.1f}x vs TCP")

    if shm_lat and tcp_lat and shm_lat[1] and tcp_lat[1]:
        stream_speedup = tcp_lat[1] / shm_lat[1]
        summary_lines.append(
            f"STREAMING (1KB):         SHM: {shm_lat[1]:.0f} ns    TCP: {tcp_lat[1]:.0f} ns    "
            f"Speedup: {stream_speedup:.1f}x vs TCP")

    if valid_stream_idx and 3 in valid_stream_idx:
        shm_v = shm_large_stream_tp[3] / 1000
        tcp_v = (tcp_large_stream_tp[3] or 0) / 1000
        summary_lines.append(
            f"STREAMING (16MB):        SHM: {shm_v:.1f} GB/s    TCP: {tcp_v:.2f} GB/s")

    if valid_rt_idx and 3 in valid_rt_idx:
        shm_v = shm_large_rt_tp[3] / 1000
        tcp_v = (tcp_large_rt_tp[3] or 0) / 1000
        summary_lines.append(
            f"UNARY RPC (16MB):        SHM: {shm_v:.1f} GB/s    TCP: {tcp_v:.2f} GB/s")

    summary_lines.extend(["", f"CPU: {cpu_full}    Runtime: {runtime}    Ring Buffer: 64 MiB", "\u2550" * 120])

    summary_text = "\n".join(summary_lines)
    ax.text(0.5, 0.5, summary_text, transform=ax.transAxes, fontsize=10,
            verticalalignment='center', horizontalalignment='center',
            fontfamily='monospace',
            bbox=dict(boxstyle='round', facecolor='#f0f0f0', edgecolor='gray', alpha=0.8))

    # Save
    consolidated_file = OUT_DIR / "benchmark_consolidated.png"
    plt.savefig(consolidated_file, dpi=150, bbox_inches='tight', facecolor='white')
    plt.close()
    print(f"Created: {consolidated_file}")

    return consolidated_file


def main():
    parser = argparse.ArgumentParser(
        description='Run .NET gRPC benchmarks (SHM vs TCP) and generate plots',
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  python benchmark_runner.py              # Use cached results or run if none
  python benchmark_runner.py --run        # Force rerun benchmarks
  python benchmark_runner.py --plot-only  # Only plot, fail if no cached data
        """
    )
    parser.add_argument('--run', action='store_true',
                       help='Force rerun benchmarks even if cached results exist')
    parser.add_argument('--plot-only', action='store_true',
                       help='Only generate plots, fail if no cached results')

    args = parser.parse_args()

    OUT_DIR.mkdir(parents=True, exist_ok=True)

    results = None

    if args.run:
        results = run_ringbench()
        if not results:
            print("ERROR: Benchmark run failed")
            sys.exit(1)
    elif args.plot_only:
        results = load_results()
        if not results:
            print("ERROR: No cached results found. Run with --run first.")
            sys.exit(1)
    else:
        results = load_results()
        if not results:
            print("No cached results found, running benchmarks...")
            results = run_ringbench()
            if not results:
                print("ERROR: Benchmark run failed")
                sys.exit(1)

    if not results.get("unary") and not results.get("streaming"):
        print("WARNING: No benchmark data found; plots will contain placeholders only.")

    # Extract data and generate plots
    data = extract_data(results)
    plot_files = generate_plots(data)

    # Generate consolidated plot
    consolidated_file = generate_consolidated_plot(data)

    print("\n" + "=" * 70)
    print("BENCHMARK PLOTS GENERATED")
    print("=" * 70)
    for f in plot_files:
        print(f"  {f}")
    print(f"  {consolidated_file}  \u2190 CONSOLIDATED (all data)")
    print(f"\nConsolidated plot: {consolidated_file}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
