using System.Net;
using System.Net.Mail;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wings.LoadBalancer.Data;
using Wings.LoadBalancer.Data.Entities;

namespace Wings.LoadBalancer.Infrastructure.Alerts;

public static class AlertTypes
{
    public const string ServerUnavailable = "ServerUnavailable";
    public const string HighCpu = "HighCpu";
    public const string HighMemory = "HighMemory";
    public const string HighErrorRate = "HighErrorRate";
    public const string SlowResponse = "SlowResponse";
    public const string NoHealthyDestination = "NoHealthyDestination";
    public const string UnexpectedFailover = "UnexpectedFailover";
    public const string GatewayConnectivity = "GatewayConnectivity";
    public const string RedisUnavailable = "RedisUnavailable";
    public const string SqlUnavailable = "SqlUnavailable";
    public const string NearCapacity = "NearCapacity";
    public const string ConfigurationInconsistency = "ConfigurationInconsistency";
}

public sealed record AlertRequest(string Type, AlertSeverity Severity, string DedupKey, string Title, string? Message = null,
    Guid? ServerId = null, Guid? CompanyId = null);

public interface IAlertService
{
    /// <summary>Raises (or refreshes) an alert. Duplicates of an unresolved alert only bump its counter.</summary>
    Task RaiseAsync(AlertRequest request, CancellationToken cancellationToken = default);

    /// <summary>Resolves an unresolved alert with this key, if any (condition cleared).</summary>
    Task ResolveAsync(string dedupKey, string note, CancellationToken cancellationToken = default);

    Task AcknowledgeAsync(Guid alertId, string by, CancellationToken cancellationToken = default);
    Task ResolveByIdAsync(Guid alertId, string by, string? note, CancellationToken cancellationToken = default);
}

