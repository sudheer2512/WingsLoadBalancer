using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Wings.Admin.Api.Security;
using Wings.Admin.Api.Services;
using Wings.LoadBalancer.Data;
using Wings.LoadBalancer.Data.Entities;
using Wings.LoadBalancer.Infrastructure.Auditing;
using Wings.LoadBalancer.Infrastructure.Security;
using Wings.Routing;

namespace Wings.Admin.Api.Controllers;

[ApiController]
[Produces("application/json")]
public abstract class AdminControllerBase : ControllerBase
{
    protected string Actor => User.Identity?.Name ?? "unknown";
}

/// <summary>Maps service exceptions to consistent problem responses.</summary>
public sealed class ApiExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        context.Result = context.Exception switch
        {
            ValidationFailedException v => new BadRequestObjectResult(new { error = "validation_failed", errors = v.Errors }),
            ConflictException c => new ConflictObjectResult(new { error = "conflict", message = c.Message }),
            DbUpdateConcurrencyException => new ConflictObjectResult(new { error = "conflict", message = "The record was changed by someone else. Reload and try again." }),
            KeyNotFoundException k => new NotFoundObjectResult(new { error = "not_found", message = k.Message }),
            _ => null
        };
        context.ExceptionHandled = context.Result is not null;
    }
}

public sealed record AdminLoginRequest(string Username, string Password, string? MfaCode);
public sealed record MfaEnableRequest(string Code);
public sealed record MfaSetupRequest(string? CurrentCode);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

