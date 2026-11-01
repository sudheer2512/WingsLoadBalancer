using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wings.LoadBalancer.Data;
using Wings.LoadBalancer.Infrastructure.Caching;
using Wings.Routing.Model;

namespace Wings.LoadBalancer.Infrastructure.Configuration;

public sealed class ConfigurationSyncOptions
{
    public const string Section = "ConfigurationSync";

    /// <summary>How often to check the database version (fallback when invalidation messages are missed).</summary>
    public int VersionPollSeconds { get; set; } = 15;

    /// <summary>TTL for snapshots cached in Redis.</summary>
    public int SnapshotCacheMinutes { get; set; } = 60;

    /// <summary>Protected last-known-good file used when the database is unreachable at startup.</summary>
    public string LastKnownGoodPath { get; set; } = "App_Data/routing-lkg.bin";
}

/// <summary>Holds the current immutable snapshot. Reads are lock-free.</summary>
public interface IRoutingSnapshotProvider
{
    RoutingSnapshot? Current { get; }
    bool IsReady { get; }
    bool DatabaseAvailable { get; }
    event Action<RoutingSnapshot>? SnapshotChanged;
}

public static class RoutingJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}

/// <summary>
/// Loads snapshots (Redis → database → last-known-good file), listens for invalidation events and polls the version.
/// One instance per gateway process.
/// </summary>
public sealed class RoutingSnapshotService(
    IServiceScopeFactory scopes,
    IStateStore store,
    IDataProtectionProvider dataProtection,
    IOptions<ConfigurationSyncOptions> options,
    TimeProvider time,
    ILogger<RoutingSnapshotService> logger) : BackgroundService, IRoutingSnapshotProvider
{
    private readonly IDataProtector _lkgProtector = dataProtection.CreateProtector("Wings.Routing.LastKnownGood.v1");
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private volatile RoutingSnapshot? _current;
    private volatile bool _databaseAvailable;
    private readonly TaskCompletionSource _firstLoad = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RoutingSnapshot? Current => _current;
    public bool IsReady => _current is not null;
    public bool DatabaseAvailable => _databaseAvailable;
    public event Action<RoutingSnapshot>? SnapshotChanged;

    /// <summary>Completes when the first snapshot (from any source) is available.</summary>
    public Task WaitForFirstLoadAsync(CancellationToken ct) => _firstLoad.Task.WaitAsync(ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        store.Subscribe(StateKeys.ConfigChannel, message =>
        {
            if (long.TryParse(message, out var version) && version != _current?.Version)
            {
                logger.LogInformation("Configuration invalidation received for v{Version}", version);
                _ = Task.Run(() => RefreshAsync(force: false, stoppingToken), stoppingToken);
            }
        });

        await RefreshAsync(force: true, stoppingToken);
        if (_current is null) await TryLoadLastKnownGoodAsync(stoppingToken);

        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.VersionPollSeconds));
        using var timer = new PeriodicTimer(interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshAsync(force: false, stoppingToken);
        }
    }

    /// <summary>Checks the authoritative version and reloads when it differs. Safe to call concurrently.</summary>
    public async Task RefreshAsync(bool force, CancellationToken ct)
    {
        await _reloadLock.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var source = scope.ServiceProvider.GetRequiredService<IRoutingConfigurationSource>();

            long dbVersion;
            try
            {
                dbVersion = await source.GetCurrentVersionAsync(ct);
                _databaseAvailable = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (_databaseAvailable || _current is null)
                    logger.LogWarning(ex, "Routing database unavailable; continuing with snapshot v{Version}", _current?.Version);
                _databaseAvailable = false;
                return;
            }

            var current = _current;
            if (!force && current is not null && current.Version == dbVersion)
            {
                current.MarkValidated(time.GetUtcNow());
                return;
            }

            var configuration = await TryReadCachedAsync(dbVersion, ct) ?? await source.LoadAsync(ct);
            // A concurrent change may have landed between the version read and the load; the next poll catches it.
            var snapshot = new RoutingSnapshot(configuration, time.GetUtcNow());
            Publish(snapshot);
            await CacheAsync(configuration, ct);
            await SaveLastKnownGoodAsync(configuration, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to refresh routing configuration");
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    private void Publish(RoutingSnapshot snapshot)
    {
        var previous = _current;
        _current = snapshot;
        _firstLoad.TrySetResult();
        if (previous?.Version != snapshot.Version)
        {
            logger.LogInformation("Routing configuration v{Version} active ({Companies} companies, {Servers} servers)",
                snapshot.Version, snapshot.Companies.Count, snapshot.Servers.Count);
            SnapshotChanged?.Invoke(snapshot);
        }
    }

    private async Task<RoutingConfiguration?> TryReadCachedAsync(long version, CancellationToken ct)
    {
        if (!store.IsDistributed) return null;
        try
        {
            var json = await store.GetAsync(StateKeys.Snapshot(version), ct);
            var cached = json is null ? null : JsonSerializer.Deserialize<RoutingConfiguration>(json, RoutingJson.Options);
            // Stale-cache protection: only accept an entry for exactly the authoritative version.
            return cached?.Version == version ? cached : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ignoring unreadable cached snapshot v{Version}", version);
            return null;
        }
    }

    private async Task CacheAsync(RoutingConfiguration configuration, CancellationToken ct)
    {
        if (!store.IsDistributed) return;
        var json = JsonSerializer.Serialize(configuration, RoutingJson.Options);
        await store.SetAsync(StateKeys.Snapshot(configuration.Version), json,
            TimeSpan.FromMinutes(options.Value.SnapshotCacheMinutes), ct);
        await store.SetAsync(StateKeys.CurrentSnapshotVersion, configuration.Version.ToString(), null, ct);
    }

    private async Task SaveLastKnownGoodAsync(RoutingConfiguration configuration, CancellationToken ct)
    {
        try
        {
            var path = Path.GetFullPath(options.Value.LastKnownGoodPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var payload = new LastKnownGood(time.GetUtcNow(), configuration);
            var bytes = _lkgProtector.Protect(JsonSerializer.SerializeToUtf8Bytes(payload, RoutingJson.Options));
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not write last-known-good routing configuration");
        }
    }

    private async Task TryLoadLastKnownGoodAsync(CancellationToken ct)
    {
        var path = Path.GetFullPath(options.Value.LastKnownGoodPath);
        if (!File.Exists(path)) return;
        try
        {
            var bytes = _lkgProtector.Unprotect(await File.ReadAllBytesAsync(path, ct));
            var lkg = JsonSerializer.Deserialize<LastKnownGood>(bytes, RoutingJson.Options);
            if (lkg is null) return;
            // The snapshot counts as validated when it was saved, so the last-known-good age limit still applies.
            Publish(new RoutingSnapshot(lkg.Configuration, lkg.SavedAt));
            logger.LogWarning("Database unavailable at startup; using last-known-good routing v{Version} saved {SavedAt:u}",
                lkg.Configuration.Version, lkg.SavedAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Last-known-good routing configuration is unreadable");
        }
    }

    private sealed record LastKnownGood(DateTimeOffset SavedAt, RoutingConfiguration Configuration);
}

/// <summary>Publishes invalidation events after configuration commits (registered with the data layer).</summary>
public sealed class StateStoreConfigurationChangeNotifier(IStateStore store) : IConfigurationChangeNotifier
{
    public Task NotifyAsync(long version, CancellationToken cancellationToken) =>
        store.PublishAsync(StateKeys.ConfigChannel, version.ToString());
}
