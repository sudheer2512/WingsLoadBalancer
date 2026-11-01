using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Wings.LoadBalancer.Infrastructure.Caching;

public sealed class RedisOptions
{
    public const string Section = "Redis";

    /// <summary>StackExchange.Redis configuration string. Empty = in-memory only (single gateway / development).</summary>
    public string? Configuration { get; set; }
    public string KeyPrefix { get; set; } = "wings:";
    public int OperationTimeoutMs { get; set; } = 250;
}

/// <summary>
/// Shared key/value + pub/sub used for routing snapshots, sessions, invalidation and health fan-out. Implemented over
/// Redis with automatic in-memory fallback, so a Redis outage degrades gracefully instead of failing requests.
/// Never store passwords or raw tokens here.
/// </summary>
public interface IStateStore
{
    /// <summary>True when Redis is configured and currently reachable.</summary>
    bool IsDistributed { get; }
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string value, TimeSpan? ttl, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
    Task PublishAsync(string channel, string message);
    void Subscribe(string channel, Action<string> handler);
}

public sealed class InMemoryStateStore(TimeProvider time) : IStateStore
{
    private readonly ConcurrentDictionary<string, (string Value, DateTimeOffset? Expires)> _items = new();
    private readonly ConcurrentDictionary<string, List<Action<string>>> _subscribers = new();

    public bool IsDistributed => false;

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_items.TryGetValue(key, out var item))
        {
            if (item.Expires is null || item.Expires > time.GetUtcNow()) return Task.FromResult<string?>(item.Value);
            _items.TryRemove(key, out _);
        }
        return Task.FromResult<string?>(null);
    }

    public Task SetAsync(string key, string value, TimeSpan? ttl, CancellationToken cancellationToken = default)
    {
        _items[key] = (value, ttl is null ? null : time.GetUtcNow() + ttl);
        if (_items.Count > 200_000) Sweep();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _items.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task PublishAsync(string channel, string message)
    {
        if (_subscribers.TryGetValue(channel, out var handlers))
        {
            Action<string>[] copy;
            lock (handlers) copy = handlers.ToArray();
            foreach (var h in copy) h(message);
        }
        return Task.CompletedTask;
    }

    public void Subscribe(string channel, Action<string> handler)
    {
        var list = _subscribers.GetOrAdd(channel, _ => []);
        lock (list) list.Add(handler);
    }

    private void Sweep()
    {
        var now = time.GetUtcNow();
        foreach (var (key, item) in _items)
        {
            if (item.Expires <= now) _items.TryRemove(key, out _);
        }
    }
}

public sealed class RedisStateStore : IStateStore, IDisposable
{
    private readonly RedisOptions _options;
    private readonly InMemoryStateStore _fallback;
    private readonly ILogger<RedisStateStore> _logger;
    private readonly Lazy<Task<ConnectionMultiplexer?>> _connection;
    private readonly ConcurrentDictionary<string, List<Action<string>>> _handlers = new();
    private volatile bool _available;

    public RedisStateStore(IOptions<RedisOptions> options, TimeProvider time, ILogger<RedisStateStore> logger)
    {
        _options = options.Value;
        _fallback = new InMemoryStateStore(time);
        _logger = logger;
        _connection = new Lazy<Task<ConnectionMultiplexer?>>(ConnectAsync);
        _ = _connection.Value;
    }

    public bool IsDistributed => _available;

    private string K(string key) => _options.KeyPrefix + key;

