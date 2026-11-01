using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Wings.Admin.Api.Services;
using Wings.LoadBalancer.Data;
using Wings.Routing.Model;

namespace Wings.Admin.Api.Hubs;

/// <summary>
/// Real-time dashboard feed. Server → client messages: <c>summary</c>, <c>decisions</c>, <c>health</c>.
/// </summary>
[Authorize(Policy = Permissions.MonitoringRead)]
public sealed class MonitoringHub : Hub;

/// <summary>Polls the database (works without Redis) and pushes changes to connected dashboards.</summary>
public sealed class MonitoringBroadcaster(
    IServiceScopeFactory scopes,
    IHubContext<MonitoringHub> hub,
    TimeProvider time,
    ILogger<MonitoringBroadcaster> logger) : BackgroundService
{
    private DateTimeOffset _decisionCursor;
    private readonly Dictionary<Guid, HealthState> _lastHealth = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _decisionCursor = time.GetUtcNow();
        var tick = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), time, stoppingToken);
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<WingsDbContext>();
                await PushDecisionsAsync(db, stoppingToken);
                await PushHealthAsync(db, stoppingToken);
                if (tick++ % 3 == 0)
                {
                    var summary = await scope.ServiceProvider.GetRequiredService<DashboardService>().SummaryAsync(stoppingToken);
                    await hub.Clients.All.SendAsync("summary", summary, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Monitoring broadcast failed");
            }
        }
    }

    private async Task PushDecisionsAsync(WingsDbContext db, CancellationToken ct)
    {
        var cursor = _decisionCursor;
        var rows = await db.RoutingDecisionLogs.AsNoTracking().Where(d => d.At > cursor).OrderBy(d => d.At).Take(200).ToListAsync(ct);
        if (rows.Count == 0) return;
        _decisionCursor = rows[^1].At;
        var servers = await db.Servers.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var companies = await db.Companies.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        await hub.Clients.All.SendAsync("decisions", rows.Select(d => new
        {
            d.Id, d.At, d.EventKind, Outcome = d.Outcome.ToString(), d.CompanyId,
            Company = d.CompanyId is { } c ? companies.GetValueOrDefault(c) : null,
            Server = d.ServerId is { } s ? servers.GetValueOrDefault(s) : null, d.ServerId, Source = d.Source.ToString(),
            d.IsFailover, d.IsFailback, d.Reason, d.GatewayInstance, Environment = d.Environment?.ToString()
        }), ct);
    }

    private async Task PushHealthAsync(WingsDbContext db, CancellationToken ct)
    {
        var rows = await db.ServerHealthCurrent.AsNoTracking().ToListAsync(ct);
        var changed = rows.Where(r => !_lastHealth.TryGetValue(r.ServerId, out var prev) || prev != r.State).ToList();
        foreach (var r in rows) _lastHealth[r.ServerId] = r.State;
        if (changed.Count == 0) return;
        await hub.Clients.All.SendAsync("health", changed.Select(r => new
        {
            r.ServerId, State = r.State.ToString(), r.StateChangedAt, r.LastReason, r.ResponseTimeMs, r.CpuPercent, r.MemoryPercent
        }), ct);
    }
}
