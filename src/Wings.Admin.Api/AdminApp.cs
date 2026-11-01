using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Scalar.AspNetCore;
using Wings.Admin.Api.Controllers;
using Wings.Admin.Api.Hubs;
using Wings.Admin.Api.Security;
using Wings.Admin.Api.Services;
using Wings.LoadBalancer.Infrastructure.Hosting;

namespace Wings.Admin.Api;

/// <summary>Composition root for the administrative API, SignalR hub and Angular UI host.</summary>
public static class AdminApp
{
    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var configuration = builder.Configuration;

        services.AddWingsInfrastructure(configuration, "admin");
        if (configuration.GetValue("Monitor:RunInAdmin", true)) services.AddWingsMonitorRuntime();
        services.AddAdminSecurity(configuration);

        services.AddScoped<AssignmentService>();
        services.AddScoped<SimulationService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<ReportService>();
        services.AddScoped<BulkImportService>();
        services.AddHttpClient();
        services.AddHostedService<MonitoringBroadcaster>();

        services.AddControllers(o => o.Filters.Add<ApiExceptionFilter>())
            .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.Configure<ApiBehaviorOptions>(o => o.InvalidModelStateResponseFactory = ctx => new BadRequestObjectResult(new
        {
            error = "validation_failed",
            errors = ctx.ModelState.Where(e => e.Value?.Errors.Count > 0)
                .SelectMany(e => e.Value!.Errors.Select(x => string.IsNullOrEmpty(e.Key) ? x.ErrorMessage : $"{e.Key}: {x.ErrorMessage}"))
        }));

        var signalR = services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        var redis = configuration["Redis:Configuration"];
        if (!string.IsNullOrWhiteSpace(redis)) signalR.AddStackExchangeRedis(redis, o => o.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("wings-admin"));

        services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
        {
            doc.Info.Title = "Wings Intelligent Load Balancer — Administration API";
            doc.Info.Version = "v1";
            doc.Info.Description = "Server registry, tenant assignments, pools, policies, routing simulation, monitoring, audit and reports. "
                                   + "All endpoints require an administrator session cookie and the X-XSRF-TOKEN header on unsafe methods.";
            return Task.CompletedTask;
        }));

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(AuthController.RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = configuration.GetValue("Admin:LoginPermitsPerMinute", 10), Window = TimeSpan.FromMinutes(1) }));
        });
    }

    public static async Task ConfigurePipelineAsync(WebApplication app)
    {
        if (app.Configuration.GetValue("Seed:RunOnAdmin", true))
            await DatabaseInitializer.InitializeAsync(app.Services);

        var secure = app.Configuration.GetValue("Admin:RequireHttpsCookies", true);
        if (!app.Environment.IsDevelopment()) app.UseHsts();
        app.UseAdminSecurityHeaders();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAdminCsrfProtection(secure);

        app.MapOpenApi().AllowAnonymous();
        app.MapScalarApiReference("/api/docs", o => o.WithTitle("Wings LB Admin API")).AllowAnonymous();
        app.MapControllers();
        app.MapHub<MonitoringHub>("/hubs/monitoring");
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous().ExcludeFromDescription();
        app.MapFallbackToFile("index.html").AllowAnonymous();
    }
}
