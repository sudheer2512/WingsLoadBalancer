using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wings.LoadBalancer.Data;
using Wings.LoadBalancer.Data.Entities;
using Wings.LoadBalancer.Infrastructure.Auditing;
using Wings.LoadBalancer.Infrastructure.Security;
using Wings.Routing;
using Wings.Routing.Model;

namespace Wings.LoadBalancer.Infrastructure.Authentication;

public sealed record CredentialRequest(string Company, string Username, string Password, string? MfaCode, string? ClientIp);

public enum AuthenticationStatus { Success, InvalidCredentials, MfaRequired, Unavailable }

public sealed record AuthenticationResult(AuthenticationStatus Status)
{
    public Guid CompanyId { get; init; }
    public Guid UserId { get; init; }
    public string? CompanyCode { get; init; }
    public string? Username { get; init; }
    public string? DisplayName { get; init; }
    public string? Method { get; init; }
    public bool MfaSatisfied { get; init; }
    /// <summary>Internal reason for audit logs only; never returned to the client.</summary>
    public string? InternalReason { get; init; }

    public static AuthenticationResult Invalid(string reason) =>
        new(AuthenticationStatus.InvalidCredentials) { InternalReason = reason };
}

public sealed class AuthenticationOptions
{
    public const string Section = "Authentication";
    public int MaxFailedAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
}

/// <summary>
/// Validates user credentials for a company. Company and username only locate the identity; authorisation comes from
/// verified credentials plus an active membership in our own database. All failures look identical to callers.
/// </summary>
public interface IUserAuthenticator
{
    Task<AuthenticationResult> AuthenticateAsync(CredentialRequest request, CancellationToken cancellationToken);

    /// <summary>Maps a validated external identity (OIDC) to a membership, re-checking the company association.</summary>
    Task<AuthenticationResult> ResolveExternalAsync(string company, string subject, string? username, CancellationToken cancellationToken);
}

/// <summary>Adapter for the existing Wings Authentication service.</summary>
public interface IExternalCredentialValidator
{
    Task<ExternalValidationResult> ValidateAsync(string companyCode, string username, string password, string? mfaCode,
        CancellationToken cancellationToken);
}

public sealed record ExternalValidationResult(bool Valid, string? Subject, string? CompanyCode, bool MfaSatisfied, bool ServiceAvailable = true);

