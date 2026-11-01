using Microsoft.EntityFrameworkCore;
using Wings.LoadBalancer.Data;
using Wings.LoadBalancer.Data.Entities;
using Wings.LoadBalancer.Infrastructure.Auditing;
using Wings.LoadBalancer.Infrastructure.Caching;
using Wings.LoadBalancer.Infrastructure.Sessions;
using Wings.Routing.Model;

namespace Wings.Admin.Api.Services;

public sealed class ValidationFailedException(IReadOnlyList<string> errors) : Exception(string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public sealed class ConflictException(string message) : Exception(message);

public sealed record UserAssignmentRequest
{
    public required Guid CompanyId { get; init; }
    public required Guid PrimaryServerId { get; init; }
    public Guid? SecondaryServerId { get; init; }
    public Guid? FailoverPolicyId { get; init; }
    public Guid? ApplicationId { get; init; }
    public DeploymentEnvironment Environment { get; init; } = DeploymentEnvironment.Production;
    public bool AllowCompanyRouteFallback { get; init; }
    public bool IsEnabled { get; init; } = true;
    /// <summary>Future value schedules the change; null means now.</summary>
    public DateTimeOffset? EffectiveFrom { get; init; }
    public DateTimeOffset? EffectiveTo { get; init; }
    public string? Reason { get; init; }
    /// <summary>Concurrency stamp of the membership as last read by the client.</summary>
    public Guid? ExpectedStamp { get; init; }
}

public sealed record CompanyAssignmentRequest
{
    public required Guid PrimaryServerId { get; init; }
    public Guid? SecondaryServerId { get; init; }
    public Guid? ClusterPoolId { get; init; }
    public ClusterMode ClusterMode { get; init; }
    public Guid? FailoverPolicyId { get; init; }
    public Guid? ApplicationId { get; init; }
    public DeploymentEnvironment Environment { get; init; } = DeploymentEnvironment.Production;
    public DateTimeOffset? EffectiveFrom { get; init; }
    public DateTimeOffset? EffectiveTo { get; init; }
    public string? Reason { get; init; }
    public Guid? ExpectedStamp { get; init; }
}

public sealed record SharedPoolAssignmentRequest
{
    public required Guid PoolId { get; init; }
    public Guid? ApplicationId { get; init; }
    public DeploymentEnvironment Environment { get; init; } = DeploymentEnvironment.Production;
    public int Priority { get; init; }
    public DateTimeOffset? EffectiveFrom { get; init; }
    public DateTimeOffset? EffectiveTo { get; init; }
    public string? Reason { get; init; }
    /// <summary>When true, ends the company's dedicated assignment in the same scope.</summary>
    public bool ReplaceDedicated { get; init; }
    public Guid? ExpectedStamp { get; init; }
}

public enum CutoverMode { Immediate, Scheduled, ForceReauthentication }

public sealed record TransferRequest
{
    public required Guid TargetServerId { get; init; }
    public Guid? TargetSecondaryServerId { get; init; }
    public DeploymentEnvironment Environment { get; init; } = DeploymentEnvironment.Production;
    public Guid? ApplicationId { get; init; }
    public CutoverMode Mode { get; init; }
    public DateTimeOffset? ScheduledAt { get; init; }
    public bool ValidateOnly { get; init; }
    public string? Reason { get; init; }
    public Guid? ExpectedStamp { get; init; }
}

public sealed record TransferCheck(string Check, bool Passed, string Detail);

/// <summary>
/// All assignment writes go through here so validation (environment, application compatibility, tenant isolation,
/// data authorisation, effective windows) and optimistic concurrency are applied uniformly.
/// </summary>
public sealed class AssignmentService(WingsDbContext db, IAuditTrail audit, IStateStore store, TimeProvider time)
{
    // ---------------------------------------------------------------------------------------------------------------
    // User assignments
    // ---------------------------------------------------------------------------------------------------------------

    public async Task<UserServerAssignment> SetUserAssignmentAsync(Guid userId, UserAssignmentRequest r, string actor, CancellationToken ct)
    {
        var membership = await db.UserCompanyMemberships.FirstOrDefaultAsync(m => m.UserId == userId && m.CompanyId == r.CompanyId, ct)
            ?? throw new KeyNotFoundException("User is not a member of this company");
        if (r.ExpectedStamp is { } stamp && stamp != membership.ConcurrencyStamp)
            throw new ConflictException("The user's assignment was changed by someone else. Reload and try again.");

        var now = time.GetUtcNow();
        var from = r.EffectiveFrom ?? now;
        var errors = await ValidateUserAssignmentAsync(r, from, ct);
        if (errors.Count > 0) throw new ValidationFailedException(errors);

        var current = await CurrentUserAssignmentsAsync(r.CompanyId, userId, r.Environment, r.ApplicationId, from, ct);
        var assignment = new UserServerAssignment
        {
            CompanyId = r.CompanyId, UserId = userId, ApplicationId = r.ApplicationId, Environment = r.Environment,
            PrimaryServerId = r.PrimaryServerId, SecondaryServerId = r.SecondaryServerId, FailoverPolicyId = r.FailoverPolicyId,
            AllowCompanyRouteFallback = r.AllowCompanyRouteFallback, IsEnabled = r.IsEnabled, EffectiveFrom = from,
            EffectiveTo = r.EffectiveTo, Reason = r.Reason, CreatedAt = now, CreatedBy = actor
        };
        foreach (var c in current)
        {
            c.EffectiveTo = from;
            c.SupersededById = assignment.Id;
        }
        db.UserServerAssignments.Add(assignment);
        membership.ConcurrencyStamp = Guid.NewGuid(); // serialises concurrent edits of this user's assignments

        audit.Add(db, new AuditEvent(AuditEvents.UserAssignmentChanged)
        {
            CompanyId = r.CompanyId, UserId = userId, ServerId = r.PrimaryServerId, EntityType = nameof(UserServerAssignment),
            EntityId = assignment.Id, Previous = current.Select(Describe).ToArray(), New = Describe(assignment), Reason = r.Reason
        });
        await SaveAsync(ct);
        return assignment;
    }

    public async Task RemoveUserAssignmentAsync(Guid userId, Guid companyId, DeploymentEnvironment environment, Guid? applicationId,
        string? reason, Guid? expectedStamp, CancellationToken ct)
    {
        var membership = await db.UserCompanyMemberships.FirstOrDefaultAsync(m => m.UserId == userId && m.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException();
        if (expectedStamp is { } stamp && stamp != membership.ConcurrencyStamp)
            throw new ConflictException("The user's assignment was changed by someone else. Reload and try again.");
        var now = time.GetUtcNow();
        var current = await CurrentUserAssignmentsAsync(companyId, userId, environment, applicationId, now, ct);
        if (current.Count == 0) throw new KeyNotFoundException("No current user-level assignment");
        foreach (var c in current) c.EffectiveTo = now;
        membership.ConcurrencyStamp = Guid.NewGuid();
        audit.Add(db, new AuditEvent(AuditEvents.UserAssignmentChanged)
        {
            CompanyId = companyId, UserId = userId, Previous = current.Select(Describe).ToArray(), New = "removed (company route applies)",
            Reason = reason
        });
        await SaveAsync(ct);
    }

    public async Task<UserServerAssignment> RollbackUserAssignmentAsync(Guid userId, Guid historicalAssignmentId, string actor,
        string? reason, CancellationToken ct)
    {
        var old = await db.UserServerAssignments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == historicalAssignmentId && a.UserId == userId, ct)
            ?? throw new KeyNotFoundException("Assignment not found in this user's history");
        var restored = await SetUserAssignmentAsync(userId, new UserAssignmentRequest
        {
            CompanyId = old.CompanyId, PrimaryServerId = old.PrimaryServerId, SecondaryServerId = old.SecondaryServerId,
            FailoverPolicyId = old.FailoverPolicyId, ApplicationId = old.ApplicationId, Environment = old.Environment,
            AllowCompanyRouteFallback = old.AllowCompanyRouteFallback, IsEnabled = true,
            Reason = reason ?? $"Rollback to assignment {old.Id}"
        }, actor, ct);
        await audit.WriteAsync(new AuditEvent(AuditEvents.UserAssignmentRolledBack)
        {
            CompanyId = old.CompanyId, UserId = userId, EntityId = restored.Id, Previous = historicalAssignmentId.ToString(), Reason = reason
        }, ct);
        return restored;
    }

    public async Task<List<string>> ValidateUserAssignmentAsync(UserAssignmentRequest r, DateTimeOffset from, CancellationToken ct)
    {
        var errors = new List<string>();
        if (r.EffectiveTo is { } to && to <= from) errors.Add("EffectiveTo must be after EffectiveFrom");
        if (r.SecondaryServerId == r.PrimaryServerId) errors.Add("Secondary server must differ from the primary");
        await ValidateFailoverPolicyAsync(r.FailoverPolicyId, errors, ct);
        await ValidateApplicationAsync(r.ApplicationId, errors, ct);

        var authorized = await AuthorizedServersAsync(r.CompanyId, ct);
        foreach (var (serverId, role) in Servers(r.PrimaryServerId, r.SecondaryServerId))
        {
            await ValidateServerAsync(serverId, role, r.CompanyId, r.Environment, r.ApplicationId, errors, ct);
            if (!authorized.Contains(serverId))
                errors.Add($"{role} server is not authorised for this company's data. Grant it on the company first.");
        }
        return errors;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Company assignments
    // ---------------------------------------------------------------------------------------------------------------

    public async Task<CompanyServerAssignment> SetCompanyAssignmentAsync(Guid companyId, CompanyAssignmentRequest r, string actor,
        CancellationToken ct, string eventType = AuditEvents.CompanyAssignmentChanged)
    {
        var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct) ?? throw new KeyNotFoundException();
        if (r.ExpectedStamp is { } stamp && stamp != company.ConcurrencyStamp)
            throw new ConflictException("The company was changed by someone else. Reload and try again.");
        var now = time.GetUtcNow();
        var from = r.EffectiveFrom ?? now;
        var errors = await ValidateCompanyAssignmentAsync(companyId, r, from, ct);
        if (errors.Count > 0) throw new ValidationFailedException(errors);

        var current = await db.CompanyServerAssignments
            .Where(a => a.CompanyId == companyId && a.Environment == r.Environment && a.ApplicationId == r.ApplicationId
                        && a.IsEnabled && (a.EffectiveTo == null || a.EffectiveTo > from))
            .ToListAsync(ct);
        var assignment = new CompanyServerAssignment
        {
            CompanyId = companyId, ApplicationId = r.ApplicationId, Environment = r.Environment, PrimaryServerId = r.PrimaryServerId,
            SecondaryServerId = r.SecondaryServerId, ClusterPoolId = r.ClusterPoolId, ClusterMode = r.ClusterMode,
            FailoverPolicyId = r.FailoverPolicyId, EffectiveFrom = from, EffectiveTo = r.EffectiveTo, Reason = r.Reason,
            CreatedAt = now, CreatedBy = actor
        };
        foreach (var c in current)
        {
            c.EffectiveTo = from;
            c.SupersededById = assignment.Id;
        }
        db.CompanyServerAssignments.Add(assignment);
        company.RoutingMode = r.ClusterPoolId is null ? CompanyRoutingMode.Dedicated : CompanyRoutingMode.Cluster;

        audit.Add(db, new AuditEvent(eventType)
        {
            CompanyId = companyId, ServerId = r.PrimaryServerId, EntityType = nameof(CompanyServerAssignment), EntityId = assignment.Id,
            Previous = current.Select(Describe).ToArray(), New = Describe(assignment), Reason = r.Reason
        });
        await SaveAsync(ct);
        return assignment;
    }

    public async Task<List<string>> ValidateCompanyAssignmentAsync(Guid companyId, CompanyAssignmentRequest r, DateTimeOffset from, CancellationToken ct)
    {
        var errors = new List<string>();
        if (r.EffectiveTo is { } to && to <= from) errors.Add("EffectiveTo must be after EffectiveFrom");
        if (r.SecondaryServerId == r.PrimaryServerId) errors.Add("Secondary server must differ from the primary");
        await ValidateFailoverPolicyAsync(r.FailoverPolicyId, errors, ct);
        await ValidateApplicationAsync(r.ApplicationId, errors, ct);
        foreach (var (serverId, role) in Servers(r.PrimaryServerId, r.SecondaryServerId))
            await ValidateServerAsync(serverId, role, companyId, r.Environment, r.ApplicationId, errors, ct);
        if (r.ClusterPoolId is { } poolId)
        {
            var pool = await db.ServerPools.Include(p => p.Members).AsNoTracking().FirstOrDefaultAsync(p => p.Id == poolId, ct);
            if (pool is null) errors.Add("Cluster pool not found");
            else
            {
                if (pool.Environment != r.Environment) errors.Add($"Cluster pool is for {pool.Environment}");
                if (r.ClusterMode == ClusterMode.Balanced && pool.Members.All(m => m.ServerId != r.PrimaryServerId))
                    errors.Add("In balanced mode the primary must be a member of the cluster pool");
                foreach (var m in pool.Members)
                    await ValidateServerAsync(m.ServerId, "Cluster member", companyId, r.Environment, r.ApplicationId, errors, ct);
            }
        }
        return errors;
    }

    public async Task<SharedPoolAssignment> AssignSharedPoolAsync(Guid companyId, SharedPoolAssignmentRequest r, string actor, CancellationToken ct)
    {
        var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct) ?? throw new KeyNotFoundException();
        if (r.ExpectedStamp is { } stamp && stamp != company.ConcurrencyStamp)
            throw new ConflictException("The company was changed by someone else. Reload and try again.");
        var pool = await db.ServerPools.AsNoTracking().FirstOrDefaultAsync(p => p.Id == r.PoolId, ct);
        var errors = new List<string>();
        if (pool is null) errors.Add("Pool not found");
        else
        {
            if (pool.Environment != r.Environment) errors.Add($"Pool '{pool.Name}' is for {pool.Environment}");
            if (!string.IsNullOrWhiteSpace(pool.EligiblePlans) && (company.ServicePlan is null ||
                !pool.EligiblePlans.Split(',', StringSplitOptions.TrimEntries).Contains(company.ServicePlan, StringComparer.OrdinalIgnoreCase)))
                errors.Add($"Service plan '{company.ServicePlan}' is not eligible for pool '{pool.Name}'");
        }
        await ValidateApplicationAsync(r.ApplicationId, errors, ct);
        if (errors.Count > 0) throw new ValidationFailedException(errors);

        var now = time.GetUtcNow();
        var from = r.EffectiveFrom ?? now;
        var assignment = new SharedPoolAssignment
        {
            CompanyId = companyId, PoolId = r.PoolId, ApplicationId = r.ApplicationId, Environment = r.Environment, Priority = r.Priority,
            EffectiveFrom = from, EffectiveTo = r.EffectiveTo, Reason = r.Reason, CreatedAt = now, CreatedBy = actor
        };
        db.SharedPoolAssignments.Add(assignment);
        if (r.ReplaceDedicated)
        {
            foreach (var c in await db.CompanyServerAssignments.Where(a => a.CompanyId == companyId && a.Environment == r.Environment
                         && a.ApplicationId == r.ApplicationId && (a.EffectiveTo == null || a.EffectiveTo > from)).ToListAsync(ct))
                c.EffectiveTo = from;
            company.RoutingMode = CompanyRoutingMode.SharedPool;
        }
        audit.Add(db, new AuditEvent(AuditEvents.CompanyAssignmentChanged)
        {
            CompanyId = companyId, EntityType = nameof(SharedPoolAssignment), EntityId = assignment.Id,
            New = $"shared pool {pool!.Name} ({r.Environment})", Reason = r.Reason
        });
        await SaveAsync(ct);
        return assignment;
    }

    /// <summary>Controlled company transfer: validation report, then cutover per mode.</summary>
    public async Task<(IReadOnlyList<TransferCheck> Checks, CompanyServerAssignment? Assignment)> TransferCompanyAsync(Guid companyId,
        TransferRequest r, string actor, CancellationToken ct)
    {
        var checks = new List<TransferCheck>();
        var target = await db.Servers.Include(s => s.Applications).AsNoTracking().FirstOrDefaultAsync(s => s.Id == r.TargetServerId, ct);
        if (target is null) throw new ValidationFailedException(["Target server not found"]);

        var errors = new List<string>();
        await ValidateServerAsync(target.Id, "Target", companyId, r.Environment, r.ApplicationId, errors, ct);
        checks.Add(new("Environment, application and isolation", errors.Count == 0, errors.Count == 0 ? "OK" : string.Join("; ", errors)));

        var health = await db.ServerHealthCurrent.AsNoTracking().FirstOrDefaultAsync(h => h.ServerId == target.Id, ct);
        var healthy = health?.State is HealthState.Healthy or HealthState.Degraded;
        checks.Add(new("Target health", healthy, health is null ? "No health data yet" : $"{health.State}, last check {health.LastCheckAt:u}"));
        checks.Add(new("Target accepting sessions", target.AdminState == ServerAdminState.Active, target.AdminState.ToString()));

        var minimums = await db.Applications.AsNoTracking().Where(a => a.MinimumServerVersion != null
            && (r.ApplicationId == null || a.Id == r.ApplicationId)).ToListAsync(ct);
        var versionOk = minimums.All(a => Version.TryParse(target.ApplicationVersion, out var v) && Version.TryParse(a.MinimumServerVersion, out var m) && v >= m
                                          || target.Applications.All(sa => sa.ApplicationId != a.Id));
        checks.Add(new("Application version", versionOk, $"Target runs {target.ApplicationVersion ?? "unknown"}"));

        var activeSince = time.GetUtcNow().AddMinutes(-30);
        var companySessions = await db.ActiveSessions.CountAsync(s => s.CompanyId == companyId && s.EndedAt == null && s.LastSeenAt >= activeSince, ct);
        var targetSessions = health?.ActiveSessions ?? 0;
        var capacityOk = target.MaxCapacity == 0 || targetSessions + companySessions <= target.MaxCapacity;
        checks.Add(new("Capacity", capacityOk, $"{companySessions} company sessions + {targetSessions} on target vs capacity {target.MaxCapacity}"));

        if (r.Mode == CutoverMode.Scheduled && r.ScheduledAt is null)
            checks.Add(new("Schedule", false, "ScheduledAt is required for scheduled cutover"));

        if (r.ValidateOnly || checks.Any(c => !c.Passed)) return (checks, null);

        var current = await db.CompanyServerAssignments.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.Environment == r.Environment && a.ApplicationId == r.ApplicationId
                        && a.IsEnabled && (a.EffectiveTo == null || a.EffectiveTo > time.GetUtcNow()))
            .OrderByDescending(a => a.EffectiveFrom).FirstOrDefaultAsync(ct);
        var assignment = await SetCompanyAssignmentAsync(companyId, new CompanyAssignmentRequest
        {
            PrimaryServerId = r.TargetServerId,
            SecondaryServerId = r.TargetSecondaryServerId ?? (current?.SecondaryServerId == r.TargetServerId ? null : current?.SecondaryServerId),
            FailoverPolicyId = current?.FailoverPolicyId,
            ApplicationId = r.ApplicationId,
            Environment = r.Environment,
            EffectiveFrom = r.Mode == CutoverMode.Scheduled ? r.ScheduledAt : null,
            Reason = r.Reason ?? $"Transfer to {target.Name}",
            ExpectedStamp = r.ExpectedStamp
        }, actor, ct, AuditEvents.CompanyTransferred);

        if (r.Mode == CutoverMode.ForceReauthentication)
            await RevokeCompanySessionsAsync(companyId, actor, "Company transferred", ct);
        return (checks, assignment);
    }

    public async Task<int> RevokeCompanySessionsAsync(Guid companyId, string actor, string reason, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var sessions = await db.ActiveSessions.Where(s => s.CompanyId == companyId && s.EndedAt == null && s.RevokedAt == null).ToListAsync(ct);
        foreach (var s in sessions)
        {
            s.RevokedAt = now;
            s.RevokedBy = actor;
            s.EndedAt = now;
            s.EndReason = reason;
        }
        await db.SaveChangesAsync(ct);
        foreach (var key in sessions.Select(s => s.SessionKey).Distinct())
            await store.PublishAsync(StateKeys.SessionRevokedChannel, key);
        await audit.WriteAsync(new AuditEvent(AuditEvents.SessionRevoked) { CompanyId = companyId, Reason = $"{reason}: {sessions.Count} session(s)" }, ct);
        return sessions.Count;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Validation helpers
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Servers authorised for the company's data: explicit grants plus servers named by its own assignments.</summary>
    public async Task<HashSet<Guid>> AuthorizedServersAsync(Guid companyId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var set = (await db.CompanyServerAuthorizations.Where(a => a.CompanyId == companyId).Select(a => a.ServerId).ToListAsync(ct)).ToHashSet();
        var company = await db.CompanyServerAssignments.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.IsEnabled && (a.EffectiveTo == null || a.EffectiveTo > now)).ToListAsync(ct);
        var poolIds = new HashSet<Guid>();
        foreach (var a in company)
        {
            set.Add(a.PrimaryServerId);
            if (a.SecondaryServerId is { } s) set.Add(s);
            if (a.ClusterPoolId is { } p) poolIds.Add(p);
            if (a.FailoverPolicyId is { } f && await db.FailoverPolicies.AsNoTracking().Where(x => x.Id == f).Select(x => x.FailoverPoolId).FirstOrDefaultAsync(ct) is { } fp)
                poolIds.Add(fp);
        }
        foreach (var poolId in await db.SharedPoolAssignments.AsNoTracking()
                     .Where(a => a.CompanyId == companyId && a.IsEnabled && (a.EffectiveTo == null || a.EffectiveTo > now))
                     .Select(a => a.PoolId).ToListAsync(ct))
            poolIds.Add(poolId);
        foreach (var id in await db.ServerPoolMembers.AsNoTracking().Where(m => poolIds.Contains(m.PoolId)).Select(m => m.ServerId).ToListAsync(ct))
            set.Add(id);
        return set;
    }

    private async Task ValidateServerAsync(Guid serverId, string role, Guid companyId, DeploymentEnvironment env, Guid? applicationId,
        List<string> errors, CancellationToken ct)
    {
        var server = await db.Servers.Include(s => s.Applications).AsNoTracking().FirstOrDefaultAsync(s => s.Id == serverId, ct);
        if (server is null)
        {
            errors.Add($"{role} server not found");
            return;
        }
        if (server.AdminState == ServerAdminState.Retired) errors.Add($"{role} server {server.Name} is retired");
        if (server.Environment != env) errors.Add($"{role} server {server.Name} is in {server.Environment}, not {env}");
        if (applicationId is { } app && server.Applications.All(a => a.ApplicationId != app))
            errors.Add($"{role} server {server.Name} does not host the selected application");
        if (server.ServerType == ServerType.Dedicated)
        {
            var now = time.GetUtcNow();
            var otherCompany = await db.CompanyServerAssignments.AsNoTracking()
                .Where(a => a.CompanyId != companyId && a.IsEnabled && (a.EffectiveTo == null || a.EffectiveTo > now)
                            && (a.PrimaryServerId == serverId || a.SecondaryServerId == serverId))
                .Select(a => a.Company!.Code).FirstOrDefaultAsync(ct)
                ?? await db.UserServerAssignments.AsNoTracking()
                    .Where(a => a.CompanyId != companyId && a.IsEnabled && (a.EffectiveTo == null || a.EffectiveTo > now)
                                && (a.PrimaryServerId == serverId || a.SecondaryServerId == serverId))
                    .Select(a => a.Company!.Code).FirstOrDefaultAsync(ct);
            if (otherCompany is not null)
                errors.Add($"{role} server {server.Name} is dedicated to {otherCompany} (tenant isolation)");
        }
    }

    private async Task ValidateFailoverPolicyAsync(Guid? id, List<string> errors, CancellationToken ct)
    {
        if (id is { } policyId && !await db.FailoverPolicies.AnyAsync(f => f.Id == policyId, ct)) errors.Add("Failover policy not found");
    }

    private async Task ValidateApplicationAsync(Guid? id, List<string> errors, CancellationToken ct)
    {
        if (id is { } appId && !await db.Applications.AnyAsync(a => a.Id == appId, ct)) errors.Add("Application not found");
    }

    private Task<List<UserServerAssignment>> CurrentUserAssignmentsAsync(Guid companyId, Guid userId, DeploymentEnvironment env,
        Guid? applicationId, DateTimeOffset from, CancellationToken ct) =>
        db.UserServerAssignments
            .Where(a => a.CompanyId == companyId && a.UserId == userId && a.Environment == env && a.ApplicationId == applicationId
                        && a.IsEnabled && (a.EffectiveTo == null || a.EffectiveTo > from))
            .ToListAsync(ct);

    private static IEnumerable<(Guid Id, string Role)> Servers(Guid primary, Guid? secondary)
    {
        yield return (primary, "Primary");
        if (secondary is { } s) yield return (s, "Secondary");
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("Another administrator changed this record at the same time. Reload and try again.");
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
                                           || ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new ConflictException("An assignment with the same scope and start time already exists.");
        }
    }

    private static object Describe(UserServerAssignment a) => new
    {
        a.Id, a.PrimaryServerId, a.SecondaryServerId, a.FailoverPolicyId, a.ApplicationId, Environment = a.Environment.ToString(),
        a.EffectiveFrom, a.EffectiveTo, a.IsEnabled
    };

    private static object Describe(CompanyServerAssignment a) => new
    {
        a.Id, a.PrimaryServerId, a.SecondaryServerId, a.ClusterPoolId, ClusterMode = a.ClusterMode.ToString(), a.FailoverPolicyId,
        a.ApplicationId, Environment = a.Environment.ToString(), a.EffectiveFrom, a.EffectiveTo
    };
}
