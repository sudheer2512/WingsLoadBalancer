using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wings.LoadBalancer.Data;
using Wings.LoadBalancer.Data.Entities;
using Wings.Routing;
using Wings.Routing.Model;

namespace Wings.Admin.Api.Controllers;

public sealed record PoolRequest
{
    [Required, MaxLength(100)] public required string Name { get; init; }
    [MaxLength(1000)] public string? Description { get; init; }
    public DeploymentEnvironment Environment { get; init; }
    [MaxLength(100)] public string? Region { get; init; }
    public LoadBalancingAlgorithm Algorithm { get; init; }
    public AffinityMode Affinity { get; init; } = AffinityMode.PerSession;
    public Guid? FailoverPolicyId { get; init; }
    [Range(0, 10_000_000)] public int? MaxCapacity { get; init; }
    public List<string> EligiblePlans { get; init; } = [];
    public List<Guid> ApplicationIds { get; init; } = [];
    public Guid? ConcurrencyStamp { get; init; }
}

public sealed record PoolMemberRequest(Guid ServerId, [Range(0, 10_000)] int? WeightOverride, [Range(0, 1000)] int Priority, bool AcceptingNewSessions = true);

[Route("api/pools")]
public sealed class PoolsController(WingsDbContext db, TimeProvider time) : AdminControllerBase
{
    [HttpGet, Authorize(Permissions.PoolsRead)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var pools = await db.ServerPools.AsNoTracking().Include(p => p.Members).ThenInclude(m => m.Server).Include(p => p.Applications)
            .Include(p => p.FailoverPolicy).OrderBy(p => p.Name).ToListAsync(ct);
        var health = await db.ServerHealthCurrent.AsNoTracking().ToDictionaryAsync(h => h.ServerId, h => h.State, ct);
        var now = time.GetUtcNow();
        var tenants = await db.SharedPoolAssignments.AsNoTracking()
            .Where(a => a.IsEnabled && a.EffectiveFrom <= now && (a.EffectiveTo == null || a.EffectiveTo > now))
            .GroupBy(a => a.PoolId).Select(g => new { g.Key, Count = g.Select(a => a.CompanyId).Distinct().Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return Ok(pools.Select(p => new
        {
            p.Id, p.Name, p.Description, Environment = p.Environment.ToString(), p.Region, Algorithm = p.Algorithm.ToString(),
            Affinity = p.Affinity.ToString(), p.FailoverPolicyId, FailoverPolicy = p.FailoverPolicy?.Name, p.MaxCapacity,
            EligiblePlans = Split(p.EligiblePlans), ApplicationIds = p.Applications.Select(a => a.ApplicationId), p.ConcurrencyStamp,
            Tenants = tenants.GetValueOrDefault(p.Id),
            Members = p.Members.OrderBy(m => m.Priority).Select(m => new
            {
                m.Id, m.ServerId, Server = m.Server!.Name, m.WeightOverride, EffectiveWeight = m.WeightOverride ?? m.Server.Weight, m.Priority,
                m.AcceptingNewSessions, AdminState = m.Server.AdminState.ToString(),
                Health = (health.TryGetValue(m.ServerId, out var h) ? h : HealthState.Unknown).ToString()
            })
        }));
    }

    [HttpPost, Authorize(Permissions.PoolsWrite)]
    public async Task<IActionResult> Create(PoolRequest request, CancellationToken ct)
    {
        if (await db.ServerPools.AnyAsync(p => p.Name == request.Name, ct)) return Conflict(new { error = "duplicate_name" });
        var pool = new ServerPool { Name = request.Name };
        Apply(pool, request);
        db.ServerPools.Add(pool);
        await db.SaveChangesAsync(ct);
        return Ok(new { pool.Id });
    }

    [HttpPut("{id:guid}"), Authorize(Permissions.PoolsWrite)]
    public async Task<IActionResult> Update(Guid id, PoolRequest request, CancellationToken ct)
    {
        var pool = await db.ServerPools.Include(p => p.Applications).Include(p => p.Members).ThenInclude(m => m.Server)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pool is null) return NotFound();
        if (request.ConcurrencyStamp is { } stamp) db.Entry(pool).Property(p => p.ConcurrencyStamp).OriginalValue = stamp;
        if (pool.Environment != request.Environment && pool.Members.Any(m => m.Server!.Environment != request.Environment))
            return BadRequest(new { error = "validation_failed", errors = new[] { "All pool members must be in the pool's environment" } });
        pool.Name = request.Name;
        Apply(pool, request);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}"), Authorize(Permissions.PoolsWrite)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var inUse = await db.SharedPoolAssignments.AnyAsync(a => a.PoolId == id, ct)
                    || await db.CompanyServerAssignments.AnyAsync(a => a.ClusterPoolId == id, ct)
                    || await db.FailoverPolicies.AnyAsync(f => f.FailoverPoolId == id, ct);
        if (inUse) return Conflict(new { error = "in_use", message = "The pool is referenced by assignments or failover policies." });
        var pool = await db.ServerPools.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pool is null) return NotFound();
        db.ServerPools.Remove(pool);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Add or update a member (a server may belong to several pools when environment and isolation allow).</summary>
    [HttpPut("{id:guid}/members"), Authorize(Permissions.PoolsWrite)]
    public async Task<IActionResult> UpsertMember(Guid id, PoolMemberRequest request, CancellationToken ct)
    {
        var pool = await db.ServerPools.Include(p => p.Members).FirstOrDefaultAsync(p => p.Id == id, ct);
        var server = await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.ServerId, ct);
        if (pool is null || server is null) return NotFound();
        var errors = new List<string>();
        if (server.Environment != pool.Environment) errors.Add($"{server.Name} is in {server.Environment}; the pool is {pool.Environment}");
        if (server.AdminState == ServerAdminState.Retired) errors.Add($"{server.Name} is retired");
        var isSharedPool = await db.SharedPoolAssignments.AnyAsync(a => a.PoolId == id, ct);
        if (server.ServerType == ServerType.Dedicated && isSharedPool)
            errors.Add($"{server.Name} is a dedicated server and cannot join a shared tenant pool (tenant isolation)");
        if (errors.Count > 0) return BadRequest(new { error = "validation_failed", errors });

