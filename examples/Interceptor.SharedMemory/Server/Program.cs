using Echo;
using Grpc.Net.SharedMemory;
using Server;

var service = new EchoService();

await using var server = new ShmGrpcServer("interceptor_shm_example");

server.MapUnary<EchoRequest, EchoResponse>(
    "/echo.Echo/UnaryEcho", service.UnaryEcho);
server.MapDuplexStreaming<EchoRequest, EchoResponse>(
    "/echo.Echo/BidirectionalStreamingEcho", service.BidirectionalStreamingEcho);

await server.RunAsync();
