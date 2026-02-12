#!/bin/bash

# gRPC .NET Shared Memory Benchmark Runner
# This script builds and runs the benchmark suite, then generates plots

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# Default configuration
TRANSPORT="both"
WORKLOAD="both"
WARMUP=3
DURATION=10
SIZES="64,1024,4096,16384,65536"
CONCURRENCY="1,8"
OUTPUT_DIR="results"
PLOTS_DIR="plots"
GENERATE_PLOTS=true
SERVER_PID=""

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

print_header() {
    echo -e "${BLUE}========================================${NC}"
    echo -e "${BLUE}  gRPC .NET Shared Memory Benchmark${NC}"
    echo -e "${BLUE}========================================${NC}"
}

print_usage() {
    cat << EOF
Usage: $0 [options]

Options:
  --transport <tcp|shm|both>     Transport to benchmark (default: both)
  --workload <unary|streaming|both>  Workload type (default: both)
  --warmup <seconds>             Warmup duration (default: 3)
  --duration <seconds>           Test duration per benchmark (default: 10)
  --sizes <n1,n2,...>            Message sizes in bytes (default: 64,1024,4096,16384,65536)
  --concurrency <n1,n2,...>      Concurrency levels (default: 1,8)
  --output <dir>                 Output directory (default: results)
  --no-plots                     Skip plot generation
  --quick                        Quick run (2s warmup, 5s duration, fewer sizes)
  --full                         Full run (5s warmup, 30s duration, all sizes)
  -h, --help                     Show this help

Examples:
  $0                             # Run default benchmarks
  $0 --quick                     # Quick benchmark run
  $0 --transport shm --workload unary  # SHM unary only
  $0 --full --sizes 64,1024,4096,16384,65536,262144,1048576
EOF
}

cleanup() {
    echo -e "\n${YELLOW}Cleaning up...${NC}"
    if [ -n "$SERVER_PID" ] && kill -0 "$SERVER_PID" 2>/dev/null; then
        kill "$SERVER_PID" 2>/dev/null || true
        wait "$SERVER_PID" 2>/dev/null || true
    fi
    # Clean up shared memory files
    rm -f /dev/shm/grpc_bench* 2>/dev/null || true
}

trap cleanup EXIT

# Parse command line arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --transport)
            TRANSPORT="$2"
            shift 2
            ;;
        --workload)
            WORKLOAD="$2"
            shift 2
            ;;
        --warmup)
            WARMUP="$2"
            shift 2
            ;;
        --duration)
            DURATION="$2"
            shift 2
            ;;
        --sizes)
            SIZES="$2"
            shift 2
            ;;
        --concurrency)
            CONCURRENCY="$2"
            shift 2
            ;;
        --output)
            OUTPUT_DIR="$2"
            shift 2
            ;;
        --no-plots)
            GENERATE_PLOTS=false
            shift
            ;;
        --quick)
            WARMUP=2
            DURATION=5
            SIZES="64,1024,4096"
            CONCURRENCY="1"
            shift
            ;;
        --full)
            WARMUP=5
            DURATION=30
            SIZES="1,64,1024,4096,16384,65536,262144,1048576"
            CONCURRENCY="1,8,64"
            shift
            ;;
        -h|--help)
            print_usage
            exit 0
            ;;
        *)
            echo "Unknown option: $1"
            print_usage
            exit 1
            ;;
    esac
done

print_header

echo -e "${GREEN}Configuration:${NC}"
echo "  Transport: $TRANSPORT"
echo "  Workload: $WORKLOAD"
echo "  Warmup: ${WARMUP}s"
echo "  Duration: ${DURATION}s"
echo "  Message sizes: $SIZES"
echo "  Concurrency: $CONCURRENCY"
echo "  Output: $OUTPUT_DIR"
echo ""

# Create output directories
mkdir -p "$OUTPUT_DIR" "$PLOTS_DIR"

# Build projects
echo -e "${YELLOW}Building projects...${NC}"
dotnet build server/BenchmarkServer.csproj -c Release --nologo -v q
dotnet build client/BenchmarkClient.csproj -c Release --nologo -v q
echo -e "${GREEN}Build complete${NC}"
echo ""

# Clean up any existing server
pkill -f "BenchmarkServer" 2>/dev/null || true
rm -f /dev/shm/grpc_bench* 2>/dev/null || true
sleep 1

# Start server
echo -e "${YELLOW}Starting benchmark server...${NC}"
dotnet run --project server/BenchmarkServer.csproj -c Release --no-build -- \
    --port 50051 \
    --shm-segment grpc_bench &
SERVER_PID=$!

# Wait for server to start
echo "Waiting for server to start..."
for i in {1..30}; do
    if [ -f /dev/shm/grpc_bench ] || nc -z localhost 50051 2>/dev/null; then
        echo -e "${GREEN}Server started (PID: $SERVER_PID)${NC}"
        break
    fi
    if ! kill -0 "$SERVER_PID" 2>/dev/null; then
        echo -e "${RED}Server failed to start${NC}"
        exit 1
    fi
    sleep 0.5
done

# Give server a moment to fully initialize
sleep 2

# Run benchmarks
echo ""
echo -e "${YELLOW}Running benchmarks...${NC}"
TIMESTAMP=$(date +%Y%m%d_%H%M%S)
RESULTS_FILE="$OUTPUT_DIR/benchmark_results_$TIMESTAMP.json"

dotnet run --project client/BenchmarkClient.csproj -c Release --no-build -- \
    --transport "$TRANSPORT" \
    --workload "$WORKLOAD" \
    --warmup "$WARMUP" \
    --duration "$DURATION" \
    --sizes "$SIZES" \
    --concurrency "$CONCURRENCY" \
    --output "$RESULTS_FILE" \
    --tcp-address "http://localhost:50051" \
    --shm-segment "grpc_bench"

# Also create a symlink to latest results
ln -sf "benchmark_results_$TIMESTAMP.json" "$OUTPUT_DIR/benchmark_results.json"

echo ""
echo -e "${GREEN}Benchmark complete!${NC}"
echo "Results saved to: $RESULTS_FILE"

# Generate plots
if [ "$GENERATE_PLOTS" = true ]; then
    echo ""
    echo -e "${YELLOW}Generating plots...${NC}"
    
    # Check if matplotlib is available
    if python3 -c "import matplotlib" 2>/dev/null; then
        python3 benchmark_runner.py --plot-only
        echo -e "${GREEN}Plots saved to: out/${NC}"
    else
        echo -e "${YELLOW}matplotlib not found. Installing...${NC}"
        pip3 install matplotlib numpy --quiet 2>/dev/null || pip install matplotlib numpy --quiet 2>/dev/null || {
            echo -e "${RED}Failed to install matplotlib. Skipping plot generation.${NC}"
            echo "Run manually: pip install matplotlib numpy && python3 benchmark_runner.py --plot-only"
        }
        
        if python3 -c "import matplotlib" 2>/dev/null; then
            python3 benchmark_runner.py --plot-only
            echo -e "${GREEN}Plots saved to: out/${NC}"
        fi
    fi
fi

# Stop server
echo ""
echo -e "${YELLOW}Stopping server...${NC}"
kill "$SERVER_PID" 2>/dev/null || true
SERVER_PID=""

echo ""
echo -e "${GREEN}========================================${NC}"
echo -e "${GREEN}  Benchmark Complete!${NC}"
echo -e "${GREEN}========================================${NC}"
echo ""
echo "Results: $RESULTS_FILE"
echo "Plots: $PLOTS_DIR/"
echo ""
echo "To regenerate plots:"
echo "  python3 benchmark_runner.py --plot-only"