        var member = pool.Members.FirstOrDefault(m => m.ServerId == request.ServerId);
        if (member is null)
        {
            member = new ServerPoolMember { PoolId = id, ServerId = request.ServerId, AddedAt = time.GetUtcNow() };
            db.ServerPoolMembers.Add(member);
        }
        member.WeightOverride = request.WeightOverride;
        member.Priority = request.Priority;
        member.AcceptingNewSessions = request.AcceptingNewSessions;
        pool.ConcurrencyStamp = Guid.NewGuid();
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/members/{serverId:guid}"), Authorize(Permissions.PoolsWrite)]
    public async Task<IActionResult> RemoveMember(Guid id, Guid serverId, CancellationToken ct)
    {
        var member = await db.ServerPoolMembers.FirstOrDefaultAsync(m => m.PoolId == id && m.ServerId == serverId, ct);
        if (member is null) return NotFound();
        db.ServerPoolMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static void Apply(ServerPool p, PoolRequest r)
    {
        p.Description = r.Description;
        p.Environment = r.Environment;
        p.Region = r.Region;
        p.Algorithm = r.Algorithm;
        p.Affinity = r.Affinity;
        p.FailoverPolicyId = r.FailoverPolicyId;
        p.MaxCapacity = r.MaxCapacity;
        p.EligiblePlans = r.EligiblePlans.Count == 0 ? null : string.Join(',', r.EligiblePlans.Select(x => x.Trim()).Where(x => x.Length > 0));
        var wanted = r.ApplicationIds.ToHashSet();
        p.Applications.RemoveAll(a => !wanted.Contains(a.ApplicationId));
        foreach (var appId in wanted.Where(a => p.Applications.All(x => x.ApplicationId != a)))
            p.Applications.Add(new ServerPoolApplication { PoolId = p.Id, ApplicationId = appId });
    }

    private static string[] Split(string? s) => string.IsNullOrWhiteSpace(s) ? [] : s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

public sealed record FailoverPolicyRequest
{
    [Required, MaxLength(100)] public required string Name { get; init; }
    [MaxLength(1000)] public string? Description { get; init; }
    public bool FailoverEnabled { get; init; }
    public Guid? FailoverPoolId { get; init; }
    public FailbackPolicy FailbackPolicy { get; init; }
    [Range(0, 50)] public int MaxFailoverAttempts { get; init; } = 3;
    [Range(0, 1000)] public int FailoverPriority { get; init; }
    public Guid? ConcurrencyStamp { get; init; }
}

public sealed record RoutingPolicyRequest
{
    [Required, MaxLength(100)] public required string Name { get; init; }
    public bool IsDefault { get; init; }
    public bool AllowDegradedServers { get; init; } = true;
    public AffinityMode DefaultAffinity { get; init; } = AffinityMode.PerSession;
    [Range(0, 1)] public double CpuWeight { get; init; } = 0.30;
    [Range(0, 1)] public double MemoryWeight { get; init; } = 0.20;
    [Range(0, 1)] public double ActiveRequestsWeight { get; init; } = 0.25;
    [Range(0, 1)] public double ResponseTimeWeight { get; init; } = 0.15;
    [Range(0, 1)] public double ErrorRateWeight { get; init; } = 0.10;
    [Range(100, 60_000)] public double ResponseTimeCeilingMs { get; init; } = 2000;
    [Range(1, 1440)] public int SessionIdleTimeoutMinutes { get; init; } = 30;
    [Range(1, 168)] public int SessionAbsoluteTimeoutHours { get; init; } = 12;
    public Guid? ConcurrencyStamp { get; init; }
}

[Route("api")]
public sealed class PoliciesController(WingsDbContext db) : AdminControllerBase
{
    [HttpGet("failover-policies"), Authorize(Permissions.PoolsRead)]
    public async Task<IActionResult> FailoverPolicies(CancellationToken ct) =>
        Ok(await db.FailoverPolicies.AsNoTracking().Include(f => f.FailoverPool).OrderBy(f => f.Name).Select(f => new
        {
            f.Id, f.Name, f.Description, f.FailoverEnabled, f.FailoverPoolId, FailoverPool = f.FailoverPool!.Name,
            FailbackPolicy = f.FailbackPolicy.ToString(), f.MaxFailoverAttempts, f.FailoverPriority, f.ConcurrencyStamp
        }).ToListAsync(ct));

    [HttpPost("failover-policies"), Authorize(Permissions.PoliciesWrite)]
    public async Task<IActionResult> CreateFailoverPolicy(FailoverPolicyRequest r, CancellationToken ct)
    {
        var policy = new FailoverPolicy { Name = r.Name };
        Apply(policy, r);
        db.FailoverPolicies.Add(policy);
        await db.SaveChangesAsync(ct);
        return Ok(new { policy.Id });
    }

    [HttpPut("failover-policies/{id:guid}"), Authorize(Permissions.PoliciesWrite)]
    public async Task<IActionResult> UpdateFailoverPolicy(Guid id, FailoverPolicyRequest r, CancellationToken ct)
    {
        var policy = await db.FailoverPolicies.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (policy is null) return NotFound();
        if (r.ConcurrencyStamp is { } stamp) db.Entry(policy).Property(p => p.ConcurrencyStamp).OriginalValue = stamp;
        policy.Name = r.Name;
        Apply(policy, r);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("failover-policies/{id:guid}"), Authorize(Permissions.PoliciesWrite)]
    public async Task<IActionResult> DeleteFailoverPolicy(Guid id, CancellationToken ct)
    {
        var inUse = await db.CompanyServerAssignments.AnyAsync(a => a.FailoverPolicyId == id, ct)
                    || await db.UserServerAssignments.AnyAsync(a => a.FailoverPolicyId == id, ct)
                    || await db.ServerPools.AnyAsync(p => p.FailoverPolicyId == id, ct);
        if (inUse) return Conflict(new { error = "in_use", message = "The policy is referenced by assignments or pools (including history)." });
        var policy = await db.FailoverPolicies.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (policy is null) return NotFound();
        db.FailoverPolicies.Remove(policy);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("routing-policies"), Authorize(Permissions.PoolsRead)]
    public async Task<IActionResult> RoutingPolicies(CancellationToken ct) =>
        Ok(await db.RoutingPolicies.AsNoTracking().OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name).ToListAsync(ct));

    [HttpPost("routing-policies"), Authorize(Permissions.PoliciesWrite)]
    public async Task<IActionResult> CreateRoutingPolicy(RoutingPolicyRequest r, CancellationToken ct)
    {
        var policy = new RoutingPolicy { Name = r.Name };
        await ApplyAsync(policy, r, ct);
        db.RoutingPolicies.Add(policy);
        await db.SaveChangesAsync(ct);
        return Ok(new { policy.Id });
    }

    /// <summary>Includes the configurable load-score metric weights.</summary>
    [HttpPut("routing-policies/{id:guid}"), Authorize(Permissions.PoliciesWrite)]
    public async Task<IActionResult> UpdateRoutingPolicy(Guid id, RoutingPolicyRequest r, CancellationToken ct)
    {
        var policy = await db.RoutingPolicies.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (policy is null) return NotFound();
        if (r.ConcurrencyStamp is { } stamp) db.Entry(policy).Property(p => p.ConcurrencyStamp).OriginalValue = stamp;
        policy.Name = r.Name;
        await ApplyAsync(policy, r, ct);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("routing-policies/{id:guid}"), Authorize(Permissions.PoliciesWrite)]
    public async Task<IActionResult> DeleteRoutingPolicy(Guid id, CancellationToken ct)
    {
        var policy = await db.RoutingPolicies.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (policy is null) return NotFound();
        if (policy.IsDefault) return Conflict(new { error = "default_policy", message = "Make another policy the default first." });
        if (await db.Companies.AnyAsync(c => c.RoutingPolicyId == id, ct))
            return Conflict(new { error = "in_use", message = "Companies use this policy." });
        db.RoutingPolicies.Remove(policy);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static void Apply(FailoverPolicy p, FailoverPolicyRequest r)
    {
        p.Description = r.Description;
        p.FailoverEnabled = r.FailoverEnabled;
        p.FailoverPoolId = r.FailoverPoolId;
        p.FailbackPolicy = r.FailbackPolicy;
        p.MaxFailoverAttempts = r.MaxFailoverAttempts;
        p.FailoverPriority = r.FailoverPriority;
    }

    private async Task ApplyAsync(RoutingPolicy p, RoutingPolicyRequest r, CancellationToken ct)
    {
        if (r.IsDefault)
            foreach (var other in await db.RoutingPolicies.Where(x => x.IsDefault && x.Id != p.Id).ToListAsync(ct)) other.IsDefault = false;
        p.IsDefault = r.IsDefault;
        p.AllowDegradedServers = r.AllowDegradedServers;
        p.DefaultAffinity = r.DefaultAffinity;
        p.CpuWeight = r.CpuWeight;
        p.MemoryWeight = r.MemoryWeight;
        p.ActiveRequestsWeight = r.ActiveRequestsWeight;
        p.ResponseTimeWeight = r.ResponseTimeWeight;
        p.ErrorRateWeight = r.ErrorRateWeight;
        p.ResponseTimeCeilingMs = r.ResponseTimeCeilingMs;
        p.SessionIdleTimeoutMinutes = r.SessionIdleTimeoutMinutes;
        p.SessionAbsoluteTimeoutHours = r.SessionAbsoluteTimeoutHours;
    }
}

public sealed record ApplicationRequest
{
    [Required, MaxLength(40)] public required string Code { get; init; }
    [Required, MaxLength(200)] public required string Name { get; init; }
    [MaxLength(1000)] public string? Description { get; init; }
    public bool IsEnabled { get; init; } = true;
    [MaxLength(50)] public string? MinimumServerVersion { get; init; }
    public Guid? ConcurrencyStamp { get; init; }
}

public sealed record ApplicationEnvironmentRequest
{
    public DeploymentEnvironment Environment { get; init; }
    [Required, MaxLength(253)] public required string PublicHost { get; init; }
    [MaxLength(200)] public string PathPrefix { get; init; } = "/";
    public ApplicationRoutingMode RoutingMode { get; init; }
    public bool IsEnabled { get; init; } = true;
    [Range(1, 3600)] public int RequestTimeoutSeconds { get; init; } = 100;
    [Range(1, 10L * 1024 * 1024 * 1024)] public long? MaxRequestBodyBytes { get; init; }
}

[Route("api/applications")]
public sealed class ApplicationsController(WingsDbContext db) : AdminControllerBase
{
    [HttpGet, Authorize(Permissions.ServersRead)]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(await db.Applications.AsNoTracking().Include(a => a.Environments).OrderBy(a => a.Code).Select(a => new
        {
            a.Id, a.Code, a.Name, a.Description, a.IsEnabled, a.MinimumServerVersion, a.ConcurrencyStamp,
            Environments = a.Environments.Select(e => new
            {
                e.Id, Environment = e.Environment.ToString(), e.PublicHost, e.PathPrefix, RoutingMode = e.RoutingMode.ToString(),
                e.IsEnabled, e.RequestTimeoutSeconds, e.MaxRequestBodyBytes
            })
        }).ToListAsync(ct));

    [HttpPost, Authorize(Permissions.PoliciesWrite)]
    public async Task<IActionResult> Create(ApplicationRequest r, CancellationToken ct)
    {
        var code = r.Code.Trim().ToUpperInvariant();
        if (await db.Applications.AnyAsync(a => a.Code == code, ct)) return Conflict(new { error = "duplicate_code" });
        var app = new Application { Code = code, Name = r.Name, Description = r.Description, IsEnabled = r.IsEnabled, MinimumServerVersion = r.MinimumServerVersion };
        db.Applications.Add(app);
        await db.SaveChangesAsync(ct);
        return Ok(new { app.Id });
    }

    [HttpPut("{id:guid}"), Authorize(Permissions.PoliciesWrite)]
    public async Task<IActionResult> Update(Guid id, ApplicationRequest r, CancellationToken ct)
    {
        var app = await db.Applications.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (app is null) return NotFound();
        if (r.ConcurrencyStamp is { } stamp) db.Entry(app).Property(a => a.ConcurrencyStamp).OriginalValue = stamp;
        app.Name = r.Name;
        app.Description = r.Description;
        app.IsEnabled = r.IsEnabled;
        app.MinimumServerVersion = r.MinimumServerVersion;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}"), Authorize(Permissions.PoliciesWrite)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var inUse = await db.ServerApplications.AnyAsync(a => a.ApplicationId == id, ct)
                    || await db.CompanyServerAssignments.AnyAsync(a => a.ApplicationId == id, ct)
                    || await db.UserServerAssignments.AnyAsync(a => a.ApplicationId == id, ct)
                    || await db.SharedPoolAssignments.AnyAsync(a => a.ApplicationId == id, ct);
        if (inUse) return Conflict(new { error = "in_use", message = "Servers or assignments reference this application; disable it instead." });
        var app = await db.Applications.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (app is null) return NotFound();
        db.Applications.Remove(app);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/environments"), Authorize(Permissions.PoliciesWrite)]
    public async Task<IActionResult> AddEnvironment(Guid id, ApplicationEnvironmentRequest r, CancellationToken ct)
    {
        var host = IdentifierNormalizer.NormalizeHost(r.PublicHost);
        var prefix = NormalizePrefix(r.PathPrefix);
        if (await db.ApplicationEnvironments.AnyAsync(e => e.PublicHost == host && e.PathPrefix == prefix, ct))
            return Conflict(new { error = "duplicate_binding", message = $"{host}{prefix} is already bound" });
        var binding = new ApplicationEnvironment
        {
            ApplicationId = id, Environment = r.Environment, PublicHost = host, PathPrefix = prefix, RoutingMode = r.RoutingMode,
            IsEnabled = r.IsEnabled, RequestTimeoutSeconds = r.RequestTimeoutSeconds, MaxRequestBodyBytes = r.MaxRequestBodyBytes
        };
        db.ApplicationEnvironments.Add(binding);
        await db.SaveChangesAsync(ct);
        return Ok(new { binding.Id });
    }

    [HttpPut("{id:guid}/environments/{bindingId:guid}"), Authorize(Permissions.PoliciesWrite)]
    public async Task<IActionResult> UpdateEnvironment(Guid id, Guid bindingId, ApplicationEnvironmentRequest r, CancellationToken ct)
    {
        var binding = await db.ApplicationEnvironments.FirstOrDefaultAsync(e => e.Id == bindingId && e.ApplicationId == id, ct);
        if (binding is null) return NotFound();
        binding.Environment = r.Environment;
        binding.PublicHost = IdentifierNormalizer.NormalizeHost(r.PublicHost);
        binding.PathPrefix = NormalizePrefix(r.PathPrefix);
        binding.RoutingMode = r.RoutingMode;
        binding.IsEnabled = r.IsEnabled;
        binding.RequestTimeoutSeconds = r.RequestTimeoutSeconds;
        binding.MaxRequestBodyBytes = r.MaxRequestBodyBytes;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/environments/{bindingId:guid}"), Authorize(Permissions.PoliciesWrite)]
    public async Task<IActionResult> DeleteEnvironment(Guid id, Guid bindingId, CancellationToken ct)
    {
        var binding = await db.ApplicationEnvironments.FirstOrDefaultAsync(e => e.Id == bindingId && e.ApplicationId == id, ct);
        if (binding is null) return NotFound();
        db.ApplicationEnvironments.Remove(binding);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static string NormalizePrefix(string? prefix)
    {
        var p = string.IsNullOrWhiteSpace(prefix) ? "/" : prefix.Trim();
        if (!p.StartsWith('/')) p = "/" + p;
        return p.Length > 1 ? p.TrimEnd('/') : p;
    }
}
