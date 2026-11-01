using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wings.Admin.Api.Services;
using Wings.LoadBalancer.Data;
using Wings.LoadBalancer.Data.Entities;
using Wings.LoadBalancer.Infrastructure.Alerts;
using Wings.LoadBalancer.Infrastructure.Auditing;
using Wings.LoadBalancer.Infrastructure.Caching;
using Wings.LoadBalancer.Infrastructure.Configuration;
using Wings.LoadBalancer.Infrastructure.Security;
using Wings.Routing;

namespace Wings.Admin.Api.Controllers;

[Route("api/dashboard")]
public sealed class DashboardController(DashboardService dashboard) : AdminControllerBase
{
    [HttpGet("summary"), Authorize(Permissions.MonitoringRead)]
    public async Task<IActionResult> Summary(CancellationToken ct) => Ok(await dashboard.SummaryAsync(ct));

    [HttpGet("utilization"), Authorize(Permissions.MonitoringRead)]
    public async Task<IActionResult> Utilization([FromQuery] int hours = 6, CancellationToken ct = default) =>
        Ok(await dashboard.UtilizationAsync(hours, ct));

    [HttpGet("timeseries"), Authorize(Permissions.MonitoringRead)]
    public async Task<IActionResult> TimeSeries([FromQuery] DashboardFilter filter, [FromQuery] int bucketMinutes = 15, CancellationToken ct = default) =>
        Ok(await dashboard.TimeSeriesAsync(filter, bucketMinutes, ct));

    [HttpGet("topology"), Authorize(Permissions.MonitoringRead)]
    public async Task<IActionResult> Topology(CancellationToken ct) => Ok(await dashboard.TopologyAsync(ct));
}

public sealed record MaintenanceWindowRequest(Guid ServerId, DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    [Range(1, 1440)] int DrainTimeoutMinutes = 30, bool AutoRestore = true, string? Reason = null);

[Route("api/maintenance")]
public sealed class MaintenanceController(WingsDbContext db, TimeProvider time) : AdminControllerBase
{
    [HttpGet, Authorize(Permissions.ServersRead)]
    public async Task<IActionResult> List([FromQuery] bool includeCompleted, CancellationToken ct)
    {
        var q = db.MaintenanceWindows.AsNoTracking().Include(w => w.Server).AsQueryable();
        if (!includeCompleted) q = q.Where(w => w.Status != MaintenanceStatus.Completed && w.Status != MaintenanceStatus.Cancelled);
        return Ok(await q.OrderBy(w => w.StartsAt).Take(500).Select(w => new
        {
            w.Id, w.ServerId, Server = w.Server!.Name, w.StartsAt, w.EndsAt, w.DrainTimeoutMinutes, w.AutoRestore,
            Status = w.Status.ToString(), w.Reason, w.CreatedBy, w.CreatedAt
        }).ToListAsync(ct));
    }

    [HttpPost, Authorize(Permissions.ServersOperate)]
    public async Task<IActionResult> Schedule(MaintenanceWindowRequest r, CancellationToken ct)
    {
        if (r.EndsAt <= r.StartsAt) return BadRequest(new { error = "validation_failed", errors = new[] { "EndsAt must be after StartsAt" } });
        if (r.EndsAt <= time.GetUtcNow()) return BadRequest(new { error = "validation_failed", errors = new[] { "The window is in the past" } });
        if (!await db.Servers.AnyAsync(s => s.Id == r.ServerId, ct)) return NotFound();
        var overlap = await db.MaintenanceWindows.AnyAsync(w => w.ServerId == r.ServerId && w.Status != MaintenanceStatus.Cancelled
            && w.Status != MaintenanceStatus.Completed && w.StartsAt < r.EndsAt && r.StartsAt < w.EndsAt, ct);
        if (overlap) return Conflict(new { error = "overlap", message = "The server already has a maintenance window in that period." });
        var window = new MaintenanceWindow
        {
            ServerId = r.ServerId, StartsAt = r.StartsAt, EndsAt = r.EndsAt, DrainTimeoutMinutes = r.DrainTimeoutMinutes,
            AutoRestore = r.AutoRestore, Reason = r.Reason, CreatedBy = Actor, CreatedAt = time.GetUtcNow()
        };
        db.MaintenanceWindows.Add(window);
        await db.SaveChangesAsync(ct);
        return Ok(new { window.Id });
    }