public sealed class UserAuthenticator(
    WingsDbContext db,
    IPasswordHasher hasher,
    ITotpService totp,
    IExternalCredentialValidator external,
    IAuditTrail audit,
    IOptions<AuthenticationOptions> options,
    TimeProvider time,
    ILogger<UserAuthenticator> logger) : IUserAuthenticator
{
    public async Task<AuthenticationResult> AuthenticateAsync(CredentialRequest request, CancellationToken ct)
    {
        var result = await AuthenticateCoreAsync(request, ct);
        await audit.WriteAsync(new AuditEvent(result.Status == AuthenticationStatus.Success ? AuditEvents.UserLogin : AuditEvents.UserLoginFailed)
        {
            CompanyId = result.CompanyId == Guid.Empty ? null : result.CompanyId,
            UserId = result.UserId == Guid.Empty ? null : result.UserId,
            Reason = result.Status == AuthenticationStatus.Success ? result.Method : result.InternalReason ?? result.Status.ToString(),
            Actor = Truncate($"{request.Company}/{request.Username}"),
            ActorType = "User",
            SourceIp = request.ClientIp,
            Severity = result.Status == AuthenticationStatus.Success ? AlertSeverity.Info : AlertSeverity.Warning
        }, ct);
        return result;
    }

    private async Task<AuthenticationResult> AuthenticateCoreAsync(CredentialRequest request, CancellationToken ct)
    {
        var company = await FindCompanyAsync(request.Company, ct);
        if (company is null || company.Status != CompanyStatus.Active)
        {
            hasher.VerifyDummy(request.Password);
            return AuthenticationResult.Invalid(company is null ? "Unknown company" : $"Company {company.Status}");
        }

        var normalizedUser = IdentifierNormalizer.Normalize(request.Username);
        var membership = await db.UserCompanyMemberships.Include(m => m.User)
            .FirstOrDefaultAsync(m => m.CompanyId == company.Id && m.NormalizedUsername == normalizedUser, ct);

        return company.AuthenticationMode switch
        {
            AuthenticationMode.Local => await LocalAsync(company, membership, request, ct),
            AuthenticationMode.WingsAuthService => await ExternalAsync(company, membership, request, ct),
            _ => DummyInvalid(request, "Company requires federated sign-in")
        };
    }

    private AuthenticationResult DummyInvalid(CredentialRequest request, string reason)
    {
        hasher.VerifyDummy(request.Password);
        return AuthenticationResult.Invalid(reason);
    }

    private async Task<AuthenticationResult> LocalAsync(Company company, UserCompanyMembership? membership,
        CredentialRequest request, CancellationToken ct)
    {
        var user = membership?.User;
        if (membership is null || user is null)
            return DummyInvalid(request, "Unknown user");
        if (membership.Status != AccountStatus.Active || user.Status != AccountStatus.Active)
            return DummyInvalid(request, "Disabled account") with { CompanyId = company.Id, UserId = user.Id };

        var now = time.GetUtcNow();
        if (user.LockoutEndAt > now)
            return DummyInvalid(request, "Locked out") with { CompanyId = company.Id, UserId = user.Id };

        if (!hasher.Verify(request.Password, user.PasswordHash))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= options.Value.MaxFailedAttempts)
            {
                var multiplier = Math.Min(8, 1 << Math.Min(3, user.FailedLoginCount - options.Value.MaxFailedAttempts));
                user.LockoutEndAt = now.AddMinutes(options.Value.LockoutMinutes * multiplier);
            }
            await SaveQuietlyAsync(ct);
            return AuthenticationResult.Invalid("Wrong password") with { CompanyId = company.Id, UserId = user.Id };
        }

        if (user.MfaEnabled)
        {
            if (string.IsNullOrWhiteSpace(request.MfaCode))
                return new AuthenticationResult(AuthenticationStatus.MfaRequired) { CompanyId = company.Id, UserId = user.Id };
            if (!totp.Verify(user.MfaSecretProtected, request.MfaCode))
            {
                user.FailedLoginCount++;
                await SaveQuietlyAsync(ct);
                return AuthenticationResult.Invalid("Wrong MFA code") with { CompanyId = company.Id, UserId = user.Id };
            }
        }

        user.FailedLoginCount = 0;
        user.LockoutEndAt = null;
        user.LastLoginAt = now;
        await SaveQuietlyAsync(ct);
        return Success(company, membership, user, "password", user.MfaEnabled);
    }

    private async Task<AuthenticationResult> ExternalAsync(Company company, UserCompanyMembership? membership,
        CredentialRequest request, CancellationToken ct)
    {
        var validation = await external.ValidateAsync(company.Code, request.Username, request.Password, request.MfaCode, ct);
        if (!validation.ServiceAvailable) return new AuthenticationResult(AuthenticationStatus.Unavailable) { InternalReason = "Wings auth service unavailable" };
        if (!validation.Valid) return AuthenticationResult.Invalid("Rejected by Wings authentication service");

        // Never trust the external service's company claim on its own: it must match the company being signed in to.
        if (!string.Equals(IdentifierNormalizer.Normalize(validation.CompanyCode), company.NormalizedCode, StringComparison.Ordinal))
        {
            logger.LogWarning("Wings auth returned company {Returned} for login to {Company}", validation.CompanyCode, company.Code);
            return AuthenticationResult.Invalid("Company mismatch from authentication service");
        }
        if (membership is null && validation.Subject is { } subject)
        {
            membership = await db.UserCompanyMemberships.Include(m => m.User)
                .FirstOrDefaultAsync(m => m.CompanyId == company.Id && m.ExternalSubject == subject, ct);
        }
        if (membership?.User is not { } user || membership.Status != AccountStatus.Active || user.Status != AccountStatus.Active)
            return AuthenticationResult.Invalid("No active membership for externally validated user");
        if (membership.ExternalSubject is { } expected && validation.Subject is { } actual && expected != actual)
            return AuthenticationResult.Invalid("External subject mismatch");

        user.LastLoginAt = time.GetUtcNow();
        await SaveQuietlyAsync(ct);
        return Success(company, membership, user, "wings-auth", validation.MfaSatisfied);
    }

    public async Task<AuthenticationResult> ResolveExternalAsync(string companyInput, string subject, string? username, CancellationToken ct)
    {
        var company = await FindCompanyAsync(companyInput, ct);
        if (company is null || company.Status != CompanyStatus.Active || company.AuthenticationMode != AuthenticationMode.Oidc)
            return AuthenticationResult.Invalid("Company not configured for OIDC");
        var membership = await db.UserCompanyMemberships.Include(m => m.User)
            .FirstOrDefaultAsync(m => m.CompanyId == company.Id && m.ExternalSubject == subject, ct);
        if (membership?.User is not { } user || membership.Status != AccountStatus.Active || user.Status != AccountStatus.Active)
            return AuthenticationResult.Invalid("No active membership for OIDC subject");
        user.LastLoginAt = time.GetUtcNow();
        await SaveQuietlyAsync(ct);
        return Success(company, membership, user, "oidc", mfa: true);
    }

    private async Task<Company?> FindCompanyAsync(string input, CancellationToken ct)
    {
        var normalized = IdentifierNormalizer.Normalize(input);
        if (normalized.Length == 0) return null;
        return await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.NormalizedCode == normalized, ct)
               ?? await db.CompanyAliases.AsNoTracking().Where(a => a.NormalizedAlias == normalized)
                   .Select(a => a.Company).FirstOrDefaultAsync(ct);
    }

    private static AuthenticationResult Success(Company company, UserCompanyMembership membership, User user, string method, bool mfa) =>
        new(AuthenticationStatus.Success)
        {
            CompanyId = company.Id, UserId = user.Id, CompanyCode = company.Code, Username = membership.Username,
            DisplayName = user.DisplayName, Method = method, MfaSatisfied = mfa
        };

    private async Task SaveQuietlyAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Parallel logins for the same account; counters are best-effort.
        }
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200];
}

