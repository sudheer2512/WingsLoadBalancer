using Microsoft.EntityFrameworkCore;
using Wings.LoadBalancer.Data;
using Wings.LoadBalancer.Data.Entities;
using Wings.LoadBalancer.Infrastructure.Configuration;
using Wings.Routing.Model;

namespace Wings.Admin.Api.Services;

public sealed record DashboardFilter(Guid? CompanyId, Guid? ServerId, Guid? PoolId, Guid? ApplicationId, DeploymentEnvironment? Environment,
    DateTimeOffset? From, DateTimeOffset? To, string? Username);

public sealed class DashboardService(WingsDbContext db, ISystemSettings settings, TimeProvider time)
{
    public async Task<object> SummaryAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var servers = await db.Servers.AsNoTracking().Where(s => s.AdminState != ServerAdminState.Retired)
            .Select(s => new { s.Id, s.AdminState }).ToListAsync(ct);
        var health = await db.ServerHealthCurrent.AsNoTracking().ToDictionaryAsync(h => h.ServerId, h => h.State, ct);
        HealthState H(Guid id) => health.TryGetValue(id, out var h) ? h : HealthState.Unknown;
        bool Offline(Guid id, ServerAdminState admin) => admin is ServerAdminState.Disabled or ServerAdminState.Maintenance || H(id) == HealthState.Unhealthy;

        var activeSince = now.AddMinutes(-await settings.GetNumberAsync(SettingKeys.SessionIdleMinutes, ct));
        var companies = await db.Companies.AsNoTracking().CountAsync(ct);
        var dedicatedCompanies = await db.CompanyServerAssignments.AsNoTracking()
            .Where(a => a.IsEnabled && a.EffectiveFrom <= now && (a.EffectiveTo == null || a.EffectiveTo > now))
            .Select(a => a.CompanyId).Distinct().CountAsync(ct);
        var sharedCompanies = await db.SharedPoolAssignments.AsNoTracking()
            .Where(a => a.IsEnabled && a.EffectiveFrom <= now && (a.EffectiveTo == null || a.EffectiveTo > now))
            .Select(a => a.CompanyId).Distinct().CountAsync(ct);
        var dedicatedUsers = await db.UserServerAssignments.AsNoTracking()
            .Where(a => a.IsEnabled && a.EffectiveFrom <= now && (a.EffectiveTo == null || a.EffectiveTo > now))
            .Select(a => a.UserId).Distinct().CountAsync(ct);
        var sessions = await db.ActiveSessions.AsNoTracking()
            .Where(s => s.EndedAt == null && s.RevokedAt == null && s.LastSeenAt >= activeSince)
            .Select(s => s.SessionKey).Distinct().CountAsync(ct);

        var gatewaySince = now.AddMinutes(-2);
        var gateways = await db.GatewayInstances.AsNoTracking().Where(g => g.LastHeartbeatAt >= now.AddDays(-1)).ToListAsync(ct);
        var live = gateways.Where(g => g.LastHeartbeatAt >= gatewaySince).ToList();
        var hour = now.AddHours(-1);
        var failed = await db.RoutingDecisionLogs.AsNoTracking().CountAsync(d => d.At >= hour && d.Outcome != RoutingOutcome.Routed, ct);
        var failovers = await db.ActiveSessions.AsNoTracking()
            .Where(s => s.EndedAt == null && s.LastSeenAt >= activeSince)
            .Join(db.RoutingDecisionLogs.AsNoTracking().Where(d => d.At >= now.AddMinutes(-30) && d.EventKind == "Failover"),
                s => s.SessionKey, d => d.SessionKey, (s, d) => s.SessionKey)
            .Distinct().CountAsync(ct);
        var openAlerts = await db.Alerts.AsNoTracking().CountAsync(a => a.Status != AlertStatus.Resolved, ct);