    [HttpPost("{id:guid}/cancel"), Authorize(Permissions.ServersOperate)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var w = await db.MaintenanceWindows.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w is null) return NotFound();
        if (w.Status != MaintenanceStatus.Scheduled) return Conflict(new { error = "in_progress", message = "Only scheduled windows can be cancelled; restore the server instead." });
        w.Status = MaintenanceStatus.Cancelled;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

public sealed record RevokeRequest(string? Reason);

[Route("api/sessions")]
public sealed class SessionsController(WingsDbContext db, IStateStore store, IAuditTrail audit, ISystemSettings settings, TimeProvider time)
    : AdminControllerBase
{
    [HttpGet, Authorize(Permissions.SessionsRead)]
    public async Task<IActionResult> List([FromQuery] Guid? companyId, [FromQuery] Guid? serverId, [FromQuery] string? username,
        [FromQuery] int take = 200, CancellationToken ct = default)
    {
        var since = time.GetUtcNow().AddMinutes(-await settings.GetNumberAsync(SettingKeys.SessionIdleMinutes, ct));
        var q = db.ActiveSessions.AsNoTracking().Where(s => s.EndedAt == null && s.RevokedAt == null && s.LastSeenAt >= since);
        if (companyId is { } c) q = q.Where(s => s.CompanyId == c);
        if (serverId is { } sv) q = q.Where(s => s.ServerId == sv);
        var joined = q.Join(db.UserCompanyMemberships, s => new { s.CompanyId, s.UserId }, m => new { m.CompanyId, m.UserId },
            (s, m) => new { s, m.Username, Company = m.Company!.Code });
        if (!string.IsNullOrWhiteSpace(username))
        {
            var n = IdentifierNormalizer.Normalize(username);
            joined = joined.Where(x => x.Username.ToUpper().Contains(n));
        }
        var rows = await joined.OrderByDescending(x => x.s.LastSeenAt).Take(Math.Clamp(take, 1, 2000)).ToListAsync(ct);
        var servers = await db.Servers.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var apps = await db.Applications.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Code, ct);
        return Ok(rows.Select(x => new
        {
            x.s.Id, x.s.SessionKey, x.s.CompanyId, x.Company, x.s.UserId, x.Username, x.s.ApplicationId,
            Application = apps.GetValueOrDefault(x.s.ApplicationId), x.s.ServerId,
            Server = x.s.ServerId is { } id ? servers.GetValueOrDefault(id) : null, x.s.StartedAt, x.s.LastSeenAt, x.s.GatewayInstance,
            x.s.ClientAddress, x.s.UserAgent
        }));
    }

    [HttpPost("{sessionKey}/revoke"), Authorize(Permissions.SessionsRevoke)]
    public async Task<IActionResult> Revoke(string sessionKey, RevokeRequest? request, CancellationToken ct)
    {
        var rows = await db.ActiveSessions.Where(s => s.SessionKey == sessionKey && s.RevokedAt == null).ToListAsync(ct);
        if (rows.Count == 0) return NotFound();
        var now = time.GetUtcNow();
        foreach (var r in rows)
        {
            r.RevokedAt = now;
            r.RevokedBy = Actor;
            r.EndedAt = now;
            r.EndReason = request?.Reason ?? "Revoked by administrator";
        }
        await db.SaveChangesAsync(ct);
        await store.PublishAsync(StateKeys.SessionRevokedChannel, sessionKey);
        await audit.WriteAsync(new AuditEvent(AuditEvents.SessionRevoked)
        {
            CompanyId = rows[0].CompanyId, UserId = rows[0].UserId, Reason = request?.Reason
        }, ct);
        return NoContent();
    }
}

