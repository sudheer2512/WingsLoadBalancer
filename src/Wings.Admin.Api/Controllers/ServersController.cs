using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wings.LoadBalancer.Data;
using Wings.LoadBalancer.Data.Entities;
using Wings.LoadBalancer.Infrastructure.Auditing;
using Wings.LoadBalancer.Infrastructure.Health;
using Wings.LoadBalancer.Infrastructure.Security;
using Wings.Routing.Model;

namespace Wings.Admin.Api.Controllers;

public sealed record ServerRequest
{
    [Required, MaxLength(100)] public required string Name { get; init; }
    [Required, MaxLength(253)] public required string Hostname { get; init; }
    [MaxLength(45)] public string? IpAddress { get; init; }
    [Required] public required string BaseUrl { get; init; }
    public string? PublicUrl { get; init; }
    public string? HealthCheckUrl { get; init; }
    public DeploymentEnvironment Environment { get; init; }
    [MaxLength(100)] public string? Region { get; init; }
    public ServerType ServerType { get; init; }
    [Range(0, 4096)] public int? CpuCores { get; init; }
    [Range(0, 65536)] public int? RamGb { get; init; }
    [Range(0, 1_000_000)] public int MaxCapacity { get; init; }
    [MaxLength(50)] public string? ApplicationVersion { get; init; }
    [Range(0, 1000)] public int Priority { get; init; }
    [Range(0, 10_000)] public int Weight { get; init; } = 100;
    public bool RequireManualRestore { get; init; }
    [Range(1, 1440)] public int DrainTimeoutMinutes { get; init; } = 30;
    [MaxLength(64)] public string? ClientCertificateThumbprint { get; init; }
    [MaxLength(64)] public string? BackendCertificateThumbprint { get; init; }
    [MaxLength(4000)] public string? Notes { get; init; }
    public List<Guid> ApplicationIds { get; init; } = [];
    public Guid? ConcurrencyStamp { get; init; }
}

public sealed record DrainRequest([Range(1, 1440)] int? TimeoutMinutes, string? Reason);
public sealed record ReservationRequest(Guid CompanyId, [Range(0, 1_000_000)] int ReservedSessions);

