using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wings.Gateway.Proxy;
using Wings.Gateway.Sessions;
using Wings.LoadBalancer.Data;
using Wings.LoadBalancer.Data.Entities;
using Wings.LoadBalancer.Infrastructure.Authentication;
using Wings.LoadBalancer.Infrastructure.Configuration;
using Wings.LoadBalancer.Infrastructure.Monitoring;
using Wings.LoadBalancer.Infrastructure.Sessions;
using Wings.Routing;

namespace Wings.Gateway.Endpoints;

public sealed record DiscoverRequest(string? Company);
public sealed record LoginRequest(string? Company, string? Username, string? Password, string? MfaCode, string? ReturnUrl);

public static class AuthEndpoints
{
    public const string LoginRateLimitPolicy = "login";
    public const string ExternalScheme = "Wings.External";
    private const string InvalidMessage = "The company, username or password is incorrect.";

    public static void MapWingsAuth(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/_wings").ExcludeFromDescription();

        group.MapGet("/login", (IWebHostEnvironment env) =>
            Results.File(Path.Combine(env.WebRootPath, "_wings", "login.html"), "text/html; charset=utf-8"));

        group.MapGet("/logout", (IWebHostEnvironment env) =>
            Results.File(Path.Combine(env.WebRootPath, "_wings", "logout.html"), "text/html; charset=utf-8"));

        group.MapGet("/auth/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { token = tokens.RequestToken });
        });

