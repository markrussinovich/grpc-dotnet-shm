// +build linux windows

/*
 * Binary compatibility test for grpc-dotnet shared memory implementation.
 * This program generates binary dumps of the segment and ring headers
 * that can be verified against the .NET implementation.
 *
 * Usage:
 *   go run binary_dump.go -output <dir>
 */

package main

import (
	"flag"
	"fmt"
	"log"
	"os"
	"path/filepath"
	"unsafe"
)

var outputDir = flag.String("output", ".", "Output directory for binary dumps")

// SegmentHeader matches grpc-go-shmem shm_segment.go SegmentHeader
type SegmentHeader struct {
	magic       [8]byte  // 0x00: "GRPCSHM\0"
	version     uint32   // 0x08: protocol version
	flags       uint32   // 0x0C: reserved flags
	totalSize   uint64   // 0x10: total segment size
	ringAOff    uint64   // 0x18: offset to ring A header
	ringACap    uint64   // 0x20: ring A capacity
	ringBOff    uint64   // 0x28: offset to ring B header
	ringBCap    uint64   // 0x30: ring B capacity
	serverPID   uint32   // 0x38: server process ID
	clientPID   uint32   // 0x3C: client process ID
	serverReady uint32   // 0x40: server ready flag
	clientReady uint32   // 0x44: client ready flag
	closed      uint32   // 0x48: closed flag
	pad         uint32   // 0x4C: padding
	maxStreams  uint32   // 0x50: max concurrent streams
	reserved    [44]byte // 0x54-0x7F: reserved to 128B
}

// RingHeader matches grpc-go-shmem shm_segment.go RingHeader
type RingHeader struct {
	capacity      uint64  // 0x00: power-of-two capacity
	widx          uint64  // 0x08: monotonic write index
	ridx          uint64  // 0x10: monotonic read index
	dataSeq       uint32  // 0x18: data sequence
	spaceSeq      uint32  // 0x1C: space sequence
	closed        uint32  // 0x20: closed flag
	pad           uint32  // 0x24: padding
	contigSeq     uint32  // 0x28: contiguity sequence
	spaceWaiters  uint32  // 0x2C: writers waiting on space
	contigWaiters uint32  // 0x30: writers waiting on contiguity
	dataWaiters   uint32  // 0x34: readers waiting for data
	reserved      [8]byte // 0x38-0x3F: reserved to 64B
}

// FrameHeader matches grpc-go-shmem frame.go
type FrameHeader struct {
	Length    uint32 // 0x00: payload length
	StreamID  uint32 // 0x04: stream identifier
	Type      uint8  // 0x08: frame type
	Flags     uint8  // 0x09: frame flags
	Reserved1 uint16 // 0x0A: reserved
	Reserved2 uint32 // 0x0C: reserved
}

func main() {
	flag.Parse()

	if err := os.MkdirAll(*outputDir, 0755); err != nil {
		log.Fatalf("Failed to create output directory: %v", err)
	}

	// Generate segment header dump
	segHdr := SegmentHeader{
		version:     1,
		flags:       0,
		totalSize:   0x20000,    // 128KB
		ringAOff:    0x80,       // After 128-byte segment header
		ringACap:    0x10000,    // 64KB
		ringBOff:    0x10080,    // After ring A
		ringBCap:    0x10000,    // 64KB
		serverPID:   1234,
		clientPID:   5678,
		serverReady: 1,
		clientReady: 0,
		closed:      0,
		pad:         0,
		maxStreams:  100,
	}
	copy(segHdr.magic[:], "GRPCSHM\x00")

	segBytes := (*[128]byte)(unsafe.Pointer(&segHdr))[:]
	if err := os.WriteFile(filepath.Join(*outputDir, "go_segment_header.bin"), segBytes, 0644); err != nil {
		log.Fatalf("Failed to write segment header: %v", err)
	}
	fmt.Printf("Written segment header (%d bytes) to %s\n", len(segBytes), filepath.Join(*outputDir, "go_segment_header.bin"))

	// Generate ring header dump
	ringHdr := RingHeader{
		capacity:      4096,
		widx:          100,
		ridx:          50,
		dataSeq:       10,
		spaceSeq:      5,
		closed:        0,
		pad:           0,
		contigSeq:     7,
		spaceWaiters:  0,
		contigWaiters: 0,
		dataWaiters:   0,
	}

	ringBytes := (*[64]byte)(unsafe.Pointer(&ringHdr))[:]
	if err := os.WriteFile(filepath.Join(*outputDir, "go_ring_header.bin"), ringBytes, 0644); err != nil {
		log.Fatalf("Failed to write ring header: %v", err)
	}
	fmt.Printf("Written ring header (%d bytes) to %s\n", len(ringBytes), filepath.Join(*outputDir, "go_ring_header.bin"))

	// Generate frame header dump
	frameHdr := FrameHeader{
		Length:   100,
		StreamID: 1,
		Type:     0x01, // HEADERS
		Flags:    0x01, // Initial
	}

	frameBytes := (*[16]byte)(unsafe.Pointer(&frameHdr))[:]
	if err := os.WriteFile(filepath.Join(*outputDir, "go_frame_header.bin"), frameBytes, 0644); err != nil {
		log.Fatalf("Failed to write frame header: %v", err)
	}
	fmt.Printf("Written frame header (%d bytes) to %s\n", len(frameBytes), filepath.Join(*outputDir, "go_frame_header.bin"))

	fmt.Println("\nBinary dumps generated successfully!")
	fmt.Println("To verify .NET compatibility, run:")
	fmt.Println("  dotnet test --filter GoBinaryCompatibility")
}