    private async Task<ConnectionMultiplexer?> ConnectAsync()
    {
        try
        {
            var config = ConfigurationOptions.Parse(_options.Configuration!);
            config.AbortOnConnectFail = false;
            config.ConnectTimeout = Math.Max(config.ConnectTimeout, 2000);
            config.SyncTimeout = _options.OperationTimeoutMs;
            config.AsyncTimeout = _options.OperationTimeoutMs * 2;
            var mux = await ConnectionMultiplexer.ConnectAsync(config);
            _available = mux.IsConnected;
            mux.ConnectionFailed += (_, e) =>
            {
                _available = false;
                _logger.LogWarning("Redis connection failed ({FailureType}); using in-memory fallback", e.FailureType);
            };
            mux.ConnectionRestored += (_, _) =>
            {
                _available = true;
                _logger.LogInformation("Redis connection restored");
            };
            var subscriber = mux.GetSubscriber();
            foreach (var (channel, handlers) in _handlers) await SubscribeRedisAsync(subscriber, channel, handlers);
            return mux;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis unavailable; using in-memory fallback");
            return null;
        }
    }

    private async Task<IDatabase?> DbAsync()
    {
        var mux = await _connection.Value;
        return mux is { IsConnected: true } ? mux.GetDatabase() : null;
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            if (await DbAsync() is { } db)
            {
                var value = await db.StringGetAsync(K(key));
                return value.HasValue ? value.ToString() : null;
            }
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            MarkUnavailable(ex);
        }
        return await _fallback.GetAsync(key, cancellationToken);
    }

    public async Task SetAsync(string key, string value, TimeSpan? ttl, CancellationToken cancellationToken = default)
    {
        // Always keep a local copy so a later Redis outage can still serve the last value from this instance.
        await _fallback.SetAsync(key, value, ttl, cancellationToken);
        try
        {
            if (await DbAsync() is { } db)
            {
                if (ttl is { } t) await db.StringSetAsync(K(key), value, t);
                else await db.StringSetAsync(K(key), value);
            }
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            MarkUnavailable(ex);
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        await _fallback.DeleteAsync(key, cancellationToken);
        try
        {
            if (await DbAsync() is { } db) await db.KeyDeleteAsync(K(key));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            MarkUnavailable(ex);
        }
    }

    public async Task PublishAsync(string channel, string message)
    {
        try
        {
            var mux = await _connection.Value;
            if (mux is { IsConnected: true })
            {
                await mux.GetSubscriber().PublishAsync(RedisChannel.Literal(K(channel)), message);
                return;
            }
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            MarkUnavailable(ex);
        }
        await _fallback.PublishAsync(channel, message);
    }

    public void Subscribe(string channel, Action<string> handler)
    {
        var list = _handlers.GetOrAdd(channel, _ => []);
        lock (list) list.Add(handler);
        _fallback.Subscribe(channel, handler);
        if (_connection.IsValueCreated && _connection.Value.IsCompletedSuccessfully && _connection.Value.Result is { } mux)
        {
            _ = SubscribeRedisAsync(mux.GetSubscriber(), channel, [handler]);
        }
    }

    private async Task SubscribeRedisAsync(ISubscriber subscriber, string channel, IEnumerable<Action<string>> handlers)
    {
        Action<string>[] copy;
        lock (handlers) copy = handlers.ToArray();
        await subscriber.SubscribeAsync(RedisChannel.Literal(K(channel)), (_, value) =>
        {
            foreach (var h in copy)
            {
                try
                {
                    h(value.ToString());
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Subscriber for {Channel} failed", channel);
                }
            }
        });
    }

    private void MarkUnavailable(Exception ex)
    {
        if (_available) _logger.LogWarning(ex, "Redis operation failed; using in-memory fallback");
        _available = false;
    }

    public void Dispose()
    {
        if (_connection.IsValueCreated && _connection.Value.IsCompletedSuccessfully) _connection.Value.Result?.Dispose();
    }
}

public static class StateKeys
{
    public const string ConfigChannel = "config";
    public const string HealthChannel = "health";
    public const string SessionRevokedChannel = "session-revoked";
    public const string DecisionChannel = "decisions";
    public static string Snapshot(long version) => $"config:snapshot:{version}";
    public const string CurrentSnapshotVersion = "config:current";
    public static string Session(string sessionId) => $"session:{sessionId}";
    public static string LoginFailures(string key) => $"login-failures:{key}";
    public static string Handoff(string tokenId) => $"handoff:{tokenId}";
    public const string HealthState = "health:state";
}
