#!/bin/bash
# stress test Plan C — 30 unary stress runs on WSL Linux
export PATH=/usr/bin:/usr/local/bin:/home/qimingsun/.dotnet:$PATH
cd /home/qimingsun/grpc-dotnet-shm

# Sync from Windows tree
cp /mnt/c/src/grpc-dotnet-shm/src/Grpc.Net.SharedMemory/ShmControlHandler.cs src/Grpc.Net.SharedMemory/ShmControlHandler.cs
cp /mnt/c/src/grpc-dotnet-shm/src/Grpc.Net.SharedMemory/ShmGrpcStream.cs src/Grpc.Net.SharedMemory/ShmGrpcStream.cs
dotnet build -c Release benchmark-shm/ringbench/RingBench.csproj 2>&1 | tail -3

pass=0
fail=0
for i in $(seq 1 30); do
    ls /dev/shm 2>/dev/null | grep -E '^bench_shm_' | xargs -r -I{} rm -f /dev/shm/{} 2>/dev/null
    out=$(dotnet run --no-build -c Release --project benchmark-shm/ringbench -- --output /tmp/wsl_loop --only shm --sizes 1024,4096,16384 2>&1 | tail -3)
    if echo "$out" | grep -q 'Plot generation skipped'; then
        pass=$((pass+1))
    else
        fail=$((fail+1))
        echo "=== FAIL run${i} ==="
        echo "$out"
    fi
done
echo "PASS=${pass} FAIL=${fail}"
