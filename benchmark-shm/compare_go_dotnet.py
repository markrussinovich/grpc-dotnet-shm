#!/usr/bin/env python3
"""
Compare grpc-go-shmem vs grpc-dotnet-shm benchmark results.

Go data sources:
  - shm-rfc/README.md raw benchmark results (2026-02-02)
  - benchmark/shmemtcp/plot_benchmarks.py hardcoded data
  - benchmark/shmemtcp/generate_benchmark_plots.py hardcoded data

.NET data source:
  - benchmark-shm/results/ringbench_results.json (2026-02-08)
"""

import json
import os
import matplotlib.pyplot as plt
import matplotlib.gridspec as gridspec
import numpy as np

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
PLOTS_DIR = os.path.join(SCRIPT_DIR, "plots")
os.makedirs(PLOTS_DIR, exist_ok=True)

# =============================================================================
# GO BENCHMARK DATA  (from shm-rfc/README.md raw results, 2026-02-02)
# Ring size: 64 MB, CPU: AMD EPYC 7763
# =============================================================================

go_shm_stream = {  # BenchmarkShmRingWriteRead
    64:      {"ns_op": 65,    "mb_s": 987.65},
    256:     {"ns_op": 84,    "mb_s": 3044.78},
    1024:    {"ns_op": 113,   "mb_s": 9053.78},
    4096:    {"ns_op": 324,   "mb_s": 12636.60},
    16384:   {"ns_op": 1181,  "mb_s": 13872.65},
    65536:   {"ns_op": 4751,  "mb_s": 13793.29},
    262144:  {"ns_op": 17942, "mb_s": 14610.89},
    1048576: {"ns_op": 35524, "mb_s": 29517.65},
}

go_shm_roundtrip = {  # BenchmarkShmRingRoundtrip
    64:   {"ns_op": 307, "mb_s": 416.82},
    256:  {"ns_op": 366, "mb_s": 1400.28},
    1024: {"ns_op": 335, "mb_s": 6105.95},
    4096: {"ns_op": 550, "mb_s": 14891.16},
}

go_tcp_stream = {  # from plot_benchmarks.py (actual test runs)
    64:    {"ns_op": 7663,  "mb_s": 8.35},
    256:   {"ns_op": 8026,  "mb_s": 31.90},
    1024:  {"ns_op": 6404,  "mb_s": 159.89},
    4096:  {"ns_op": 7538,  "mb_s": 543.41},
    16384: {"ns_op": 11195, "mb_s": 1463.50},
    65536: {"ns_op": 25351, "mb_s": 2585.14},
}

go_tcp_roundtrip = {  # BenchmarkTCPLoopbackRoundtrip
    64:   {"ns_op": 17897, "mb_s": 7.15},
    256:  {"ns_op": 18376, "mb_s": 27.86},
    1024: {"ns_op": 17738, "mb_s": 115.46},
    4096: {"ns_op": 19864, "mb_s": 412.41},
}

go_unix_stream = {  # from plot_benchmarks.py
    64:    {"ns_op": 2187,  "mb_s": 29.26},
    256:   {"ns_op": 2220,  "mb_s": 115.29},
    1024:  {"ns_op": 2646,  "mb_s": 387.05},
    4096:  {"ns_op": 3223,  "mb_s": 1270.92},
    16384: {"ns_op": 5213,  "mb_s": 3142.86},
    65536: {"ns_op": 13012, "mb_s": 5036.76},
}

# =============================================================================
# .NET BENCHMARK DATA  (from ringbench_results.json, 2026-02-08)
# Ring size: 4 MB, CPU: AMD EPYC 7763
# =============================================================================

with open(os.path.join(SCRIPT_DIR, "results", "ringbench_results.json")) as f:
    dotnet_raw = json.load(f)

dotnet_benchmarks = dotnet_raw["benchmarks"]

def parse_dotnet(prefix):
    """Parse .NET benchmark results into {size: {ns_op, mb_s}} dict."""
    result = {}
    for key, val in dotnet_benchmarks.items():
        if key.startswith(prefix):
            size_str = key.split("size=")[1]
            size = int(size_str)
            result[size] = {"ns_op": val["ns_per_op"], "mb_s": val["mb_per_s"]}
    return result