        return new
        {
            generatedAt = now,
            servers = new
            {
                total = servers.Count,
                healthy = servers.Count(s => s.AdminState == ServerAdminState.Active && H(s.Id) == HealthState.Healthy),
                degraded = servers.Count(s => !Offline(s.Id, s.AdminState) && H(s.Id) is HealthState.Degraded or HealthState.Recovering),
                offline = servers.Count(s => Offline(s.Id, s.AdminState)),
                draining = servers.Count(s => s.AdminState == ServerAdminState.Draining),
                unknown = servers.Count(s => H(s.Id) == HealthState.Unknown)
            },
            companies = new { total = companies, dedicated = dedicatedCompanies, sharedPools = sharedCompanies },
            usersOnDedicatedServers = dedicatedUsers,
            activeSessions = sessions,
            requestsPerSecond = Math.Round(live.Sum(g => g.RequestsPerSecond), 2),
            failedRoutingLastHour = failed,
            averageGatewayLatencyMs = live.Count == 0 ? 0 : Math.Round(live.Average(g => g.AverageLatencyMs), 2),
            averageDecisionMicroseconds = live.Count == 0 ? 0 : Math.Round(live.Average(g => g.AverageDecisionMicroseconds), 1),
            currentFailovers = failovers,
            openAlerts,
            gateways = gateways.Select(g => new
            {
                g.InstanceName, g.Host, g.Version, g.LastHeartbeatAt, online = g.LastHeartbeatAt >= gatewaySince, g.ConfigVersion,
                g.RequestsPerSecond, g.AverageLatencyMs, g.ActiveRequests, g.RedisAvailable, g.DatabaseAvailable, g.ConfigurationStale
            })
        };
    }

    public async Task<object> UtilizationAsync(int hours, CancellationToken ct)
    {
        var since = time.GetUtcNow().AddHours(-Math.Clamp(hours, 1, 168));
        var servers = await db.Servers.AsNoTracking().Where(s => s.AdminState != ServerAdminState.Retired)
            .Select(s => new { s.Id, s.Name, s.Environment, s.ServerType, s.AdminState, s.MaxCapacity, s.ApplicationVersion }).ToListAsync(ct);
        var health = await db.ServerHealthCurrent.AsNoTracking().ToDictionaryAsync(h => h.ServerId, ct);
        var samples = await db.ServerMetrics.AsNoTracking().Where(m => m.CapturedAt >= since)
            .OrderBy(m => m.CapturedAt).ToListAsync(ct);
        var byServer = samples.GroupBy(m => m.ServerId).ToDictionary(g => g.Key, g => g.ToList());

        return servers.OrderBy(s => s.Name).Select(s =>
        {
            health.TryGetValue(s.Id, out var h);
            var series = byServer.TryGetValue(s.Id, out var list) ? list : [];
            return new
            {
                s.Id, s.Name, Environment = s.Environment.ToString(), Type = s.ServerType.ToString(), AdminState = s.AdminState.ToString(),
                Health = (h?.State ?? HealthState.Unknown).ToString(), s.MaxCapacity, s.ApplicationVersion,
                current = h is null ? null : new
                {
                    h.CpuPercent, h.MemoryPercent, h.DiskPercent, h.ActiveRequests, h.ActiveSessions, h.ResponseTimeMs,
                    h.ErrorRatePercent, h.LastCheckAt, h.LastReason, h.ReportedVersion, h.AwaitingManualRestore
                },
                series = series.Select(m => new { at = m.CapturedAt, cpu = m.CpuPercent, memory = m.MemoryPercent, requests = m.ActiveRequests,
                    responseMs = m.ResponseTimeMs, errors = m.ErrorRatePercent, sessions = m.ActiveSessions })
            };
        });
    }

    public IQueryable<RoutingDecisionLog> Decisions(DashboardFilter f)
    {
        var q = db.RoutingDecisionLogs.AsNoTracking();
        if (f.CompanyId is { } c) q = q.Where(d => d.CompanyId == c);
        if (f.ServerId is { } s) q = q.Where(d => d.ServerId == s);
        if (f.PoolId is { } p) q = q.Where(d => d.PoolId == p);
        if (f.ApplicationId is { } a) q = q.Where(d => d.ApplicationId == a);
        if (f.Environment is { } e) q = q.Where(d => d.Environment == e);
        if (f.From is { } from) q = q.Where(d => d.At >= from);
        if (f.To is { } to) q = q.Where(d => d.At <= to);
        if (!string.IsNullOrWhiteSpace(f.Username))
        {
            var normalized = Wings.Routing.IdentifierNormalizer.Normalize(f.Username);
            var users = db.UserCompanyMemberships.Where(m => m.NormalizedUsername.Contains(normalized)).Select(m => m.UserId);
            q = q.Where(d => d.UserId != null && users.Contains(d.UserId.Value));
        }
        return q;
    }

    public async Task<object> TimeSeriesAsync(DashboardFilter f, int bucketMinutes, CancellationToken ct)
    {
        var to = f.To ?? time.GetUtcNow();
        var from = f.From ?? to.AddHours(-24);
        var rows = await Decisions(f with { From = from, To = to }).Select(d => new { d.At, d.Outcome, d.IsFailover }).ToListAsync(ct);
        var traffic = await db.GatewayTrafficSamples.AsNoTracking().Where(t => t.At >= from && t.At <= to)
            .Select(t => new { t.At, t.Requests, t.AverageLatencyMs, t.FailedRouting }).ToListAsync(ct);
        var bucket = TimeSpan.FromMinutes(Math.Clamp(bucketMinutes, 1, 1440));
        DateTimeOffset B(DateTimeOffset at) => new(at.UtcTicks - at.UtcTicks % bucket.Ticks, TimeSpan.Zero);

        var buckets = new SortedDictionary<DateTimeOffset, (int Routed, int Failed, int Failovers, long Requests, double Latency, int Samples)>();
        foreach (var r in rows)
        {
            var k = B(r.At);
            buckets.TryGetValue(k, out var v);
            buckets[k] = (v.Routed + (r.Outcome == RoutingOutcome.Routed ? 1 : 0), v.Failed + (r.Outcome != RoutingOutcome.Routed ? 1 : 0),
                v.Failovers + (r.IsFailover ? 1 : 0), v.Requests, v.Latency, v.Samples);
        }
        foreach (var t in traffic)
        {
            var k = B(t.At);
            buckets.TryGetValue(k, out var v);
            buckets[k] = (v.Routed, v.Failed, v.Failovers, v.Requests + t.Requests, v.Latency + t.AverageLatencyMs, v.Samples + 1);
        }
        return buckets.Select(kv => new
        {
            at = kv.Key, routedDecisions = kv.Value.Routed, failedDecisions = kv.Value.Failed, failovers = kv.Value.Failovers,
            requests = kv.Value.Requests, averageLatencyMs = kv.Value.Samples == 0 ? 0 : Math.Round(kv.Value.Latency / kv.Value.Samples, 2)
        });
    }

    /// <summary>Environment → pools / dedicated servers → servers → companies, for the topology tree view.</summary>
    public async Task<object> TopologyAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var servers = await db.Servers.AsNoTracking().Where(s => s.AdminState != ServerAdminState.Retired).ToListAsync(ct);
        var health = await db.ServerHealthCurrent.AsNoTracking().ToDictionaryAsync(h => h.ServerId, h => h.State, ct);
        var pools = await db.ServerPools.AsNoTracking().Include(p => p.Members).ToListAsync(ct);
        var companies = await db.Companies.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var companyAssignments = await db.CompanyServerAssignments.AsNoTracking()
            .Where(a => a.IsEnabled && a.EffectiveFrom <= now && (a.EffectiveTo == null || a.EffectiveTo > now)).ToListAsync(ct);
        var userAssignments = await db.UserServerAssignments.AsNoTracking()
            .Where(a => a.IsEnabled && a.EffectiveFrom <= now && (a.EffectiveTo == null || a.EffectiveTo > now))
            .GroupBy(a => a.PrimaryServerId).Select(g => new { ServerId = g.Key, Users = g.Count() }).ToDictionaryAsync(x => x.ServerId, x => x.Users, ct);
        var poolAssignments = await db.SharedPoolAssignments.AsNoTracking()
            .Where(a => a.IsEnabled && a.EffectiveFrom <= now && (a.EffectiveTo == null || a.EffectiveTo > now)).ToListAsync(ct);

        object ServerNode(Server s) => new
        {
            id = s.Id, label = s.Name, kind = "server", type = s.ServerType.ToString(), adminState = s.AdminState.ToString(),
            health = (health.TryGetValue(s.Id, out var h) ? h : HealthState.Unknown).ToString(),
            dedicatedUsers = userAssignments.GetValueOrDefault(s.Id),
            children = companyAssignments.Where(a => a.PrimaryServerId == s.Id || a.SecondaryServerId == s.Id)
                .Select(a => (object)new
                {
                    id = a.CompanyId, label = companies.TryGetValue(a.CompanyId, out var c) ? $"{c.Code} · {c.Name}" : a.CompanyId.ToString(),
                    kind = "company", role = a.PrimaryServerId == s.Id ? "primary" : "secondary"
                }).ToList()
        };

        return Enum.GetValues<DeploymentEnvironment>()
            .Where(env => servers.Any(s => s.Environment == env))
            .Select(env => new
            {
                id = env.ToString(), label = env.ToString(), kind = "environment",
                children = pools.Where(p => p.Environment == env).OrderBy(p => p.Name).Select(p => (object)new
                {
                    id = p.Id, label = p.Name, kind = "pool", algorithm = p.Algorithm.ToString(), affinity = p.Affinity.ToString(),
                    tenants = poolAssignments.Where(a => a.PoolId == p.Id).Select(a => companies.TryGetValue(a.CompanyId, out var c) ? c.Code : "?").Distinct().ToList(),
                    children = p.Members.OrderBy(m => m.Priority).Select(m => servers.FirstOrDefault(s => s.Id == m.ServerId))
                        .Where(s => s is not null).Select(s => ServerNode(s!)).ToList()
                })
                .Append(new
                {
                    id = $"dedicated-{env}", label = "Dedicated and hybrid servers", kind = "group",
                    children = servers.Where(s => s.Environment == env && s.ServerType != ServerType.Shared).OrderBy(s => s.Name).Select(ServerNode).ToList()
                })
                .ToList()
            });
    }
}
