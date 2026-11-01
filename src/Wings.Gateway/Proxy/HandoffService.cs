using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Wings.LoadBalancer.Infrastructure.Caching;
using Wings.LoadBalancer.Infrastructure.Configuration;
using Wings.LoadBalancer.Infrastructure.Security;
using Wings.LoadBalancer.Infrastructure.Sessions;
using Wings.Routing.Model;

namespace Wings.Gateway.Proxy;

/// <summary>
/// Redirect mode: the browser is sent to an allow-listed public server URL carrying a single-use, 60-second token.
/// The backend redeems it server-to-server (signed with its derived key) to learn the verified identity.
/// </summary>
public sealed class HandoffService(
    IStateStore store,
    IDestinationValidator destinations,
    IBackendKeyProvider keys,
    IOptions<GatewayOptions> options,
    TimeProvider time)
{
    public sealed record HandoffTicket(Guid ServerId, Guid CompanyId, Guid UserId, string CompanyCode, string Username,
        string ApplicationCode, string Environment, string SessionKey, DateTimeOffset ExpiresAt);

    public async Task RedirectAsync(HttpContext context, ServerInfo server, SessionRecord session, ApplicationInfo app,
        ApplicationBindingInfo binding)
    {
        if (server.PublicUrl is null || !destinations.ValidateRedirectUrl(server.PublicUrl).IsValid)
        {
            await GatewayResponses.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "service_unavailable",
                "The service is temporarily unavailable. Please try again shortly.");
            return;
        }
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var ticket = new HandoffTicket(server.Id, session.CompanyId, session.UserId, session.CompanyCode, session.Username,
            app.Code, binding.Environment.ToString(), SessionKeys.Hash(session.SessionId),
            time.GetUtcNow().AddSeconds(options.Value.HandoffTokenSeconds));
        await store.SetAsync(StateKeys.Handoff(SessionKeys.Hash(token)), JsonSerializer.Serialize(ticket, RoutingJson.Options),
            TimeSpan.FromSeconds(options.Value.HandoffTokenSeconds));
        var path = context.Request.Path + context.Request.QueryString;
        var target = $"{server.PublicUrl!.TrimEnd('/')}/_wings-handoff?token={token}&returnUrl={Uri.EscapeDataString(path)}";
        context.Response.Redirect(target);
    }

    /// <summary>Single use: the token is deleted on first redemption; the caller must be the target server.</summary>
    public async Task<HandoffTicket?> RedeemAsync(string token, Guid serverId, string signature)
    {
        var expected = Convert.ToBase64String(HMACSHA256.HashData(keys.GetSigningKey(serverId), Encoding.UTF8.GetBytes(token)));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature)))
            return null;
        var key = StateKeys.Handoff(SessionKeys.Hash(token));
        var json = await store.GetAsync(key);
        if (json is null) return null;
        await store.DeleteAsync(key);
        var ticket = JsonSerializer.Deserialize<HandoffTicket>(json, RoutingJson.Options);
        return ticket is not null && ticket.ServerId == serverId && ticket.ExpiresAt > time.GetUtcNow() ? ticket : null;
    }
}