dotnet_shm_stream = parse_dotnet("ShmRingWriteRead")
dotnet_shm_roundtrip = parse_dotnet("ShmRingRoundtrip")
dotnet_tcp_stream = parse_dotnet("TCPLoopback/")
dotnet_tcp_roundtrip = parse_dotnet("TCPLoopbackRoundtrip")
dotnet_shm_large = parse_dotnet("ShmRingLargePayloads/")
dotnet_shm_large_rt = parse_dotnet("ShmRingLargePayloadsRoundtrip")
dotnet_tcp_large = parse_dotnet("TCPLargePayloads/")
dotnet_tcp_large_rt = parse_dotnet("TCPLargePayloadsRoundtrip")


# =============================================================================
# COMPARISON PLOT
# =============================================================================

plt.style.use('default')
plt.rcParams.update({
    'figure.facecolor': 'white',
    'axes.facecolor': '#fafafa',
    'axes.grid': True,
    'grid.alpha': 0.3,
    'font.size': 10,
    'axes.spines.top': False,
    'axes.spines.right': False,
})

colors = {
    'go_shm':     '#2ecc71',  # green
    'dotnet_shm': '#00cc6a',  # bright green
    'go_tcp':     '#e74c3c',  # red
    'dotnet_tcp': '#ff6b6b',  # light red
    'go_unix':    '#3498db',  # blue
    'speedup':    '#9b59b6',  # purple
}

fig = plt.figure(figsize=(20, 24))
gs = gridspec.GridSpec(4, 3, hspace=0.35, wspace=0.3,
                       left=0.06, right=0.97, top=0.94, bottom=0.04)

fig.suptitle(
    'grpc-go-shmem vs grpc-dotnet-shm  —  Ring Buffer Benchmark Comparison\n'
    'AMD EPYC 7763  •  Go: 64 MB Ring  •  .NET: 4 MB Ring',
    fontsize=15, fontweight='bold', y=0.97
)

# -------------------------------------------------------------------------
# Row 1, Col 0: SHM Streaming Latency (Go vs .NET)
# -------------------------------------------------------------------------
ax = fig.add_subplot(gs[0, 0])
stream_sizes = [64, 256, 1024, 4096, 16384, 65536]
labels = ['64B', '256B', '1KB', '4KB', '16KB', '64KB']
x = np.arange(len(stream_sizes))
width = 0.35

go_lat = [go_shm_stream[s]["ns_op"] for s in stream_sizes]
dn_lat = [dotnet_shm_stream[s]["ns_op"] for s in stream_sizes]

bars1 = ax.bar(x - width/2, go_lat, width, label='Go SHM', color=colors['go_shm'],
               edgecolor='black', linewidth=0.5, alpha=0.85)
bars2 = ax.bar(x + width/2, dn_lat, width, label='.NET SHM', color=colors['dotnet_shm'],
               edgecolor='black', linewidth=0.5, alpha=0.85)

# Add ratio annotations
for i, (g, d) in enumerate(zip(go_lat, dn_lat)):
    ratio = g / d
    ax.annotate(f'{ratio:.1f}x', xy=(i, min(g, d)),
                xytext=(0, -14), textcoords='offset points',
                ha='center', fontsize=8, fontweight='bold',
                color='darkgreen' if ratio > 1 else 'darkred')

ax.set_xticks(x)
ax.set_xticklabels(labels)
ax.set_ylabel('Latency (ns/op)')
ax.set_title('SHM Streaming Latency\n(lower is better)', fontweight='bold')
ax.set_yscale('log')
ax.legend(loc='upper left', fontsize=9)

# -------------------------------------------------------------------------
# Row 1, Col 1: SHM Streaming Throughput (Go vs .NET)
# -------------------------------------------------------------------------
ax = fig.add_subplot(gs[0, 1])

go_tp = [go_shm_stream[s]["mb_s"] for s in stream_sizes]
dn_tp = [dotnet_shm_stream[s]["mb_s"] for s in stream_sizes]

bars1 = ax.bar(x - width/2, go_tp, width, label='Go SHM', color=colors['go_shm'],
               edgecolor='black', linewidth=0.5, alpha=0.85)
bars2 = ax.bar(x + width/2, dn_tp, width, label='.NET SHM', color=colors['dotnet_shm'],
               edgecolor='black', linewidth=0.5, alpha=0.85)

