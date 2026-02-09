#!/usr/bin/env python3
"""
Benchmark runner and plotter for .NET SHM vs TCP transport.
Exact .NET equivalent of grpc-go-shmem/benchmark/shmemtcp/benchmark_runner.py.

Usage:
    python3 benchmark_runner.py              # Plot from cached results (or run if none)
    python3 benchmark_runner.py --run        # Force rerun benchmarks, then plot
    python3 benchmark_runner.py --plot-only  # Only plot (fail if no cached results)
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

# Directory setup
SCRIPT_DIR = Path(__file__).parent.absolute()
OUT_ROOT = SCRIPT_DIR / "plots"
OUT_DIR = OUT_ROOT
RESULTS_DIR = SCRIPT_DIR / "results"
RESULTS_FILE = RESULTS_DIR / "ringbench_results.json"


def run_ringbench() -> dict:
    """Run .NET ring buffer benchmarks and return JSON results."""
    print("=" * 70)
    print("Running .NET Transport Benchmarks (RingBench)...")
    print("=" * 70)

    RESULTS_DIR.mkdir(parents=True, exist_ok=True)
    output_file = str(RESULTS_FILE)

    cmd = [
        "dotnet", "run",
        "--project", str(SCRIPT_DIR / "ringbench" / "RingBench.csproj"),
        "-c", "Release",
        "--",
        "--output", output_file
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

    # Load results
    try:
        with open(output_file) as f:
            results = json.load(f)
        n = len(results.get("benchmarks", {}))
        print(f"Parsed {n} benchmark results")
        return results
    except Exception as e:
        print(f"ERROR: Failed to load results: {e}")
        return None


def load_results() -> dict:
    """Load benchmark results from cached JSON file."""
    if not RESULTS_FILE.exists():
        return None
    try:
        with open(RESULTS_FILE) as f:
            return json.load(f)
    except Exception:
        return None


def save_results(results: dict):
    """Save benchmark results to JSON file."""
    RESULTS_DIR.mkdir(parents=True, exist_ok=True)
    with open(RESULTS_FILE, "w") as f:
        json.dump(results, f, indent=2)
    print(f"Results saved to: {RESULTS_FILE}")


def extract_data(results: dict) -> dict:
    """Extract plotting data from benchmark results.
    Matches Go's extract_data() structure exactly."""
    benchmarks = results.get("benchmarks", {})

    # Standard sizes (matching Go: 64B to 64KB for streaming)
    sizes = [64, 256, 1024, 4096, 16384, 65536]
    size_labels = ['64B', '256B', '1KB', '4KB', '16KB', '64KB']

    # Roundtrip sizes (matching Go: 64B to 4KB)
    rt_sizes = [64, 256, 1024, 4096]
    rt_labels = ['64B', '256B', '1KB', '4KB']

    # Large payload sizes
    large_sizes = [65536, 262144, 1048576]
    large_labels = ['64KB', '256KB', '1MB']

    data = {
        "sizes": sizes,
        "size_labels": size_labels,
        "rt_sizes": rt_sizes,
        "rt_size_labels": rt_labels,
        "large_sizes": large_sizes,
        "large_size_labels": large_labels,
        "cpu": results.get("cpu", "Unknown CPU"),
        "timestamp": results.get("timestamp", ""),
        "runtime": results.get("runtime", ""),
        "ring_capacity_mb": results.get("ring_capacity_mb", 0),
    }

    def get_val(bench_name, size, field):
        """Get a value from benchmarks[bench_name/size=X][field]."""
        key = f"{bench_name}/size={size}"
        entry = benchmarks.get(key, {})
        return entry.get(field)

    def get_latency(bench_name, sizes_list):
        return [get_val(bench_name, s, "ns_per_op") for s in sizes_list]

    def get_throughput(bench_name, sizes_list):
        return [get_val(bench_name, s, "mb_per_s") for s in sizes_list]

    # Streaming (one-way) benchmarks — Go: BenchmarkShmRingWriteRead / BenchmarkTCPLoopback
    data["shm_stream_latency"] = get_latency("ShmRingWriteRead", sizes)
    data["tcp_stream_latency"] = get_latency("TCPLoopback", sizes)

    data["shm_stream_throughput"] = get_throughput("ShmRingWriteRead", sizes)
    data["tcp_stream_throughput"] = get_throughput("TCPLoopback", sizes)

    # Roundtrip (unary) benchmarks — Go: BenchmarkShmRingRoundtrip / BenchmarkTCPLoopbackRoundtrip
    data["shm_rt_latency"] = get_latency("ShmRingRoundtrip", rt_sizes)
    data["tcp_rt_latency"] = get_latency("TCPLoopbackRoundtrip", rt_sizes)

    data["shm_rt_throughput"] = get_throughput("ShmRingRoundtrip", rt_sizes)
    data["tcp_rt_throughput"] = get_throughput("TCPLoopbackRoundtrip", rt_sizes)

    # Large payload streaming
    data["shm_large_stream_throughput"] = get_throughput("ShmRingLargePayloads", large_sizes)
    data["shm_large_stream_latency"] = get_latency("ShmRingLargePayloads", large_sizes)
    data["tcp_large_stream_throughput"] = get_throughput("TCPLargePayloads", large_sizes)
    data["tcp_large_stream_latency"] = get_latency("TCPLargePayloads", large_sizes)

    # Large payload roundtrip
    data["shm_large_rt_throughput"] = get_throughput("ShmRingLargePayloadsRoundtrip", large_sizes)
    data["shm_large_rt_latency"] = get_latency("ShmRingLargePayloadsRoundtrip", large_sizes)
    data["tcp_large_rt_throughput"] = get_throughput("TCPLargePayloadsRoundtrip", large_sizes)
    data["tcp_large_rt_latency"] = get_latency("TCPLargePayloadsRoundtrip", large_sizes)

    return data


