using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wings.LoadBalancer.Data;
using Wings.LoadBalancer.Data.Entities;
using Wings.LoadBalancer.Infrastructure.Alerts;
using Wings.LoadBalancer.Infrastructure.Auditing;
using Wings.LoadBalancer.Infrastructure.Caching;
using Wings.LoadBalancer.Infrastructure.Configuration;
using Wings.Routing.Model;

namespace Wings.LoadBalancer.Infrastructure.Monitoring;

public sealed class OperationsOptions
{
    public const string Section = "Operations";
    public int DrainCheckSeconds { get; set; } = 15;
    public int AlertEvaluationSeconds { get; set; } = 30;
    public int RetentionRunHours { get; set; } = 6;
    public int GatewayHeartbeatStaleSeconds { get; set; } = 120;
}

/// <summary>
/// Leader-only background work: drain completion, scheduled maintenance windows, alert evaluation and retention.
/// Uses its own lease so it can run in the admin API or the standalone monitor service.
/// </summary>
public sealed class OperationsService(
    IServiceScopeFactory scopes,
    ILeaseService leases,
    IStateStore store,
    IOptions<OperationsOptions> options,
    IOptions<RedisOptions> redis,
    TimeProvider time,
    ILogger<OperationsService> logger) : BackgroundService
{
    public const string LeaseName = "operations";
    private readonly string _holder = LeaseHolder.Create();
    private DateTimeOffset _lastAlerts = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRetention = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tick = TimeSpan.FromSeconds(Math.Max(1, options.Value.DrainCheckSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await leases.TryAcquireAsync(LeaseName, _holder, tick * 4, stoppingToken))
                {
                    await RunOnceAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Operations cycle failed");
                await RaiseSqlUnavailableBestEffortAsync(ex);
            }
            await Task.Delay(tick, time, stoppingToken);
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await ProcessMaintenanceWindowsAsync(now, ct);
        await ProcessDrainsAsync(now, ct);
        if (now - _lastAlerts >= TimeSpan.FromSeconds(options.Value.AlertEvaluationSeconds))
        {
            await EvaluateAlertsAsync(now, ct);
            _lastAlerts = now;
        }
        if (now - _lastRetention >= TimeSpan.FromHours(options.Value.RetentionRunHours))
        {
            await ApplyRetentionAsync(now, ct);
            _lastRetention = now;
        }
    }

    // ---- Draining ------------------------------------------------------------------------------------------------

    public async Task ProcessDrainsAsync(DateTimeOffset now, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WingsDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditTrail>();
        var settings = scope.ServiceProvider.GetRequiredService<ISystemSettings>();
        var activeSince = now - TimeSpan.FromMinutes(await settings.GetNumberAsync(SettingKeys.SessionIdleMinutes, ct));

        var draining = await db.Servers.Where(s => s.AdminState == ServerAdminState.Draining).ToListAsync(ct);
        foreach (var server in draining)
        {
            var sessions = await db.ActiveSessions.CountAsync(s => s.ServerId == server.Id && s.EndedAt == null
                && s.RevokedAt == null && s.LastSeenAt >= activeSince, ct);
            var requests = await db.ServerHealthCurrent.Where(h => h.ServerId == server.Id)
                .Select(h => h.ActiveRequests).FirstOrDefaultAsync(ct) ?? 0;
            var started = server.DrainStartedAt ?? now;
            var timedOut = now - started >= TimeSpan.FromMinutes(server.DrainTimeoutMinutes);
            if (sessions > 0 && !timedOut) continue;

            server.AdminState = ServerAdminState.Maintenance;
            audit.Add(db, new AuditEvent(AuditEvents.ServerDrainCompleted)
            {
                ServerId = server.Id, EntityType = nameof(Server), EntityId = server.Id,
                Previous = nameof(ServerAdminState.Draining), New = nameof(ServerAdminState.Maintenance),
                Reason = timedOut
                    ? $"Drain timeout of {server.DrainTimeoutMinutes} min reached with {sessions} sessions and {requests} requests remaining"
                    : "All sessions drained",
                Actor = "drain-controller", ActorType = "System"
            });
            logger.LogInformation("{Server} drained ({Reason}); now in maintenance", server.Name, timedOut ? "timeout" : "no sessions");
        }
        if (draining.Count > 0) await db.SaveChangesAsync(ct);
    }

    // ---- Maintenance windows --------------------------------------------------------------------------------------

    public async Task ProcessMaintenanceWindowsAsync(DateTimeOffset now, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WingsDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditTrail>();

        var windows = await db.MaintenanceWindows.Include(w => w.Server)
            .Where(w => w.Status == MaintenanceStatus.Scheduled && w.StartsAt <= now
                        || w.Status == MaintenanceStatus.Draining
                        || w.Status == MaintenanceStatus.InMaintenance)
            .ToListAsync(ct);
        if (windows.Count == 0) return;
        var health = await db.ServerHealthCurrent.AsNoTracking().ToDictionaryAsync(h => h.ServerId, ct);

        foreach (var w in windows)
        {
            var server = w.Server!;
            switch (w.Status)
            {
                case MaintenanceStatus.Scheduled when server.AdminState is ServerAdminState.Active or ServerAdminState.Draining:
                    server.AdminState = ServerAdminState.Draining;
                    server.DrainStartedAt ??= now;
                    server.DrainTimeoutMinutes = w.DrainTimeoutMinutes;
                    w.Status = MaintenanceStatus.Draining;
                    Audit(audit, db, w, "Maintenance window started; draining");
                    break;
                case MaintenanceStatus.Scheduled:
                    w.Status = MaintenanceStatus.Draining;
                    break;
                case MaintenanceStatus.Draining when server.AdminState == ServerAdminState.Maintenance:
                    w.Status = MaintenanceStatus.InMaintenance;
                    Audit(audit, db, w, "Server drained; maintenance in progress");
                    break;
                case MaintenanceStatus.InMaintenance when w.EndsAt <= now && !w.AutoRestore:
                    w.Status = MaintenanceStatus.Completed;
                    Audit(audit, db, w, "Maintenance window ended; awaiting manual restore");
                    break;
                case MaintenanceStatus.InMaintenance when w.EndsAt <= now:
                    // Restore only once active checks show the server healthy (stabilisation is in the state machine).
                    if (health.TryGetValue(server.Id, out var h) && h.State == HealthState.Healthy && !h.AwaitingManualRestore)
                    {
                        server.AdminState = ServerAdminState.Active;
                        server.DrainStartedAt = null;
                        w.Status = MaintenanceStatus.Completed;
                        Audit(audit, db, w, "Maintenance completed; server healthy and restored");
                    }
                    break;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static void Audit(IAuditTrail audit, WingsDbContext db, MaintenanceWindow w, string reason) =>
        audit.Add(db, new AuditEvent(AuditEvents.MaintenanceEvent)
        {
            ServerId = w.ServerId, EntityType = nameof(MaintenanceWindow), EntityId = w.Id, New = w.Status.ToString(),
            Reason = reason, Actor = "maintenance-scheduler", ActorType = "System"
        });

    // ---- Alerts --------------------------------------------------------------------------------------------------

    public async Task EvaluateAlertsAsync(DateTimeOffset now, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WingsDbContext>();
        var alerts = scope.ServiceProvider.GetRequiredService<IAlertService>();
        var settings = scope.ServiceProvider.GetRequiredService<ISystemSettings>();
        var source = scope.ServiceProvider.GetRequiredService<IRoutingConfigurationSource>();

        var cpu = await settings.GetNumberAsync(SettingKeys.AlertCpuPercent, ct);
        var memory = await settings.GetNumberAsync(SettingKeys.AlertMemoryPercent, ct);
        var errors = await settings.GetNumberAsync(SettingKeys.AlertErrorRatePercent, ct);
        var latency = await settings.GetNumberAsync(SettingKeys.AlertResponseTimeMs, ct);
        var capacity = await settings.GetNumberAsync(SettingKeys.AlertCapacityPercent, ct);

        var servers = await db.Servers.AsNoTracking().Where(s => s.AdminState == ServerAdminState.Active || s.AdminState == ServerAdminState.Draining)
            .ToListAsync(ct);
        var health = await db.ServerHealthCurrent.AsNoTracking().ToDictionaryAsync(h => h.ServerId, ct);

        async Task Toggle(bool condition, AlertRequest request)
        {
            if (condition) await alerts.RaiseAsync(request, ct);
            else await alerts.ResolveAsync(request.DedupKey, "Condition cleared", ct);
        }

        foreach (var s in servers)
        {
            health.TryGetValue(s.Id, out var h);
            await Toggle(h?.State == HealthState.Unhealthy, new(AlertTypes.ServerUnavailable, AlertSeverity.Critical,
                $"server-down:{s.Id}", $"{s.Name} is unavailable", h?.LastReason, s.Id));
            await Toggle(h?.CpuPercent > cpu, new(AlertTypes.HighCpu, AlertSeverity.Warning, $"cpu:{s.Id}",
                $"{s.Name} CPU {h?.CpuPercent:0}% (threshold {cpu:0}%)", null, s.Id));
            await Toggle(h?.MemoryPercent > memory, new(AlertTypes.HighMemory, AlertSeverity.Warning, $"memory:{s.Id}",
                $"{s.Name} memory {h?.MemoryPercent:0}% (threshold {memory:0}%)", null, s.Id));
            await Toggle(h?.ErrorRatePercent > errors, new(AlertTypes.HighErrorRate, AlertSeverity.Warning, $"errors:{s.Id}",
                $"{s.Name} error rate {h?.ErrorRatePercent:0.0}% (threshold {errors:0.0}%)", null, s.Id));
            await Toggle(h?.ResponseTimeMs > latency, new(AlertTypes.SlowResponse, AlertSeverity.Warning, $"latency:{s.Id}",
                $"{s.Name} responds in {h?.ResponseTimeMs:0} ms (threshold {latency:0} ms)", null, s.Id));
            var used = s.MaxCapacity > 0 && h?.ActiveSessions is { } sessions ? sessions * 100.0 / s.MaxCapacity : 0;
            await Toggle(used >= capacity, new(AlertTypes.NearCapacity, AlertSeverity.Warning, $"capacity:{s.Id}",
                $"{s.Name} at {used:0}% of session capacity", null, s.Id));
        }

        var window = now.AddMinutes(-5);
        var noDestination = await db.RoutingDecisionLogs.AsNoTracking()
            .Where(d => d.At >= window && d.Outcome == RoutingOutcome.NoHealthyDestination && d.CompanyId != null)
            .GroupBy(d => d.CompanyId!.Value).Select(g => new { CompanyId = g.Key, Count = g.Count() }).ToListAsync(ct);
        foreach (var n in noDestination)
        {
            await alerts.RaiseAsync(new(AlertTypes.NoHealthyDestination, AlertSeverity.Critical, $"no-destination:{n.CompanyId}",
                "No healthy authorised destination for a tenant", $"{n.Count} requests refused in the last 5 minutes", CompanyId: n.CompanyId), ct);
        }

        var drainingIds = servers.Where(s => s.AdminState == ServerAdminState.Draining).Select(s => s.Id).ToHashSet();
        var failovers = await db.RoutingDecisionLogs.AsNoTracking()
            .Where(d => d.At >= window && d.IsFailover && d.CompanyId != null && d.EventKind == "Failover")
            .GroupBy(d => d.CompanyId!.Value).Select(g => new { CompanyId = g.Key, Count = g.Count() }).ToListAsync(ct);
        foreach (var f in failovers)
        {
            var planned = await db.CompanyServerAssignments.AsNoTracking()
                .AnyAsync(a => a.CompanyId == f.CompanyId && a.IsEnabled && a.EffectiveTo == null && drainingIds.Contains(a.PrimaryServerId), ct);
            if (!planned)
                await alerts.RaiseAsync(new(AlertTypes.UnexpectedFailover, AlertSeverity.Warning, $"failover:{f.CompanyId}",
                    "Unexpected failover for a tenant", $"{f.Count} failover decisions in the last 5 minutes", CompanyId: f.CompanyId), ct);
        }

        var staleBefore = now.AddSeconds(-options.Value.GatewayHeartbeatStaleSeconds);
        var recent = now.AddDays(-1);
        foreach (var g in await db.GatewayInstances.AsNoTracking().Where(g => g.LastHeartbeatAt >= recent).ToListAsync(ct))
        {
            await Toggle(g.LastHeartbeatAt < staleBefore || !g.DatabaseAvailable, new(AlertTypes.GatewayConnectivity, AlertSeverity.Warning,
                $"gateway:{g.InstanceName}", $"Gateway {g.InstanceName} connectivity problem",
                g.LastHeartbeatAt < staleBefore ? $"No heartbeat since {g.LastHeartbeatAt:u}" : "Gateway reports the routing database unavailable"));
            await Toggle(g.ConfigurationStale, new(AlertTypes.ConfigurationInconsistency, AlertSeverity.Critical,
                $"gateway-stale:{g.InstanceName}", $"Gateway {g.InstanceName} is running on stale configuration"));
        }

        if (!string.IsNullOrWhiteSpace(redis.Value.Configuration))
        {
            await Toggle(!store.IsDistributed, new(AlertTypes.RedisUnavailable, AlertSeverity.Warning, "redis",
                "Redis unavailable", "Gateways are using in-memory fallback for sessions and invalidation"));
        }
        await alerts.ResolveAsync("sql", "Database reachable");

        var snapshot = new RoutingSnapshot(await source.LoadAsync(ct), now);
        var issues = ConfigurationValidator.Validate(snapshot, now);
        var errorsFound = issues.Where(i => i.Severity == IssueSeverity.Error).ToList();
        await Toggle(errorsFound.Count > 0, new(AlertTypes.ConfigurationInconsistency, AlertSeverity.Warning, "config-validation",
            $"{errorsFound.Count} routing configuration inconsistencies", string.Join("\n", errorsFound.Take(10).Select(i => i.Message))));
    }

    private async Task RaiseSqlUnavailableBestEffortAsync(Exception ex)
    {
        // The alert itself cannot be stored while SQL is down; log loudly so log-based monitoring picks it up.
        if (ex is DbUpdateException or InvalidOperationException or System.Data.Common.DbException)
            logger.LogCritical(ex, "ALERT {AlertType}: routing database unavailable", AlertTypes.SqlUnavailable);
        await Task.CompletedTask;
    }

    // ---- Retention -----------------------------------------------------------------------------------------------

    public async Task ApplyRetentionAsync(DateTimeOffset now, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WingsDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<ISystemSettings>();
        async Task<DateTimeOffset> Cutoff(string key) => now.AddDays(-await settings.GetNumberAsync(key, ct));

        var audit = await Cutoff(SettingKeys.AuditRetentionDays);
        var decisions = await Cutoff(SettingKeys.DecisionLogRetentionDays);
        var metrics = await Cutoff(SettingKeys.MetricsRetentionDays);
        var history = await Cutoff(SettingKeys.HealthHistoryRetentionDays);
        var sessions = await Cutoff(SettingKeys.SessionRetentionDays);
        var alertCutoff = await Cutoff(SettingKeys.AlertRetentionDays);

        var removed = 0;
        removed += await db.AuditLogs.Where(a => a.At < audit).ExecuteDeleteAsync(ct);
        removed += await db.RoutingDecisionLogs.Where(d => d.At < decisions).ExecuteDeleteAsync(ct);
        removed += await db.ServerMetrics.Where(m => m.CapturedAt < metrics).ExecuteDeleteAsync(ct);
        removed += await db.ServerHealthHistory.Where(h => h.At < history).ExecuteDeleteAsync(ct);
        removed += await db.ActiveSessions.Where(s => s.EndedAt != null && s.EndedAt < sessions || s.LastSeenAt < sessions).ExecuteDeleteAsync(ct);
        removed += await db.Alerts.Where(a => a.Status == AlertStatus.Resolved && a.ResolvedAt < alertCutoff).ExecuteDeleteAsync(ct);
        if (removed > 0) logger.LogInformation("Retention removed {Count} rows", removed);
    }
}
