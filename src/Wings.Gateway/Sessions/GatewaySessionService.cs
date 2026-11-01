using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Wings.LoadBalancer.Infrastructure.Authentication;
using Wings.LoadBalancer.Infrastructure.Monitoring;
using Wings.LoadBalancer.Infrastructure.Sessions;

namespace Wings.Gateway.Sessions;

public static class WingsClaims
{
    public const string SessionId = "wings:sid";
    public const string CompanyId = "wings:cid";
    public const string UserId = "wings:uid";
    public const string CompanyCode = "wings:ccode";
    public const string Username = "wings:uname";
    public const string Method = "wings:amr";
    public const string IssuedAt = "wings:iat";
}

public enum SessionResolution { Valid, Anonymous, Revoked, Expired }

/// <summary>
/// Creates and validates gateway sessions. The cookie is a Data Protection-encrypted ticket (tamper-proof); the
/// session record in the store adds revocation, absolute expiry and per-application affinity.
/// </summary>
public sealed class GatewaySessionService(
    ISessionStore store,
    SessionActivityWriter activity,
    IOptions<GatewayOptions> options,
    TimeProvider time,
    ILogger<GatewaySessionService> logger)
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    public async Task<SessionRecord> SignInAsync(HttpContext context, AuthenticationResult result)
    {
        var now = time.GetUtcNow();
        var record = new SessionRecord
        {
            SessionId = SessionKeys.NewSessionId(),
            CompanyId = result.CompanyId,
            UserId = result.UserId,
            CompanyCode = result.CompanyCode!,
            Username = result.Username!,
            AuthenticationMethod = result.Method ?? "password",
            CreatedAt = now,
            LastSeenAt = now,
            AbsoluteExpiresAt = now.AddHours(options.Value.SessionAbsoluteHours)
        };
        await store.SaveAsync(record);

        var identity = new ClaimsIdentity(
        [
            new Claim(WingsClaims.SessionId, record.SessionId),
            new Claim(WingsClaims.CompanyId, record.CompanyId.ToString()),
            new Claim(WingsClaims.UserId, record.UserId.ToString()),
            new Claim(WingsClaims.CompanyCode, record.CompanyCode),
            new Claim(WingsClaims.Username, record.Username),
            new Claim(WingsClaims.Method, record.AuthenticationMethod),
            new Claim(WingsClaims.IssuedAt, now.ToUnixTimeSeconds().ToString()),
            new Claim(ClaimTypes.Name, $"{record.CompanyCode}/{record.Username}")
        ], Scheme);
        await context.SignInAsync(Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            IsPersistent = false,
            AllowRefresh = true,
            ExpiresUtc = record.AbsoluteExpiresAt
        });
        return record;
    }

    /// <summary>Resolves the session for a request from the protected cookie ticket and the session store.</summary>
    public async Task<(SessionResolution Resolution, SessionRecord? Record)> ResolveAsync(HttpContext context)
    {
        var result = await context.AuthenticateAsync(Scheme);
        if (!result.Succeeded || result.Principal is not { } principal) return (SessionResolution.Anonymous, null);

        var sid = principal.FindFirstValue(WingsClaims.SessionId);
        if (!Guid.TryParse(principal.FindFirstValue(WingsClaims.CompanyId), out var companyId)
            || !Guid.TryParse(principal.FindFirstValue(WingsClaims.UserId), out var userId)
            || string.IsNullOrEmpty(sid))
            return (SessionResolution.Anonymous, null);

        var lookup = await store.GetAsync(sid);
        var now = time.GetUtcNow();
        switch (lookup.Status)
        {
            case SessionLookupStatus.Revoked:
                return (SessionResolution.Revoked, null);
            case SessionLookupStatus.Expired:
                return (SessionResolution.Expired, null);
            case SessionLookupStatus.Found when lookup.Record!.CompanyId != companyId || lookup.Record.UserId != userId:
                // Cookie and server-side record disagree: treat as tampering.
                logger.LogWarning("Session {SessionKey} identity mismatch between ticket and store", SessionKeys.Hash(sid));
                return (SessionResolution.Revoked, null);
            case SessionLookupStatus.Found:
                return (SessionResolution.Valid, lookup.Record);
        }

        // Missing record. With a shared store this means expired/evicted → sign in again. Without one (Redis down or
        // not configured, or another gateway) the encrypted ticket is still authoritative within its lifetime.
        if (store.IsShared) return (SessionResolution.Expired, null);
        var issued = long.TryParse(principal.FindFirstValue(WingsClaims.IssuedAt), out var iat)
            ? DateTimeOffset.FromUnixTimeSeconds(iat)
            : now;
        var rebuilt = new SessionRecord
        {
            SessionId = sid,
            CompanyId = companyId,
            UserId = userId,
            CompanyCode = principal.FindFirstValue(WingsClaims.CompanyCode) ?? "",
            Username = principal.FindFirstValue(WingsClaims.Username) ?? "",
            AuthenticationMethod = principal.FindFirstValue(WingsClaims.Method) ?? "password",
            CreatedAt = issued,
            LastSeenAt = now,
            AbsoluteExpiresAt = issued.AddHours(options.Value.SessionAbsoluteHours)
        };
        if (rebuilt.AbsoluteExpiresAt <= now) return (SessionResolution.Expired, null);
        await store.SaveAsync(rebuilt);
        return (SessionResolution.Valid, rebuilt);
    }

    public async Task SignOutAsync(HttpContext context, string reason)
    {
        var (resolution, record) = await ResolveAsync(context);
        if (resolution == SessionResolution.Valid && record is not null)
        {
            await store.RevokeAsync(record.SessionId);
            activity.Record(new SessionActivity(SessionKeys.Hash(record.SessionId), record.CompanyId, record.UserId,
                Guid.Empty, null, time.GetUtcNow(), null, null, reason));
        }
        await context.SignOutAsync(Scheme);
    }

    public Task SaveAsync(SessionRecord record) => store.SaveAsync(record);
}