[Route("api/audit")]
public sealed class AuditController(WingsDbContext db) : AdminControllerBase
{
    [HttpGet, Authorize(Permissions.AuditRead)]
    public async Task<IActionResult> Search([FromQuery] string? eventType, [FromQuery] string? actor, [FromQuery] Guid? companyId,
        [FromQuery] Guid? serverId, [FromQuery] Guid? userId, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] string? search, [FromQuery] int skip = 0, [FromQuery] int take = 100, [FromQuery] string? format = null,
        CancellationToken ct = default)
    {
        var q = db.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(eventType)) q = q.Where(a => a.EventType == eventType);
        if (!string.IsNullOrWhiteSpace(actor)) q = q.Where(a => a.Actor.Contains(actor));
        if (companyId is { } c) q = q.Where(a => a.CompanyId == c);
        if (serverId is { } s) q = q.Where(a => a.ServerId == s);
        if (userId is { } u) q = q.Where(a => a.UserId == u);
        if (from is { } f) q = q.Where(a => a.At >= f);
        if (to is { } t) q = q.Where(a => a.At <= t);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpper();
            q = q.Where(a => (a.Reason != null && a.Reason.ToUpper().Contains(term)) || (a.NewValue != null && a.NewValue.ToUpper().Contains(term)));
        }

        if (format is "csv" or "xlsx")
        {
            var export = await q.OrderByDescending(a => a.At).Take(100_000).ToListAsync(ct);
            var table = new ReportTable("audit", "Audit log",
                ["Time (UTC)", "Event", "Actor", "Actor type", "Company", "User", "Server", "Entity", "Previous", "New", "Reason", "Correlation", "Source IP", "Severity"],
                export.Select(a => new object?[] { a.At, a.EventType, a.Actor, a.ActorType, a.CompanyId, a.UserId, a.ServerId,
                    $"{a.EntityType} {a.EntityId}", a.PreviousValue, a.NewValue, a.Reason, a.CorrelationId, a.SourceIp, a.Severity.ToString() }).ToList());
            return format == "csv"
                ? File(ReportService.ToCsv(table), "text/csv", "audit-log.csv")
                : File(ReportService.ToExcel(table), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "audit-log.xlsx");
        }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(a => a.At).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
        return Ok(new { total, rows });
    }

    [HttpGet("event-types"), Authorize(Permissions.AuditRead)]
    public async Task<IActionResult> EventTypes(CancellationToken ct) =>
        Ok(await db.AuditLogs.AsNoTracking().Select(a => a.EventType).Distinct().OrderBy(e => e).ToListAsync(ct));
}

public sealed record ResolveAlertRequest(string? Note);
public sealed record ChannelRequest([Required, MaxLength(100)] string Name, NotificationChannelType Type, [Required, MaxLength(2000)] string Target,
    AlertSeverity MinimumSeverity, bool IsEnabled);

