using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wings.Admin.Api.Services;
using Wings.LoadBalancer.Data;
using Wings.LoadBalancer.Data.Entities;
using Wings.LoadBalancer.Infrastructure.Security;
using Wings.Routing;
using Wings.Routing.Model;

namespace Wings.Admin.Api.Controllers;

public sealed record CreateUserRequest
{
    public required Guid CompanyId { get; init; }
    [Required, MaxLength(200)] public required string Username { get; init; }
    [Required, MaxLength(200)] public required string DisplayName { get; init; }
    [EmailAddress, MaxLength(320)] public string? Email { get; init; }
    /// <summary>Initial password for local authentication; omit for externally authenticated users.</summary>
    [MinLength(12), MaxLength(1024)] public string? Password { get; init; }
    [MaxLength(200)] public string? ExternalSubject { get; init; }
}

public sealed record MembershipStatusRequest(Guid CompanyId, AccountStatus Status, string? Reason, Guid? ExpectedStamp = null, bool RevokeSessions = false);
public sealed record ResetPasswordRequest([Required, MinLength(12), MaxLength(1024)] string Password);
public sealed record RollbackRequest(Guid AssignmentId, string? Reason);
public sealed record PreviewUserAssignmentRequest(UserAssignmentRequest Assignment);

[Route("api/users")]
public sealed class UsersController(
    WingsDbContext db,
    AssignmentService assignments,
    SimulationService simulation,
    BulkImportService bulk,
    IPasswordHasher hasher,
    TimeProvider time) : AdminControllerBase
{
    /// <summary>Search memberships by company and username. Identical usernames in other companies are separate rows.</summary>
    [HttpGet, Authorize(Permissions.CompaniesRead)]
    public async Task<IActionResult> Search([FromQuery] Guid? companyId, [FromQuery] string? search, [FromQuery] bool dedicatedOnly,
        [FromQuery] int take = 200, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var q = db.UserCompanyMemberships.AsNoTracking().Include(m => m.User).Include(m => m.Company).AsQueryable();
        if (companyId is { } c) q = q.Where(m => m.CompanyId == c);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var n = IdentifierNormalizer.Normalize(search);
            q = q.Where(m => m.NormalizedUsername.Contains(n) || m.User!.DisplayName.ToUpper().Contains(n));
        }
        var current = db.UserServerAssignments.AsNoTracking()
            .Where(a => a.IsEnabled && a.EffectiveFrom <= now && (a.EffectiveTo == null || a.EffectiveTo > now));
        if (dedicatedOnly) q = q.Where(m => current.Any(a => a.UserId == m.UserId && a.CompanyId == m.CompanyId));
        var members = await q.OrderBy(m => m.Company!.Code).ThenBy(m => m.Username).Take(Math.Clamp(take, 1, 1000)).ToListAsync(ct);
        var ids = members.Select(m => m.UserId).ToList();
        var userAssignments = await current.Include(a => a.PrimaryServer).Include(a => a.SecondaryServer)
            .Where(a => ids.Contains(a.UserId)).ToListAsync(ct);
        var companyIds = members.Select(m => m.CompanyId).Distinct().ToList();
        var companyAssignments = await db.CompanyServerAssignments.AsNoTracking().Include(a => a.PrimaryServer)
            .Where(a => companyIds.Contains(a.CompanyId) && a.IsEnabled && a.EffectiveFrom <= now && (a.EffectiveTo == null || a.EffectiveTo > now)
                        && a.Environment == DeploymentEnvironment.Production && a.ApplicationId == null)
            .ToListAsync(ct);
        var pools = await db.SharedPoolAssignments.AsNoTracking().Include(a => a.Pool)
            .Where(a => companyIds.Contains(a.CompanyId) && a.IsEnabled && a.EffectiveFrom <= now && (a.EffectiveTo == null || a.EffectiveTo > now)
                        && a.Environment == DeploymentEnvironment.Production)
            .ToListAsync(ct);

        return Ok(members.Select(m =>
        {
            var ua = userAssignments.Where(a => a.UserId == m.UserId && a.CompanyId == m.CompanyId && a.Environment == DeploymentEnvironment.Production)
                .OrderByDescending(a => a.ApplicationId == null).FirstOrDefault();
            var ca = companyAssignments.FirstOrDefault(a => a.CompanyId == m.CompanyId);
            var pool = pools.Where(a => a.CompanyId == m.CompanyId).OrderBy(a => a.Priority).FirstOrDefault();
            return new
            {
                m.UserId, m.CompanyId, Company = m.Company!.Code, CompanyName = m.Company.Name, m.Username, m.User!.DisplayName, m.User.Email,
                Status = m.Status.ToString(), UserStatus = m.User.Status.ToString(), m.User.LastLoginAt, MembershipStamp = m.ConcurrencyStamp,
                AssignedServer = ua?.PrimaryServer?.Name ?? ca?.PrimaryServer?.Name ?? (pool is null ? null : $"Pool: {pool.Pool!.Name}"),
                SecondaryServer = ua?.SecondaryServer?.Name,
                RoutingType = ua is not null ? "User Dedicated" : ca is not null ? "Company Default" : pool is not null ? "Shared Pool" : "Unassigned",
                AssignmentId = ua?.Id
            };
        }));
    }

    [HttpPost, Authorize(Permissions.CompaniesWrite)]
    public async Task<IActionResult> Create(CreateUserRequest request, CancellationToken ct)
    {
        var normalized = IdentifierNormalizer.Normalize(request.Username);
        if (!await db.Companies.AnyAsync(c => c.Id == request.CompanyId, ct)) return NotFound(new { error = "company_not_found" });
        if (await db.UserCompanyMemberships.AnyAsync(m => m.CompanyId == request.CompanyId && m.NormalizedUsername == normalized, ct))
            return Conflict(new { error = "duplicate_username", message = "This username already exists in the company." });
        var user = new User
        {
            DisplayName = request.DisplayName, Email = request.Email,
            PasswordHash = request.Password is null ? null : hasher.Hash(request.Password)
        };
        user.Memberships.Add(new UserCompanyMembership
        {
            UserId = user.Id, CompanyId = request.CompanyId, Username = request.Username.Trim(), NormalizedUsername = normalized,
            ExternalSubject = request.ExternalSubject, CreatedAt = time.GetUtcNow()
        });
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return Ok(new { user.Id });
    }

    [HttpPut("{id:guid}/status"), Authorize(Permissions.CompaniesWrite)]
    public async Task<IActionResult> SetStatus(Guid id, MembershipStatusRequest request, CancellationToken ct)
    {
        var membership = await db.UserCompanyMemberships.FirstOrDefaultAsync(m => m.UserId == id && m.CompanyId == request.CompanyId, ct);
        if (membership is null) return NotFound();
        if (request.ExpectedStamp is { } stamp) db.Entry(membership).Property(m => m.ConcurrencyStamp).OriginalValue = stamp;
        membership.Status = request.Status;
        membership.ConcurrencyStamp = Guid.NewGuid();
        await db.SaveChangesAsync(ct);
        var revoked = 0;
        if (request.RevokeSessions)
        {
            var now = time.GetUtcNow();
            var sessions = await db.ActiveSessions.Where(s => s.UserId == id && s.CompanyId == request.CompanyId && s.RevokedAt == null).ToListAsync(ct);
            foreach (var s in sessions)
            {
                s.RevokedAt = now;
                s.RevokedBy = Actor;
                s.EndedAt ??= now;
                s.EndReason = request.Reason ?? "Membership status changed";
            }
            await db.SaveChangesAsync(ct);
            revoked = sessions.Count;
        }
        return Ok(new { status = membership.Status.ToString(), membershipStamp = membership.ConcurrencyStamp, revokedSessions = revoked });
    }

    [HttpPut("{id:guid}/password"), Authorize(Permissions.CompaniesWrite)]
    public async Task<IActionResult> ResetPassword(Guid id, ResetPasswordRequest request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return NotFound();
        user.PasswordHash = hasher.Hash(request.Password);
        user.FailedLoginCount = 0;
        user.LockoutEndAt = null;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Current and scheduled user-level assignments plus the effective company route.</summary>
    [HttpGet("{id:guid}/assignment"), Authorize(Permissions.AssignmentsRead)]
    public async Task<IActionResult> GetAssignment(Guid id, [FromQuery] Guid companyId, CancellationToken ct)
    {
        var membership = await db.UserCompanyMemberships.AsNoTracking().Include(m => m.Company)
            .FirstOrDefaultAsync(m => m.UserId == id && m.CompanyId == companyId, ct);
        if (membership is null) return NotFound();
        var now = time.GetUtcNow();
        var rows = await db.UserServerAssignments.AsNoTracking().Include(a => a.PrimaryServer).Include(a => a.SecondaryServer)
            .Include(a => a.FailoverPolicy).Include(a => a.Application)
            .Where(a => a.UserId == id && a.CompanyId == companyId && (a.EffectiveTo == null || a.EffectiveTo > now))
            .OrderBy(a => a.EffectiveFrom).ToListAsync(ct);
        return Ok(new
        {
            membership.UserId, membership.CompanyId, Company = membership.Company!.Code, membership.Username, MembershipStamp = membership.ConcurrencyStamp,
            Assignments = rows.Select(a => AssignmentDto(a, now)),
            AuthorizedServerIds = await assignments.AuthorizedServersAsync(companyId, ct)
        });
    }

    [HttpPut("{id:guid}/assignment"), Authorize(Permissions.AssignmentsWrite)]
    public async Task<IActionResult> SetAssignment(Guid id, UserAssignmentRequest request, CancellationToken ct)
    {
        var assignment = await assignments.SetUserAssignmentAsync(id, request, Actor, ct);
        return Ok(new { assignment.Id, assignment.EffectiveFrom });
    }

    [HttpDelete("{id:guid}/assignment"), Authorize(Permissions.AssignmentsWrite)]
    public async Task<IActionResult> RemoveAssignment(Guid id, [FromQuery] Guid companyId, [FromQuery] DeploymentEnvironment environment = DeploymentEnvironment.Production,
        [FromQuery] Guid? applicationId = null, [FromQuery] Guid? expectedStamp = null, [FromQuery] string? reason = null, CancellationToken ct = default)
    {
        await assignments.RemoveUserAssignmentAsync(id, companyId, environment, applicationId, reason, expectedStamp, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/assignment/preview"), Authorize(Permissions.AssignmentsRead)]
    public async Task<IActionResult> Preview(Guid id, PreviewUserAssignmentRequest request, CancellationToken ct)
    {
        var a = request.Assignment;
        var errors = await assignments.ValidateUserAssignmentAsync(a, a.EffectiveFrom ?? time.GetUtcNow(), ct);
        var membership = await db.UserCompanyMemberships.AsNoTracking().Include(m => m.Company).FirstOrDefaultAsync(m => m.UserId == id && m.CompanyId == a.CompanyId, ct);
        if (membership is null) return NotFound();
        var preview = await simulation.PreviewAsync(config => config with
        {
            UserAssignments = config.UserAssignments
                .Where(x => !(x.UserId == id && x.CompanyId == a.CompanyId && x.Environment == a.Environment && x.ApplicationId == a.ApplicationId))
                .Append(new UserAssignmentInfo(Guid.NewGuid(), a.CompanyId, id, a.ApplicationId, a.Environment, a.PrimaryServerId, a.SecondaryServerId,
                    a.FailoverPolicyId, a.AllowCompanyRouteFallback, true, time.GetUtcNow().AddSeconds(-1), a.EffectiveTo))
                .ToArray()
        }, [new SimulationRequest(membership.Company!.Code, membership.Username, null, a.Environment)], ct);
        return Ok(new { validationErrors = errors, preview });
    }

    [HttpGet("{id:guid}/assignment/history"), Authorize(Permissions.AssignmentsRead)]
    public async Task<IActionResult> History(Guid id, [FromQuery] Guid companyId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var rows = await db.UserServerAssignments.AsNoTracking().Include(a => a.PrimaryServer).Include(a => a.SecondaryServer)
            .Include(a => a.FailoverPolicy).Include(a => a.Application)
            .Where(a => a.UserId == id && a.CompanyId == companyId).OrderByDescending(a => a.EffectiveFrom).Take(200).ToListAsync(ct);
        return Ok(rows.Select(a => AssignmentDto(a, now)));
    }

    [HttpPost("{id:guid}/assignment/rollback"), Authorize(Permissions.AssignmentsWrite)]
    public async Task<IActionResult> Rollback(Guid id, RollbackRequest request, CancellationToken ct)
    {
        var restored = await assignments.RollbackUserAssignmentAsync(id, request.AssignmentId, Actor, request.Reason, ct);
        return Ok(new { restored.Id });
    }

    /// <summary>Upload CSV or Excel. dryRun=true (default) validates only.</summary>
    [HttpPost("bulk-import"), Authorize(Permissions.AssignmentsWrite), RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> BulkImport(IFormFile file, [FromQuery] bool dryRun = true, CancellationToken ct = default)
    {
        if (file.Length == 0) return BadRequest(new { error = "empty_file" });
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not (".csv" or ".xlsx")) return BadRequest(new { error = "unsupported_file", message = "Upload a .csv or .xlsx file." });
        await using var stream = file.OpenReadStream();
        return Ok(await bulk.ImportAsync(stream, file.FileName, dryRun, Actor, ct));
    }

    [HttpGet("bulk-import/template"), Authorize(Permissions.AssignmentsRead)]
    public IActionResult Template() => File(BulkImportService.Template(), "text/csv", "user-assignments-template.csv");

    private static object AssignmentDto(UserServerAssignment a, DateTimeOffset now) => new
    {
        a.Id, Environment = a.Environment.ToString(), a.ApplicationId, Application = a.Application?.Code, a.PrimaryServerId,
        PrimaryServer = a.PrimaryServer?.Name, a.SecondaryServerId, SecondaryServer = a.SecondaryServer?.Name, a.FailoverPolicyId,
        FailoverPolicy = a.FailoverPolicy?.Name, a.AllowCompanyRouteFallback, a.IsEnabled, a.EffectiveFrom, a.EffectiveTo, a.Reason,
        a.CreatedAt, a.CreatedBy, a.SupersededById,
        State = !a.IsEnabled ? "Disabled" : a.EffectiveFrom > now ? "Scheduled" : a.EffectiveTo is { } to && to <= now ? "Ended" : "Current"
    };
}
