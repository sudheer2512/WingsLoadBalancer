using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wings.Admin.Api.Services;
using Wings.LoadBalancer.Data;
using Wings.LoadBalancer.Data.Entities;
using Wings.Routing;
using Wings.Routing.Model;

namespace Wings.Admin.Api.Controllers;

public sealed record CompanyRequest
{
    [Required, MaxLength(64)] public required string Code { get; init; }
    [Required, MaxLength(200)] public required string Name { get; init; }
    [MaxLength(100)] public string? ServicePlan { get; init; }
    public CompanyRoutingMode RoutingMode { get; init; }
    public CompanyStatus Status { get; init; }
    public DeploymentEnvironment DefaultEnvironment { get; init; }
    [Range(0, 1_000_000)] public int? MaxConcurrentUsers { get; init; }
    public AuthenticationMode AuthenticationMode { get; init; }
    public Guid? RoutingPolicyId { get; init; }
    [MaxLength(4000)] public string? Notes { get; init; }
    public List<string> Aliases { get; init; } = [];
    public Guid? ConcurrencyStamp { get; init; }
}

public sealed record AuthorizationRequest(Guid ServerId, string? Notes);
public sealed record CompanyStatusRequest(CompanyStatus Status, bool RevokeSessions, string? Reason);
public sealed record PreviewCompanyAssignmentRequest(CompanyAssignmentRequest Assignment, int SampleUsers = 10);

