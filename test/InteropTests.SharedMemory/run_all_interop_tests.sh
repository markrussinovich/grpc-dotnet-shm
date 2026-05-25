#!/bin/bash
# Run all shared memory interop tests

set -e
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "============================================"
echo "Shared Memory Interop Test Suite"
echo "============================================"
echo "Tests cross-language gRPC communication between"
echo ".NET (Grpc.Net.SharedMemory) and Go (grpc-go-shmem)"
echo ""

PASS_COUNT=0
FAIL_COUNT=0

# Clean up any leftover shared memory segments and fd-pass sockets.
# The .NET / Go transports create files at /dev/shm/grpc_shm_<segName>
# (data segment + .lock + .fds.sock), so the cleanup pattern must
# match the full prefix rather than the bare segment name.
rm -f /dev/shm/grpc_shm_interop_test_* 2>/dev/null || true

# Test 1: Go Server + .NET Client
echo ">>> Test 1: Go Server + .NET Client"
echo ""
if "$SCRIPT_DIR/test_go_server_dotnet_client.sh" interop_test_1; then
    PASS_COUNT=$((PASS_COUNT + 1))
else
    FAIL_COUNT=$((FAIL_COUNT + 1))
fi

sleep 1

# Test 2: .NET Server + Go Client
echo ""
echo ">>> Test 2: .NET Server + Go Client"
echo ""
if "$SCRIPT_DIR/test_dotnet_server_go_client.sh" interop_test_2; then
    PASS_COUNT=$((PASS_COUNT + 1))
else
    FAIL_COUNT=$((FAIL_COUNT + 1))
fi

echo ""
echo "============================================"
echo "SUMMARY: Passed: $PASS_COUNT / Failed: $FAIL_COUNT"
echo "============================================"

# Final cleanup (match .NET / Go file naming: grpc_shm_<seg>* covers
# data segment, .lock, and .fds.sock).
rm -f /dev/shm/grpc_shm_interop_test_* 2>/dev/null || true

[ $FAIL_COUNT -eq 0 ] && exit 0 || exit 1
