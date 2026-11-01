using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wings.LoadBalancer.Data;
using Wings.LoadBalancer.Data.Entities;

namespace Wings.Admin.Api.Security;

public static class AdminClaims
{
    public const string Permission = "wings:perm";
    public const string AdministratorId = "wings:admin";
}

/// <summary>Audit identity for the current admin request. Reason comes from the optional X-Audit-Reason header.</summary>
public sealed class HttpAuditContext(IHttpContextAccessor accessor) : IAuditContext
{
    private string? _reason;

    // No HTTP request means background work (seeding, drain controller, monitor) running in this process.
    public string Actor => accessor.HttpContext is { } http ? http.User.Identity?.Name ?? "anonymous" : "system";
    public string ActorType => accessor.HttpContext is { } http
        ? http.User.Identity?.IsAuthenticated == true ? "Administrator" : "Anonymous"
        : "System";
    public string? SourceIp => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
    public string? CorrelationId => accessor.HttpContext?.TraceIdentifier;

    public string? Reason
    {
        get
        {
            if (_reason is not null) return _reason;
            var header = accessor.HttpContext?.Request.Headers["X-Audit-Reason"].ToString();
            return string.IsNullOrWhiteSpace(header) ? null : header.Length > 500 ? header[..500] : header;
        }
        set => _reason = value;
    }
}

public static class AdminSecurityExtensions
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    public static IServiceCollection AddAdminSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        var secure = configuration.GetValue("Admin:RequireHttpsCookies", true);
        services.AddHttpContextAccessor();
        services.AddScoped<IAuditContext, HttpAuditContext>();

        services.AddAuthentication(Scheme).AddCookie(Scheme, o =>
        {
            o.Cookie.Name = configuration["Admin:CookieName"] ?? "__Host-Wings.Admin";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = secure ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromMinutes(configuration.GetValue("Admin:SessionMinutes", 60));
            o.SlidingExpiration = true;
            o.Events.OnRedirectToLogin = ctx => Status(ctx.Response, 401);
            o.Events.OnRedirectToAccessDenied = ctx => Status(ctx.Response, 403);
            o.Events.OnValidatePrincipal = async ctx =>
            {
                // Re-check the administrator every few minutes so disabling an account takes effect quickly.
                var issued = ctx.Properties.IssuedUtc ?? DateTimeOffset.MinValue;
                var lastCheck = ctx.Properties.Items.TryGetValue("checked", out var c) && DateTimeOffset.TryParse(c, out var v) ? v : issued;
                if (DateTimeOffset.UtcNow - lastCheck < TimeSpan.FromMinutes(5)) return;
                var id = ctx.Principal?.FindFirstValue(AdminClaims.AdministratorId);
                var db = ctx.HttpContext.RequestServices.GetRequiredService<WingsDbContext>();
                var active = Guid.TryParse(id, out var adminId) &&
                             await db.Administrators.AnyAsync(a => a.Id == adminId && a.Status == AccountStatus.Active);
                if (!active)
                {
                    ctx.RejectPrincipal();
                    return;
                }
                ctx.Properties.Items["checked"] = DateTimeOffset.UtcNow.ToString("O");
                ctx.ShouldRenew = true;
            };
        });

        services.AddAuthorization(o =>
        {
            foreach (var permission in Permissions.Descriptions.Keys)
                o.AddPolicy(permission, p => p.RequireAuthenticatedUser().RequireClaim(AdminClaims.Permission, permission));
            o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        });

        services.AddAntiforgery(o =>
        {
            o.HeaderName = "X-XSRF-TOKEN";
            o.Cookie.Name = "__Host-Wings.Admin.AF";
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = secure ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            if (!secure) o.Cookie.Name = "Wings.Admin.AF";
        });
        return services;
    }

    private static Task Status(HttpResponse response, int code)
    {
        response.StatusCode = code;
        return Task.CompletedTask;
    }

    /// <summary>
    /// CSRF protection for the cookie-authenticated API: every unsafe /api request must carry the antiforgery header.
    /// The token is issued as the XSRF-TOKEN cookie (readable by the Angular client, which echoes it back).
    /// </summary>
    public static IApplicationBuilder UseAdminCsrfProtection(this IApplicationBuilder app, bool secureCookies)
    {
        return app.Use(async (context, next) =>
        {
            var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
            var path = context.Request.Path;
            if (path.StartsWithSegments("/api"))
            {
                if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
                    && !HttpMethods.IsOptions(context.Request.Method))
                {
                    if (!await antiforgery.IsRequestValidAsync(context))
                    {
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        await context.Response.WriteAsJsonAsync(new { error = "csrf_token_invalid" });
                        return;
                    }
                }
                else
                {
                    var tokens = antiforgery.GetAndStoreTokens(context);
                    context.Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken!, new CookieOptions
                    {
                        HttpOnly = false, SameSite = SameSiteMode.Strict, Secure = secureCookies, Path = "/"
                    });
                }
            }
            await next();
        });
    }

    /// <summary>Conservative browser security headers for the admin UI.</summary>
    public static IApplicationBuilder UseAdminSecurityHeaders(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var h = context.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        if (!context.Request.Path.StartsWithSegments("/api/docs") && !context.Request.Path.StartsWithSegments("/openapi"))
        {
            h["Content-Security-Policy"] =
                "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
                "font-src 'self' https://fonts.gstatic.com data:; img-src 'self' data:; connect-src 'self' ws: wss:; " +
                "frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
        }
        await next();
    });
}