for i, (g, d) in enumerate(zip(go_tp, dn_tp)):
    ratio = d / g
    y_pos = max(g, d)
    ax.annotate(f'{ratio:.1f}x', xy=(i, y_pos),
                xytext=(0, 3), textcoords='offset points',
                ha='center', fontsize=8, fontweight='bold',
                color='darkgreen' if ratio > 1 else 'darkred')

ax.set_xticks(x)
ax.set_xticklabels(labels)
ax.set_ylabel('Throughput (MB/s)')
ax.set_title('SHM Streaming Throughput\n(higher is better)', fontweight='bold')
ax.set_yscale('log')
ax.legend(loc='upper left', fontsize=9)

# -------------------------------------------------------------------------
# Row 1, Col 2: SHM Roundtrip Latency (Go vs .NET)
# -------------------------------------------------------------------------
ax = fig.add_subplot(gs[0, 2])
rt_sizes = [64, 256, 1024, 4096]
rt_labels = ['64B', '256B', '1KB', '4KB']
x_rt = np.arange(len(rt_sizes))

go_rt = [go_shm_roundtrip[s]["ns_op"] for s in rt_sizes]
dn_rt = [dotnet_shm_roundtrip[s]["ns_op"] for s in rt_sizes]

bars1 = ax.bar(x_rt - width/2, go_rt, width, label='Go SHM', color=colors['go_shm'],
               edgecolor='black', linewidth=0.5, alpha=0.85)
bars2 = ax.bar(x_rt + width/2, dn_rt, width, label='.NET SHM', color=colors['dotnet_shm'],
               edgecolor='black', linewidth=0.5, alpha=0.85)

for i, (g, d) in enumerate(zip(go_rt, dn_rt)):
    ratio = g / d
    ax.annotate(f'{ratio:.1f}x', xy=(i, min(g, d)),
                xytext=(0, -14), textcoords='offset points',
                ha='center', fontsize=8, fontweight='bold',
                color='darkgreen' if ratio > 1 else 'darkred')

ax.set_xticks(x_rt)
ax.set_xticklabels(rt_labels)
ax.set_ylabel('Latency (ns/op)')
ax.set_title('SHM Roundtrip Latency\n(lower is better)', fontweight='bold')
ax.legend(loc='upper left', fontsize=9)

# -------------------------------------------------------------------------
# Row 2, Col 0: TCP Streaming Latency (Go vs .NET)
# -------------------------------------------------------------------------
ax = fig.add_subplot(gs[1, 0])

go_tcp_lat = [go_tcp_stream[s]["ns_op"] for s in stream_sizes]
dn_tcp_lat = [dotnet_tcp_stream[s]["ns_op"] for s in stream_sizes]

bars1 = ax.bar(x - width/2, go_tcp_lat, width, label='Go TCP', color=colors['go_tcp'],
               edgecolor='black', linewidth=0.5, alpha=0.85)
bars2 = ax.bar(x + width/2, dn_tcp_lat, width, label='.NET TCP', color=colors['dotnet_tcp'],
               edgecolor='black', linewidth=0.5, alpha=0.85)

for i, (g, d) in enumerate(zip(go_tcp_lat, dn_tcp_lat)):
    ratio = g / d
    label = f'{ratio:.1f}x' if ratio >= 1 else f'{1/ratio:.1f}x slower'
    ax.annotate(f'{ratio:.2f}x', xy=(i, min(g, d)),
                xytext=(0, -14), textcoords='offset points',
                ha='center', fontsize=7, fontweight='bold',
                color='darkgreen' if ratio > 1 else 'darkred')

ax.set_xticks(x)
ax.set_xticklabels(labels)
ax.set_ylabel('Latency (ns/op)')
ax.set_title('TCP Streaming Latency\n(lower is better)', fontweight='bold')
ax.set_yscale('log')
ax.legend(loc='upper left', fontsize=9)

# -------------------------------------------------------------------------
# Row 2, Col 1: TCP Roundtrip Latency (Go vs .NET)
# -------------------------------------------------------------------------
ax = fig.add_subplot(gs[1, 1])

go_tcp_rt = [go_tcp_roundtrip[s]["ns_op"] for s in rt_sizes]
dn_tcp_rt = [dotnet_tcp_roundtrip[s]["ns_op"] for s in rt_sizes]

bars1 = ax.bar(x_rt - width/2, go_tcp_rt, width, label='Go TCP', color=colors['go_tcp'],
               edgecolor='black', linewidth=0.5, alpha=0.85)
