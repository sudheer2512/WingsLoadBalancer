using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wings.Admin.Api.Services;
using Wings.LoadBalancer.Data;
using Wings.LoadBalancer.Infrastructure.Monitoring;
using Wings.Routing.Model;

namespace Wings.Admin.Api.Controllers;

public sealed record EndToEndTestRequest(string Company, string Username, string Password, string? Path, string? Host);

[Route("api/routing")]
public sealed class RoutingController(
    WingsDbContext db,
    SimulationService simulation,
    DashboardService dashboard,
    IHttpClientFactory httpClients,
    IConfiguration configuration,
    TimeProvider time) : AdminControllerBase
{
    /// <summary>Shows what policy would select. Does not change assignments, sessions or affinity.</summary>
    [HttpPost("simulate"), Authorize(Permissions.RoutingSimulate)]
    public async Task<IActionResult> Simulate(SimulationRequest request, CancellationToken ct) =>
        Ok(await simulation.SimulateAsync(request, ct));

    [HttpGet("history"), Authorize(Permissions.MonitoringRead)]
    public async Task<IActionResult> History([FromQuery] DashboardFilter filter, [FromQuery] RoutingOutcome? outcome,
        [FromQuery] string? eventKind, [FromQuery] int skip = 0, [FromQuery] int take = 100, CancellationToken ct = default)
    {
        var q = dashboard.Decisions(filter);
        if (outcome is { } o) q = q.Where(d => d.Outcome == o);
        if (!string.IsNullOrWhiteSpace(eventKind)) q = q.Where(d => d.EventKind == eventKind);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(d => d.At).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
        var serverNames = await db.Servers.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var companyCodes = await db.Companies.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        var userIds = rows.Where(r => r.UserId != null).Select(r => r.UserId!.Value).Distinct().ToList();
        var usernames = await db.UserCompanyMemberships.AsNoTracking().Where(m => userIds.Contains(m.UserId))
            .ToDictionaryAsync(m => (m.CompanyId, m.UserId), m => m.Username, ct);
        return Ok(new
        {
            total,
            rows = rows.Select(d => new
            {
                d.Id, d.At, d.EventKind, Outcome = d.Outcome.ToString(), d.CompanyId,
                Company = d.CompanyId is { } c && companyCodes.TryGetValue(c, out var code) ? code : null,
                d.UserId, Username = d.CompanyId is { } cid && d.UserId is { } uid && usernames.TryGetValue((cid, uid), out var u) ? u : null,
                d.ServerId, Server = d.ServerId is { } s && serverNames.TryGetValue(s, out var n) ? n : null,
                Source = d.Source.ToString(), d.PoolId, d.ApplicationId, Environment = d.Environment?.ToString(), d.IsFailover, d.IsFailback,
                d.AffinityApplied, d.Reason, d.ConfigVersion, d.DecisionMicroseconds, d.GatewayInstance, d.CorrelationId
            })
        });
    }

    /// <summary>Current configuration version, recent version log and validation issues.</summary>
    [HttpGet("config"), Authorize(Permissions.MonitoringRead)]
    public async Task<IActionResult> Config(CancellationToken ct)
    {
        var snapshot = await simulation.LoadSnapshotAsync(ct);
        var versions = await db.ConfigurationVersions.AsNoTracking().OrderByDescending(v => v.ChangedAt).Take(50).ToListAsync(ct);
        var gateways = await db.GatewayInstances.AsNoTracking().Select(g => new { g.InstanceName, g.ConfigVersion, g.LastHeartbeatAt }).ToListAsync(ct);
        return Ok(new
        {
            version = snapshot.Version,
            gateways = gateways.Select(g => new { g.InstanceName, g.ConfigVersion, g.LastHeartbeatAt, inSync = g.ConfigVersion == snapshot.Version }),
            versions,
            issues = ConfigurationValidator.Validate(snapshot, time.GetUtcNow())
                .Select(i => new { Severity = i.Severity.ToString(), i.Code, i.Message, i.CompanyId, i.ServerId })
        });
    }

    /// <summary>
    /// Authenticated end-to-end test through the configured gateway with a (test) account: real sign-in, then a
    /// request. Credentials are used once and never stored, logged or returned.
    /// </summary>
    [HttpPost("e2e"), Authorize(Permissions.RoutingSimulate)]
    public async Task<IActionResult> EndToEnd(EndToEndTestRequest request, CancellationToken ct)
    {
        var gatewayUrl = configuration["Admin:GatewayUrl"];
        if (string.IsNullOrWhiteSpace(gatewayUrl)) return BadRequest(new { error = "not_configured", message = "Set Admin:GatewayUrl to enable end-to-end tests." });
        var path = request.Path is { Length: > 0 } p && p.StartsWith('/') && !p.StartsWith("//") ? p : "/";

        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(gatewayUrl), Timeout = TimeSpan.FromSeconds(30) };
        void Host(HttpRequestMessage m)
        {
            if (request.Host is { Length: > 0 } h) m.Headers.Host = h;
        }

        var steps = new List<object>();
        var sw = Stopwatch.StartNew();
        using var csrfRequest = new HttpRequestMessage(HttpMethod.Get, "/_wings/auth/csrf");
        Host(csrfRequest);
        var csrf = await (await client.SendAsync(csrfRequest, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);
        using var login = new HttpRequestMessage(HttpMethod.Post, "/_wings/auth/login")
        {
            Content = JsonContent.Create(new { company = request.Company, username = request.Username, password = request.Password })
        };
        login.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        Host(login);
        var loginResponse = await client.SendAsync(login, ct);
        steps.Add(new { step = "sign-in", status = (int)loginResponse.StatusCode, ms = sw.ElapsedMilliseconds });
        if (!loginResponse.IsSuccessStatusCode) return Ok(new { success = false, steps });

        sw.Restart();
        using var probe = new HttpRequestMessage(HttpMethod.Get, path);
        Host(probe);
        var response = await client.SendAsync(probe, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        string? servedBy = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("server", out var s)) servedBy = s.GetString();
        }
        catch (JsonException)
        {
        }
        steps.Add(new { step = $"GET {path}", status = (int)response.StatusCode, ms = sw.ElapsedMilliseconds, servedBy });
        return Ok(new { success = response.IsSuccessStatusCode, steps });
    }
}