        // Pre-authentication discovery. Unknown and local-password companies get the same answer, so the response
        // does not reveal which companies exist. Only companies that federate to OIDC are redirected.
        group.MapPost("/auth/discover", async (DiscoverRequest body, HttpContext context, IAntiforgery antiforgery,
            WingsDbContext db, IOptions<OidcOptions> oidc) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context)) return Results.BadRequest(new { error = "invalid_request" });
            var normalized = IdentifierNormalizer.Normalize(body.Company);
            if (oidc.Value.Enabled && normalized.Length > 0)
            {
                var federated = await db.Companies.AsNoTracking().AnyAsync(c =>
                    c.AuthenticationMode == AuthenticationMode.Oidc && c.Status == Wings.Routing.Model.CompanyStatus.Active &&
                    (c.NormalizedCode == normalized || c.Aliases.Any(a => a.NormalizedAlias == normalized)));
                if (federated)
                    return Results.Ok(new { method = "oidc", url = $"/_wings/auth/oidc?company={Uri.EscapeDataString(body.Company!)}" });
            }
            return Results.Ok(new { method = "password" });
        }).RequireRateLimiting(LoginRateLimitPolicy);

        group.MapPost("/auth/login", async (LoginRequest body, HttpContext context, IAntiforgery antiforgery,
            IUserAuthenticator authenticator, GatewaySessionService sessions, WingsDbContext db,
            ISystemSettings settings, TimeProvider time) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context)) return Results.BadRequest(new { error = "invalid_request" });
            if (string.IsNullOrWhiteSpace(body.Company) || string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrEmpty(body.Password)
                || body.Company.Length > 200 || body.Username.Length > 200 || body.Password.Length > 1024)
                return Results.Json(new { error = "invalid_credentials", message = InvalidMessage }, statusCode: 401);

            AuthenticationResult result;
            try
            {
                result = await authenticator.AuthenticateAsync(new CredentialRequest(body.Company, body.Username, body.Password,
                    body.MfaCode, RoutingProxyMiddleware.MaskAddress(context.Connection.RemoteIpAddress)), context.RequestAborted);
            }
            catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException or DbUpdateException)
            {
                // Identity store unreachable: existing sessions keep working; new sign-ins fail gracefully.
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Wings.Login")
                    .LogError(ex, "Sign-in unavailable: identity store error");
                return Results.Json(new { error = "unavailable", message = "Sign-in is temporarily unavailable. Please try again shortly." }, statusCode: 503);
            }

            switch (result.Status)
            {
                case AuthenticationStatus.MfaRequired:
                    return Results.Ok(new { mfaRequired = true });
                case AuthenticationStatus.Unavailable:
                    return Results.Json(new { error = "unavailable", message = "Sign-in is temporarily unavailable. Please try again shortly." }, statusCode: 503);
                case AuthenticationStatus.InvalidCredentials:
                    return Results.Json(new { error = "invalid_credentials", message = InvalidMessage }, statusCode: 401);
            }

            if (!await WithinConcurrentUserLimitAsync(db, settings, time, result.CompanyId, context.RequestAborted))
                return Results.Json(new { error = "capacity", message = "Your organisation has reached its concurrent user limit." }, statusCode: 429);

            await sessions.SignInAsync(context, result);
            return Results.Ok(new { redirect = GatewayResponses.SafeReturnUrl(body.ReturnUrl) });
        }).RequireRateLimiting(LoginRateLimitPolicy);

        group.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery, GatewaySessionService sessions) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context)) return Results.BadRequest(new { error = "invalid_request" });
            await sessions.SignOutAsync(context, "Logout");
            return Results.Ok(new { redirect = GatewayResponses.LoginPath });
        });

        group.MapGet("/session", async (HttpContext context, GatewaySessionService sessions) =>
        {
            var (resolution, record) = await sessions.ResolveAsync(context);
            context.Response.Headers.CacheControl = "no-store";
            return resolution == SessionResolution.Valid && record is not null
                ? Results.Ok(new { authenticated = true, company = record.CompanyCode, username = record.Username, expiresAt = record.AbsoluteExpiresAt })
                : Results.Ok(new { authenticated = false });
        });

        group.MapGet("/auth/oidc", (string company, IOptions<OidcOptions> oidc) =>
        {
            if (!oidc.Value.Enabled) return Results.NotFound();
            var props = new AuthenticationProperties { RedirectUri = "/_wings/auth/oidc/complete" };
            props.Items["wings:company"] = company;
            return Results.Challenge(props, ["oidc"]);
        });

        group.MapGet("/auth/oidc/complete", async (HttpContext context, IUserAuthenticator authenticator,
            GatewaySessionService sessions, IOptions<OidcOptions> oidc) =>
        {
            var external = await context.AuthenticateAsync(ExternalScheme);
            await context.SignOutAsync(ExternalScheme);
            if (!external.Succeeded || external.Properties?.Items.TryGetValue("wings:company", out var company) != true || company is null)
                return Results.Redirect(GatewayResponses.LoginPath + "?error=1");
            var subject = external.Principal!.FindFirstValue(oidc.Value.SubjectClaim) ?? external.Principal!.FindFirstValue(ClaimTypes.NameIdentifier);
            if (subject is null) return Results.Redirect(GatewayResponses.LoginPath + "?error=1");
            var result = await authenticator.ResolveExternalAsync(company, subject, external.Principal!.Identity?.Name, context.RequestAborted);
            if (result.Status != AuthenticationStatus.Success) return Results.Redirect(GatewayResponses.LoginPath + "?error=1");
            await sessions.SignInAsync(context, result);
            return Results.Redirect("/");
        });

        group.MapPost("/handoff/redeem", async (HttpContext context, HandoffService handoff) =>
        {
            var token = context.Request.Form["token"].ToString();
            if (!Guid.TryParse(context.Request.Headers["X-Wings-Server-Id"], out var serverId) || token.Length == 0)
                return Results.BadRequest();
            var ticket = await handoff.RedeemAsync(token, serverId, context.Request.Headers["X-Wings-Signature"].ToString());
            return ticket is null ? Results.NotFound() : Results.Ok(ticket);
        }).DisableAntiforgery();
    }

    private static async Task<bool> WithinConcurrentUserLimitAsync(WingsDbContext db, ISystemSettings settings,
        TimeProvider time, Guid companyId, CancellationToken ct)
    {
        try
        {
            var limit = await db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.MaxConcurrentUsers).FirstOrDefaultAsync(ct);
            if (limit is not > 0) return true;
            var since = time.GetUtcNow().AddMinutes(-await settings.GetNumberAsync(SettingKeys.SessionIdleMinutes, ct));
            var active = await db.ActiveSessions.AsNoTracking()
                .Where(s => s.CompanyId == companyId && s.EndedAt == null && s.RevokedAt == null && s.LastSeenAt >= since)
                .Select(s => s.UserId).Distinct().CountAsync(ct);
            return active < limit;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Limit enforcement is not worth failing a valid login over.
            return true;
        }
    }
}