bars2 = ax.bar(x_rt + width/2, dn_tcp_rt, width, label='.NET TCP', color=colors['dotnet_tcp'],
               edgecolor='black', linewidth=0.5, alpha=0.85)

for i, (g, d) in enumerate(zip(go_tcp_rt, dn_tcp_rt)):
    ratio = g / d
    ax.annotate(f'{ratio:.2f}x', xy=(i, min(g, d)),
                xytext=(0, -14), textcoords='offset points',
                ha='center', fontsize=7, fontweight='bold',
                color='darkgreen' if ratio > 1 else 'darkred')

ax.set_xticks(x_rt)
ax.set_xticklabels(rt_labels)
ax.set_ylabel('Latency (ns/op)')
ax.set_title('TCP Roundtrip Latency\n(lower is better)', fontweight='bold')
ax.legend(loc='upper left', fontsize=9)

# -------------------------------------------------------------------------
# Row 2, Col 2: SHM vs TCP Speedup comparison (Go vs .NET)
# -------------------------------------------------------------------------
ax = fig.add_subplot(gs[1, 2])

# Speedup at each streaming size
go_speedups = [go_tcp_stream[s]["ns_op"] / go_shm_stream[s]["ns_op"] for s in stream_sizes]
dn_speedups = [dotnet_tcp_stream[s]["ns_op"] / dotnet_shm_stream[s]["ns_op"] for s in stream_sizes]

bars1 = ax.bar(x - width/2, go_speedups, width, label='Go (SHM vs TCP)', color=colors['go_shm'],
               edgecolor='black', linewidth=0.5, alpha=0.85)
bars2 = ax.bar(x + width/2, dn_speedups, width, label='.NET (SHM vs TCP)', color=colors['dotnet_shm'],
               edgecolor='black', linewidth=0.5, alpha=0.85)

for i, (g, d) in enumerate(zip(go_speedups, dn_speedups)):
    ax.annotate(f'{g:.0f}x', xy=(bars1[i].get_x() + bars1[i].get_width()/2, g),
                xytext=(0, 3), textcoords='offset points', ha='center', fontsize=7, fontweight='bold')
    ax.annotate(f'{d:.0f}x', xy=(bars2[i].get_x() + bars2[i].get_width()/2, d),
                xytext=(0, 3), textcoords='offset points', ha='center', fontsize=7, fontweight='bold')

ax.axhline(y=1, color='gray', linestyle='--', alpha=0.5)
ax.set_xticks(x)
ax.set_xticklabels(labels)
ax.set_ylabel('Speedup Factor')
ax.set_title('SHM-over-TCP Speedup\n(higher = SHM advantage)', fontweight='bold')
ax.legend(loc='upper right', fontsize=9)

# -------------------------------------------------------------------------
# Row 3, Col 0: .NET vs Go SHM Latency Ratio (bar chart)
# -------------------------------------------------------------------------
ax = fig.add_subplot(gs[2, 0])

all_sizes = [64, 256, 1024, 4096, 16384, 65536]
all_labels = ['64B', '256B', '1KB', '4KB', '16KB', '64KB']
ratios_stream = [go_shm_stream[s]["ns_op"] / dotnet_shm_stream[s]["ns_op"] for s in all_sizes]

bar_colors = ['#2ecc71' if r > 1 else '#e74c3c' for r in ratios_stream]
bars = ax.bar(range(len(all_sizes)), ratios_stream, color=bar_colors, edgecolor='black', linewidth=0.5)

for bar, ratio in zip(bars, ratios_stream):
    label = f'{ratio:.1f}x' if ratio >= 1 else f'{ratio:.1f}x'
    ax.annotate(label, xy=(bar.get_x() + bar.get_width()/2, ratio),
                xytext=(0, 3), textcoords='offset points',
                ha='center', fontsize=9, fontweight='bold')

ax.axhline(y=1.0, color='black', linewidth=1.5, linestyle='-')
ax.set_xticks(range(len(all_sizes)))
ax.set_xticklabels(all_labels)
ax.set_ylabel('Go / .NET Latency Ratio')
ax.set_title('.NET SHM Speedup over Go SHM\n(>1 = .NET faster)', fontweight='bold')
ax.text(0.02, 0.95, '↑ .NET faster', transform=ax.transAxes, fontsize=9,
        color='green', fontweight='bold', va='top')
