//go:build linux || windows
// +build linux windows

package main

import (
	"context"
	"flag"
	"fmt"
	"log"
	"os"
	"os/signal"
	"syscall"

	pb "interop-shm/greetpb"

	"google.golang.org/grpc"
	"google.golang.org/grpc/experimental/shm"
)

var (
	segment = flag.String("segment", "interop_greeter", "Shared memory segment name")
)

type server struct {
	pb.UnimplementedGreeterServer
}

func (s *server) SayHello(ctx context.Context, req *pb.HelloRequest) (*pb.HelloReply, error) {
	log.Printf("Received request: name=%q", req.GetName())
	return &pb.HelloReply{
		Message: fmt.Sprintf("Hello %s from Go server!", req.GetName()),
	}, nil
}

func main() {
	flag.Parse()

	fmt.Println("Go Greeter Server - Shared Memory")
	fmt.Printf("Segment: %s\n", *segment)

	lis, err := shm.NewListener(*segment, nil)
	if err != nil {
		log.Fatalf("Failed to listen: %v", err)
	}
	defer lis.Close()

	s := grpc.NewServer()
	pb.RegisterGreeterServer(s, &server{})

	go func() {
		sigCh := make(chan os.Signal, 1)
		signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)
		<-sigCh
		fmt.Println("\nShutting down...")
		s.GracefulStop()
	}()

	fmt.Println("Server ready. Ctrl+C to stop.")
	if err := s.Serve(lis); err != nil {
		log.Fatalf("Failed to serve: %v", err)
	}
}
