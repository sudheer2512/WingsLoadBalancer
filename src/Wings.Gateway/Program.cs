using Wings.Gateway;

var builder = WebApplication.CreateBuilder(args);
GatewayApp.ConfigureServices(builder);

var app = builder.Build();
await GatewayApp.ConfigurePipelineAsync(app);
app.Run();

namespace Wings.Gateway
{
    /// <summary>Marker type for WebApplicationFactory in integration tests.</summary>
    public sealed class GatewayEntryPoint;
}
