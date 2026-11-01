using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wings.LoadBalancer.Data.Entities;

namespace Wings.LoadBalancer.Data;

/// <summary>Who is making the current change. Scoped per request in the admin API; system identity elsewhere.</summary>
public interface IAuditContext
{
    string Actor { get; }
    string ActorType { get; }
    string? SourceIp { get; }
    string? CorrelationId { get; }
    /// <summary>Optional reason entered by the administrator for the current operation.</summary>
    string? Reason { get; set; }
}

public sealed class SystemAuditContext(string actor = "system") : IAuditContext
{
    public string Actor { get; } = actor;
    public string ActorType => "System";
    public string? SourceIp => null;
    public string? CorrelationId { get; init; }
    public string? Reason { get; set; }
}

/// <summary>Raised after a commit that changed routing configuration (used to publish invalidation events).</summary>
public interface IConfigurationChangeNotifier
{
    Task NotifyAsync(long version, CancellationToken cancellationToken);
}

/// <summary>
/// Cross-cutting save behaviour: timestamps, concurrency stamps, configuration version bump, and an audit row for
/// every change to routing configuration and administrative entities (sensitive columns redacted).
/// </summary>
public sealed class WingsSaveChangesInterceptor(
    IAuditContext audit,
    TimeProvider time,
    IEnumerable<IConfigurationChangeNotifier> notifiers) : SaveChangesInterceptor
{
    private static readonly HashSet<string> Redacted = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(User.PasswordHash), nameof(User.MfaSecretProtected), nameof(IConcurrencyStamped.ConcurrencyStamp)
    };

    private static readonly HashSet<Type> AuditedTypes =
    [
        typeof(Administrator), typeof(AdministratorRole), typeof(Role), typeof(RolePermission),
        typeof(SystemSetting), typeof(NotificationChannel), typeof(MaintenanceWindow)
    ];

    private long? _pendingVersion;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } context) BeforeSave(context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context) BeforeSave(context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        AfterSaveAsync(CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        await AfterSaveAsync(cancellationToken);
        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => _pendingVersion = null;

    private async Task AfterSaveAsync(CancellationToken ct)
    {
        if (_pendingVersion is not { } version) return;
        _pendingVersion = null;
        foreach (var notifier in notifiers)
        {
            try
            {
                await notifier.NotifyAsync(version, ct);
            }
            catch
            {
                // Notification is best-effort; gateways also poll the version.
            }
        }
    }

    private void BeforeSave(DbContext context)
    {
        var now = time.GetUtcNow();
        var entries = context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();
        if (entries.Count == 0) return;

        var routingChanges = new List<string>();
        foreach (var entry in entries)
        {
            if (entry.Entity is ITracked tracked)
            {
                if (entry.State == EntityState.Added) tracked.CreatedAt = now;
                if (entry.State is EntityState.Added or EntityState.Modified)
                {
                    tracked.ModifiedAt = now;
                    tracked.ModifiedBy = audit.Actor;
                }
            }
            if (entry is { State: EntityState.Modified, Entity: IConcurrencyStamped stamped })
            {
                stamped.ConcurrencyStamp = Guid.NewGuid();
            }

            var type = entry.Entity.GetType();
            var isRouting = entry.Entity is IRoutingConfigEntity;
            if (IsNoiseOnly(entry)) continue;
            if (isRouting) routingChanges.Add($"{type.Name} {entry.State}");
            if (isRouting || AuditedTypes.Contains(type))
            {
                context.Add(CreateAudit(entry, type, now));
            }
        }

        if (routingChanges.Count > 0)
        {
            var version = now.ToUnixTimeMilliseconds() * 1000 + Random.Shared.Next(0, 1000);
            context.Add(new ConfigurationVersion
            {
                Version = version,
                ChangedAt = now,
                ChangedBy = audit.Actor,
                Summary = Truncate(string.Join(", ", routingChanges.Distinct().Take(20)), 2000)
            });
            _pendingVersion = version;
        }
    }

    // Login bookkeeping on users must not invalidate every gateway's routing snapshot.
    private static bool IsNoiseOnly(EntityEntry entry) =>
        entry is { State: EntityState.Modified, Entity: User or Administrator }
        && entry.Properties.Where(p => p.IsModified).All(p => p.Metadata.Name is nameof(User.FailedLoginCount)
            or nameof(User.LockoutEndAt) or nameof(User.LastLoginAt) or nameof(User.ModifiedAt)
            or nameof(User.ModifiedBy) or nameof(User.ConcurrencyStamp) or nameof(User.PasswordHash)
            or nameof(User.MfaSecretProtected) or nameof(User.MfaEnabled));

    private AuditLog CreateAudit(EntityEntry entry, Type type, DateTimeOffset now)
    {
        Dictionary<string, object?>? previous = null;
        Dictionary<string, object?>? current = null;
        foreach (var property in entry.Properties)
        {
            var name = property.Metadata.Name;
            var redact = Redacted.Contains(name);
            switch (entry.State)
            {
                case EntityState.Added:
                    (current ??= [])[name] = redact ? Mask(property.CurrentValue) : Readable(property.CurrentValue);
                    break;
                case EntityState.Deleted:
                    (previous ??= [])[name] = redact ? Mask(property.OriginalValue) : Readable(property.OriginalValue);
                    break;
                case EntityState.Modified when property.IsModified && !Equals(property.OriginalValue, property.CurrentValue):
                    if (name == nameof(IConcurrencyStamped.ConcurrencyStamp)) break;
                    (previous ??= [])[name] = redact ? Mask(property.OriginalValue) : Readable(property.OriginalValue);
                    (current ??= [])[name] = redact ? Mask(property.CurrentValue) : Readable(property.CurrentValue);
                    break;
            }
        }

        return new AuditLog
        {
            At = now,
            Actor = audit.Actor,
            ActorType = audit.ActorType,
            EventType = $"{type.Name}.{entry.State}",
            EntityType = type.Name,
            EntityId = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "Id")?.CurrentValue as Guid?
                       ?? entry.Properties.FirstOrDefault(p => p.Metadata.Name == "ServerId")?.CurrentValue as Guid?,
            // The entity's own id fills the matching column so audit filters by company/user/server find it.
            CompanyId = Find(entry, "CompanyId") ?? (entry.Entity is Company ? Find(entry, "Id") : null),
            UserId = Find(entry, "UserId") ?? (entry.Entity is User ? Find(entry, "Id") : null),
            ServerId = Find(entry, "ServerId") ?? Find(entry, "PrimaryServerId") ?? (entry.Entity is Server ? Find(entry, "Id") : null),
            PreviousValue = previous is null ? null : Truncate(JsonSerializer.Serialize(previous), 8000),
            NewValue = current is null ? null : Truncate(JsonSerializer.Serialize(current), 8000),
            Reason = audit.Reason,
            CorrelationId = audit.CorrelationId,
            SourceIp = audit.SourceIp
        };
    }

    private static Guid? Find(EntityEntry entry, string name) =>
        entry.Properties.FirstOrDefault(p => p.Metadata.Name == name)?.CurrentValue as Guid?;

    private static string? Mask(object? value) => value is null ? null : "***";

    // Enums are stored as strings; record them the same way so audit values stay readable.
    private static object? Readable(object? value) => value is Enum e ? e.ToString() : value;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
