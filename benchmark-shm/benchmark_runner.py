#!/usr/bin/env python3
"""
Benchmark runner and plotter for .NET gRPC SHM vs TCP transport.
Matches grpc-go-shmem/benchmark/shmemtcp — actual gRPC UnaryCall and StreamingCall.

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
from pathlib import Path

import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import numpy as np

# Directory setup — matches Go's out/<platform>/ convention
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
        "--out", str(OUT_DIR)
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


def load_results() -> dict:
    """Load benchmark results from cached JSON file."""
    if RESULTS_FILE.exists():
        try:
            with open(RESULTS_FILE) as f:
                return json.load(f)
        except Exception:
            pass
    return None


def format_size(b):
    if b == 0: return '0B'
    if b >= 1048576: return f'{b // 1048576}MB'
    if b >= 1024: return f'{b // 1024}KB'
    return f'{b}B'


def extract_data(results: dict) -> dict:
    """Extract plotting data from the new JSON format."""
    sizes = results.get("sizes_bytes", [])
    size_labels = [format_size(s) for s in sizes]

    data = {
        "sizes": sizes,
        "size_labels": size_labels,
        "cpu": results.get("cpu", "Unknown CPU"),
        "runtime": results.get("runtime", ""),
        "timestamp": results.get("timestamp", "")[:10],
    }

    def get_series(bench_list, transport, field):
        """Extract values for a transport at each size."""
        lookup = {}
        for entry in bench_list:
            if entry.get("transport") == transport:
                lookup[entry["size_bytes"]] = entry.get(field)
        return [lookup.get(s) for s in sizes]

    unary = results.get("unary", [])
    streaming = results.get("streaming", [])

    data["shm_unary_latency"] = get_series(unary, "shm", "avg_latency_us")
    data["tcp_unary_latency"] = get_series(unary, "tcp", "avg_latency_us")
    data["shm_unary_throughput"] = get_series(unary, "shm", "throughput_mb_per_s")
    data["tcp_unary_throughput"] = get_series(unary, "tcp", "throughput_mb_per_s")

    data["shm_stream_latency"] = get_series(streaming, "shm", "avg_latency_us")
    data["tcp_stream_latency"] = get_series(streaming, "tcp", "avg_latency_us")
    data["shm_stream_throughput"] = get_series(streaming, "shm", "throughput_mb_per_s")
    data["tcp_stream_throughput"] = get_series(streaming, "tcp", "throughput_mb_per_s")

    return data


def _filter_numeric(seq):
    return [x for x in seq if x is not None and isinstance(x, (int, float)) and x > 0]


def _has_numeric(seq):
    return len(_filter_numeric(seq)) > 0


def generate_plots(data: dict):
    """Generate benchmark comparison plots — SHM vs TCP, unary + streaming."""
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    plt.style.use('default')
    plt.rcParams.update({
        'figure.facecolor': 'white',
        'axes.facecolor': 'white',
        'axes.grid': True,
        'grid.alpha': 0.3,
        'font.size': 10,
    })

    colors = {'shm': '#d62728', 'tcp': '#1f77b4'}
    cpu = data.get("cpu", "")
    runtime = data.get("runtime", "")
    timestamp = data.get("timestamp", "")
    size_labels = data["size_labels"]
    x = np.arange(len(size_labels))
    width = 0.35

    plot_files = []

    # ================================================================
    # Plot 1: Unary + Streaming latency & throughput (2x2)
    # ================================================================
    fig, axes = plt.subplots(2, 2, figsize=(14, 10))
    fig.suptitle(
        f'gRPC .NET: SHM vs TCP Transport\n{runtime} \u2022 {cpu[:50]}',
        fontsize=14, fontweight='bold'
    )

    # --- Unary Latency ---
    ax = axes[0, 0]
    shm_ul = data["shm_unary_latency"]
    tcp_ul = data["tcp_unary_latency"]
    if _has_numeric(shm_ul) and _has_numeric(tcp_ul):
        shm_v = [v or 0 for v in shm_ul]
        tcp_v = [v or 0 for v in tcp_ul]
        ax.bar(x - width/2, tcp_v, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.bar(x + width/2, shm_v, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.set_ylabel('Avg latency (\u00b5s)')
        ax.set_title('Unary Ping-Pong Latency\n(lower is better)')
        ax.set_xticks(x); ax.set_xticklabels(size_labels, rotation=45, ha='right')
        ax.legend(); ax.set_yscale('log')
        for i in range(len(shm_v)):
            if shm_v[i] > 0 and tcp_v[i] > 0:
                speedup = tcp_v[i] / shm_v[i]
                ax.annotate(f'{speedup:.1f}x', xy=(i + width/2, shm_v[i]),
                           xytext=(0, 5), textcoords='offset points', ha='center',
                           fontsize=8, color=colors['shm'], fontweight='bold')

    # --- Unary Throughput ---
    ax = axes[0, 1]
    shm_ut = data["shm_unary_throughput"]
    tcp_ut = data["tcp_unary_throughput"]
    if _has_numeric(shm_ut) and _has_numeric(tcp_ut):
        shm_v = [v or 0 for v in shm_ut]
        tcp_v = [v or 0 for v in tcp_ut]
        ax.bar(x - width/2, tcp_v, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.bar(x + width/2, shm_v, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.set_ylabel('Throughput (MB/s)')
        ax.set_title('Unary Throughput\n(higher is better)')
        ax.set_xticks(x); ax.set_xticklabels(size_labels, rotation=45, ha='right')
        ax.legend(); ax.set_yscale('log')

    # --- Streaming Latency ---
    ax = axes[1, 0]
    shm_sl = data["shm_stream_latency"]
    tcp_sl = data["tcp_stream_latency"]
    if _has_numeric(shm_sl) and _has_numeric(tcp_sl):
        shm_v = [v or 0 for v in shm_sl]
        tcp_v = [v or 0 for v in tcp_sl]
        ax.bar(x - width/2, tcp_v, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.bar(x + width/2, shm_v, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.set_ylabel('Avg latency (\u00b5s)')
        ax.set_title('Streaming Ping-Pong Latency\n(lower is better)')
        ax.set_xticks(x); ax.set_xticklabels(size_labels, rotation=45, ha='right')
        ax.legend(); ax.set_yscale('log')
        for i in range(len(shm_v)):
            if shm_v[i] > 0 and tcp_v[i] > 0:
                speedup = tcp_v[i] / shm_v[i]
                ax.annotate(f'{speedup:.1f}x', xy=(i + width/2, shm_v[i]),
                           xytext=(0, 5), textcoords='offset points', ha='center',
                           fontsize=8, color=colors['shm'], fontweight='bold')

    # --- Streaming Throughput ---
    ax = axes[1, 1]
    shm_st = data["shm_stream_throughput"]
    tcp_st = data["tcp_stream_throughput"]
    if _has_numeric(shm_st) and _has_numeric(tcp_st):
        shm_v = [v or 0 for v in shm_st]
        tcp_v = [v or 0 for v in tcp_st]
        ax.bar(x - width/2, tcp_v, width, label='TCP', color=colors['tcp'], edgecolor='black', linewidth=0.5)
        ax.bar(x + width/2, shm_v, width, label='SHM', color=colors['shm'], edgecolor='black', linewidth=0.5)
        ax.set_ylabel('Throughput (MB/s)')
        ax.set_title('Streaming Throughput\n(higher is better)')
        ax.set_xticks(x); ax.set_xticklabels(size_labels, rotation=45, ha='right')
        ax.legend(); ax.set_yscale('log')

    plt.tight_layout(rect=[0, 0, 1, 0.94])
    main_file = OUT_DIR / "benchmark_comparison.png"
    plt.savefig(main_file, dpi=150, bbox_inches='tight', facecolor='white')
    plt.close()
    print(f"Created: {main_file}")
    plot_files.append(main_file)

    # ================================================================
    # Plot 2: Speedup summary
    # ================================================================
    fig, axes = plt.subplots(1, 2, figsize=(14, 5))
    fig.suptitle(f'SHM Speedup over TCP\n{timestamp}', fontsize=14, fontweight='bold')

    for idx, (label, shm_lat, tcp_lat) in enumerate([
        ("Unary", data["shm_unary_latency"], data["tcp_unary_latency"]),
        ("Streaming", data["shm_stream_latency"], data["tcp_stream_latency"]),
    ]):
        ax = axes[idx]
        if _has_numeric(shm_lat) and _has_numeric(tcp_lat):
            speedups = [(tcp_lat[i] / shm_lat[i]) if (shm_lat[i] and tcp_lat[i] and shm_lat[i] > 0) else 0
                        for i in range(len(shm_lat))]
            bars = ax.bar(x, speedups, 0.6, color=colors['shm'], edgecolor='black', linewidth=0.5)
            ax.set_ylabel('Speedup (\u00d7)')
            ax.set_title(f'{label} Latency Speedup')
            ax.set_xticks(x); ax.set_xticklabels(size_labels, rotation=45, ha='right')
            ax.axhline(y=1, color='gray', linestyle='--', alpha=0.5)
            for i, s in enumerate(speedups):
                if s > 0:
                    ax.annotate(f'{s:.1f}\u00d7', xy=(i, s), xytext=(0, 4),
                               textcoords='offset points', ha='center', fontsize=8, fontweight='bold')

    plt.tight_layout(rect=[0, 0, 1, 0.90])
    speedup_file = OUT_DIR / "benchmark_speedup.png"
    plt.savefig(speedup_file, dpi=150, bbox_inches='tight', facecolor='white')
    plt.close()
    print(f"Created: {speedup_file}")
    plot_files.append(speedup_file)

    return plot_files


def main():
    parser = argparse.ArgumentParser(
        description='Run .NET gRPC benchmarks (SHM vs TCP) and generate plots',
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

    data = extract_data(results)
    plot_files = generate_plots(data)

    print("\n" + "=" * 70)
    print("BENCHMARK PLOTS GENERATED")
    print("=" * 70)
    for f in plot_files:
        print(f"  {f}")
    print(f"\nResults: {RESULTS_FILE}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
