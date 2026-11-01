using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wings.LoadBalancer.Data;
using Wings.LoadBalancer.Data.Entities;
using Wings.LoadBalancer.Infrastructure.Caching;
using Wings.LoadBalancer.Infrastructure.Configuration;
using Wings.LoadBalancer.Infrastructure.Sessions;

namespace Wings.LoadBalancer.Infrastructure.Monitoring;

/// <summary>
/// Buffers routing decision logs and writes them in batches off the request path. When SQL is unavailable the buffer
/// is bounded; the oldest entries are dropped and counted.
/// </summary>
public sealed class DecisionLogWriter(
    IServiceScopeFactory scopes,
    IStateStore store,
    ILogger<DecisionLogWriter> logger) : BackgroundService
{
    private readonly Channel<RoutingDecisionLog> _channel = Channel.CreateBounded<RoutingDecisionLog>(
        new BoundedChannelOptions(20_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private long _dropped;

    public long Dropped => Interlocked.Read(ref _dropped);

    public void Enqueue(RoutingDecisionLog log)
    {
        if (!_channel.Writer.TryWrite(log)) Interlocked.Increment(ref _dropped);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<RoutingDecisionLog>(500);
        while (await _channel.Reader.WaitToReadAsync(stoppingToken))
        {
            while (batch.Count < 500 && _channel.Reader.TryRead(out var item)) batch.Add(item);
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<WingsDbContext>();
                db.RoutingDecisionLogs.AddRange(batch);
                await db.SaveChangesAsync(stoppingToken);
                if (store.IsDistributed)
                    await store.PublishAsync(StateKeys.DecisionChannel, JsonSerializer.Serialize(batch, RoutingJson.Options));
                batch.Clear();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not persist {Count} routing decisions; will retry", batch.Count);
                if (batch.Count >= 5_000)
                {
                    Interlocked.Add(ref _dropped, batch.Count);
                    batch.Clear();
                }
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
        }
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        var items = new List<RoutingDecisionLog>();
        while (_channel.Reader.TryRead(out var item)) items.Add(item);
        if (items.Count == 0) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WingsDbContext>();
        db.RoutingDecisionLogs.AddRange(items);
        await db.SaveChangesAsync(ct);
    }
}

public sealed record SessionActivity(string SessionKey, Guid CompanyId, Guid UserId, Guid ApplicationId, Guid? ServerId,
    DateTimeOffset At, string? ClientAddress, string? UserAgent, string? EndReason = null);

/// <summary>
/// Maintains the ActiveSessions table from gateway activity (throttled per session/application) and polls it for
/// administrator revocations so every gateway enforces them even without Redis.
/// </summary>
public sealed class SessionActivityWriter(
    IServiceScopeFactory scopes,
    ISessionStore sessions,
    TimeProvider time,
    ILogger<SessionActivityWriter> logger) : BackgroundService
{
    private static readonly TimeSpan TouchInterval = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<(string, Guid), (Guid? ServerId, DateTimeOffset At)> _lastWritten = new();
    private readonly ConcurrentQueue<SessionActivity> _queue = new();
    private DateTimeOffset _revocationCursor;

    public string InstanceName { get; set; } = Environment.MachineName;

    public void Record(SessionActivity activity)
    {
        var key = (activity.SessionKey, activity.ApplicationId);
        if (activity.EndReason is null && _lastWritten.TryGetValue(key, out var last)
            && last.ServerId == activity.ServerId && activity.At - last.At < TouchInterval)
            return;
        _lastWritten[key] = (activity.ServerId, activity.At);
        _queue.Enqueue(activity);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _revocationCursor = time.GetUtcNow().AddMinutes(-5);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FlushAsync(stoppingToken);
                await PollRevocationsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Session activity sync failed; will retry");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), time, stoppingToken);
        }
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        if (_queue.IsEmpty) return;
        var items = new List<SessionActivity>();
        while (items.Count < 1000 && _queue.TryDequeue(out var a)) items.Add(a);

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WingsDbContext>();
        var keys = items.Select(i => i.SessionKey).Distinct().ToList();
        var existing = await db.ActiveSessions.Where(s => keys.Contains(s.SessionKey)).ToListAsync(ct);
        foreach (var item in items)
        {
            if (item.EndReason is not null)
            {
                foreach (var row in existing.Where(r => r.SessionKey == item.SessionKey && r.EndedAt == null))
                {
                    row.EndedAt = item.At;
                    row.EndReason = item.EndReason;
                }
                continue;
            }
            var session = existing.FirstOrDefault(r => r.SessionKey == item.SessionKey && r.ApplicationId == item.ApplicationId);
            if (session is null)
            {
                session = new ActiveSession
                {
                    SessionKey = item.SessionKey, CompanyId = item.CompanyId, UserId = item.UserId,
                    ApplicationId = item.ApplicationId, StartedAt = item.At, ClientAddress = item.ClientAddress,
                    UserAgent = item.UserAgent is { Length: > 300 } ua ? ua[..300] : item.UserAgent
                };
                db.ActiveSessions.Add(session);
                existing.Add(session);
            }
            session.ServerId = item.ServerId;
            session.LastSeenAt = item.At;
            session.GatewayInstance = InstanceName;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task PollRevocationsAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WingsDbContext>();
        var cursor = _revocationCursor;
        var revoked = await db.ActiveSessions.AsNoTracking()
            .Where(s => s.RevokedAt != null && s.RevokedAt > cursor)
            .Select(s => new { s.SessionKey, s.RevokedAt })
            .ToListAsync(ct);
        foreach (var r in revoked)
        {
            sessions.MarkRevokedByKey(r.SessionKey);
            if (r.RevokedAt > _revocationCursor) _revocationCursor = r.RevokedAt!.Value;
        }
    }
}
