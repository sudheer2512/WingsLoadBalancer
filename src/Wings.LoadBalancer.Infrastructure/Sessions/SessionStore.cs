using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Wings.LoadBalancer.Infrastructure.Caching;
using Wings.LoadBalancer.Infrastructure.Configuration;

namespace Wings.LoadBalancer.Infrastructure.Sessions;

/// <summary>Server-side session state. Holds tenant identity and per-application affinity; no credentials or tokens.</summary>
public sealed record SessionRecord
{
    public required string SessionId { get; init; }
    public required Guid CompanyId { get; init; }
    public required Guid UserId { get; init; }
    public required string CompanyCode { get; init; }
    public required string Username { get; init; }
    public required string AuthenticationMethod { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastSeenAt { get; init; }
    public DateTimeOffset AbsoluteExpiresAt { get; init; }
    public bool Revoked { get; init; }
    /// <summary>Application id → server id the session is pinned to.</summary>
    public Dictionary<Guid, Guid> Affinity { get; init; } = [];
}

public enum SessionLookupStatus { Found, Missing, Revoked, Expired }

public readonly record struct SessionLookup(SessionLookupStatus Status, SessionRecord? Record);

public interface ISessionStore
{
    /// <summary>True when sessions are shared across gateways (Redis reachable).</summary>
    bool IsShared { get; }
    Task<SessionLookup> GetAsync(string sessionId, CancellationToken cancellationToken = default);
    Task SaveAsync(SessionRecord record, CancellationToken cancellationToken = default);
    Task RevokeAsync(string sessionId, CancellationToken cancellationToken = default);
    /// <summary>Marks a session revoked by its database key (hash), e.g. from an admin action polled from SQL.</summary>
    void MarkRevokedByKey(string sessionKey);
    bool IsRevokedByKey(string sessionKey);
}

public static class SessionKeys
{
    /// <summary>Stable non-reversible key used in SQL, logs and admin views instead of the raw session id.</summary>
    public static string Hash(string sessionId) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string NewSessionId() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Session store over <see cref="IStateStore"/> with a short-lived local cache so most requests avoid a network hop.
/// </summary>
public sealed class SessionStore(IStateStore store, TimeProvider time, ILogger<SessionStore> logger) : ISessionStore
{
    private static readonly TimeSpan LocalCacheTtl = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, (SessionRecord Record, DateTimeOffset CachedAt)> _local = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _revokedKeys = new();

    public bool IsShared => store.IsDistributed;

    public async Task<SessionLookup> GetAsync(string sessionId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        if (_revokedKeys.ContainsKey(SessionKeys.Hash(sessionId))) return new(SessionLookupStatus.Revoked, null);

        if (_local.TryGetValue(sessionId, out var cached) && now - cached.CachedAt < LocalCacheTtl)
            return Evaluate(cached.Record, now);

        string? json;
        try
        {
            json = await store.GetAsync(StateKeys.Session(sessionId), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Session store read failed");
            json = null;
        }
        if (json is null)
        {
            _local.TryRemove(sessionId, out _);
            return new(SessionLookupStatus.Missing, null);
        }
        var record = JsonSerializer.Deserialize<SessionRecord>(json, RoutingJson.Options)!;
        _local[sessionId] = (record, now);
        return Evaluate(record, now);
    }

    public async Task SaveAsync(SessionRecord record, CancellationToken ct = default)
    {
        var ttl = record.AbsoluteExpiresAt - time.GetUtcNow();
        if (ttl <= TimeSpan.Zero) return;
        _local[record.SessionId] = (record, time.GetUtcNow());
        await store.SetAsync(StateKeys.Session(record.SessionId), JsonSerializer.Serialize(record, RoutingJson.Options), ttl, ct);
    }

    public async Task RevokeAsync(string sessionId, CancellationToken ct = default)
    {
        var lookup = await GetAsync(sessionId, ct);
        MarkRevokedByKey(SessionKeys.Hash(sessionId));
        if (lookup.Record is { } record)
        {
            await SaveAsync(record with { Revoked = true }, ct);
        }
        await store.PublishAsync(StateKeys.SessionRevokedChannel, SessionKeys.Hash(sessionId));
    }

    public void MarkRevokedByKey(string sessionKey)
    {
        _revokedKeys[sessionKey] = time.GetUtcNow();
        if (_revokedKeys.Count > 100_000)
        {
            var cutoff = time.GetUtcNow().AddDays(-1);
            foreach (var (k, at) in _revokedKeys) if (at < cutoff) _revokedKeys.TryRemove(k, out _);
        }
        foreach (var (sid, _) in _local)
        {
            if (SessionKeys.Hash(sid) == sessionKey) _local.TryRemove(sid, out _);
        }
    }

    public bool IsRevokedByKey(string sessionKey) => _revokedKeys.ContainsKey(sessionKey);

    private static SessionLookup Evaluate(SessionRecord record, DateTimeOffset now)
    {
        if (record.Revoked) return new(SessionLookupStatus.Revoked, record);
        if (record.AbsoluteExpiresAt <= now) return new(SessionLookupStatus.Expired, record);
        return new(SessionLookupStatus.Found, record);
    }
}
