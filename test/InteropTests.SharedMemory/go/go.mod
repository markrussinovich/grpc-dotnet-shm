module interop-shm

go 1.25.0

require (
	google.golang.org/grpc v1.60.0
	google.golang.org/protobuf v1.36.11
)

require (
	golang.org/x/net v0.55.0 // indirect
	golang.org/x/sys v0.45.0 // indirect
	golang.org/x/text v0.37.0 // indirect
	google.golang.org/genproto/googleapis/rpc v0.0.0-20260414002931-afd174a4e478 // indirect
)

// Local-clone dev override: the grpc-go-shmem fork is expected to be
// checked out alongside grpc-dotnet-shm (sibling repos). Adjust this
// path or use `go work` to override locally if the fork lives elsewhere.
// The interop suite is dev-only; production consumers do not pick up
// this go.mod.
replace google.golang.org/grpc => ../../../../grpc-go-shmem