public sealed class WingsAuthServiceOptions
{
    public const string Section = "WingsAuthService";
    /// <summary>Base URL of the Wings Authentication service. Empty disables the adapter.</summary>
    public string? BaseUrl { get; set; }
    public string ValidatePath { get; set; } = "/api/v1/credentials/validate";
    /// <summary>Service-to-service API key from a secret store.</summary>
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// HTTP adapter. Request: <c>{ companyCode, username, password, mfaCode }</c>; response:
/// <c>{ valid, subject, companyCode, mfaSatisfied }</c>. Adjust <see cref="WingsAuthServiceOptions.ValidatePath"/> or
/// replace this class to match the deployed service.
/// </summary>
public sealed class WingsAuthServiceValidator(
    IHttpClientFactory httpClients,
    IOptions<WingsAuthServiceOptions> options,
    ILogger<WingsAuthServiceValidator> logger) : IExternalCredentialValidator
{
    public const string HttpClientName = "wings-auth-service";

    public async Task<ExternalValidationResult> ValidateAsync(string companyCode, string username, string password,
        string? mfaCode, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.BaseUrl)) return new(false, null, null, false, ServiceAvailable: false);
        try
        {
            var client = httpClients.CreateClient(HttpClientName);
            client.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(o.BaseUrl), o.ValidatePath))
            {
                Content = JsonContent.Create(new { companyCode, username, password, mfaCode })
            };
            if (o.ApiKey is { Length: > 0 }) request.Headers.TryAddWithoutValidation("X-Api-Key", o.ApiKey);
            using var response = await client.SendAsync(request, ct);
            if ((int)response.StatusCode >= 500) return new(false, null, null, false, ServiceAvailable: false);
            if (!response.IsSuccessStatusCode) return new(false, null, null, false);
            var body = await response.Content.ReadFromJsonAsync<ExternalValidationResult>(ct);
            return body is null ? new(false, null, null, false) : body with { ServiceAvailable = true };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Wings authentication service call failed");
            return new(false, null, null, false, ServiceAvailable: false);
        }
    }
}