[Route("api/alerts")]
public sealed class AlertsController(WingsDbContext db, IAlertService alerts, INotificationDispatcher notifications) : AdminControllerBase
{
    [HttpGet, Authorize(Permissions.AlertsRead)]
    public async Task<IActionResult> List([FromQuery] AlertStatus? status, [FromQuery] AlertSeverity? severity, [FromQuery] int take = 200,
        CancellationToken ct = default)
    {
        var q = db.Alerts.AsNoTracking().AsQueryable();
        if (status is { } s) q = q.Where(a => a.Status == s);
        else q = q.Where(a => a.Status != AlertStatus.Resolved);
        if (severity is { } sev) q = q.Where(a => a.Severity >= sev);
        var servers = await db.Servers.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var companies = await db.Companies.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Code, ct);
        var rows = await q.OrderByDescending(a => a.Severity).ThenByDescending(a => a.LastRaisedAt).Take(Math.Clamp(take, 1, 1000)).ToListAsync(ct);
        return Ok(rows.Select(a => new
        {
            a.Id, a.Type, Severity = a.Severity.ToString(), Status = a.Status.ToString(), a.Title, a.Message, a.ServerId,
            Server = a.ServerId is { } sid ? servers.GetValueOrDefault(sid) : null, a.CompanyId,
            Company = a.CompanyId is { } cid ? companies.GetValueOrDefault(cid) : null, a.FirstRaisedAt, a.LastRaisedAt, a.Occurrences,
            a.AcknowledgedAt, a.AcknowledgedBy, a.ResolvedAt, a.ResolvedBy, a.ResolutionNote
        }));
    }

    [HttpPost("{id:guid}/acknowledge"), Authorize(Permissions.AlertsManage)]
    public async Task<IActionResult> Acknowledge(Guid id, CancellationToken ct)
    {
        await alerts.AcknowledgeAsync(id, Actor, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/resolve"), Authorize(Permissions.AlertsManage)]
    public async Task<IActionResult> Resolve(Guid id, ResolveAlertRequest? request, CancellationToken ct)
    {
        await alerts.ResolveByIdAsync(id, Actor, request?.Note, ct);
        return NoContent();
    }

    [HttpGet("channels"), Authorize(Permissions.AlertsRead)]
    public async Task<IActionResult> Channels(CancellationToken ct) =>
        Ok(await db.NotificationChannels.AsNoTracking().OrderBy(c => c.Name).Select(c => new
        {
            c.Id, c.Name, Type = c.Type.ToString(), c.Target, MinimumSeverity = c.MinimumSeverity.ToString(), c.IsEnabled
        }).ToListAsync(ct));

    [HttpPost("channels"), Authorize(Permissions.AlertsManage)]
    public async Task<IActionResult> CreateChannel(ChannelRequest r, CancellationToken ct)
    {
        var error = ValidateTarget(r);
        if (error is not null) return BadRequest(new { error = "validation_failed", errors = new[] { error } });
        var channel = new NotificationChannel { Name = r.Name, Type = r.Type, Target = r.Target, MinimumSeverity = r.MinimumSeverity, IsEnabled = r.IsEnabled };
        db.NotificationChannels.Add(channel);
        await db.SaveChangesAsync(ct);
        return Ok(new { channel.Id });
    }

    [HttpPut("channels/{id:guid}"), Authorize(Permissions.AlertsManage)]
    public async Task<IActionResult> UpdateChannel(Guid id, ChannelRequest r, CancellationToken ct)
    {
        var channel = await db.NotificationChannels.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (channel is null) return NotFound();
        var error = ValidateTarget(r);
        if (error is not null) return BadRequest(new { error = "validation_failed", errors = new[] { error } });
        channel.Name = r.Name;
        channel.Type = r.Type;
        channel.Target = r.Target;
        channel.MinimumSeverity = r.MinimumSeverity;
        channel.IsEnabled = r.IsEnabled;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("channels/{id:guid}"), Authorize(Permissions.AlertsManage)]
    public async Task<IActionResult> DeleteChannel(Guid id, CancellationToken ct)
    {
        var channel = await db.NotificationChannels.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (channel is null) return NotFound();
        db.NotificationChannels.Remove(channel);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("channels/{id:guid}/test"), Authorize(Permissions.AlertsManage)]
    public async Task<IActionResult> TestChannel(Guid id, CancellationToken ct)
    {
        var channel = await db.NotificationChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (channel is null) return NotFound();
        var error = await notifications.TestAsync(channel, ct);
        return Ok(new { success = error is null, error });
    }

    private static string? ValidateTarget(ChannelRequest r) => r.Type switch
    {
        NotificationChannelType.Email when r.Target.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Any(a => !new EmailAddressAttribute().IsValid(a.Trim())) => "Enter valid e-mail addresses separated by commas",
        NotificationChannelType.Webhook or NotificationChannelType.Teams when !Uri.TryCreate(r.Target, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps
            => "Webhook URLs must be absolute https URLs",
        _ => null
    };
}

[Route("api/reports")]
public sealed class ReportsController(ReportService reports) : AdminControllerBase
{
    [HttpGet, Authorize(Permissions.ReportsRead)]
    public IActionResult List() => Ok(ReportService.Definitions);

    [HttpGet("{name}"), Authorize(Permissions.ReportsRead)]
    public async Task<IActionResult> Run(string name, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] string? format, CancellationToken ct)
    {
        var table = await reports.RunAsync(name, from, to, ct);
        var file = $"{name}-{DateTime.UtcNow:yyyyMMdd-HHmm}";
        return format switch
        {
            "csv" => File(ReportService.ToCsv(table), "text/csv", file + ".csv"),
            "xlsx" => File(ReportService.ToExcel(table), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file + ".xlsx"),
            _ => Ok(table)
        };
    }
}

public sealed record SettingUpdate([Required] string Key, [Required, MaxLength(4000)] string Value);

[Route("api/settings")]
public sealed class SettingsController(WingsDbContext db, IAuditTrail audit, TimeProvider time) : AdminControllerBase
{
    [HttpGet, Authorize(Permissions.MonitoringRead)]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(await db.SystemSettings.AsNoTracking().OrderBy(s => s.Key).ToListAsync(ct));

    [HttpPut, Authorize(Permissions.SettingsWrite)]
    public async Task<IActionResult> Update(List<SettingUpdate> updates, CancellationToken ct)
    {
        var known = SettingKeys.Defaults.Keys.ToHashSet();
        var unknown = updates.Where(u => !known.Contains(u.Key)).Select(u => u.Key).ToList();
        if (unknown.Count > 0) return BadRequest(new { error = "validation_failed", errors = unknown.Select(k => $"Unknown setting {k}") });
        var bad = updates.Where(u => !double.TryParse(u.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) || v < 0)
            .Select(u => $"{u.Key} must be a non-negative number").ToList();
        if (bad.Count > 0) return BadRequest(new { error = "validation_failed", errors = bad });
        var rows = await db.SystemSettings.ToDictionaryAsync(s => s.Key, ct);
        foreach (var u in updates)
        {
            if (!rows.TryGetValue(u.Key, out var row))
                db.SystemSettings.Add(new SystemSetting { Key = u.Key, Value = u.Value, ModifiedAt = time.GetUtcNow(), ModifiedBy = Actor });
            else
            {
                row.Value = u.Value;
                row.ModifiedAt = time.GetUtcNow();
                row.ModifiedBy = Actor;
            }
        }
        audit.Add(db, new AuditEvent(AuditEvents.SettingsChanged) { New = updates });
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

public sealed record AdministratorRequest([Required, MaxLength(100)] string Username, [Required, MaxLength(200)] string DisplayName,
    [EmailAddress] string? Email, [MinLength(12)] string? Password, AccountStatus Status, List<string> Roles);

[Route("api/admins")]
public sealed class AdministratorsController(WingsDbContext db, IPasswordHasher hasher) : AdminControllerBase
{
    [HttpGet, Authorize(Permissions.AdministratorsManage)]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(await db.Administrators.AsNoTracking().Include(a => a.Roles).ThenInclude(r => r.Role).OrderBy(a => a.Username).Select(a => new
        {
            a.Id, a.Username, a.DisplayName, a.Email, Status = a.Status.ToString(), a.MfaEnabled, a.LastLoginAt, a.LockoutEndAt,
            Roles = a.Roles.Select(r => r.Role!.Name)
        }).ToListAsync(ct));

    [HttpGet("roles"), Authorize(Permissions.AdministratorsManage)]
    public async Task<IActionResult> Roles(CancellationToken ct) =>
        Ok(await db.Roles.AsNoTracking().Include(r => r.Permissions).ThenInclude(p => p.Permission).OrderBy(r => r.Name).Select(r => new
        {
            r.Id, r.Name, r.Description, r.IsSystem, Permissions = r.Permissions.Select(p => p.Permission!.Code)
        }).ToListAsync(ct));

    [HttpGet("permissions"), Authorize(Permissions.AdministratorsManage)]
    public IActionResult PermissionCatalog() => Ok(Permissions.Descriptions.Select(p => new { code = p.Key, description = p.Value }));

    [HttpPost, Authorize(Permissions.AdministratorsManage)]
    public async Task<IActionResult> Create(AdministratorRequest r, CancellationToken ct)
    {
        var normalized = IdentifierNormalizer.Normalize(r.Username);
        if (await db.Administrators.AnyAsync(a => a.NormalizedUsername == normalized, ct)) return Conflict(new { error = "duplicate_username" });
        if (string.IsNullOrEmpty(r.Password)) return BadRequest(new { error = "validation_failed", errors = new[] { "A password of at least 12 characters is required" } });
        var admin = new Administrator
        {
            Username = r.Username.Trim(), NormalizedUsername = normalized, DisplayName = r.DisplayName, Email = r.Email,
            PasswordHash = hasher.Hash(r.Password), Status = r.Status
        };
        await SetRolesAsync(admin, r.Roles, ct);
        db.Administrators.Add(admin);
        await db.SaveChangesAsync(ct);
        return Ok(new { admin.Id });
    }

    [HttpPut("{id:guid}"), Authorize(Permissions.AdministratorsManage)]
    public async Task<IActionResult> Update(Guid id, AdministratorRequest r, CancellationToken ct)
    {
        var admin = await db.Administrators.Include(a => a.Roles).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (admin is null) return NotFound();
        if (admin.Username == Actor && r.Status != AccountStatus.Active)
            return BadRequest(new { error = "validation_failed", errors = new[] { "You cannot disable your own account" } });
        admin.DisplayName = r.DisplayName;
        admin.Email = r.Email;
        admin.Status = r.Status;
        if (!string.IsNullOrEmpty(r.Password)) admin.PasswordHash = hasher.Hash(r.Password);
        await SetRolesAsync(admin, r.Roles, ct);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task SetRolesAsync(Administrator admin, List<string> roleNames, CancellationToken ct)
    {
        var roles = await db.Roles.Where(x => roleNames.Contains(x.Name)).ToListAsync(ct);
        admin.Roles.RemoveAll(x => roles.All(role => role.Id != x.RoleId));
        foreach (var role in roles.Where(role => admin.Roles.All(x => x.RoleId != role.Id)))
            admin.Roles.Add(new AdministratorRole { AdministratorId = admin.Id, RoleId = role.Id });
    }
}