[Route("api/servers")]
public sealed class ServersController(
    WingsDbContext db,
    IDestinationValidator destinations,
    IHealthProbe probe,
    IAuditTrail audit,
    IBackendKeyProvider keys,
    TimeProvider time) : AdminControllerBase
{
    [HttpGet, Authorize(Permissions.ServersRead)]
    public async Task<IActionResult> List([FromQuery] DeploymentEnvironment? environment, [FromQuery] string? search,
        [FromQuery] bool includeRetired, CancellationToken ct)
    {
        var q = db.Servers.AsNoTracking().Include(s => s.Applications).AsQueryable();
        if (!includeRetired) q = q.Where(s => s.AdminState != ServerAdminState.Retired);
        if (environment is { } env) q = q.Where(s => s.Environment == env);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpper();
            q = q.Where(s => s.Name.ToUpper().Contains(term) || s.Hostname.ToUpper().Contains(term));
        }
        var servers = await q.OrderBy(s => s.Name).ToListAsync(ct);
        var health = await db.ServerHealthCurrent.AsNoTracking().ToDictionaryAsync(h => h.ServerId, ct);
        var pools = await db.ServerPoolMembers.AsNoTracking().Include(m => m.Pool)
            .GroupBy(m => m.ServerId).Select(g => new { g.Key, Pools = g.Select(m => m.Pool!.Name).ToList() }).ToDictionaryAsync(x => x.Key, x => x.Pools, ct);
        return Ok(servers.Select(s => ToDto(s, health.GetValueOrDefault(s.Id), pools.GetValueOrDefault(s.Id) ?? [])));
    }

    [HttpGet("{id:guid}"), Authorize(Permissions.ServersRead)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var server = await db.Servers.AsNoTracking().Include(s => s.Applications).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (server is null) return NotFound();
        var health = await db.ServerHealthCurrent.AsNoTracking().FirstOrDefaultAsync(h => h.ServerId == id, ct);
        var pools = await db.ServerPoolMembers.AsNoTracking().Where(m => m.ServerId == id).Select(m => m.Pool!.Name).ToListAsync(ct);
        return Ok(ToDto(server, health, pools));
    }

    [HttpPost, Authorize(Permissions.ServersWrite)]
    public async Task<IActionResult> Create(ServerRequest request, CancellationToken ct)
    {
        var errors = Validate(request);
        if (await db.Servers.AnyAsync(s => s.Name == request.Name, ct)) errors.Add($"A server named {request.Name} already exists");
        if (errors.Count > 0) return BadRequest(new { error = "validation_failed", errors });

        var server = new Server { Name = request.Name, Hostname = request.Hostname, BaseUrl = request.BaseUrl };
        Apply(server, request);
        foreach (var appId in request.ApplicationIds.Distinct())
            server.Applications.Add(new ServerApplication { ServerId = server.Id, ApplicationId = appId });
        db.Servers.Add(server);
        audit.Add(db, new AuditEvent(AuditEvents.ServerCreated) { ServerId = server.Id, EntityType = nameof(Server), EntityId = server.Id, New = request.Name });
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = server.Id }, new { server.Id });
    }

    [HttpPut("{id:guid}"), Authorize(Permissions.ServersWrite)]
    public async Task<IActionResult> Update(Guid id, ServerRequest request, CancellationToken ct)
    {
        var server = await db.Servers.Include(s => s.Applications).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (server is null) return NotFound();
        if (request.ConcurrencyStamp is { } stamp) db.Entry(server).Property(s => s.ConcurrencyStamp).OriginalValue = stamp;
        var errors = Validate(request);
        if (await db.Servers.AnyAsync(s => s.Name == request.Name && s.Id != id, ct)) errors.Add($"A server named {request.Name} already exists");
        if (server.Environment != request.Environment && await HasActiveAssignmentsAsync(id, ct))
            errors.Add("Cannot change the environment of a server with active assignments; migrate them first");
        if (errors.Count > 0) return BadRequest(new { error = "validation_failed", errors });

        server.Name = request.Name;
        server.Hostname = request.Hostname;
        server.BaseUrl = request.BaseUrl;
        Apply(server, request);
        var wanted = request.ApplicationIds.Distinct().ToHashSet();
        server.Applications.RemoveAll(a => !wanted.Contains(a.ApplicationId));
        foreach (var appId in wanted.Where(a => server.Applications.All(x => x.ApplicationId != a)))
            server.Applications.Add(new ServerApplication { ServerId = id, ApplicationId = appId });
        audit.Add(db, new AuditEvent(AuditEvents.ServerModified) { ServerId = id, EntityType = nameof(Server), EntityId = id, New = request.Name });
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/enable"), Authorize(Permissions.ServersOperate)]
    public Task<IActionResult> Enable(Guid id, CancellationToken ct) => ChangeState(id, ServerAdminState.Active, AuditEvents.ServerStateChanged, null, ct);

    [HttpPost("{id:guid}/disable"), Authorize(Permissions.ServersOperate)]
    public Task<IActionResult> Disable(Guid id, CancellationToken ct) => ChangeState(id, ServerAdminState.Disabled, AuditEvents.ServerStateChanged, null, ct);

    /// <summary>Stop new sessions; existing sessions continue until they end or the drain timeout expires.</summary>
    [HttpPost("{id:guid}/drain"), Authorize(Permissions.ServersOperate)]
    public Task<IActionResult> Drain(Guid id, DrainRequest? request, CancellationToken ct) =>
        ChangeState(id, ServerAdminState.Draining, AuditEvents.ServerDrainStarted, s =>
        {
            s.DrainStartedAt = time.GetUtcNow();
            if (request?.TimeoutMinutes is { } t) s.DrainTimeoutMinutes = t;
        }, ct, request?.Reason);

    [HttpPost("{id:guid}/maintenance"), Authorize(Permissions.ServersOperate)]
    public Task<IActionResult> Maintenance(Guid id, CancellationToken ct) =>
        ChangeState(id, ServerAdminState.Maintenance, AuditEvents.MaintenanceEvent, null, ct);

    /// <summary>Return to service (also clears a manual-restore hold after recovery).</summary>
    [HttpPost("{id:guid}/restore"), Authorize(Permissions.ServersOperate)]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct)
    {
        var health = await db.ServerHealthCurrent.FirstOrDefaultAsync(h => h.ServerId == id, ct);
        if (health is not null) health.AwaitingManualRestore = false;
        return await ChangeState(id, ServerAdminState.Active, AuditEvents.ServerRestored, s => s.DrainStartedAt = null, ct);
    }

    [HttpPost("{id:guid}/retire"), Authorize(Permissions.ServersWrite)]
    public async Task<IActionResult> Retire(Guid id, CancellationToken ct)
    {
        var blockers = await AssignmentBlockersAsync(id, ct);
        if (blockers.Count > 0)
            return Conflict(new { error = "server_in_use", message = "Migrate these assignments before retiring the server.", blockers });
        return await ChangeState(id, ServerAdminState.Retired, AuditEvents.ServerRetired, s => s.RetiredAt = time.GetUtcNow(), ct);
    }

    /// <summary>Deletes a server that was never used. Servers with any assignment history must be retired instead.</summary>
    [HttpDelete("{id:guid}"), Authorize(Permissions.ServersWrite)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (server is null) return NotFound();
        var blockers = await AssignmentBlockersAsync(id, ct, includeHistory: true);
        if (blockers.Count > 0)
            return Conflict(new { error = "server_in_use", message = "The server has assignments or history; retire it instead.", blockers });
        db.Servers.Remove(server);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/test"), Authorize(Permissions.ServersOperate)]
    public async Task<IActionResult> TestConnectivity(Guid id, CancellationToken ct)
    {
        var server = await db.Servers.AsNoTracking().Include(s => s.Applications).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (server is null) return NotFound();
        var info = Wings.LoadBalancer.Data.DbRoutingConfigurationSource.ToInfo(server);
        var check = destinations.ValidateBackendUrl(info.HealthCheckUrl ?? info.BaseUrl, info.BaseUrl);
        if (!check.IsValid) return BadRequest(new { error = "destination_not_allowed", message = check.Error });
        var result = await probe.ProbeAsync(info, ct);
        return Ok(new { success = result.Success, responseTimeMs = result.ResponseTimeMs, failure = result.Failure, metrics = result.Metrics });
    }

    [HttpGet("{id:guid}/health"), Authorize(Permissions.ServersRead)]
    public async Task<IActionResult> Health(Guid id, [FromQuery] int days = 7, CancellationToken ct = default)
    {
        var since = time.GetUtcNow().AddDays(-Math.Clamp(days, 1, 180));
        var current = await db.ServerHealthCurrent.AsNoTracking().FirstOrDefaultAsync(h => h.ServerId == id, ct);
        var history = await db.ServerHealthHistory.AsNoTracking().Where(h => h.ServerId == id && h.At >= since)
            .OrderByDescending(h => h.At).Take(500).ToListAsync(ct);
        return Ok(new { current, history });
    }

    [HttpGet("{id:guid}/metrics"), Authorize(Permissions.ServersRead)]
    public async Task<IActionResult> Metrics(Guid id, [FromQuery] int hours = 24, CancellationToken ct = default)
    {
        var since = time.GetUtcNow().AddHours(-Math.Clamp(hours, 1, 24 * 90));
        return Ok(await db.ServerMetrics.AsNoTracking().Where(m => m.ServerId == id && m.CapturedAt >= since)
            .OrderBy(m => m.CapturedAt).ToListAsync(ct));
    }

    [HttpGet("{id:guid}/assignments"), Authorize(Permissions.AssignmentsRead)]
    public async Task<IActionResult> Assignments(Guid id, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var company = await db.CompanyServerAssignments.AsNoTracking().Include(a => a.Company)
            .Where(a => (a.PrimaryServerId == id || a.SecondaryServerId == id) && (a.EffectiveTo == null || a.EffectiveTo > now))
            .Select(a => new { a.Id, a.CompanyId, Company = a.Company!.Code, Role = a.PrimaryServerId == id ? "Primary" : "Secondary",
                Environment = a.Environment.ToString(), a.EffectiveFrom, a.EffectiveTo }).ToListAsync(ct);
        var users = await db.UserServerAssignments.AsNoTracking().Include(a => a.Company)
            .Where(a => (a.PrimaryServerId == id || a.SecondaryServerId == id) && (a.EffectiveTo == null || a.EffectiveTo > now))
            .Join(db.UserCompanyMemberships, a => new { a.CompanyId, a.UserId }, m => new { m.CompanyId, m.UserId },
                (a, m) => new { a.Id, a.CompanyId, a.UserId, Company = a.Company!.Code, m.Username, Role = a.PrimaryServerId == id ? "Primary" : "Secondary",
                    Environment = a.Environment.ToString(), a.EffectiveFrom, a.EffectiveTo })
            .ToListAsync(ct);
        var pools = await db.ServerPoolMembers.AsNoTracking().Where(m => m.ServerId == id)
            .Select(m => new { m.PoolId, Pool = m.Pool!.Name, m.Priority, m.WeightOverride, m.AcceptingNewSessions }).ToListAsync(ct);
        var reservations = await db.CapacityReservations.AsNoTracking().Where(r => r.ServerId == id)
            .Select(r => new { r.Id, r.CompanyId, Company = r.Company!.Code, r.ReservedSessions }).ToListAsync(ct);
        var authorizations = await db.CompanyServerAuthorizations.AsNoTracking().Where(a => a.ServerId == id)
            .Select(a => new { a.Id, a.CompanyId, Company = a.Company!.Code, a.GrantedAt, a.GrantedBy }).ToListAsync(ct);
        return Ok(new { company, users, pools, reservations, authorizations });
    }

    [HttpGet("{id:guid}/sessions"), Authorize(Permissions.SessionsRead)]
    public async Task<IActionResult> Sessions(Guid id, CancellationToken ct)
    {
        var since = time.GetUtcNow().AddMinutes(-30);
        return Ok(await db.ActiveSessions.AsNoTracking().Where(s => s.ServerId == id && s.EndedAt == null && s.LastSeenAt >= since)
            .OrderByDescending(s => s.LastSeenAt).Take(1000)
            .Join(db.UserCompanyMemberships, s => new { s.CompanyId, s.UserId }, m => new { m.CompanyId, m.UserId },
                (s, m) => new { s.SessionKey, s.CompanyId, Company = m.Company!.Code, s.UserId, m.Username, s.ApplicationId, s.StartedAt,
                    s.LastSeenAt, s.GatewayInstance, s.ClientAddress })
            .ToListAsync(ct));
    }

    [HttpGet("{id:guid}/history"), Authorize(Permissions.AuditRead)]
    public async Task<IActionResult> History(Guid id, CancellationToken ct) =>
        Ok(await db.AuditLogs.AsNoTracking().Where(a => a.ServerId == id || a.EntityId == id)
            .OrderByDescending(a => a.At).Take(500).ToListAsync(ct));

    [HttpPut("{id:guid}/reservations"), Authorize(Permissions.ServersWrite)]
    public async Task<IActionResult> SetReservation(Guid id, ReservationRequest request, CancellationToken ct)
    {
        var existing = await db.CapacityReservations.FirstOrDefaultAsync(r => r.ServerId == id && r.CompanyId == request.CompanyId, ct);
        if (request.ReservedSessions == 0)
        {
            if (existing is not null) db.CapacityReservations.Remove(existing);
        }
        else if (existing is null)
            db.CapacityReservations.Add(new CapacityReservation { ServerId = id, CompanyId = request.CompanyId, ReservedSessions = request.ReservedSessions });
        else existing.ReservedSessions = request.ReservedSessions;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Configuration a backend needs to verify gateway-signed identity and protect its health endpoint.</summary>
    [HttpGet("{id:guid}/backend-config"), Authorize(Permissions.SecretsExport)]
    public async Task<IActionResult> BackendConfig(Guid id, CancellationToken ct)
    {
        if (!await db.Servers.AnyAsync(s => s.Id == id, ct)) return NotFound();
        await audit.WriteAsync(new AuditEvent(AuditEvents.SecretsExported) { ServerId = id, Severity = AlertSeverity.Warning }, ct);
        Response.Headers.CacheControl = "no-store";
        return Ok(new
        {
            serverId = id,
            signingKey = Convert.ToBase64String(keys.GetSigningKey(id)),
            healthKey = keys.GetHealthKey(id),
            note = "Store these in the application server's secret store. They are derived from the gateway master key."
        });
    }

    private async Task<IActionResult> ChangeState(Guid id, ServerAdminState state, string eventType, Action<Server>? extra,
        CancellationToken ct, string? reason = null)
    {
        var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (server is null) return NotFound();
        if (server.AdminState == ServerAdminState.Retired && state != ServerAdminState.Retired)
            return Conflict(new { error = "retired", message = "Retired servers cannot be returned to service." });
        var previous = server.AdminState;
        server.AdminState = state;
        extra?.Invoke(server);
        audit.Add(db, new AuditEvent(eventType)
        {
            ServerId = id, EntityType = nameof(Server), EntityId = id, Previous = previous.ToString(), New = state.ToString(), Reason = reason
        });
        await db.SaveChangesAsync(ct);
        return Ok(new { id, previous = previous.ToString(), state = state.ToString() });
    }

    private async Task<bool> HasActiveAssignmentsAsync(Guid id, CancellationToken ct) =>
        (await AssignmentBlockersAsync(id, ct)).Count > 0;

    private async Task<List<string>> AssignmentBlockersAsync(Guid id, CancellationToken ct, bool includeHistory = false)
    {
        var now = time.GetUtcNow();
        var blockers = new List<string>();
        blockers.AddRange(await db.CompanyServerAssignments.AsNoTracking()
            .Where(a => (a.PrimaryServerId == id || a.SecondaryServerId == id) && (includeHistory || a.EffectiveTo == null || a.EffectiveTo > now))
            .Select(a => "Company " + a.Company!.Code + " (" + a.Environment + ")").ToListAsync(ct));
        blockers.AddRange(await db.UserServerAssignments.AsNoTracking()
            .Where(a => (a.PrimaryServerId == id || a.SecondaryServerId == id) && (includeHistory || a.EffectiveTo == null || a.EffectiveTo > now))
            .Select(a => "User assignment in " + a.Company!.Code).ToListAsync(ct));
        blockers.AddRange(await db.ServerPoolMembers.AsNoTracking().Where(m => m.ServerId == id)
            .Select(m => "Pool " + m.Pool!.Name).ToListAsync(ct));
        return blockers.Distinct().ToList();
    }

    private List<string> Validate(ServerRequest r)
    {
        var errors = new List<string>();
        if (destinations.ValidateBackendUrl(r.BaseUrl) is { IsValid: false } b) errors.Add($"Base URL: {b.Error}");
        if (r.HealthCheckUrl is { Length: > 0 } h && destinations.ValidateBackendUrl(h, r.BaseUrl) is { IsValid: false } hv)
            errors.Add($"Health check URL: {hv.Error}");
        if (r.PublicUrl is { Length: > 0 } p && destinations.ValidateRedirectUrl(p) is { IsValid: false } pv)
            errors.Add($"Public URL: {pv.Error}");
        if (r.IpAddress is { Length: > 0 } ip && !System.Net.IPAddress.TryParse(ip, out _)) errors.Add("IP address is not valid");
        return errors;
    }

    private static void Apply(Server s, ServerRequest r)
    {
        s.IpAddress = r.IpAddress;
        s.PublicUrl = r.PublicUrl;
        s.HealthCheckUrl = r.HealthCheckUrl;
        s.Environment = r.Environment;
        s.Region = r.Region;
        s.ServerType = r.ServerType;
        s.CpuCores = r.CpuCores;
        s.RamGb = r.RamGb;
        s.MaxCapacity = r.MaxCapacity;
        s.ApplicationVersion = r.ApplicationVersion;
        s.Priority = r.Priority;
        s.Weight = r.Weight;
        s.RequireManualRestore = r.RequireManualRestore;
        s.DrainTimeoutMinutes = r.DrainTimeoutMinutes;
        s.ClientCertificateThumbprint = r.ClientCertificateThumbprint;
        s.BackendCertificateThumbprint = r.BackendCertificateThumbprint;
        s.Notes = r.Notes;
    }

    private static object ToDto(Server s, ServerHealthCurrent? h, List<string> pools) => new
    {
        s.Id, s.Name, s.Hostname, s.IpAddress, s.BaseUrl, s.PublicUrl, s.HealthCheckUrl, Environment = s.Environment.ToString(), s.Region,
        ServerType = s.ServerType.ToString(), s.CpuCores, s.RamGb, s.MaxCapacity, s.ApplicationVersion, AdminState = s.AdminState.ToString(),
        Status = DisplayStatus(s.AdminState, h?.State), s.Priority, s.Weight, s.RequireManualRestore, s.DrainStartedAt, s.DrainTimeoutMinutes,
        s.ClientCertificateThumbprint, s.BackendCertificateThumbprint, s.Notes, s.CreatedAt, s.ModifiedAt, s.ModifiedBy, s.RetiredAt,
        s.ConcurrencyStamp, ApplicationIds = s.Applications.Select(a => a.ApplicationId).ToList(), Pools = pools,
        Health = (h?.State ?? HealthState.Unknown).ToString(), LastHeartbeat = h?.LastHeartbeatAt, h?.LastCheckAt, h?.LastReason,
        h?.ResponseTimeMs, h?.CpuPercent, h?.MemoryPercent, h?.ActiveRequests, h?.ActiveSessions, h?.ErrorRatePercent,
        ReportedVersion = h?.ReportedVersion, AwaitingManualRestore = h?.AwaitingManualRestore ?? false
    };

    /// <summary>The single "Server Status" column: Online / Degraded / Offline / Maintenance.</summary>
    internal static string DisplayStatus(ServerAdminState admin, HealthState? health) => admin switch
    {
        ServerAdminState.Retired => "Retired",
        ServerAdminState.Maintenance or ServerAdminState.Draining => "Maintenance",
        ServerAdminState.Disabled => "Offline",
        _ => health switch
        {
            HealthState.Healthy => "Online",
            HealthState.Degraded or HealthState.Recovering => "Degraded",
            HealthState.Unhealthy => "Offline",
            _ => "Unknown"
        }
    };
}
