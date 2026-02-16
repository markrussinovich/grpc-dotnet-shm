# gRPC .NET Shared Memory Benchmark

This directory contains benchmarks comparing gRPC .NET performance over TCP vs shared memory transport.

## Current Status

- **TCP Transport**: Fully functional
- **SHM Transport**: Active and benchmarked alongside TCP using shared-memory control/data segments

## Quick Start

```bash
# Build benchmark runner
dotnet build benchmark-shm/ringbench/RingBench.csproj -c Release

# Run measured SHM+TCP benchmark and generate artifacts
python benchmark-shm/benchmark_runner.py --run

# Re-generate plots from cached measured results only
python benchmark-shm/benchmark_runner.py --plot-only
```

## Benchmark Types

### Workloads

- **Unary**: Single request/response pattern (typical RPC)
- **Streaming**: Bidirectional streaming with ping-pong pattern

### Transports

- **TCP**: Standard HTTP/2 over TCP loopback
- **SHM**: Shared memory transport using Unix domain socket for control

## Command Line Options

| Option | Description | Default |
|--------|-------------|---------|
| `--transport` | tcp, shm, or both | both |
| `--workload` | unary, streaming, or both | both |
| `--warmup` | Warmup duration in seconds | 3 |
| `--duration` | Test duration per benchmark | 10 |
| `--sizes` | Message sizes (comma-separated) | 64,1024,4096,16384,65536 |
| `--concurrency` | Concurrency levels (comma-separated) | 1,8 |
| `--output` | Results output directory | results |
| `--no-plots` | Skip plot generation | false |
| `--quick` | Quick run preset | - |
| `--full` | Full run preset | - |

## Output

### Results Directory (`results/`)

- `benchmark_results_YYYYMMDD_HHMMSS.json` - Raw benchmark data
- `benchmark_results.json` - Symlink to latest results

### Ringbench Output (`out/<platform>/`)

- `results.json` - Measured benchmark results from `RingBench` (source of truth)
- `results.csv` - Tabular export of measured results
- `benchmark_patterns.png` - Measured unary/streaming comparison panels
- `benchmark_summary.png` - Measured summary dashboard
- `benchmark_large_payloads.png` - Measured large-payload panels
- `benchmark_consolidated.png` - Combined measured dashboard

### Plots Directory (`plots/`)

- `throughput_unary.png` - Throughput comparison for unary RPCs
- `throughput_streaming.png` - Throughput comparison for streaming
- `latency_unary.png` - Latency comparison for unary RPCs
- `latency_streaming.png` - Latency comparison for streaming
- `speedup.png` - SHM speedup over TCP
- `ops_per_second.png` - Operations per second breakdown
- `latency_distribution.png` - Latency distribution visualization
- `summary.md` - Markdown summary table

## Metrics Collected

- **Operations/Second** - Number of complete RPCs per second
- **Throughput (MB/s)** - Data transferred per second
- **Latency Percentiles** - P50, P90, P99, min, max, avg in microseconds

## Requirements

- .NET SDK compatible with repo `global.json` (currently .NET 10.x)
- Python 3 with matplotlib and numpy (for plot generation)

```bash
# Install Python dependencies
pip install matplotlib numpy
```

## Directory Structure

```
benchmark-shm/
├── run_benchmarks.sh      # Main runner script
├── plot_results.py        # Plot generator
├── README.md              # This file
├── proto/
│   └── benchmark.proto    # Service definition
├── server/                # Benchmark server
│   ├── BenchmarkServer.csproj
│   └── Program.cs
├── client/                # Benchmark client
│   ├── BenchmarkClient.csproj
│   └── Program.cs
├── results/               # Benchmark results (JSON)
└── plots/                 # Generated plots (PNG)
```

## Interpreting Results

### Speedup

A speedup value indicates how much faster shared memory is compared to TCP:
- **2x speedup** = SHM is twice as fast
- **1x** = Same performance (baseline)
- **< 1x** = TCP is faster (unexpected)

### Expected Results

Shared memory transport typically shows:
- **Lower latency**: 2-10x improvement for small messages
- **Higher throughput**: 1.5-3x improvement depending on message size
- **Reduced CPU overhead**: Less system call overhead

Note: Reported charts and summaries in `benchmark_runner.py` are measured-only.
No synthetic/estimated bidirectional panels are included in default outputs.

The improvement is most pronounced for:
- Small messages (< 4KB)
- High concurrency scenarios
- Latency-sensitive workloads

## Troubleshooting

### Server fails to start

```bash
# Check if port is in use
lsof -i :50051
# Kill existing process
pkill -f BenchmarkServer
# Remove stale socket
rm -f /tmp/grpc_benchmark.sock
```

### SHM transport fails

```bash
# Check shared memory files
ls -la /dev/shm/ | grep grpc
# Check Unix socket
ls -la /tmp/grpc_benchmark.sock
```

### Plot generation fails

```bash
# Install dependencies
pip install matplotlib numpy

# Run manually
python3 plot_results.py results/benchmark_results.json plots/
```
