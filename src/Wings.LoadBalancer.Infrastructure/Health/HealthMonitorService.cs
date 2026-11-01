using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wings.LoadBalancer.Data;
using Wings.LoadBalancer.Data.Entities;
using Wings.LoadBalancer.Infrastructure.Auditing;
using Wings.LoadBalancer.Infrastructure.Caching;
using Wings.LoadBalancer.Infrastructure.Configuration;
using Wings.LoadBalancer.Infrastructure.Monitoring;
using Wings.Routing.Health;
using Wings.Routing.Model;

namespace Wings.LoadBalancer.Infrastructure.Health;

/// <summary>
/// Central active health monitor. Only the lease holder probes; standby instances wait. Writes current health,
/// transitions (history + audit), periodic metric samples, and fans out state on the shared store.
/// </summary>
public sealed class HealthMonitorService(
    IServiceScopeFactory scopes,
    ILeaseService leases,
    IHealthProbe probe,
    IStateStore store,
    IOptionsMonitor<HealthOptions> options,
    TimeProvider time,
    ILogger<HealthMonitorService> logger) : BackgroundService
{
    public const string LeaseName = "health-monitor";
    private readonly string _holder = LeaseHolder.Create();
    private readonly ConcurrentDictionary<Guid, HealthStateMachine> _machines = new();
    private DateTimeOffset _lastMetricsAt = DateTimeOffset.MinValue;
    private bool _isLeader;

    public bool IsLeader => _isLeader;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(1, options.CurrentValue.IntervalSeconds));
            try
            {
                var leader = await leases.TryAcquireAsync(LeaseName, _holder, interval * 3, stoppingToken);
                if (leader != _isLeader)
                {
                    logger.LogInformation(leader ? "Health monitor is now the active leader" : "Health monitor is standby");
                    if (leader) _machines.Clear(); // re-seed from shared state
                    _isLeader = leader;
                }
                if (leader) await RunCycleAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Health monitor cycle failed");
            }
            await Task.Delay(interval, time, stoppingToken);
        }
    }

    public async Task RunCycleAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WingsDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditTrail>();
        var settings = scope.ServiceProvider.GetRequiredService<ISystemSettings>();
        var now = time.GetUtcNow();
        var o = options.CurrentValue;

        var servers = await db.Servers.AsNoTracking().Include(s => s.Applications)
            .Where(s => s.AdminState != ServerAdminState.Retired).ToListAsync(ct);
        var current = await db.ServerHealthCurrent.ToDictionaryAsync(h => h.ServerId, ct);

        var idle = TimeSpan.FromMinutes(await settings.GetNumberAsync(SettingKeys.SessionIdleMinutes, ct));
        var activeSince = now - idle;
        var sessionCounts = await db.ActiveSessions.AsNoTracking()
            .Where(s => s.EndedAt == null && s.RevokedAt == null && s.LastSeenAt >= activeSince && s.ServerId != null)
            .GroupBy(s => s.ServerId!.Value)
            .Select(g => new { ServerId = g.Key, Count = g.Select(x => x.SessionKey).Distinct().Count() })
            .ToDictionaryAsync(x => x.ServerId, x => x.Count, ct);

        var results = new ConcurrentBag<(Server Server, HealthObservation Observation)>();
        await Parallel.ForEachAsync(servers, new ParallelOptions { MaxDegreeOfParallelism = o.MaxParallelChecks, CancellationToken = ct },
            async (server, token) => results.Add((server, await probe.ProbeAsync(DbRoutingConfigurationSource.ToInfo(server), token))));

        var recordMetrics = now - _lastMetricsAt >= TimeSpan.FromSeconds(o.MetricsIntervalSeconds);
        foreach (var (server, observation) in results)
        {
            if (!current.TryGetValue(server.Id, out var row))
            {
                row = new ServerHealthCurrent { ServerId = server.Id, State = HealthState.Unknown };
                db.ServerHealthCurrent.Add(row);
                current[server.Id] = row;
            }

            var machine = _machines.GetOrAdd(server.Id, _ =>
            {
                var m = new HealthStateMachine(o.ToThresholds(), row.State);
                if (row.StateChangedAt is { } changed) m.Reset(row.State, changed);
                return m;
            });

            var transition = machine.Observe(observation);
            row.LastCheckAt = observation.At;
            row.LastReason = machine.LastReason;
            row.ConsecutiveFailures = machine.ConsecutiveFailures;
            row.ResponseTimeMs = observation.ResponseTimeMs;
            if (observation.Success) row.LastHeartbeatAt = observation.At;
            if (observation.Metrics is { } m)
            {
                row.CpuPercent = m.CpuPercent;
                row.MemoryPercent = m.MemoryPercent;
                row.DiskPercent = m.DiskPercent;
                row.ActiveConnections = m.ActiveConnections;
                row.ActiveRequests = m.ActiveRequests;
                row.QueueLength = m.QueueLength;
                row.ErrorRatePercent = m.ErrorRatePercent;
                row.DatabaseHealthy = m.DatabaseHealthy;
                row.ReportedVersion = m.ApplicationVersion;
            }
            // Gateway-tracked sessions are authoritative for capacity and draining.
            row.ActiveSessions = Math.Max(sessionCounts.GetValueOrDefault(server.Id), 0);

            if (transition is not null)
            {
                row.State = transition.To;
                row.StateChangedAt = transition.At;
                if (transition.To == HealthState.Healthy && transition.From == HealthState.Recovering && server.RequireManualRestore)
                    row.AwaitingManualRestore = true;
                if (transition.To == HealthState.Unhealthy) row.AwaitingManualRestore = false;

                db.ServerHealthHistory.Add(new ServerHealthHistory
                {
                    ServerId = server.Id, FromState = transition.From, ToState = transition.To, At = transition.At,
                    Reason = transition.Reason, Source = HealthCheckSource.Active, ReportedBy = _holder
                });
                audit.Add(db, new AuditEvent(AuditEvents.HealthTransition)
                {
                    ServerId = server.Id, EntityType = nameof(Server), EntityId = server.Id,
                    Previous = transition.From.ToString(), New = transition.To.ToString(), Reason = transition.Reason,
                    Severity = transition.To == HealthState.Unhealthy ? AlertSeverity.Critical : AlertSeverity.Info,
                    Actor = "health-monitor", ActorType = "System"
                });
                logger.LogInformation("{Server} health {From} → {To}: {Reason}", server.Name, transition.From, transition.To, transition.Reason);
            }

            if (recordMetrics)
            {
                db.ServerMetrics.Add(new ServerMetric
                {
                    ServerId = server.Id, CapturedAt = observation.At, CpuPercent = row.CpuPercent, MemoryPercent = row.MemoryPercent,
                    DiskPercent = row.DiskPercent, ActiveConnections = row.ActiveConnections, ActiveRequests = row.ActiveRequests,
                    QueueLength = row.QueueLength, ErrorRatePercent = row.ErrorRatePercent, ResponseTimeMs = observation.ResponseTimeMs,
                    ActiveSessions = row.ActiveSessions, DatabaseHealthy = row.DatabaseHealthy
                });
            }
        }
        if (recordMetrics) _lastMetricsAt = now;

        await db.SaveChangesAsync(ct);
        await store.PublishAsync(StateKeys.HealthChannel, JsonSerializer.Serialize(current.Values, RoutingJson.Options));
    }
}

