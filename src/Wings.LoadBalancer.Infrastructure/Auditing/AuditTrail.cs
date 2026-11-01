using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wings.LoadBalancer.Data;
using Wings.LoadBalancer.Data.Entities;

namespace Wings.LoadBalancer.Infrastructure.Auditing;

public static class AuditEvents
{
    public const string ServerCreated = "ServerCreated";
    public const string ServerModified = "ServerModified";
    public const string ServerStateChanged = "ServerStateChanged";
    public const string ServerDrainStarted = "ServerDrainStarted";
    public const string ServerDrainCompleted = "ServerDrainCompleted";
    public const string ServerRestored = "ServerRestored";
    public const string ServerRetired = "ServerRetired";
    public const string HealthTransition = "HealthTransition";
    public const string CompanyAssignmentChanged = "CompanyAssignmentChanged";
    public const string CompanyTransferred = "CompanyTransferred";
    public const string UserAssignmentChanged = "UserAssignmentChanged";
    public const string UserAssignmentRolledBack = "UserAssignmentRolledBack";
    public const string BulkAssignmentImport = "BulkAssignmentImport";
    public const string ManualFailover = "ManualFailover";
    public const string AutomaticFailover = "AutomaticFailover";
    public const string MaintenanceEvent = "MaintenanceEvent";
    public const string RoutingConfigurationChanged = "RoutingConfigurationChanged";
    public const string AdministratorLogin = "AdministratorLogin";
    public const string AdministratorLoginFailed = "AdministratorLoginFailed";
    public const string UserLogin = "UserLogin";
    public const string UserLoginFailed = "UserLoginFailed";
    public const string UnauthorizedRoutingAttempt = "UnauthorizedRoutingAttempt";
    public const string SessionRevoked = "SessionRevoked";
    public const string SettingsChanged = "SettingsChanged";
    public const string SecretsExported = "SecretsExported";
}

public sealed record AuditEvent(string EventType)
{
    public string? Reason { get; init; }
    public Guid? CompanyId { get; init; }
    public Guid? UserId { get; init; }
    public Guid? ServerId { get; init; }
    public string? EntityType { get; init; }
    public Guid? EntityId { get; init; }
    public object? Previous { get; init; }
    public object? New { get; init; }
    public AlertSeverity Severity { get; init; }
    /// <summary>Overrides the ambient actor (for example "gateway:GW-01" or a user being authenticated).</summary>
    public string? Actor { get; init; }
    public string? ActorType { get; init; }
    public string? SourceIp { get; init; }
    public string? CorrelationId { get; init; }
}

public interface IAuditTrail
{
    /// <summary>Adds the event to <paramref name="db"/> so it commits atomically with the change.</summary>
    void Add(WingsDbContext db, AuditEvent auditEvent);

    /// <summary>Writes the event in its own unit of work; never throws (audit failures are logged).</summary>
    Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
}

public sealed class AuditTrail(
    IServiceScopeFactory scopes,
    IAuditContext context,
    TimeProvider time,
    ILogger<AuditTrail> logger) : IAuditTrail
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Add(WingsDbContext db, AuditEvent e) => db.AuditLogs.Add(Create(e));

    public async Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WingsDbContext>();
            db.AuditLogs.Add(Create(auditEvent));
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Audit write failed for {EventType} (company {CompanyId}, server {ServerId})",
                auditEvent.EventType, auditEvent.CompanyId, auditEvent.ServerId);
        }
    }

    private AuditLog Create(AuditEvent e) => new()
    {
        At = time.GetUtcNow(),
        Actor = e.Actor ?? context.Actor,
        ActorType = e.ActorType ?? context.ActorType,
        EventType = e.EventType,
        EntityType = e.EntityType,
        EntityId = e.EntityId,
        CompanyId = e.CompanyId,
        UserId = e.UserId,
        ServerId = e.ServerId,
        PreviousValue = Serialize(e.Previous),
        NewValue = Serialize(e.New),
        Reason = e.Reason ?? context.Reason,
        CorrelationId = e.CorrelationId ?? context.CorrelationId,
        SourceIp = e.SourceIp ?? context.SourceIp,
        Severity = e.Severity
    };

    private static string? Serialize(object? value) => value switch
    {
        null => null,
        string s => s,
        _ => Truncate(JsonSerializer.Serialize(value, Json))
    };

    private static string Truncate(string s) => s.Length <= 8000 ? s : s[..8000];
}
