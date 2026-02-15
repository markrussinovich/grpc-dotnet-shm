using Grpc.Net.SharedMemory;
using RouteGuide;
using Server.Services;

var service = new RouteGuideService();

await using var server = new ShmGrpcServer("routeguide_shm_example");

server.MapUnary<Point, Feature>(
    "/routeguide.RouteGuide/GetFeature", service.GetFeature);
server.MapServerStreaming<Rectangle, Feature>(
    "/routeguide.RouteGuide/ListFeatures", service.ListFeatures);
server.MapClientStreaming<Point, RouteSummary>(
    "/routeguide.RouteGuide/RecordRoute", service.RecordRoute);
server.MapDuplexStreaming<RouteNote, RouteNote>(
    "/routeguide.RouteGuide/RouteChat", service.RouteChat);

await server.RunAsync();