ax.text(0.02, 0.05, '↓ Go faster', transform=ax.transAxes, fontsize=9,
        color='red', fontweight='bold', va='bottom')

# -------------------------------------------------------------------------
# Row 3, Col 1: .NET vs Go Roundtrip Ratio
# -------------------------------------------------------------------------
ax = fig.add_subplot(gs[2, 1])

ratios_rt = [go_shm_roundtrip[s]["ns_op"] / dotnet_shm_roundtrip[s]["ns_op"] for s in rt_sizes]

bar_colors = ['#2ecc71' if r > 1 else '#e74c3c' for r in ratios_rt]
bars = ax.bar(range(len(rt_sizes)), ratios_rt, color=bar_colors, edgecolor='black', linewidth=0.5)

for bar, ratio in zip(bars, ratios_rt):
    ax.annotate(f'{ratio:.1f}x', xy=(bar.get_x() + bar.get_width()/2, ratio),
                xytext=(0, 3), textcoords='offset points',
                ha='center', fontsize=10, fontweight='bold')

ax.axhline(y=1.0, color='black', linewidth=1.5, linestyle='-')
ax.set_xticks(range(len(rt_sizes)))
ax.set_xticklabels(rt_labels)
ax.set_ylabel('Go / .NET Latency Ratio')
ax.set_title('.NET SHM Roundtrip Speedup over Go\n(>1 = .NET faster)', fontweight='bold')
ax.text(0.02, 0.95, '↑ .NET faster', transform=ax.transAxes, fontsize=9,
        color='green', fontweight='bold', va='top')

# -------------------------------------------------------------------------
# Row 3, Col 2: Peak Throughput Comparison
# -------------------------------------------------------------------------
ax = fig.add_subplot(gs[2, 2])

categories = ['SHM Stream\n64KB', 'SHM Roundtrip\n4KB', 'TCP Stream\n64KB', 'TCP Roundtrip\n4KB']
go_peaks = [
    go_shm_stream[65536]["mb_s"],
    go_shm_roundtrip[4096]["mb_s"],
    go_tcp_stream[65536]["mb_s"],
    go_tcp_roundtrip[4096]["mb_s"],
]
dn_peaks = [
    dotnet_shm_stream[65536]["mb_s"],
    dotnet_shm_roundtrip[4096]["mb_s"],
    dotnet_tcp_stream[65536]["mb_s"],
    dotnet_tcp_roundtrip[4096]["mb_s"],
]

x_peak = np.arange(len(categories))
bars1 = ax.bar(x_peak - width/2, go_peaks, width, label='Go', color='#3498db',
               edgecolor='black', linewidth=0.5, alpha=0.85)
bars2 = ax.bar(x_peak + width/2, dn_peaks, width, label='.NET', color='#e67e22',
               edgecolor='black', linewidth=0.5, alpha=0.85)

for bar, val in zip(list(bars1) + list(bars2), go_peaks + dn_peaks):
    label = f'{val/1000:.1f} GB/s' if val >= 1000 else f'{val:.0f} MB/s'
    ax.annotate(label, xy=(bar.get_x() + bar.get_width()/2, val),
                xytext=(0, 3), textcoords='offset points',
                ha='center', fontsize=7, fontweight='bold')

ax.set_xticks(x_peak)
ax.set_xticklabels(categories, fontsize=9)
ax.set_ylabel('Throughput (MB/s)')
ax.set_title('Peak Throughput Comparison\n(higher is better)', fontweight='bold')
ax.set_yscale('log')
ax.legend(fontsize=9)

# -------------------------------------------------------------------------
# Row 4: Summary Table
# -------------------------------------------------------------------------
ax = fig.add_subplot(gs[3, :])
ax.axis('off')

# Build summary text
summary = """
╔══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════╗
║                        grpc-go-shmem  vs  grpc-dotnet-shm  —  Ring Buffer Performance Comparison                  ║
╠════════════════════════╤═══════════════════╤═══════════════════╤════════════╤═══════════════════════════════════════╣
║ Benchmark              │    Go (64MB Ring) │ .NET (4MB Ring)   │ .NET/Go    │ Notes                                ║
╠════════════════════════╪═══════════════════╪═══════════════════╪════════════╪═══════════════════════════════════════╣
"""

