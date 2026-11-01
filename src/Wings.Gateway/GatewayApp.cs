using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Wings.Gateway.Endpoints;
using Wings.Gateway.Proxy;
using Wings.Gateway.Sessions;
using Wings.LoadBalancer.Infrastructure.Hosting;

namespace Wings.Gateway;

/// <summary>Composition root for the routing gateway (also used by integration tests).</summary>
public static class GatewayApp
{
    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var configuration = builder.Configuration;
        services.Configure<GatewayOptions>(configuration.GetSection(GatewayOptions.Section));
        services.Configure<OidcOptions>(configuration.GetSection(OidcOptions.Section));
        var gateway = configuration.GetSection(GatewayOptions.Section).Get<GatewayOptions>() ?? new GatewayOptions();

        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = gateway.MaxRequestBodyBytes;
        });
        services.Configure<IISServerOptions>(o => o.MaxRequestBodySize = gateway.MaxRequestBodyBytes);
        services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(configuration.GetValue("Gateway:ShutdownTimeoutSeconds", 30)));

        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = 2;
            // Only configured NLB/proxy addresses may set X-Forwarded-*. (Empty lists would mean "trust everyone",
            // so the middleware is not added at all when nothing is configured.)
            o.KnownProxies.Clear();
            o.KnownIPNetworks.Clear();
#pragma warning disable ASPDEPR005 // legacy list is still honoured; clear it too
            o.KnownNetworks.Clear();
#pragma warning restore ASPDEPR005
            foreach (var proxy in gateway.KnownProxies) o.KnownProxies.Add(IPAddress.Parse(proxy));
            foreach (var network in gateway.KnownNetworks)
            {
                var parts = network.Split('/');
                o.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse(parts[0]), int.Parse(parts[1])));
            }
        });

        services.AddWingsInfrastructure(configuration, "gateway");
        services.AddWingsGatewayRuntime();
        services.AddHttpForwarder();
        services.AddSingleton<GatewayMetrics>();
        services.AddSingleton<GatewaySessionService>();
        services.AddSingleton<HandoffService>();
        services.AddSingleton<GatewayLifetime>();
        services.AddHostedService<GatewayHeartbeatService>();

        var auth = services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, o =>
            {
                o.Cookie.Name = gateway.CookieName;
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.Path = "/";
                o.Cookie.SecurePolicy = gateway.RequireHttpsCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromMinutes(gateway.SessionIdleMinutes);
                o.SlidingExpiration = true;
                o.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
            })
            .AddCookie(AuthEndpoints.ExternalScheme, o =>
            {
                o.Cookie.Name = "Wings.External";
                o.Cookie.SecurePolicy = gateway.RequireHttpsCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromMinutes(5);
            });
        var oidc = configuration.GetSection(OidcOptions.Section).Get<OidcOptions>() ?? new OidcOptions();
        if (oidc.Enabled)
        {
            auth.AddOpenIdConnect("oidc", o =>
            {
                o.SignInScheme = AuthEndpoints.ExternalScheme;
                o.Authority = oidc.Authority;
                o.ClientId = oidc.ClientId;
                o.ClientSecret = oidc.ClientSecret;
                o.ResponseType = "code";
                o.UsePkce = true;
                o.CallbackPath = "/_wings/auth/oidc/callback";
                o.SaveTokens = false; // tokens are never stored in sessions or caches
                o.MapInboundClaims = false;
            });
        }

        services.AddAntiforgery(o =>
        {
            o.HeaderName = "X-CSRF-TOKEN";
            o.Cookie.Name = ".AspNetCore.Antiforgery.Wings";
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = gateway.RequireHttpsCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        });

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(AuthEndpoints.LoginRateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = gateway.LoginPermitsPerMinutePerIp, Window = TimeSpan.FromMinutes(1) }));
            if (gateway.ProxyPermitsPerMinutePerIp > 0)
            {
                o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => RateLimitPartition.GetTokenBucketLimiter(
                    ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = Math.Max(100, gateway.ProxyPermitsPerMinutePerIp / 6),
                        TokensPerPeriod = Math.Max(1, gateway.ProxyPermitsPerMinutePerIp / 60),
                        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                        QueueLimit = 0
                    }));
            }
        });

        var otel = services.AddOpenTelemetry()
            .WithMetrics(m => m.AddMeter(GatewayMetrics.MeterName).AddAspNetCoreInstrumentation().AddHttpClientInstrumentation())
            .WithTracing(t => t.AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/_wings/health"))
                .AddHttpClientInstrumentation());
        if (!string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])) otel.UseOtlpExporter();
    }

    public static async Task ConfigurePipelineAsync(WebApplication app)
    {
        if (app.Configuration.GetValue("Seed:RunOnGateway", false))
            await DatabaseInitializer.InitializeAsync(app.Services);

        var gateway = app.Services.GetRequiredService<IOptions<GatewayOptions>>().Value;
        if (gateway.KnownProxies.Count > 0 || gateway.KnownNetworks.Count > 0) app.UseForwardedHeaders();
        if (!app.Environment.IsDevelopment()) app.UseHsts();
        app.UseRateLimiter();

        var webRoot = Path.Combine(app.Environment.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot"), "_wings");
        if (Directory.Exists(webRoot))
            app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(webRoot), RequestPath = "/_wings" });

        app.MapWingsHealth();
        app.MapWingsAuth();

        // Everything outside /_wings is application traffic for the routing proxy.
        app.MapWhen(ctx => !ctx.Request.Path.StartsWithSegments("/_wings"),
            branch => branch.UseMiddleware<RoutingProxyMiddleware>());
    }
}
