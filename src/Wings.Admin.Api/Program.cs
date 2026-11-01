using Wings.Admin.Api;

var builder = WebApplication.CreateBuilder(args);
AdminApp.ConfigureServices(builder);

var app = builder.Build();
await AdminApp.ConfigurePipelineAsync(app);
app.Run();

namespace Wings.Admin.Api
{
    /// <summary>Marker type for WebApplicationFactory in integration tests.</summary>
    public sealed class AdminEntryPoint;
}