rows = [
    ("SHM Stream 64B",   f"{go_shm_stream[64]['ns_op']:.0f} ns", f"{dotnet_shm_stream[64]['ns_op']:.1f} ns",
     go_shm_stream[64]['ns_op'] / dotnet_shm_stream[64]['ns_op'], ".NET 2.8x faster"),
    ("SHM Stream 1KB",   f"{go_shm_stream[1024]['ns_op']:.0f} ns", f"{dotnet_shm_stream[1024]['ns_op']:.1f} ns",
     go_shm_stream[1024]['ns_op'] / dotnet_shm_stream[1024]['ns_op'], ".NET 2.4x faster"),
    ("SHM Stream 64KB",  f"{go_shm_stream[65536]['ns_op']:.0f} ns", f"{dotnet_shm_stream[65536]['ns_op']:.1f} ns",
     go_shm_stream[65536]['ns_op'] / dotnet_shm_stream[65536]['ns_op'], ".NET 1.5x faster"),
    ("SHM Roundtrip 64B",  f"{go_shm_roundtrip[64]['ns_op']:.0f} ns", f"{dotnet_shm_roundtrip[64]['ns_op']:.1f} ns",
     go_shm_roundtrip[64]['ns_op'] / dotnet_shm_roundtrip[64]['ns_op'], ".NET 12x faster"),
    ("SHM Roundtrip 1KB",  f"{go_shm_roundtrip[1024]['ns_op']:.0f} ns", f"{dotnet_shm_roundtrip[1024]['ns_op']:.1f} ns",
     go_shm_roundtrip[1024]['ns_op'] / dotnet_shm_roundtrip[1024]['ns_op'], ".NET 7.2x faster"),
    ("SHM Roundtrip 4KB",  f"{go_shm_roundtrip[4096]['ns_op']:.0f} ns", f"{dotnet_shm_roundtrip[4096]['ns_op']:.1f} ns",
     go_shm_roundtrip[4096]['ns_op'] / dotnet_shm_roundtrip[4096]['ns_op'], ".NET 2.9x faster"),
    ("TCP Stream 64B",   f"{go_tcp_stream[64]['ns_op']:.0f} ns", f"{dotnet_tcp_stream[64]['ns_op']:.1f} ns",
     go_tcp_stream[64]['ns_op'] / dotnet_tcp_stream[64]['ns_op'], "Go 1.3x faster"),
    ("TCP Stream 64KB",  f"{go_tcp_stream[65536]['ns_op']:.0f} ns", f"{dotnet_tcp_stream[65536]['ns_op']:.1f} ns",
     go_tcp_stream[65536]['ns_op'] / dotnet_tcp_stream[65536]['ns_op'], "Comparable"),
    ("TCP Roundtrip 64B", f"{go_tcp_roundtrip[64]['ns_op']:.0f} ns", f"{dotnet_tcp_roundtrip[64]['ns_op']:.1f} ns",
     go_tcp_roundtrip[64]['ns_op'] / dotnet_tcp_roundtrip[64]['ns_op'], "Go 2.1x faster"),
    ("TCP Roundtrip 4KB", f"{go_tcp_roundtrip[4096]['ns_op']:.0f} ns", f"{dotnet_tcp_roundtrip[4096]['ns_op']:.1f} ns",
     go_tcp_roundtrip[4096]['ns_op'] / dotnet_tcp_roundtrip[4096]['ns_op'], "Go 2.1x faster"),
    ("SHM Peak Thruput",  f"{go_shm_stream[65536]['mb_s']/1000:.1f} GB/s", f"{dotnet_shm_stream[65536]['mb_s']/1000:.1f} GB/s",
     dotnet_shm_stream[65536]['mb_s'] / go_shm_stream[65536]['mb_s'], ".NET 1.5x higher"),
]

for name, go_val, dn_val, ratio, note in rows:
    marker = "✓" if ratio >= 1.0 else "✗"
    ratio_str = f"{ratio:.1f}x {'faster' if ratio >= 1 else 'slower'}"
    summary += f"║ {name:<22} │ {go_val:>17} │ {dn_val:>17} │ {ratio_str:>10} │ {note:<37} ║\n"

summary += """╠════════════════════════╧═══════════════════╧═══════════════════╧════════════╧═══════════════════════════════════════╣
║ Key Finding: .NET SHM ring is 2-12x faster than Go SHM ring in latency.                                           ║
║ Go TCP loopback has ~2x lower latency than .NET TCP (Go's net package is highly optimized).                        ║
║ But .NET's SHM advantage over its own TCP is even larger (265x at 64B) vs Go's (118x at 64B).                     ║
║ Note: Go uses 64MB rings, .NET uses 4MB rings — ring size has minimal impact on small-message latency.             ║
╚══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════╝"""

