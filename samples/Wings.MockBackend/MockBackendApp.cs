using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR;

namespace Wings.MockBackend;

public sealed class MockBackendSettings
{
    public string ServerName { get; set; } = "APP-SERVER-01";
    public Guid ServerId { get; set; }
    public string Version { get; set; } = "2.1.0";
    /// <summary>Base64 gateway master key (BackendTrust:MasterKey). Empty disables signature checks.</summary>
    public string? MasterKey { get; set; }
    public bool RequireSignature { get; set; } = true;
    /// <summary>Exposes /_test/* endpoints to change health behaviour (tests and demos only).</summary>
    public bool EnableTestControls { get; set; }
}

/// <summary>Runtime knobs for simulating failures.</summary>
public sealed class MockBackendState
{
    public volatile bool Healthy = true;
    public volatile bool Reject = false;
    public int HealthDelayMs;
    public double CpuPercent = 20;
    public double ErrorRatePercent;
    public int ActiveRequests;
    public long TotalRequests;
    public readonly ConcurrentDictionary<string, DateTimeOffset> Sessions = new();
}

public sealed class EchoHub(MockBackendSettings settings) : Hub
{
    public Task<string> Echo(string message) => Task.FromResult($"{settings.ServerName}:{message}");
}

/// <summary>A sample Wings application server. Every response says which server handled it.</summary>
public static class MockBackendApp
{
    public static WebApplication Build(string[] args, Action<MockBackendSettings>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        var settings = builder.Configuration.GetSection("MockBackend").Get<MockBackendSettings>() ?? new MockBackendSettings();
        configure?.Invoke(settings);
        if (settings.ServerId == Guid.Empty) settings.ServerId = Guid.NewGuid();
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<MockBackendState>();
        builder.Services.AddSignalR();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var app = builder.Build();
        var state = app.Services.GetRequiredService<MockBackendState>();
        byte[]? master = string.IsNullOrWhiteSpace(settings.MasterKey) ? null : Convert.FromBase64String(settings.MasterKey);
        var signingKey = master is null ? null : GatewayIdentity.DeriveSigningKey(master, settings.ServerId);
        var healthKey = master is null ? null : GatewayIdentity.DeriveHealthKey(master, settings.ServerId);

        app.UseWebSockets();
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (path.StartsWithSegments("/health") || path.StartsWithSegments("/_test"))
            {
                await next();
                return;
            }
            Interlocked.Increment(ref state.TotalRequests);
            Interlocked.Increment(ref state.ActiveRequests);
            try
            {
                if (state.Reject)
                {
                    context.Response.StatusCode = 500;
                    return;
                }
                if (settings.RequireSignature && signingKey is not null)
                {
                    var identity = GatewayIdentity.Verify(context.Request, signingKey, TimeSpan.FromMinutes(2));
                    if (identity is null)
                    {
                        context.Response.StatusCode = 401;
                        await context.Response.WriteAsJsonAsync(new { error = "untrusted_request", server = settings.ServerName });
                        return;
                    }
                    context.Items["identity"] = identity;
                    state.Sessions[identity.Session] = DateTimeOffset.UtcNow;
                }
                await next();
            }
            finally
            {
                Interlocked.Decrement(ref state.ActiveRequests);
            }
        });

        app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy" }));
        app.MapGet("/health/ready", async (HttpContext context) =>
        {
            if (healthKey is not null && context.Request.Headers["X-Wings-Health-Key"] != healthKey)
                return Results.Unauthorized();
            if (state.HealthDelayMs > 0) await Task.Delay(state.HealthDelayMs);
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-30);
            foreach (var (k, v) in state.Sessions) if (v < cutoff) state.Sessions.TryRemove(k, out _);
            var process = Process.GetCurrentProcess();
            var report = new
            {
                status = state.Healthy ? "Healthy" : "Unhealthy",
                serverName = settings.ServerName,
                version = settings.Version,
                cpuPercent = state.CpuPercent,
                memoryPercent = Math.Min(100, process.WorkingSet64 / (double)(8L * 1024 * 1024 * 1024) * 100),
                diskPercent = 40.0,
                activeConnections = state.ActiveRequests,
                activeRequests = state.ActiveRequests,
                queueLength = 0,
                errorRatePercent = state.ErrorRatePercent,
                activeSessions = state.Sessions.Count,
                database = "Healthy"
            };
            return state.Healthy ? Results.Ok(report) : Results.Json(report, statusCode: 503);
        });

        app.MapGet("/", (HttpContext context) =>
        {
            var identity = context.Items["identity"] as GatewayIdentity;
            string E(string? v) => System.Net.WebUtility.HtmlEncode(v ?? "");
            var html = $$"""
                <!doctype html><html><head><meta charset="utf-8"><title>{{E(settings.ServerName)}}</title>
                <style>body{font-family:system-ui;margin:40px;background:#f7f9fc}code{background:#eef;padding:2px 6px;border-radius:4px}</style></head>
                <body><h1>Served by {{E(settings.ServerName)}}</h1>
                <p>Company <code>{{E(identity?.CompanyCode)}}</code> · user <code>{{E(identity?.Username)}}</code> · app <code>{{E(identity?.Application)}}</code> ({{E(identity?.Environment)}})</p>
                <p>Version {{E(settings.Version)}}. Identity signature {{(identity is null ? "not checked" : "verified")}}.</p>
                <p><a href="/api/whoami">/api/whoami</a> · <a href="/_wings/logout">sign out</a></p></body></html>
                """;
            return Results.Content(html, "text/html");
        });

        app.MapGet("/api/whoami", (HttpContext context) => Results.Ok(WhoAmI(context, settings)));
        app.MapGet("/hr/api/whoami", (HttpContext context) => Results.Ok(WhoAmI(context, settings)));

        app.MapPost("/api/upload", async (HttpContext context) =>
        {
            using var sha = SHA256.Create();
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
                total += read;
            }
            sha.TransformFinalBlock([], 0, 0);
            return Results.Ok(new { server = settings.ServerName, bytes = total, sha256 = Convert.ToHexString(sha.Hash!) });
        });

        app.MapGet("/api/download", async (HttpContext context, long size) =>
        {
            context.Response.ContentType = "application/octet-stream";
            context.Response.Headers["X-Served-By"] = settings.ServerName;
            var chunk = new byte[65536];
            Array.Fill(chunk, (byte)'w');
            for (long sent = 0; sent < size; sent += chunk.Length)
                await context.Response.Body.WriteAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, size - sent)));
        });

        app.MapGet("/api/slow", async (int ms) =>
        {
            await Task.Delay(ms);
            return Results.Ok(new { server = settings.ServerName, delayed = ms });
        });

        app.MapGet("/api/internal-redirect", (HttpContext context) =>
            Results.Redirect($"{context.Request.Scheme}://{context.Request.Host}/api/whoami?redirected=1"));

        app.MapGet("/api/set-cookie", (HttpContext context) =>
        {
            context.Response.Headers.Append("Set-Cookie", $"app-state={settings.ServerName}; Domain=app-server.internal; Path=/; HttpOnly");
            context.Response.Headers["Server"] = "Kestrel-Internal";
            context.Response.Headers["X-Powered-By"] = "ASP.NET";
            return Results.Ok(new { server = settings.ServerName });
        });

        app.Map("/ws/echo", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    break;
                }
                var reply = Encoding.UTF8.GetBytes($"{settings.ServerName}:{Encoding.UTF8.GetString(buffer, 0, result.Count)}");
                await socket.SendAsync(reply, WebSocketMessageType.Text, true, CancellationToken.None);
            }
        });

        app.MapHub<EchoHub>("/hubs/echo");

        if (settings.EnableTestControls)
        {
            app.MapPost("/_test/health", (bool healthy, int? delayMs, double? cpu, double? errorRate) =>
            {
                state.Healthy = healthy;
                state.HealthDelayMs = delayMs ?? 0;
                if (cpu is not null) state.CpuPercent = cpu.Value;
                if (errorRate is not null) state.ErrorRatePercent = errorRate.Value;
                return Results.Ok();
            });
            app.MapPost("/_test/reject", (bool reject) =>
            {
                state.Reject = reject;
                return Results.Ok();
            });
        }

        return app;
    }

    private static object WhoAmI(HttpContext context, MockBackendSettings settings)
    {
        var identity = context.Items["identity"] as GatewayIdentity;
        return new
        {
            server = settings.ServerName,
            serverId = settings.ServerId,
            companyId = identity?.CompanyId ?? context.Request.Headers["X-Wings-Company-Id"].ToString(),
            companyCode = identity?.CompanyCode,
            userId = identity?.UserId ?? context.Request.Headers["X-Wings-User-Id"].ToString(),
            username = identity?.Username,
            application = identity?.Application,
            environment = identity?.Environment,
            signatureVerified = identity is not null,
            path = context.Request.Path.Value,
            forwardedFor = context.Request.Headers["X-Forwarded-For"].ToString(),
            forwardedHost = context.Request.Headers["X-Forwarded-Host"].ToString(),
            cookies = context.Request.Cookies.Keys.ToArray(),
            wingsHeaders = context.Request.Headers.Where(h => h.Key.StartsWith("X-Wings-", StringComparison.OrdinalIgnoreCase))
                .Select(h => h.Key).OrderBy(k => k).ToArray(),
            suspiciousHeaders = context.Request.Headers
                .Where(h => h.Key is "X-Company-Id" or "X-User-Id" or "X-Remote-User" or "X-Tenant-Id")
                .Select(h => h.Key).ToArray()
        };
    }
}
