using Wings.MockBackend;

// Run several instances to simulate application servers, e.g.
//   dotnet run -- --urls http://localhost:5101 --MockBackend:ServerName APP-SERVER-01 --MockBackend:ServerId <guid>
var app = MockBackendApp.Build(args);
app.Run();

namespace Wings.MockBackend
{
    public sealed class MockBackendEntryPoint;
}