ax.text(0.5, 0.5, summary, transform=ax.transAxes, fontsize=8.5,
        verticalalignment='center', horizontalalignment='center',
        fontfamily='monospace',
        bbox=dict(boxstyle='round', facecolor='#f8f8f8', edgecolor='gray', alpha=0.9))

# Save
output_path = os.path.join(PLOTS_DIR, "go_vs_dotnet_comparison.png")
plt.savefig(output_path, dpi=150, bbox_inches='tight', facecolor='white')
plt.close()
print(f"Created: {output_path}")

# =============================================================================
# PRINT COMPARISON TABLE TO STDOUT
# =============================================================================

print("\n" + "=" * 100)
print("grpc-go-shmem vs grpc-dotnet-shm  —  Ring Buffer Benchmark Comparison")
print("=" * 100)

print(f"\n{'Benchmark':<28} {'Go (ns/op)':>12} {'Go (MB/s)':>12} {'.NET (ns/op)':>14} {'.NET (MB/s)':>14} {'.NET/Go lat':>12}")
print("-" * 100)

for size in [64, 256, 1024, 4096, 16384, 65536]:
    label = {64:'64B', 256:'256B', 1024:'1KB', 4096:'4KB', 16384:'16KB', 65536:'64KB'}[size]
    go_ns = go_shm_stream[size]['ns_op']
    go_mb = go_shm_stream[size]['mb_s']
    dn_ns = dotnet_shm_stream[size]['ns_op']
    dn_mb = dotnet_shm_stream[size]['mb_s']
    ratio = go_ns / dn_ns
    print(f"SHM Stream {label:<16} {go_ns:>12.1f} {go_mb:>12.1f} {dn_ns:>14.1f} {dn_mb:>14.1f} {ratio:>11.1f}x")

print()
for size in [64, 256, 1024, 4096]:
    label = {64:'64B', 256:'256B', 1024:'1KB', 4096:'4KB'}[size]
    go_ns = go_shm_roundtrip[size]['ns_op']
    go_mb = go_shm_roundtrip[size]['mb_s']
    dn_ns = dotnet_shm_roundtrip[size]['ns_op']
    dn_mb = dotnet_shm_roundtrip[size]['mb_s']
    ratio = go_ns / dn_ns
    print(f"SHM Roundtrip {label:<13} {go_ns:>12.1f} {go_mb:>12.1f} {dn_ns:>14.1f} {dn_mb:>14.1f} {ratio:>11.1f}x")

print()
for size in [64, 256, 1024, 4096, 16384, 65536]:
    label = {64:'64B', 256:'256B', 1024:'1KB', 4096:'4KB', 16384:'16KB', 65536:'64KB'}[size]
    go_ns = go_tcp_stream[size]['ns_op']
    go_mb = go_tcp_stream[size]['mb_s']
    dn_ns = dotnet_tcp_stream[size]['ns_op']
    dn_mb = dotnet_tcp_stream[size]['mb_s']
    ratio = go_ns / dn_ns
    print(f"TCP Stream {label:<16} {go_ns:>12.1f} {go_mb:>12.1f} {dn_ns:>14.1f} {dn_mb:>14.1f} {ratio:>11.1f}x")

print()
for size in [64, 256, 1024, 4096]:
    label = {64:'64B', 256:'256B', 1024:'1KB', 4096:'4KB'}[size]
    go_ns = go_tcp_roundtrip[size]['ns_op']
    go_mb = go_tcp_roundtrip[size]['mb_s']
    dn_ns = dotnet_tcp_roundtrip[size]['ns_op']
    dn_mb = dotnet_tcp_roundtrip[size]['mb_s']
    ratio = go_ns / dn_ns
    print(f"TCP Roundtrip {label:<13} {go_ns:>12.1f} {go_mb:>12.1f} {dn_ns:>14.1f} {dn_mb:>14.1f} {ratio:>11.1f}x")

print(f"\n{'Legend: .NET/Go > 1.0 = .NET faster, < 1.0 = Go faster'}")
print(f"{'Go ring: 64 MB, .NET ring: 4 MB'}")
print(f"{'CPU: AMD EPYC 7763 64-Core Processor'}")