/// <summary>Gateway side: applies health published by the monitor (database poll + push on the shared store).</summary>
public sealed class SharedHealthFeedService(
    IServiceScopeFactory scopes,
    ServerStateStore state,
    IStateStore store,
    IOptionsMonitor<HealthOptions> options,
    TimeProvider time,
    ILogger<SharedHealthFeedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        store.Subscribe(StateKeys.HealthChannel, message =>
        {
            try
            {
                var rows = JsonSerializer.Deserialize<List<ServerHealthCurrent>>(message, RoutingJson.Options);
                foreach (var row in rows ?? []) state.ApplyShared(row);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Ignoring malformed health message");
            }
        });
        store.Subscribe(PassiveHealthPublisher.QuarantineChannel, message =>
        {
            var parts = message.Split('|');
            if (parts.Length == 2 && Guid.TryParse(parts[0], out var id) && long.TryParse(parts[1], out var until))
                state.Quarantine(id, DateTimeOffset.FromUnixTimeMilliseconds(until));
        });

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<WingsDbContext>();
                foreach (var row in await db.ServerHealthCurrent.AsNoTracking().ToListAsync(stoppingToken))
                    state.ApplyShared(row);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Health feed poll failed; keeping last known state");
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.CurrentValue.FeedPollSeconds)), time, stoppingToken);
        }
    }
}

/// <summary>Gateway side, local mode: this process probes servers in its current snapshot itself.</summary>
public sealed class LocalHealthProbeService(
    IRoutingSnapshotProvider snapshots,
    IHealthProbe probe,
    ServerStateStore state,
    IOptionsMonitor<HealthOptions> options,
    TimeProvider time,
    ILogger<LocalHealthProbeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (options.CurrentValue.GatewayMode == GatewayHealthMode.Local && snapshots.Current is { } snapshot)
            {
                try
                {
                    await ProbeAllAsync(snapshot, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Local health probe cycle failed");
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.CurrentValue.IntervalSeconds)), time, stoppingToken);
        }
    }

    public async Task ProbeAllAsync(RoutingSnapshot snapshot, CancellationToken ct)
    {
        await Parallel.ForEachAsync(snapshot.Servers.Values.Where(s => s.AdminState != ServerAdminState.Retired),
            new ParallelOptions { MaxDegreeOfParallelism = options.CurrentValue.MaxParallelChecks, CancellationToken = ct },
            async (server, token) =>
            {
                var transition = state.ObserveActive(server.Id, await probe.ProbeAsync(server, token));
                if (transition is not null)
                    logger.LogInformation("{Server} health {From} → {To} (local): {Reason}", server.Name, transition.From, transition.To, transition.Reason);
            });
    }
}

/// <summary>Announces passive quarantines to the other gateway instances.</summary>
public sealed class PassiveHealthPublisher(IStateStore store)
{
    public const string QuarantineChannel = "health-quarantine";

    public Task PublishQuarantineAsync(Guid serverId, DateTimeOffset until) =>
        store.PublishAsync(QuarantineChannel, $"{serverId}|{until.ToUnixTimeMilliseconds()}");
}
