//go:build linux || windows
// +build linux windows

package main

import (
	"context"
	"flag"
	"fmt"
	"log"
	"time"

	pb "interop-shm/greetpb"

	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/experimental/shm"
)

var (
	segment = flag.String("segment", "interop_greeter", "Shared memory segment name")
	name    = flag.String("name", "Go Client", "Name to send")
)

func main() {
	flag.Parse()

	fmt.Println("Go Greeter Client - Shared Memory")
	fmt.Printf("Segment: %s\n", *segment)

	target := fmt.Sprintf("shm://%s", *segment)
	conn, err := grpc.NewClient(target,
		shm.WithTransport(),
		grpc.WithTransportCredentials(insecure.NewCredentials()),
	)
	if err != nil {
		log.Fatalf("Failed to connect: %v", err)
	}
	defer conn.Close()

	client := pb.NewGreeterClient(conn)

	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()

	fmt.Printf("Calling SayHello(%s)...\n", *name)

	reply, err := client.SayHello(ctx, &pb.HelloRequest{Name: *name})
	if err != nil {
		log.Fatalf("SayHello failed: %v", err)
	}

	fmt.Printf("Response: %s\n", reply.GetMessage())
	fmt.Println("PASSED!")
}
