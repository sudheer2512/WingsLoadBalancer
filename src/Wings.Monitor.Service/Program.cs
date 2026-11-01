using Wings.LoadBalancer.Infrastructure.Hosting;

// Standalone health monitor / operations service. Install as a Windows Service (see docs/deployment-iis.md) or run
// as a container. Only one instance is active at a time (database lease); additional instances are hot standby.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "Wings Load Balancer Monitor");
builder.Services.AddWingsInfrastructure(builder.Configuration, "monitor");
builder.Services.AddWingsMonitorRuntime();

var host = builder.Build();
if (builder.Configuration.GetValue("Seed:RunOnMonitor", false))
    await DatabaseInitializer.InitializeAsync(host.Services);
host.Run();