public sealed class AlertService(
    WingsDbContext db,
    INotificationDispatcher notifications,
    TimeProvider time,
    ILogger<AlertService> logger) : IAlertService
{
    public async Task RaiseAsync(AlertRequest request, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var existing = await db.Alerts
            .Where(a => a.DedupKey == request.DedupKey && a.Status != AlertStatus.Resolved)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            existing.LastRaisedAt = now;
            existing.Occurrences++;
            var escalated = request.Severity > existing.Severity;
            if (escalated) existing.Severity = request.Severity;
            existing.Message = request.Message ?? existing.Message;
            await db.SaveChangesAsync(ct);
            if (escalated) await notifications.SendAsync(existing, ct);
            return;
        }

        var alert = new Alert
        {
            Type = request.Type,
            Severity = request.Severity,
            Status = AlertStatus.Open,
            DedupKey = request.DedupKey,
            Title = request.Title,
            Message = request.Message,
            ServerId = request.ServerId,
            CompanyId = request.CompanyId,
            FirstRaisedAt = now,
            LastRaisedAt = now
        };
        db.Alerts.Add(alert);
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Alert raised: [{Severity}] {Title}", alert.Severity, alert.Title);
        await notifications.SendAsync(alert, ct);
    }

    public async Task ResolveAsync(string dedupKey, string note, CancellationToken ct = default)
    {
        var open = await db.Alerts.Where(a => a.DedupKey == dedupKey && a.Status != AlertStatus.Resolved).ToListAsync(ct);
        if (open.Count == 0) return;
        foreach (var a in open)
        {
            a.Status = AlertStatus.Resolved;
            a.ResolvedAt = time.GetUtcNow();
            a.ResolvedBy = "system";
            a.ResolutionNote = note;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task AcknowledgeAsync(Guid alertId, string by, CancellationToken ct = default)
    {
        var alert = await db.Alerts.FindAsync([alertId], ct) ?? throw new KeyNotFoundException();
        if (alert.Status != AlertStatus.Open) return;
        alert.Status = AlertStatus.Acknowledged;
        alert.AcknowledgedAt = time.GetUtcNow();
        alert.AcknowledgedBy = by;
        await db.SaveChangesAsync(ct);
    }

    public async Task ResolveByIdAsync(Guid alertId, string by, string? note, CancellationToken ct = default)
    {
        var alert = await db.Alerts.FindAsync([alertId], ct) ?? throw new KeyNotFoundException();
        if (alert.Status == AlertStatus.Resolved) return;
        alert.Status = AlertStatus.Resolved;
        alert.ResolvedAt = time.GetUtcNow();
        alert.ResolvedBy = by;
        alert.ResolutionNote = note;
        await db.SaveChangesAsync(ct);
    }
}

public sealed class NotificationOptions
{
    public const string Section = "Notifications";

    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool SmtpEnableSsl { get; set; } = true;
    public string? SmtpUser { get; set; }
    /// <summary>From a secret store; never from the database.</summary>
    public string? SmtpPassword { get; set; }
    public string From { get; set; } = "wings-lb@localhost";
    /// <summary>HMAC-SHA256 secret for signing generic webhook payloads (header X-Wings-Signature).</summary>
    public string? WebhookSecret { get; set; }
    public string? AdminPortalUrl { get; set; }
    /// <summary>Allow webhook targets on private/loopback addresses (development only).</summary>
    public bool AllowPrivateWebhookTargets { get; set; }
}

public interface INotificationDispatcher
{
    Task SendAsync(Alert alert, CancellationToken cancellationToken);
    Task<string?> TestAsync(NotificationChannel channel, CancellationToken cancellationToken);
}

public sealed class NotificationDispatcher(
    WingsDbContext db,
    IHttpClientFactory httpClients,
    IOptions<NotificationOptions> options,
    ILogger<NotificationDispatcher> logger) : INotificationDispatcher
{
    public const string HttpClientName = "wings-notifications";

    public async Task SendAsync(Alert alert, CancellationToken ct)
    {
        List<NotificationChannel> channels;
        try
        {
            channels = await db.NotificationChannels.AsNoTracking()
                .Where(c => c.IsEnabled && c.MinimumSeverity <= alert.Severity).ToListAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not load notification channels for alert {Title}", alert.Title);
            return;
        }
        foreach (var channel in channels)
        {
            var error = await DeliverAsync(channel, alert, ct);
            if (error is not null) logger.LogWarning("Notification via {Channel} failed: {Error}", channel.Name, error);
        }
    }

    public Task<string?> TestAsync(NotificationChannel channel, CancellationToken ct) =>
        DeliverAsync(channel, new Alert
        {
            Type = "Test", Severity = AlertSeverity.Info, Status = AlertStatus.Open, DedupKey = "test",
            Title = "Wings Load Balancer test notification", Message = "This is a test of the notification channel.",
            FirstRaisedAt = DateTimeOffset.UtcNow, LastRaisedAt = DateTimeOffset.UtcNow
        }, ct);

    private async Task<string?> DeliverAsync(NotificationChannel channel, Alert alert, CancellationToken ct)
    {
        try
        {
            switch (channel.Type)
            {
                case NotificationChannelType.Email:
                    await SendEmailAsync(channel.Target, alert, ct);
                    break;
                case NotificationChannelType.Webhook:
                    await PostAsync(channel.Target, JsonSerializer.Serialize(new
                    {
                        alert.Id, alert.Type, Severity = alert.Severity.ToString(), Status = alert.Status.ToString(),
                        alert.Title, alert.Message, alert.ServerId, alert.CompanyId, alert.FirstRaisedAt, alert.LastRaisedAt,
                        alert.Occurrences
                    }), sign: true, ct);
                    break;
                case NotificationChannelType.Teams:
                    // Teams incoming webhook / Workflows "post to channel" accept an Adaptive Card attachment.
                    await PostAsync(channel.Target, JsonSerializer.Serialize(new
                    {
                        type = "message",
                        attachments = new[]
                        {
                            new
                            {
                                contentType = "application/vnd.microsoft.card.adaptive",
                                content = new
                                {
                                    type = "AdaptiveCard", version = "1.4", schema = "http://adaptivecards.io/schemas/adaptive-card.json",
                                    body = new object[]
                                    {
                                        new { type = "TextBlock", size = "Medium", weight = "Bolder", text = $"[{alert.Severity}] {alert.Title}", wrap = true },
                                        new { type = "TextBlock", text = alert.Message ?? "", wrap = true },
                                        new { type = "TextBlock", isSubtle = true, text = $"{alert.Type} · first {alert.FirstRaisedAt:u} · x{alert.Occurrences}", wrap = true }
                                    }
                                }
                            }
                        }
                    }), sign: false, ct);
                    break;
            }
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.Message;
        }
    }

    private async Task SendEmailAsync(string recipients, Alert alert, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.SmtpHost)) throw new InvalidOperationException("Notifications:SmtpHost is not configured");
        using var client = new SmtpClient(o.SmtpHost, o.SmtpPort) { EnableSsl = o.SmtpEnableSsl };
        if (!string.IsNullOrEmpty(o.SmtpUser)) client.Credentials = new NetworkCredential(o.SmtpUser, o.SmtpPassword);
        using var message = new MailMessage { From = new MailAddress(o.From), Subject = $"[Wings LB][{alert.Severity}] {alert.Title}" };
        foreach (var r in recipients.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            message.To.Add(r);
        message.Body = $"{alert.Message}\n\nType: {alert.Type}\nFirst raised: {alert.FirstRaisedAt:u}\nOccurrences: {alert.Occurrences}"
                       + (o.AdminPortalUrl is null ? "" : $"\n\n{o.AdminPortalUrl.TrimEnd('/')}/alerts");
        await client.SendMailAsync(message, ct);
    }

    private async Task PostAsync(string url, string json, bool sign, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps && !options.Value.AllowPrivateWebhookTargets)
            throw new InvalidOperationException("Webhook URL must be an absolute https URL");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (sign && options.Value.WebhookSecret is { Length: > 0 } secret)
        {
            var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(json)));
            request.Headers.TryAddWithoutValidation("X-Wings-Signature", "sha256=" + signature.ToLowerInvariant());
        }
        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Handler for webhook delivery that refuses link-local/metadata (and, unless allowed, private) targets.</summary>
    public static SocketsHttpHandler CreateHandler(NotificationOptions o) => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var allowed = addresses.Where(a => IsPublicOrAllowed(a, o.AllowPrivateWebhookTargets)).ToArray();
            if (allowed.Length == 0) throw new HttpRequestException("Webhook target resolves to a blocked address");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };

    private static bool IsPublicOrAllowed(IPAddress ip, bool allowPrivate)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork && b[0] == 169 && b[1] == 254) return false;
        if (ip.IsIPv6LinkLocal) return false;
        if (allowPrivate) return true;
        if (IPAddress.IsLoopback(ip)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return !(b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 0);
        return !ip.IsIPv6SiteLocal && !(b[0] is 0xfc or 0xfd);
    }
}