def _filter_numeric(seq):
    """Return only numeric entries from a sequence."""
    return [x for x in seq if x is not None and isinstance(x, (int, float))]


def _has_numeric(seq) -> bool:
    return len(_filter_numeric(seq)) > 0


def _safe_number(seq, idx):
    if idx < len(seq) and seq[idx] is not None:
        return seq[idx]
    return None


def generate_plots(data: dict):
    """Generate all benchmark plots. Matches Go benchmark_runner.py output."""
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    plt.style.use('default')
    plt.rcParams['figure.facecolor'] = 'white'
    plt.rcParams['axes.facecolor'] = 'white'
    plt.rcParams['axes.grid'] = True
    plt.rcParams['grid.alpha'] = 0.3
    plt.rcParams['font.size'] = 10

    colors = {
        'shm': '#00cc6a',
        'tcp': '#ff5555',
    }

    cpu = data.get("cpu", "")
    runtime = data.get("runtime", "")
    timestamp = data.get("timestamp", "")[:10]
    ring_mb = data.get("ring_capacity_mb", 0)

    plot_files = []

    # ================================================================
    # Plot 1: Communication Pattern Benchmarks (3x2)
    # Matches Go's benchmark_patterns.png
    # ================================================================
    fig, axes = plt.subplots(3, 2, figsize=(14, 14))
    fig.suptitle(
        f'gRPC .NET Shared Memory Transport - Communication Pattern Benchmarks\n'
        f'{ring_mb} MiB Ring Buffer • {runtime} • {cpu[:40]}',
        fontsize=14, fontweight='bold'
    )

    width = 0.3

    # --- Row 1: Unary (Roundtrip) ---
    ax = axes[0, 0]
    rt_labels = data["rt_size_labels"]
    x = np.arange(len(rt_labels))
    shm_rt = data["shm_rt_latency"]
    tcp_rt = data["tcp_rt_latency"]

    if _has_numeric(shm_rt) and _has_numeric(tcp_rt):
        shm_vals = [v if v else 0 for v in shm_rt]
        tcp_vals = [v if v else 0 for v in tcp_rt]
        ax.bar(x - width/2, shm_vals, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.bar(x + width/2, tcp_vals, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Latency (ns)')
        ax.set_title('[UNARY] Unary RPC (Ping-Pong) - Latency\n(lower is better)')
        ax.set_xticks(x)
        ax.set_xticklabels(rt_labels)
        ax.legend(loc='upper right')
        for i, (shm, tcp) in enumerate(zip(shm_vals, tcp_vals)):
            if shm and tcp:
                speedup = tcp / shm
                ax.annotate(f'{speedup:.0f}x', xy=(i - width/2, shm), xytext=(0, 5),
                           textcoords='offset points', ha='center', fontsize=9,
                           color=colors['shm'], fontweight='bold')
    else:
        ax.text(0.5, 0.5, 'No roundtrip data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('[UNARY] Unary RPC - Latency')

    # Unary throughput (Kops/s)
    ax = axes[0, 1]
    if _has_numeric(shm_rt) and _has_numeric(tcp_rt):
        shm_kops = [1e6 / v if v else 0 for v in shm_rt]
        tcp_kops = [1e6 / v if v else 0 for v in tcp_rt]
        ax.bar(x - width/2, shm_kops, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.bar(x + width/2, tcp_kops, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Throughput (Kops/s)')
        ax.set_title('[UNARY] Unary RPC - Throughput\n(higher is better)')
        ax.set_xticks(x)
        ax.set_xticklabels(rt_labels)
        ax.legend(loc='upper right')
    else:
        ax.text(0.5, 0.5, 'No roundtrip data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('[UNARY] Unary RPC - Throughput')

    # --- Row 2: Unidirectional Streaming ---
    ax = axes[1, 0]
    size_labels = data["size_labels"]
    x2 = np.arange(len(size_labels))
    shm_lat = data["shm_stream_latency"]
    tcp_lat = data["tcp_stream_latency"]

    if _has_numeric(shm_lat) and _has_numeric(tcp_lat):
        shm_vals = [v if v else 0 for v in shm_lat]
        tcp_vals = [v if v else 0 for v in tcp_lat]
        ax.bar(x2 - width/2, shm_vals, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.bar(x2 + width/2, tcp_vals, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Latency (ns)')
        ax.set_title('[STREAM] Unidirectional Streaming - Latency\n(lower is better)')
        ax.set_xticks(x2)
        ax.set_xticklabels(size_labels)
        ax.legend(loc='upper left')
        ax.set_yscale('log')
        for i, (shm, tcp) in enumerate(zip(shm_vals, tcp_vals)):
            if shm and tcp:
                speedup = tcp / shm
                ax.annotate(f'{speedup:.0f}x', xy=(i - width/2, shm), xytext=(0, 5),
                           textcoords='offset points', ha='center', fontsize=8,
                           color=colors['shm'], fontweight='bold')
    else:
        ax.text(0.5, 0.5, 'No streaming data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('[STREAM] Streaming - Latency')

    # Streaming throughput (MB/s)
    ax = axes[1, 1]
    shm_tp = data["shm_stream_throughput"]
    tcp_tp = data["tcp_stream_throughput"]
    if _has_numeric(shm_tp) and _has_numeric(tcp_tp):
        shm_vals = [v if v else 0 for v in shm_tp]
        tcp_vals = [v if v else 0 for v in tcp_tp]
        ax.bar(x2 - width/2, shm_vals, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.bar(x2 + width/2, tcp_vals, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Throughput (MB/s)')
        ax.set_title('[STREAM] Unidirectional Streaming - Throughput\n(higher is better)')
        ax.set_xticks(x2)
        ax.set_xticklabels(size_labels)
        ax.legend(loc='upper left')
        ax.set_yscale('log')
    else:
        ax.text(0.5, 0.5, 'No streaming data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('[STREAM] Streaming - Throughput')

    # --- Row 3: Bidirectional Streaming (estimated from roundtrip + one-way) ---
    ax = axes[2, 0]
    # Bidi latency ≈ roundtrip latency (like Go estimates)
    if _has_numeric(shm_rt) and _has_numeric(tcp_rt):
        shm_vals = [v if v else 0 for v in shm_rt]
        tcp_vals = [v if v else 0 for v in tcp_rt]
        ax.bar(x - width/2, shm_vals, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5, alpha=0.7)
        ax.bar(x + width/2, tcp_vals, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5, alpha=0.7)
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Latency (ns)')
        ax.set_title('[BIDI] Bidirectional Streaming - Latency (est.)\n(lower is better)')
        ax.set_xticks(x)
        ax.set_xticklabels(rt_labels)
        ax.legend(loc='upper right')
    else:
        ax.text(0.5, 0.5, 'No bidi data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('[BIDI] Bidirectional Streaming - Latency')

    # Bidi throughput ≈ roundtrip throughput
    ax = axes[2, 1]
    shm_rt_tp = data["shm_rt_throughput"]
    tcp_rt_tp = data["tcp_rt_throughput"]
    if _has_numeric(shm_rt_tp) and _has_numeric(tcp_rt_tp):
        shm_vals = [v if v else 0 for v in shm_rt_tp]
        tcp_vals = [v if v else 0 for v in tcp_rt_tp]
        ax.bar(x - width/2, shm_vals, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5, alpha=0.7)
        ax.bar(x + width/2, tcp_vals, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5, alpha=0.7)
        ax.set_xlabel('Message Size')
        ax.set_ylabel('Throughput (MB/s)')
        ax.set_title('[BIDI] Bidirectional Streaming - Throughput (est.)\n(higher is better)')
        ax.set_xticks(x)
        ax.set_xticklabels(rt_labels)
        ax.legend(loc='upper left')
    else:
        ax.text(0.5, 0.5, 'No bidi data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('[BIDI] Bidirectional Streaming - Throughput')

    plt.tight_layout(rect=[0, 0, 1, 0.96])
    patterns_file = OUT_DIR / "benchmark_patterns.png"
    plt.savefig(patterns_file, dpi=150, bbox_inches='tight', facecolor='white')
    plt.close()
    print(f"Created: {patterns_file}")
    plot_files.append(patterns_file)

    # ================================================================
    # Plot 2: Summary Comparison (2x2)
    # Matches Go's benchmark_summary.png
    # ================================================================
    fig, axes = plt.subplots(2, 2, figsize=(14, 10))
    fig.suptitle(f'gRPC .NET Transport Performance Summary\n{timestamp}', fontsize=14, fontweight='bold')

    # Summary 1: Latency at 1KB
    ax = axes[0, 0]
    idx_1k = 2  # 1KB index in rt_sizes
    shm_rt_1k = _safe_number(data["shm_rt_latency"], idx_1k)
    tcp_rt_1k = _safe_number(data["tcp_rt_latency"], idx_1k)
    shm_stream_1k = _safe_number(data["shm_stream_latency"], idx_1k)
    tcp_stream_1k = _safe_number(data["tcp_stream_latency"], idx_1k)

    if shm_rt_1k and tcp_rt_1k and shm_stream_1k and tcp_stream_1k:
        categories = ['Unary\nSHM', 'Unary\nTCP', 'Stream\nSHM', 'Stream\nTCP']
        values = [shm_rt_1k, tcp_rt_1k, shm_stream_1k, tcp_stream_1k]
        bar_colors = [colors['shm'], colors['tcp'], colors['shm'], colors['tcp']]
        bars = ax.bar(categories, values, color=bar_colors, edgecolor='black')
        ax.set_ylabel('Latency (ns)')
        ax.set_title('Latency @ 1KB Message Size')
        ax.set_yscale('log')
        for bar, val in zip(bars, values):
            ax.annotate(f'{val:.0f}ns', xy=(bar.get_x() + bar.get_width()/2, val),
                       xytext=(0, 5), textcoords='offset points', ha='center', fontweight='bold')
    else:
        ax.text(0.5, 0.5, 'No data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('Latency @ 1KB')

    # Summary 2: Peak throughput
    ax = axes[0, 1]
    if _has_numeric(shm_tp) and _has_numeric(tcp_tp):
        transports = ['SHM', 'TCP']
        max_tp = [max(_filter_numeric(shm_tp)), max(_filter_numeric(tcp_tp))]
        bars = ax.bar(transports, max_tp, color=[colors['shm'], colors['tcp']], edgecolor='black')
        ax.set_ylabel('Throughput (MB/s)')
        ax.set_title('Peak Streaming Throughput')
        for bar, val in zip(bars, max_tp):
            if val > 1000:
                ax.annotate(f'{val/1000:.1f} GB/s', xy=(bar.get_x() + bar.get_width()/2, val),
                           xytext=(0, 5), textcoords='offset points', ha='center', fontweight='bold')
            else:
                ax.annotate(f'{val:.0f} MB/s', xy=(bar.get_x() + bar.get_width()/2, val),
                           xytext=(0, 5), textcoords='offset points', ha='center', fontweight='bold')
    else:
        ax.text(0.5, 0.5, 'No data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('Peak Throughput')

    # Summary 3: Speedup factors
    ax = axes[1, 0]
    speedups = []
    speedup_labels = []
    if shm_rt_1k and tcp_rt_1k:
        speedups.append(tcp_rt_1k / shm_rt_1k)
        speedup_labels.append('Unary\nvs TCP')
    if shm_stream_1k and tcp_stream_1k:
        speedups.append(tcp_stream_1k / shm_stream_1k)
        speedup_labels.append('Stream\nvs TCP')
    if _has_numeric(shm_tp) and _has_numeric(tcp_tp):
        shm_peak = max(_filter_numeric(shm_tp))
        tcp_peak = max(_filter_numeric(tcp_tp))
        if tcp_peak > 0:
            speedups.append(shm_peak / tcp_peak)
            speedup_labels.append('Throughput\nRatio')

    if speedups:
        bar_colors = ['#00cc6a'] * len(speedups)
        bars = ax.bar(speedup_labels, speedups, color=bar_colors, edgecolor='black')
        ax.set_ylabel('Speedup Factor (×)')
        ax.set_title('SHM Speedup over TCP\n(higher is better)')
        ax.axhline(y=1, color='gray', linestyle='--', alpha=0.5)
        for bar, val in zip(bars, speedups):
            ax.annotate(f'{val:.1f}×', xy=(bar.get_x() + bar.get_width()/2, val),
                       xytext=(0, 5), textcoords='offset points', ha='center', fontweight='bold')
    else:
        ax.text(0.5, 0.5, 'No data', ha='center', va='center', transform=ax.transAxes)
        ax.set_title('Speedup Factors')

    # Summary 4: Text summary
    ax = axes[1, 1]
    ax.axis('off')
    summary_text = f"""
BENCHMARK SUMMARY
{'=' * 45}

CPU: {cpu[:50]}
Runtime: {runtime}
Ring Buffer: {ring_mb} MiB
Date: {timestamp}

KEY RESULTS (1KB messages):
"""
    if shm_rt_1k and tcp_rt_1k:
        unary_speedup = tcp_rt_1k / shm_rt_1k
        summary_text += f"""
  Unary RPC:
    SHM: {shm_rt_1k:.0f} ns
    TCP: {tcp_rt_1k:.0f} ns
    Speedup: {unary_speedup:.0f}x
"""
    if shm_stream_1k and tcp_stream_1k:
        stream_speedup = tcp_stream_1k / shm_stream_1k
        summary_text += f"""
  Streaming:
    SHM: {shm_stream_1k:.0f} ns
    TCP: {tcp_stream_1k:.0f} ns
    Speedup: {stream_speedup:.0f}x
"""
    if _has_numeric(shm_tp):
        summary_text += f"""
  Peak Throughput:
    SHM: {max(_filter_numeric(shm_tp))/1000:.1f} GB/s
    TCP: {max(_filter_numeric(tcp_tp))/1000:.2f} GB/s
"""

    ax.text(0.1, 0.9, summary_text, transform=ax.transAxes, fontsize=11,
            verticalalignment='top', fontfamily='monospace',
            bbox=dict(boxstyle='round', facecolor='wheat', alpha=0.5))

    plt.tight_layout(rect=[0, 0, 1, 0.96])
    summary_file = OUT_DIR / "benchmark_summary.png"
    plt.savefig(summary_file, dpi=150, bbox_inches='tight', facecolor='white')
    plt.close()
    print(f"Created: {summary_file}")
    plot_files.append(summary_file)

    # ================================================================
    # Plot 3: Large Payload Performance (1x2)
    # Matches Go's benchmark_large_payloads.png
    # ================================================================
    shm_large_tp = data.get("shm_large_stream_throughput", [])
    tcp_large_tp = data.get("tcp_large_stream_throughput", [])
    shm_large_lat = data.get("shm_large_stream_latency", [])
    tcp_large_lat = data.get("tcp_large_stream_latency", [])
    large_labels = data.get("large_size_labels", [])

    if _has_numeric(shm_large_tp):
        fig, axes = plt.subplots(1, 2, figsize=(14, 6))
        fig.suptitle(
            f'Large Payload Performance - SHM vs TCP ({ring_mb} MiB Ring Buffer)\n{cpu[:40]}',
            fontsize=14, fontweight='bold'
        )

        valid_idx = [i for i, v in enumerate(shm_large_tp) if v is not None]
        if valid_idx:
            valid_labels = [large_labels[i] for i in valid_idx]
            x = np.arange(len(valid_labels))

            shm_vals = [shm_large_tp[i] if shm_large_tp[i] else 0 for i in valid_idx]
            tcp_vals = [tcp_large_tp[i] if i < len(tcp_large_tp) and tcp_large_tp[i] else 0 for i in valid_idx]

            ax = axes[0]
            ax.bar(x - width/2, shm_vals, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
            ax.bar(x + width/2, tcp_vals, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
            ax.set_xlabel('Message Size')
            ax.set_ylabel('Throughput (MB/s)')
            ax.set_title('Large Payload Throughput\n(higher is better)')
            ax.set_xticks(x)
            ax.set_xticklabels(valid_labels)
            ax.legend(loc='upper right')
            ax.set_yscale('log')

            # Latency
            ax = axes[1]
            shm_lat_vals = [shm_large_lat[i] / 1e6 if i < len(shm_large_lat) and shm_large_lat[i] else 0 for i in valid_idx]
            tcp_lat_vals = [tcp_large_lat[i] / 1e6 if i < len(tcp_large_lat) and tcp_large_lat[i] else 0 for i in valid_idx]
            ax.bar(x - width/2, shm_lat_vals, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
            ax.bar(x + width/2, tcp_lat_vals, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
            ax.set_xlabel('Message Size')
            ax.set_ylabel('Latency (ms)')
            ax.set_title('Large Payload Latency\n(lower is better)')
            ax.set_xticks(x)
            ax.set_xticklabels(valid_labels)
            ax.legend(loc='upper left')

        plt.tight_layout(rect=[0, 0, 1, 0.94])
        large_file = OUT_DIR / "benchmark_large_payloads.png"
        plt.savefig(large_file, dpi=150, bbox_inches='tight', facecolor='white')
        plt.close()
        print(f"Created: {large_file}")
        plot_files.append(large_file)

    return plot_files


def generate_consolidated_plot(data: dict):
    """Generate consolidated plot with all benchmark data.
    Matches Go's benchmark_consolidated.png layout."""
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    plt.style.use('default')
    plt.rcParams['figure.facecolor'] = 'white'
    plt.rcParams['axes.facecolor'] = 'white'
    plt.rcParams['axes.grid'] = True
    plt.rcParams['grid.alpha'] = 0.3
    plt.rcParams['font.size'] = 9

    colors = {'shm': '#00cc6a', 'tcp': '#ff5555'}

    cpu = data.get("cpu", "")[:40]
    runtime = data.get("runtime", "")
    timestamp = data.get("timestamp", "")[:10]
    ring_mb = data.get("ring_capacity_mb", 0)

    fig = plt.figure(figsize=(18, 22))
    gs = fig.add_gridspec(5, 3, hspace=0.4, wspace=0.3,
                          left=0.06, right=0.96, top=0.94, bottom=0.04)

    fig.suptitle(
        f'gRPC .NET Shared Memory Transport - Consolidated Benchmark Results\n'
        f'{ring_mb} MiB Ring Buffer • {runtime} • {cpu}',
        fontsize=14, fontweight='bold'
    )

    width = 0.3

    # ============================================================
    # ROW 1: Streaming (one-way) - all sizes
    # ============================================================
    size_labels = data["size_labels"]
    x = np.arange(len(size_labels))
    shm_lat = data["shm_stream_latency"]
    tcp_lat = data["tcp_stream_latency"]
    shm_tp = data["shm_stream_throughput"]
    tcp_tp = data["tcp_stream_throughput"]

    # Streaming Latency
    ax = fig.add_subplot(gs[0, 0])
    if _has_numeric(shm_lat) and _has_numeric(tcp_lat):
        ax.bar(x - width/2, [v or 0 for v in shm_lat], width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.bar(x + width/2, [v or 0 for v in tcp_lat], width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_ylabel('Latency (ns)')
        ax.set_title('STREAMING - Latency', fontweight='bold')
        ax.set_xticks(x)
        ax.set_xticklabels(size_labels, rotation=45, ha='right')
        ax.set_yscale('log')
        ax.legend(fontsize=8)

    # Streaming Throughput
    ax = fig.add_subplot(gs[0, 1])
    if _has_numeric(shm_tp) and _has_numeric(tcp_tp):
        ax.bar(x - width/2, [v or 0 for v in shm_tp], width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.bar(x + width/2, [v or 0 for v in tcp_tp], width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_ylabel('Throughput (MB/s)')
        ax.set_title('STREAMING - Throughput', fontweight='bold')
        ax.set_xticks(x)
        ax.set_xticklabels(size_labels, rotation=45, ha='right')
        ax.set_yscale('log')
        ax.legend(fontsize=8)

    # Streaming Speedup
    ax = fig.add_subplot(gs[0, 2])
    if _has_numeric(shm_lat) and _has_numeric(tcp_lat):
        speedups = [(tcp_lat[i] / shm_lat[i]) if (shm_lat[i] and tcp_lat[i]) else 0
                    for i in range(len(shm_lat))]
        ax.bar(x, speedups, 0.5, color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.set_ylabel('Speedup (×)')
        ax.set_title('STREAMING - SHM Speedup', fontweight='bold')
        ax.set_xticks(x)
        ax.set_xticklabels(size_labels, rotation=45, ha='right')
        ax.axhline(y=1, color='gray', linestyle='--', alpha=0.5)
        for i, s in enumerate(speedups):
            if s > 0:
                ax.annotate(f'{s:.1f}×', xy=(i, s), xytext=(0, 3),
                           textcoords='offset points', ha='center', fontsize=7, fontweight='bold')

    # ============================================================
    # ROW 2: Roundtrip (unary) - standard sizes
    # ============================================================
    rt_labels = data["rt_size_labels"]
    xr = np.arange(len(rt_labels))
    shm_rt = data["shm_rt_latency"]
    tcp_rt = data["tcp_rt_latency"]
    shm_rt_tp = data["shm_rt_throughput"]
    tcp_rt_tp = data["tcp_rt_throughput"]

    ax = fig.add_subplot(gs[1, 0])
    if _has_numeric(shm_rt) and _has_numeric(tcp_rt):
        ax.bar(xr - width/2, [v or 0 for v in shm_rt], width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.bar(xr + width/2, [v or 0 for v in tcp_rt], width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_ylabel('Latency (ns)')
        ax.set_title('UNARY (Roundtrip) - Latency', fontweight='bold')
        ax.set_xticks(xr)
        ax.set_xticklabels(rt_labels)
        ax.legend(fontsize=8)

    ax = fig.add_subplot(gs[1, 1])
    if _has_numeric(shm_rt_tp) and _has_numeric(tcp_rt_tp):
        ax.bar(xr - width/2, [v or 0 for v in shm_rt_tp], width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.bar(xr + width/2, [v or 0 for v in tcp_rt_tp], width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_ylabel('Throughput (MB/s)')
        ax.set_title('UNARY (Roundtrip) - Throughput', fontweight='bold')
        ax.set_xticks(xr)
        ax.set_xticklabels(rt_labels)
        ax.legend(fontsize=8)

    ax = fig.add_subplot(gs[1, 2])
    if _has_numeric(shm_rt) and _has_numeric(tcp_rt):
        speedups = [(tcp_rt[i] / shm_rt[i]) if (shm_rt[i] and tcp_rt[i]) else 0
                    for i in range(len(shm_rt))]
        ax.bar(xr, speedups, 0.5, color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.set_ylabel('Speedup (×)')
        ax.set_title('UNARY - SHM Speedup', fontweight='bold')
        ax.set_xticks(xr)
        ax.set_xticklabels(rt_labels)
        ax.axhline(y=1, color='gray', linestyle='--', alpha=0.5)
        for i, s in enumerate(speedups):
            if s > 0:
                ax.annotate(f'{s:.1f}×', xy=(i, s), xytext=(0, 3),
                           textcoords='offset points', ha='center', fontsize=7, fontweight='bold')

    # ============================================================
    # ROW 3: Large Payload Streaming
    # ============================================================
    large_labels = data.get("large_size_labels", [])
    shm_large_tp = data.get("shm_large_stream_throughput", [])
    tcp_large_tp = data.get("tcp_large_stream_throughput", [])
    shm_large_lat = data.get("shm_large_stream_latency", [])
    tcp_large_lat = data.get("tcp_large_stream_latency", [])

    valid_idx = [i for i in range(len(large_labels))
                 if i < len(shm_large_tp) and shm_large_tp[i] is not None]

    ax = fig.add_subplot(gs[2, 0])
    if valid_idx:
        labels = [large_labels[i] for i in valid_idx]
        xl = np.arange(len(labels))
        shm_vals = [shm_large_tp[i] or 0 for i in valid_idx]
        tcp_vals = [tcp_large_tp[i] if i < len(tcp_large_tp) and tcp_large_tp[i] else 0 for i in valid_idx]
        ax.bar(xl - width/2, shm_vals, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.bar(xl + width/2, tcp_vals, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_ylabel('Throughput (MB/s)')
        ax.set_title('LARGE STREAMING - Throughput', fontweight='bold')
        ax.set_xticks(xl)
        ax.set_xticklabels(labels)
        ax.legend(fontsize=8)

    ax = fig.add_subplot(gs[2, 1])
    if valid_idx:
        labels = [large_labels[i] for i in valid_idx]
        xl = np.arange(len(labels))
        shm_vals = [shm_large_lat[i] / 1e6 if shm_large_lat[i] else 0 for i in valid_idx]
        tcp_vals = [tcp_large_lat[i] / 1e6 if i < len(tcp_large_lat) and tcp_large_lat[i] else 0 for i in valid_idx]
        ax.bar(xl - width/2, shm_vals, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.bar(xl + width/2, tcp_vals, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_ylabel('Latency (ms)')
        ax.set_title('LARGE STREAMING - Latency', fontweight='bold')
        ax.set_xticks(xl)
        ax.set_xticklabels(labels)
        ax.legend(fontsize=8)

    ax = fig.add_subplot(gs[2, 2])
    if valid_idx and _has_numeric(shm_large_lat) and _has_numeric(tcp_large_lat):
        labels = [large_labels[i] for i in valid_idx]
        xl = np.arange(len(labels))
        speedups = [(tcp_large_lat[i] / shm_large_lat[i])
                    if (shm_large_lat[i] and tcp_large_lat[i]) else 0
                    for i in valid_idx]
        ax.bar(xl, speedups, 0.5, color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.set_ylabel('Speedup (×)')
        ax.set_title('LARGE STREAMING - SHM Speedup', fontweight='bold')
        ax.set_xticks(xl)
        ax.set_xticklabels(labels)
        ax.axhline(y=1, color='gray', linestyle='--', alpha=0.5)

    # ============================================================
    # ROW 4: Large Payload Roundtrip
    # ============================================================
    shm_large_rt_tp = data.get("shm_large_rt_throughput", [])
    tcp_large_rt_tp = data.get("tcp_large_rt_throughput", [])
    shm_large_rt_lat = data.get("shm_large_rt_latency", [])
    tcp_large_rt_lat = data.get("tcp_large_rt_latency", [])

    valid_rt_idx = [i for i in range(len(large_labels))
                    if i < len(shm_large_rt_tp) and shm_large_rt_tp[i] is not None]

    ax = fig.add_subplot(gs[3, 0])
    if valid_rt_idx:
        labels = [large_labels[i] for i in valid_rt_idx]
        xl = np.arange(len(labels))
        shm_vals = [shm_large_rt_tp[i] or 0 for i in valid_rt_idx]
        tcp_vals = [tcp_large_rt_tp[i] if i < len(tcp_large_rt_tp) and tcp_large_rt_tp[i] else 0 for i in valid_rt_idx]
        ax.bar(xl - width/2, shm_vals, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.bar(xl + width/2, tcp_vals, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_ylabel('Throughput (MB/s)')
        ax.set_title('LARGE UNARY (Roundtrip) - Throughput', fontweight='bold')
        ax.set_xticks(xl)
        ax.set_xticklabels(labels)
        ax.legend(fontsize=8)

    ax = fig.add_subplot(gs[3, 1])
    if valid_rt_idx:
        labels = [large_labels[i] for i in valid_rt_idx]
        xl = np.arange(len(labels))
        shm_vals = [shm_large_rt_lat[i] / 1e6 if shm_large_rt_lat[i] else 0 for i in valid_rt_idx]
        tcp_vals = [tcp_large_rt_lat[i] / 1e6 if i < len(tcp_large_rt_lat) and tcp_large_rt_lat[i] else 0 for i in valid_rt_idx]
        ax.bar(xl - width/2, shm_vals, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.bar(xl + width/2, tcp_vals, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.set_ylabel('Latency (ms)')
        ax.set_title('LARGE UNARY (Roundtrip) - Latency', fontweight='bold')
        ax.set_xticks(xl)
        ax.set_xticklabels(labels)
        ax.legend(fontsize=8)

    ax = fig.add_subplot(gs[3, 2])
    if valid_rt_idx and _has_numeric(shm_large_rt_lat) and _has_numeric(tcp_large_rt_lat):
        labels = [large_labels[i] for i in valid_rt_idx]
        xl = np.arange(len(labels))
        speedups = [(tcp_large_rt_lat[i] / shm_large_rt_lat[i])
                    if (shm_large_rt_lat[i] and tcp_large_rt_lat[i]) else 0
                    for i in valid_rt_idx]
        ax.bar(xl, speedups, 0.5, color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.set_ylabel('Speedup (×)')
        ax.set_title('LARGE UNARY - SHM Speedup', fontweight='bold')
        ax.set_xticks(xl)
        ax.set_xticklabels(labels)
        ax.axhline(y=1, color='gray', linestyle='--', alpha=0.5)

    # ============================================================
    # ROW 5: Summary text
    # ============================================================
    ax = fig.add_subplot(gs[4, :])
    ax.axis('off')

    summary_lines = [
        "=" * 100,
        "BENCHMARK SUMMARY - .NET SHM vs TCP Transport-Level Performance",
        "=" * 100,
        f"CPU: {cpu}  |  Runtime: {runtime}  |  Ring Buffer: {ring_mb} MiB  |  Date: {timestamp}",
        "",
    ]

    # Add key metrics
    shm_rt_1k = _safe_number(data["shm_rt_latency"], 2)
    tcp_rt_1k = _safe_number(data["tcp_rt_latency"], 2)
    if shm_rt_1k and tcp_rt_1k:
        summary_lines.append(f"Unary Latency (1KB): SHM {shm_rt_1k:.0f}ns vs TCP {tcp_rt_1k:.0f}ns = {tcp_rt_1k/shm_rt_1k:.1f}x speedup")

    shm_s_1k = _safe_number(data["shm_stream_latency"], 2)
    tcp_s_1k = _safe_number(data["tcp_stream_latency"], 2)
    if shm_s_1k and tcp_s_1k:
        summary_lines.append(f"Streaming Latency (1KB): SHM {shm_s_1k:.0f}ns vs TCP {tcp_s_1k:.0f}ns = {tcp_s_1k/shm_s_1k:.1f}x speedup")

    if _has_numeric(data["shm_stream_throughput"]):
        peak_shm = max(_filter_numeric(data["shm_stream_throughput"]))
        peak_tcp = max(_filter_numeric(data["tcp_stream_throughput"])) if _has_numeric(data["tcp_stream_throughput"]) else 0
        summary_lines.append(f"Peak Throughput: SHM {peak_shm/1000:.1f} GB/s vs TCP {peak_tcp/1000:.2f} GB/s")

    ax.text(0.5, 0.5, "\n".join(summary_lines), transform=ax.transAxes,
            fontsize=10, ha='center', va='center', fontfamily='monospace',
            bbox=dict(boxstyle='round', facecolor='#f0f0f0', edgecolor='gray', alpha=0.8))

    consolidated_file = OUT_DIR / "benchmark_consolidated.png"
    plt.savefig(consolidated_file, dpi=150, bbox_inches='tight', facecolor='white')
    plt.close()
    print(f"Created: {consolidated_file}")
    return consolidated_file


def main():
    parser = argparse.ArgumentParser(
        description='Run .NET transport benchmarks and generate plots',
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  python3 benchmark_runner.py              # Use cached results or run if none
  python3 benchmark_runner.py --run        # Force rerun benchmarks
  python3 benchmark_runner.py --plot-only  # Only plot, fail if no cached data
        """
    )
    parser.add_argument('--run', action='store_true',
                       help='Force rerun benchmarks even if cached results exist')
    parser.add_argument('--plot-only', action='store_true',
                       help='Only generate plots, fail if no cached results')

    args = parser.parse_args()

    OUT_DIR.mkdir(parents=True, exist_ok=True)
    RESULTS_DIR.mkdir(parents=True, exist_ok=True)

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

    if not results.get("benchmarks"):
        print("WARNING: No benchmark data found; plots will be empty.")

    data = extract_data(results)
    plot_files = generate_plots(data)
    consolidated_file = generate_consolidated_plot(data)

    print("\n" + "=" * 70)
    print("BENCHMARK PLOTS GENERATED")
    print("=" * 70)
    for f in plot_files:
        print(f"  {f}")
    print(f"  {consolidated_file}  <- CONSOLIDATED (all data)")
    print(f"\nConsolidated plot: {consolidated_file}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