public static class HealthEndpoints
{
    public static void MapWingsHealth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/_wings/health/live", () => Results.Ok(new { status = "live" })).ExcludeFromDescription();
        app.MapGet("/_wings/health/ready", (IRoutingSnapshotProvider snapshots, GatewayLifetime lifetime,
            Wings.Routing.Engine.RoutingEngineOptions engine, TimeProvider time) =>
        {
            if (lifetime.Stopping) return Results.Json(new { status = "draining" }, statusCode: 503);
            if (snapshots.Current is not { } snapshot) return Results.Json(new { status = "starting" }, statusCode: 503);
            var stale = engine.LastKnownGoodMaxAge is { } max && time.GetUtcNow() - snapshot.LastValidatedAt > max;
            return stale
                ? Results.Json(new { status = "stale-configuration" }, statusCode: 503)
                : Results.Ok(new { status = "ready", configVersion = snapshot.Version, database = snapshots.DatabaseAvailable });
        }).ExcludeFromDescription();
    }
}

/// <summary>Tracks graceful shutdown so readiness fails first and the load balancer stops sending new requests.</summary>
public sealed class GatewayLifetime
{
    public GatewayLifetime(IHostApplicationLifetime lifetime) => lifetime.ApplicationStopping.Register(() => Stopping = true);
    public bool Stopping { get; private set; }
}

/// <summary>Writes this instance's heartbeat and rolling statistics for the dashboard and gateway alerts.</summary>
public sealed class GatewayHeartbeatService(
    IServiceScopeFactory scopes,
    IRoutingSnapshotProvider snapshots,
    Wings.LoadBalancer.Infrastructure.Caching.IStateStore store,
    Wings.LoadBalancer.Infrastructure.Health.ServerStateStore state,
    GatewayMetrics metrics,
    SessionActivityWriter activity,
    Wings.Routing.Engine.RoutingEngineOptions engine,
    IOptions<GatewayOptions> options,
    TimeProvider time,
    ILogger<GatewayHeartbeatService> logger) : BackgroundService
{
    private readonly DateTimeOffset _started = time.GetUtcNow();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var name = options.Value.InstanceName ?? Environment.MachineName;
        activity.InstanceName = name;
        var interval = TimeSpan.FromSeconds(15);
        var last = time.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(interval, time, stoppingToken);
            var now = time.GetUtcNow();
            var (requests, latency, decisionMicros, failed, failovers) = metrics.Drain();
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<WingsDbContext>();
                var row = await db.GatewayInstances.FirstOrDefaultAsync(g => g.InstanceName == name, stoppingToken);
                if (row is null)
                {
                    row = new GatewayInstance { InstanceName = name, StartedAt = _started };
                    db.GatewayInstances.Add(row);
                }
                var snapshot = snapshots.Current;
                row.Host = Environment.MachineName;
                row.Version = typeof(GatewayHeartbeatService).Assembly.GetName().Version?.ToString();
                row.StartedAt = _started;
                row.LastHeartbeatAt = now;
                row.ConfigVersion = snapshot?.Version ?? 0;
                row.RequestsPerSecond = requests / Math.Max(1, (now - last).TotalSeconds);
                row.AverageLatencyMs = latency;
                row.AverageDecisionMicroseconds = decisionMicros;
                row.FailedRoutingLastInterval = failed;
                row.FailoversLastInterval = failovers;
                row.ActiveRequests = state.TotalActiveRequests;
                row.RedisAvailable = store.IsDistributed;
                row.DatabaseAvailable = snapshots.DatabaseAvailable;
                row.ConfigurationStale = snapshot is not null && engine.LastKnownGoodMaxAge is { } max && now - snapshot.LastValidatedAt > max;
                db.GatewayTrafficSamples.Add(new GatewayTrafficSample
                {
                    GatewayInstance = name, At = now, IntervalSeconds = (int)(now - last).TotalSeconds, Requests = requests,
                    AverageLatencyMs = latency, AverageDecisionMicroseconds = decisionMicros, FailedRouting = failed, Failovers = failovers
                });
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Gateway heartbeat failed");
            }
            last = now;
        }
    }
}