[Route("api/companies")]
public sealed class CompaniesController(WingsDbContext db, AssignmentService assignments, SimulationService simulation, TimeProvider time)
    : AdminControllerBase
{
    [HttpGet, Authorize(Permissions.CompaniesRead)]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] CompanyStatus? status, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var q = db.Companies.AsNoTracking().Include(c => c.Aliases).AsQueryable();
        if (status is { } s) q = q.Where(c => c.Status == s);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var n = IdentifierNormalizer.Normalize(search);
            q = q.Where(c => c.NormalizedCode.Contains(n) || c.Name.ToUpper().Contains(n) || c.Aliases.Any(a => a.NormalizedAlias.Contains(n)));
        }
        var companies = await q.OrderBy(c => c.Code).ToListAsync(ct);
        var ca = await db.CompanyServerAssignments.AsNoTracking().Include(a => a.PrimaryServer).Include(a => a.SecondaryServer).Include(a => a.FailoverPolicy)
            .Where(a => a.IsEnabled && a.EffectiveFrom <= now && (a.EffectiveTo == null || a.EffectiveTo > now)).ToListAsync(ct);
        var pa = await db.SharedPoolAssignments.AsNoTracking().Include(a => a.Pool)
            .Where(a => a.IsEnabled && a.EffectiveFrom <= now && (a.EffectiveTo == null || a.EffectiveTo > now)).ToListAsync(ct);
        var users = await db.UserCompanyMemberships.AsNoTracking().GroupBy(m => m.CompanyId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var since = now.AddMinutes(-30);
        var sessions = await db.ActiveSessions.AsNoTracking().Where(x => x.EndedAt == null && x.LastSeenAt >= since)
            .GroupBy(x => x.CompanyId).Select(g => new { g.Key, Count = g.Select(x => x.UserId).Distinct().Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return Ok(companies.Select(c =>
        {
            var a = ca.Where(x => x.CompanyId == c.Id).OrderBy(x => x.Environment).ThenByDescending(x => x.ApplicationId == null).FirstOrDefault();
            var p = pa.Where(x => x.CompanyId == c.Id).OrderBy(x => x.Priority).FirstOrDefault();
            return new
            {
                c.Id, c.Code, c.Name, c.ServicePlan, RoutingMode = c.RoutingMode.ToString(), Status = c.Status.ToString(),
                DefaultEnvironment = c.DefaultEnvironment.ToString(), c.MaxConcurrentUsers, AuthenticationMode = c.AuthenticationMode.ToString(),
                Aliases = c.Aliases.Select(x => x.Alias).ToList(), PrimaryServer = a?.PrimaryServer?.Name, SecondaryServer = a?.SecondaryServer?.Name,
                FailoverPolicy = a?.FailoverPolicy?.Name, SharedPool = p?.Pool?.Name, Users = users.GetValueOrDefault(c.Id),
                ActiveUsers = sessions.GetValueOrDefault(c.Id), c.CreatedAt, c.ModifiedAt, c.ConcurrencyStamp
            };
        }));
    }

    [HttpGet("{id:guid}"), Authorize(Permissions.CompaniesRead)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var c = await db.Companies.AsNoTracking().Include(x => x.Aliases).Include(x => x.RoutingPolicy).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        var now = time.GetUtcNow();
        var companyAssignments = await db.CompanyServerAssignments.AsNoTracking().Include(a => a.PrimaryServer).Include(a => a.SecondaryServer)
            .Include(a => a.FailoverPolicy).Include(a => a.ClusterPool).Include(a => a.Application)
            .Where(a => a.CompanyId == id).OrderByDescending(a => a.EffectiveFrom).Take(200).ToListAsync(ct);
        var poolAssignments = await db.SharedPoolAssignments.AsNoTracking().Include(a => a.Pool).Include(a => a.Application)
            .Where(a => a.CompanyId == id).OrderByDescending(a => a.EffectiveFrom).Take(200).ToListAsync(ct);
        var authorizations = await db.CompanyServerAuthorizations.AsNoTracking().Include(a => a.Server).Where(a => a.CompanyId == id)
            .Select(a => new { a.Id, a.ServerId, Server = a.Server!.Name, a.GrantedAt, a.GrantedBy, a.Notes }).ToListAsync(ct);
        var authorized = await assignments.AuthorizedServersAsync(id, ct);
        string State(AssignmentBase a) => !a.IsEnabled ? "Disabled" : a.EffectiveFrom > now ? "Scheduled" : a.EffectiveTo is { } to && to <= now ? "Ended" : "Current";

        return Ok(new
        {
            c.Id, c.Code, c.Name, c.ServicePlan, RoutingMode = c.RoutingMode.ToString(), Status = c.Status.ToString(),
            DefaultEnvironment = c.DefaultEnvironment.ToString(), c.MaxConcurrentUsers, AuthenticationMode = c.AuthenticationMode.ToString(),
            c.RoutingPolicyId, RoutingPolicy = c.RoutingPolicy?.Name, c.Notes, Aliases = c.Aliases.Select(a => a.Alias).ToList(),
            c.CreatedAt, c.ModifiedAt, c.ModifiedBy, c.ConcurrencyStamp,
            CompanyAssignments = companyAssignments.Select(a => new
            {
                a.Id, Environment = a.Environment.ToString(), a.ApplicationId, Application = a.Application?.Code, a.PrimaryServerId,
                PrimaryServer = a.PrimaryServer?.Name, a.SecondaryServerId, SecondaryServer = a.SecondaryServer?.Name, a.ClusterPoolId,
                ClusterPool = a.ClusterPool?.Name, ClusterMode = a.ClusterMode.ToString(), a.FailoverPolicyId, FailoverPolicy = a.FailoverPolicy?.Name,
                a.EffectiveFrom, a.EffectiveTo, a.Reason, a.CreatedBy, a.CreatedAt, State = State(a)
            }),
            PoolAssignments = poolAssignments.Select(a => new
            {
                a.Id, Environment = a.Environment.ToString(), a.ApplicationId, Application = a.Application?.Code, a.PoolId, Pool = a.Pool?.Name,
                a.Priority, a.EffectiveFrom, a.EffectiveTo, a.Reason, a.CreatedBy, State = State(a)
            }),
            ExplicitAuthorizations = authorizations,
            AuthorizedServerIds = authorized
        });
    }

    [HttpPost, Authorize(Permissions.CompaniesWrite)]
    public async Task<IActionResult> Create(CompanyRequest request, CancellationToken ct)
    {
        var errors = await ValidateIdentifiersAsync(request, null, ct);
        if (errors.Count > 0) return BadRequest(new { error = "validation_failed", errors });
        var company = new Company { Code = request.Code.Trim(), Name = request.Name.Trim() };
        Apply(company, request);
        db.Companies.Add(company);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = company.Id }, new { company.Id });
    }

    [HttpPut("{id:guid}"), Authorize(Permissions.CompaniesWrite)]
    public async Task<IActionResult> Update(Guid id, CompanyRequest request, CancellationToken ct)
    {
        var company = await db.Companies.Include(c => c.Aliases).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (company is null) return NotFound();
        if (request.ConcurrencyStamp is { } stamp) db.Entry(company).Property(c => c.ConcurrencyStamp).OriginalValue = stamp;
        var errors = await ValidateIdentifiersAsync(request, id, ct);
        if (errors.Count > 0) return BadRequest(new { error = "validation_failed", errors });
        company.Code = request.Code.Trim();
        company.Name = request.Name.Trim();
        Apply(company, request);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Disable (or re-enable) company access, optionally signing everyone out.</summary>
    [HttpPost("{id:guid}/status"), Authorize(Permissions.CompaniesWrite)]
    public async Task<IActionResult> SetStatus(Guid id, CompanyStatusRequest request, CancellationToken ct)
    {
        var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (company is null) return NotFound();
        company.Status = request.Status;
        await db.SaveChangesAsync(ct);
        var revoked = request.RevokeSessions ? await assignments.RevokeCompanySessionsAsync(id, Actor, request.Reason ?? $"Company {request.Status}", ct) : 0;
        return Ok(new { status = company.Status.ToString(), revokedSessions = revoked });
    }

    /// <summary>Assign or change the company's dedicated server (or cluster). Future EffectiveFrom schedules it.</summary>
    [HttpPost("{id:guid}/assignment"), Authorize(Permissions.AssignmentsWrite)]
    public async Task<IActionResult> Assign(Guid id, CompanyAssignmentRequest request, CancellationToken ct)
    {
        var assignment = await assignments.SetCompanyAssignmentAsync(id, request, Actor, ct);
        return Ok(new { assignment.Id, assignment.EffectiveFrom });
    }

    [HttpPost("{id:guid}/assignment/preview"), Authorize(Permissions.AssignmentsRead)]
    public async Task<IActionResult> PreviewAssignment(Guid id, PreviewCompanyAssignmentRequest request, CancellationToken ct)
    {
        var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (company is null) return NotFound();
        var errors = await assignments.ValidateCompanyAssignmentAsync(id, request.Assignment, request.Assignment.EffectiveFrom ?? time.GetUtcNow(), ct);
        var users = await db.UserCompanyMemberships.AsNoTracking().Where(m => m.CompanyId == id).OrderBy(m => m.Username)
            .Take(Math.Clamp(request.SampleUsers, 1, 100)).Select(m => m.Username).ToListAsync(ct);
        var a = request.Assignment;
        var preview = await simulation.PreviewAsync(config => config with
        {
            CompanyAssignments = config.CompanyAssignments
                .Where(x => !(x.CompanyId == id && x.Environment == a.Environment && x.ApplicationId == a.ApplicationId))
                .Append(new CompanyAssignmentInfo(Guid.NewGuid(), id, a.ApplicationId, a.Environment, a.PrimaryServerId, a.SecondaryServerId,
                    a.ClusterPoolId, a.ClusterMode, a.FailoverPolicyId, true, time.GetUtcNow().AddSeconds(-1), a.EffectiveTo))
                .ToArray()
        }, users.Select(u => new SimulationRequest(company.Code, u, null, a.Environment)), ct);
        return Ok(new { validationErrors = errors, preview });
    }

    [HttpPost("{id:guid}/shared-pool"), Authorize(Permissions.AssignmentsWrite)]
    public async Task<IActionResult> AssignPool(Guid id, SharedPoolAssignmentRequest request, CancellationToken ct)
    {
        var assignment = await assignments.AssignSharedPoolAsync(id, request, Actor, ct);
        return Ok(new { assignment.Id });
    }

    [HttpPost("{id:guid}/assignments/{assignmentId:guid}/end"), Authorize(Permissions.AssignmentsWrite)]
    public async Task<IActionResult> EndAssignment(Guid id, Guid assignmentId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        AssignmentBase? a = await db.CompanyServerAssignments.FirstOrDefaultAsync(x => x.Id == assignmentId && x.CompanyId == id, ct);
        a ??= await db.SharedPoolAssignments.FirstOrDefaultAsync(x => x.Id == assignmentId && x.CompanyId == id, ct);
        if (a is null) return NotFound();
        a.EffectiveTo = a.EffectiveFrom > now ? a.EffectiveFrom : now;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Transfer to another server with validation and controlled cutover (ValidateOnly = dry run).</summary>
    [HttpPost("{id:guid}/transfer"), Authorize(Permissions.AssignmentsWrite)]
    public async Task<IActionResult> Transfer(Guid id, TransferRequest request, CancellationToken ct)
    {
        var (checks, assignment) = await assignments.TransferCompanyAsync(id, request, Actor, ct);
        var passed = checks.All(c => c.Passed);
        return passed || request.ValidateOnly
            ? Ok(new { passed, applied = assignment is not null, assignmentId = assignment?.Id, checks })
            : UnprocessableEntity(new { passed, applied = false, checks });
    }

    [HttpPost("{id:guid}/authorizations"), Authorize(Permissions.AssignmentsWrite)]
    public async Task<IActionResult> Authorize(Guid id, AuthorizationRequest request, CancellationToken ct)
    {
        var server = await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.ServerId, ct);
        if (server is null) return NotFound();
        if (server.ServerType == ServerType.Dedicated)
        {
            var now = time.GetUtcNow();
            var owner = await db.CompanyServerAssignments.AsNoTracking()
                .Where(a => a.CompanyId != id && a.IsEnabled && (a.EffectiveTo == null || a.EffectiveTo > now) && a.PrimaryServerId == server.Id)
                .Select(a => a.Company!.Code).FirstOrDefaultAsync(ct);
            if (owner is not null) return BadRequest(new { error = "validation_failed", errors = new[] { $"{server.Name} is dedicated to {owner}" } });
        }
        if (await db.CompanyServerAuthorizations.AnyAsync(a => a.CompanyId == id && a.ServerId == request.ServerId, ct)) return NoContent();
        db.CompanyServerAuthorizations.Add(new CompanyServerAuthorization
        {
            CompanyId = id, ServerId = request.ServerId, Notes = request.Notes, GrantedAt = time.GetUtcNow(), GrantedBy = Actor
        });
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/authorizations/{serverId:guid}"), Authorize(Permissions.AssignmentsWrite)]
    public async Task<IActionResult> RemoveAuthorization(Guid id, Guid serverId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (await db.UserServerAssignments.AnyAsync(a => a.CompanyId == id && (a.PrimaryServerId == serverId || a.SecondaryServerId == serverId)
                                                          && a.IsEnabled && (a.EffectiveTo == null || a.EffectiveTo > now), ct))
            return Conflict(new { error = "in_use", message = "Users of this company are assigned to the server. Move them first." });
        var grant = await db.CompanyServerAuthorizations.FirstOrDefaultAsync(a => a.CompanyId == id && a.ServerId == serverId, ct);
        if (grant is null) return NotFound();
        db.CompanyServerAuthorizations.Remove(grant);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/users"), Authorize(Permissions.CompaniesRead)]
    public async Task<IActionResult> Users(Guid id, CancellationToken ct) =>
        Ok(await db.UserCompanyMemberships.AsNoTracking().Include(m => m.User).Where(m => m.CompanyId == id).OrderBy(m => m.Username)
            .Select(m => new { m.UserId, m.Username, m.User!.DisplayName, m.User.Email, Status = m.Status.ToString(), UserStatus = m.User.Status.ToString(),
                m.User.LastLoginAt, m.User.MfaEnabled })
            .ToListAsync(ct));

    [HttpGet("{id:guid}/sessions"), Authorize(Permissions.SessionsRead)]
    public async Task<IActionResult> Sessions(Guid id, CancellationToken ct)
    {
        var since = time.GetUtcNow().AddMinutes(-30);
        return Ok(await db.ActiveSessions.AsNoTracking().Where(s => s.CompanyId == id && s.EndedAt == null && s.LastSeenAt >= since)
            .Join(db.UserCompanyMemberships, s => new { s.CompanyId, s.UserId }, m => new { m.CompanyId, m.UserId },
                (s, m) => new { s.SessionKey, m.Username, s.ApplicationId, s.ServerId, s.StartedAt, s.LastSeenAt, s.GatewayInstance })
            .OrderByDescending(s => s.LastSeenAt).Take(1000).ToListAsync(ct));
    }

    [HttpGet("{id:guid}/history"), Authorize(Permissions.AuditRead)]
    public async Task<IActionResult> History(Guid id, CancellationToken ct) =>
        Ok(await db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == id || a.EntityId == id)
            .OrderByDescending(a => a.At).Take(500).ToListAsync(ct));

    private async Task<List<string>> ValidateIdentifiersAsync(CompanyRequest r, Guid? id, CancellationToken ct)
    {
        var errors = new List<string>();
        var code = IdentifierNormalizer.Normalize(r.Code);
        if (code.Length == 0) errors.Add("Company code is required");
        var aliases = r.Aliases.Select(IdentifierNormalizer.Normalize).Where(a => a.Length > 0).Distinct().ToList();
        var identifiers = aliases.Append(code).ToList();
        if (identifiers.Count != identifiers.Distinct().Count()) errors.Add("An alias duplicates the company code");
        var clashes = await db.Companies.AsNoTracking().Where(c => c.Id != id && identifiers.Contains(c.NormalizedCode)).Select(c => c.Code).ToListAsync(ct);
        clashes.AddRange(await db.CompanyAliases.AsNoTracking().Where(a => a.CompanyId != id && identifiers.Contains(a.NormalizedAlias)).Select(a => a.Alias).ToListAsync(ct));
        if (clashes.Count > 0) errors.Add($"Already used by another company: {string.Join(", ", clashes.Distinct())}");
        return errors;
    }

    private static void Apply(Company c, CompanyRequest r)
    {
        c.NormalizedCode = IdentifierNormalizer.Normalize(r.Code);
        c.ServicePlan = r.ServicePlan;
        c.RoutingMode = r.RoutingMode;
        c.Status = r.Status;
        c.DefaultEnvironment = r.DefaultEnvironment;
        c.MaxConcurrentUsers = r.MaxConcurrentUsers;
        c.AuthenticationMode = r.AuthenticationMode;
        c.RoutingPolicyId = r.RoutingPolicyId;
        c.Notes = r.Notes;
        var wanted = r.Aliases.Select(a => a.Trim()).Where(a => a.Length > 0).DistinctBy(IdentifierNormalizer.Normalize).ToList();
        c.Aliases.RemoveAll(a => !wanted.Any(w => IdentifierNormalizer.Normalize(w) == a.NormalizedAlias));
        foreach (var alias in wanted.Where(w => c.Aliases.All(a => a.NormalizedAlias != IdentifierNormalizer.Normalize(w))))
            c.Aliases.Add(new CompanyAlias { CompanyId = c.Id, Alias = alias, NormalizedAlias = IdentifierNormalizer.Normalize(alias) });
    }
}
