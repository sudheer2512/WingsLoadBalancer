using System.Globalization;
using System.Net.Http.Headers;
using Microsoft.Extensions.Primitives;
using Wings.LoadBalancer.Infrastructure.Security;
using Wings.LoadBalancer.Infrastructure.Sessions;
using Wings.Routing.Model;
using Yarp.ReverseProxy.Forwarder;

namespace Wings.Gateway.Proxy;

/// <summary>Trusted identity for one forwarded request, taken from the authenticated session.</summary>
public sealed record ForwardingContext(
    ServerInfo Server,
    SessionRecord Session,
    ApplicationInfo Application,
    DeploymentEnvironment Environment,
    string RequestId,
    byte[] SigningKey);

/// <summary>
/// Request: strip every client-supplied identity/forwarding header and the gateway cookie, then add verified identity
/// headers with an HMAC signature. Response: hide backend technology and internal addresses.
/// </summary>
public sealed class WingsTransformer(
    ForwardingContext forwarding,
    string cookieName,
    HashSet<string> stripRequest,
    IReadOnlyList<string> stripResponse,
    TimeProvider time) : HttpTransformer
{
    public override async ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest,
        string destinationPrefix, CancellationToken cancellationToken)
    {
        await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);
        var headers = proxyRequest.Headers;

        foreach (var name in headers.Select(h => h.Key).ToList())
        {
            if (name.StartsWith(WingsHeaders.Prefix, StringComparison.OrdinalIgnoreCase) || stripRequest.Contains(name))
                headers.Remove(name);
        }
        if (proxyRequest.Content is not null)
        {
            foreach (var name in proxyRequest.Content.Headers.Select(h => h.Key).ToList())
            {
                if (name.StartsWith(WingsHeaders.Prefix, StringComparison.OrdinalIgnoreCase) || stripRequest.Contains(name))
                    proxyRequest.Content.Headers.Remove(name);
            }
        }

        RewriteCookies(httpContext, headers);

        var request = httpContext.Request;
        var remote = httpContext.Connection.RemoteIpAddress?.ToString();
        if (remote is not null) headers.TryAddWithoutValidation("X-Forwarded-For", remote);
        headers.TryAddWithoutValidation("X-Forwarded-Proto", request.Scheme);
        headers.TryAddWithoutValidation("X-Forwarded-Host", request.Host.Value);

        var session = forwarding.Session;
        var timestamp = time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var sessionKey = SessionKeys.Hash(session.SessionId);
        // Paths are forwarded unchanged, so sign what the backend will see as Path + QueryString.
        var pathAndQuery = request.Path + request.QueryString;
        var companyId = session.CompanyId.ToString();
        var userId = session.UserId.ToString();
        var app = forwarding.Application.Code;
        var env = forwarding.Environment.ToString();

        headers.TryAddWithoutValidation(WingsHeaders.CompanyId, companyId);
        headers.TryAddWithoutValidation(WingsHeaders.CompanyCode, session.CompanyCode);
        headers.TryAddWithoutValidation(WingsHeaders.UserId, userId);
        headers.TryAddWithoutValidation(WingsHeaders.Username, Uri.EscapeDataString(session.Username));
        headers.TryAddWithoutValidation(WingsHeaders.Session, sessionKey);
        headers.TryAddWithoutValidation(WingsHeaders.Application, app);
        headers.TryAddWithoutValidation(WingsHeaders.Environment, env);
        headers.TryAddWithoutValidation(WingsHeaders.RequestId, forwarding.RequestId);
        headers.TryAddWithoutValidation(WingsHeaders.Timestamp, timestamp);
        var canonical = IdentitySignature.Canonical(companyId, userId, sessionKey, app, env, forwarding.RequestId,
            timestamp, request.Method, pathAndQuery);
        headers.TryAddWithoutValidation(WingsHeaders.Signature, IdentitySignature.Sign(forwarding.SigningKey, canonical));
    }

    public override async ValueTask<bool> TransformResponseAsync(HttpContext httpContext, HttpResponseMessage? proxyResponse,
        CancellationToken cancellationToken)
    {
        var result = await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);
        var headers = httpContext.Response.Headers;
        foreach (var name in stripResponse) headers.Remove(name);
        foreach (var name in headers.Keys.Where(k => k.StartsWith(WingsHeaders.Prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            headers.Remove(name);

        if (headers.Location is { Count: > 0 } location
            && Uri.TryCreate(location.ToString(), UriKind.Absolute, out var target)
            && Uri.TryCreate(forwarding.Server.BaseUrl, UriKind.Absolute, out var backend)
            && string.Equals(target.Authority, backend.Authority, StringComparison.OrdinalIgnoreCase))
        {
            // Internal absolute redirect → same path on the public origin.
            headers.Location = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}{target.PathAndQuery}{target.Fragment}";
        }

        if (headers.SetCookie is { Count: > 0 } cookies)
        {
            headers.SetCookie = new StringValues(cookies.Select(c => StripCookieDomain(c!)).ToArray());
        }
        return result;
    }

    private void RewriteCookies(HttpContext httpContext, HttpRequestHeaders headers)
    {
        headers.Remove("Cookie");
        // Work on the raw header so backend cookie values are forwarded byte-for-byte.
        var kept = new List<string>();
        foreach (var header in httpContext.Request.Headers.Cookie)
        {
            foreach (var pair in (header ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var name = pair.Split('=', 2)[0];
                if (string.Equals(name, cookieName, StringComparison.Ordinal)
                    || name.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal)
                    || name.StartsWith("Wings.External", StringComparison.Ordinal))
                    continue;
                kept.Add(pair);
            }
        }
        if (kept.Count > 0) headers.TryAddWithoutValidation("Cookie", string.Join("; ", kept));
    }

    internal static string StripCookieDomain(string setCookie)
    {
        var parts = setCookie.Split(';');
        return string.Join(';', parts.Where(p => !p.TrimStart().StartsWith("domain=", StringComparison.OrdinalIgnoreCase)));
    }
}