[Route("api/auth")]
public sealed class AuthController(WingsDbContext db, IPasswordHasher hasher, ITotpService totp, IAuditTrail audit, TimeProvider time)
    : AdminControllerBase
{
    public const string RateLimitPolicy = "admin-login";

    /// <summary>Issues the XSRF-TOKEN cookie (the CSRF middleware sets it on every GET).</summary>
    [HttpGet("csrf"), AllowAnonymous]
    public IActionResult Csrf() => NoContent();

    [HttpPost("login"), AllowAnonymous, EnableRateLimiting(RateLimitPolicy)]
    public async Task<IActionResult> Login(AdminLoginRequest request, CancellationToken ct)
    {
        var normalized = IdentifierNormalizer.Normalize(request.Username);
        var admin = await db.Administrators.Include(a => a.Roles).ThenInclude(r => r.Role!).ThenInclude(r => r.Permissions)
            .ThenInclude(p => p.Permission).FirstOrDefaultAsync(a => a.NormalizedUsername == normalized, ct);
        var now = time.GetUtcNow();
        var invalid = Unauthorized(new { error = "invalid_credentials", message = "The username or password is incorrect." });

        if (admin is null || admin.Status != AccountStatus.Active || admin.LockoutEndAt > now)
        {
            hasher.VerifyDummy(request.Password ?? "");
            await AuditFailure(request.Username, admin is null ? "Unknown administrator" : admin.LockoutEndAt > now ? "Locked out" : "Disabled", ct);
            return invalid;
        }
        if (!hasher.Verify(request.Password ?? "", admin.PasswordHash))
        {
            admin.FailedLoginCount++;
            if (admin.FailedLoginCount >= 5) admin.LockoutEndAt = now.AddMinutes(15);
            await db.SaveChangesAsync(ct);
            await AuditFailure(request.Username, "Wrong password", ct);
            return invalid;
        }
        if (admin.MfaEnabled)
        {
            if (string.IsNullOrWhiteSpace(request.MfaCode)) return Ok(new { mfaRequired = true });
            if (!totp.Verify(admin.MfaSecretProtected, request.MfaCode))
            {
                admin.FailedLoginCount++;
                await db.SaveChangesAsync(ct);
                await AuditFailure(request.Username, "Wrong MFA code", ct);
                return invalid;
            }
        }

        admin.FailedLoginCount = 0;
        admin.LockoutEndAt = null;
        admin.LastLoginAt = now;
        await db.SaveChangesAsync(ct);

        var permissions = admin.Roles.SelectMany(r => r.Role!.Permissions).Select(p => p.Permission!.Code).Distinct().ToList();
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, admin.Username),
            new(AdminClaims.AdministratorId, admin.Id.ToString()),
            new("display_name", admin.DisplayName)
        };
        claims.AddRange(permissions.Select(p => new Claim(AdminClaims.Permission, p)));
        claims.AddRange(admin.Roles.Select(r => new Claim(ClaimTypes.Role, r.Role!.Name)));
        await HttpContext.SignInAsync(AdminSecurityExtensions.Scheme, new ClaimsPrincipal(new ClaimsIdentity(claims, AdminSecurityExtensions.Scheme)));
        await audit.WriteAsync(new AuditEvent(AuditEvents.AdministratorLogin) { Actor = admin.Username, ActorType = "Administrator" }, ct);
        return Ok(Me(admin.Username, admin.DisplayName, permissions, admin.MfaEnabled));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(AdminSecurityExtensions.Scheme);
        return NoContent();
    }

    [HttpGet("me"), AllowAnonymous]
    public async Task<IActionResult> GetMe(CancellationToken ct)
    {
        if (User.Identity?.IsAuthenticated != true) return Ok(new { authenticated = false });
        var id = Guid.Parse(User.FindFirstValue(AdminClaims.AdministratorId)!);
        var mfa = await db.Administrators.Where(a => a.Id == id).Select(a => a.MfaEnabled).FirstOrDefaultAsync(ct);
        return Ok(Me(User.Identity.Name!, User.FindFirstValue("display_name") ?? User.Identity.Name!,
            User.FindAll(AdminClaims.Permission).Select(c => c.Value).ToList(), mfa));
    }

    /// <summary>Starts enrolment. When MFA is already on, the current code is required, so an abandoned or
    /// hijacked setup cannot switch MFA off.</summary>
    [HttpPost("mfa/setup")]
    public async Task<IActionResult> SetupMfa(MfaSetupRequest? request, CancellationToken ct)
    {
        var admin = await CurrentAsync(ct);
        if (admin.MfaEnabled && !totp.Verify(admin.MfaSecretProtected, request?.CurrentCode))
            return BadRequest(new { error = "current_code_required", message = "Enter a code from your current authenticator to re-enrol." });
        var (secret, uri) = totp.CreateSecret("Wings Load Balancer", admin.Username);
        admin.MfaSecretProtected = secret;
        admin.MfaEnabled = false;
        await db.SaveChangesAsync(ct);
        return Ok(new { provisioningUri = uri });
    }

    [HttpPost("mfa/enable")]
    public async Task<IActionResult> EnableMfa(MfaEnableRequest request, CancellationToken ct)
    {
        var admin = await CurrentAsync(ct);
        if (!totp.Verify(admin.MfaSecretProtected, request.Code)) return BadRequest(new { error = "invalid_code" });
        admin.MfaEnabled = true;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        var admin = await CurrentAsync(ct);
        if (!hasher.Verify(request.CurrentPassword, admin.PasswordHash)) return BadRequest(new { error = "invalid_password" });
        if (request.NewPassword.Length < 12) return BadRequest(new { error = "weak_password", message = "Use at least 12 characters." });
        admin.PasswordHash = hasher.Hash(request.NewPassword);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<Administrator> CurrentAsync(CancellationToken ct)
    {
        var id = Guid.Parse(User.FindFirstValue(AdminClaims.AdministratorId)!);
        return await db.Administrators.FirstAsync(a => a.Id == id, ct);
    }

    private Task AuditFailure(string username, string reason, CancellationToken ct) =>
        audit.WriteAsync(new AuditEvent(AuditEvents.AdministratorLoginFailed)
        {
            Actor = username.Length > 100 ? username[..100] : username, ActorType = "Administrator", Reason = reason,
            Severity = AlertSeverity.Warning
        }, ct);

    private static object Me(string username, string displayName, IReadOnlyCollection<string> permissions, bool mfa) =>
        new { authenticated = true, username, displayName, permissions, mfaEnabled = mfa };
}
