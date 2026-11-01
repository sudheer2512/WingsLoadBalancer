using Wings.Routing.Model;

namespace Wings.LoadBalancer.Data.Entities;

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
}

/// <summary>Optimistic concurrency: the stamp is regenerated on every update and checked by EF.</summary>
public interface IConcurrencyStamped
{
    Guid ConcurrencyStamp { get; set; }
}

public interface ITracked
{
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset ModifiedAt { get; set; }
    string? ModifiedBy { get; set; }
}

/// <summary>Marker for entities whose changes alter routing and therefore bump the configuration version.</summary>
public interface IRoutingConfigEntity;

public enum AuthenticationMode { Local = 0, WingsAuthService = 1, Oidc = 2 }
public enum AccountStatus { Active = 0, Disabled = 1 }
public enum MaintenanceStatus { Scheduled = 0, Draining = 1, InMaintenance = 2, Completed = 3, Cancelled = 4 }
public enum AlertSeverity { Info = 0, Warning = 1, Critical = 2 }
public enum AlertStatus { Open = 0, Acknowledged = 1, Resolved = 2 }
public enum NotificationChannelType { Email = 0, Webhook = 1, Teams = 2 }
public enum HealthCheckSource { Active = 0, Passive = 1, Administrator = 2 }

// ------------------------------------------------------------------------------------------------------------------
// Tenants and identities
// ------------------------------------------------------------------------------------------------------------------

public class Company : Entity, IConcurrencyStamped, ITracked, IRoutingConfigEntity
{
    public required string Code { get; set; }
    public string NormalizedCode { get; set; } = "";
    public required string Name { get; set; }
    public string? ServicePlan { get; set; }
    public CompanyRoutingMode RoutingMode { get; set; }
    public CompanyStatus Status { get; set; }
    public DeploymentEnvironment DefaultEnvironment { get; set; }
    public int? MaxConcurrentUsers { get; set; }
    public AuthenticationMode AuthenticationMode { get; set; }
    public Guid? RoutingPolicyId { get; set; }
    public RoutingPolicy? RoutingPolicy { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public string? ModifiedBy { get; set; }
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
    public List<CompanyAlias> Aliases { get; set; } = [];
}

public class CompanyAlias : Entity, IRoutingConfigEntity
{
    public Guid CompanyId { get; set; }
    public Company? Company { get; set; }
    public required string Alias { get; set; }
    public string NormalizedAlias { get; set; } = "";
}

/// <summary>A person. Login names live on <see cref="UserCompanyMembership"/>, so identical usernames in different
/// companies are different memberships (and normally different users).</summary>
public class User : Entity, IConcurrencyStamped, ITracked, IRoutingConfigEntity
{
    public required string DisplayName { get; set; }
    public string? Email { get; set; }
    public AccountStatus Status { get; set; }
    public string? PasswordHash { get; set; }
    public bool MfaEnabled { get; set; }
    public string? MfaSecretProtected { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutEndAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public string? ModifiedBy { get; set; }
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
    public List<UserCompanyMembership> Memberships { get; set; } = [];
}

public class UserCompanyMembership : Entity, IConcurrencyStamped, IRoutingConfigEntity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public Guid CompanyId { get; set; }
    public Company? Company { get; set; }
    public required string Username { get; set; }
    public string NormalizedUsername { get; set; } = "";
    public AccountStatus Status { get; set; }
    /// <summary>Subject from an external identity provider (OIDC / Wings auth service), unique per company.</summary>
    public string? ExternalSubject { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Also guards the user's assignment set: assignment changes rotate it, so concurrent edits conflict.</summary>
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}

// ------------------------------------------------------------------------------------------------------------------
// Applications
// ------------------------------------------------------------------------------------------------------------------

public class Application : Entity, IConcurrencyStamped, IRoutingConfigEntity
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string? MinimumServerVersion { get; set; }
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
    public List<ApplicationEnvironment> Environments { get; set; } = [];
}

/// <summary>Public binding of an application in one environment (host + path prefix).</summary>
public class ApplicationEnvironment : Entity, IConcurrencyStamped, IRoutingConfigEntity
{
    public Guid ApplicationId { get; set; }
    public Application? Application { get; set; }
    public DeploymentEnvironment Environment { get; set; }
    public required string PublicHost { get; set; }
    public string PathPrefix { get; set; } = "/";
    public ApplicationRoutingMode RoutingMode { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int RequestTimeoutSeconds { get; set; } = 100;
    public long? MaxRequestBodyBytes { get; set; }
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}

// ------------------------------------------------------------------------------------------------------------------
// Servers and pools
// ------------------------------------------------------------------------------------------------------------------

public class Server : Entity, IConcurrencyStamped, ITracked, IRoutingConfigEntity
{
    public required string Name { get; set; }
    public required string Hostname { get; set; }
    public string? IpAddress { get; set; }
    public required string BaseUrl { get; set; }
    public string? PublicUrl { get; set; }
    public string? HealthCheckUrl { get; set; }
    public DeploymentEnvironment Environment { get; set; }
    public string? Region { get; set; }
    public ServerType ServerType { get; set; }
    public int? CpuCores { get; set; }
    public int? RamGb { get; set; }
    public int MaxCapacity { get; set; }
    public string? ApplicationVersion { get; set; }
    public ServerAdminState AdminState { get; set; }
    public int Priority { get; set; }
    public int Weight { get; set; } = 100;
    public bool RequireManualRestore { get; set; }
    public DateTimeOffset? DrainStartedAt { get; set; }
    public int DrainTimeoutMinutes { get; set; } = 30;
    /// <summary>Thumbprint of the client certificate presented to this backend (mutual TLS). Optional.</summary>
    public string? ClientCertificateThumbprint { get; set; }
    /// <summary>Pinned backend certificate thumbprint. Optional; normal chain validation always applies.</summary>
    public string? BackendCertificateThumbprint { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public string? ModifiedBy { get; set; }
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
    public List<ServerApplication> Applications { get; set; } = [];
}

public class ServerApplication : Entity, IRoutingConfigEntity
{
    public Guid ServerId { get; set; }
    public Server? Server { get; set; }
    public Guid ApplicationId { get; set; }
    public Application? Application { get; set; }
}

public class ServerPool : Entity, IConcurrencyStamped, ITracked, IRoutingConfigEntity
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DeploymentEnvironment Environment { get; set; }
    public string? Region { get; set; }
    public LoadBalancingAlgorithm Algorithm { get; set; }
    public AffinityMode Affinity { get; set; } = AffinityMode.PerSession;
    public Guid? FailoverPolicyId { get; set; }
    public FailoverPolicy? FailoverPolicy { get; set; }
    public int? MaxCapacity { get; set; }
    /// <summary>Comma-separated service plans allowed to use the pool; empty = any explicitly assigned company.</summary>
    public string? EligiblePlans { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public string? ModifiedBy { get; set; }
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
    public List<ServerPoolMember> Members { get; set; } = [];
    public List<ServerPoolApplication> Applications { get; set; } = [];
}

public class ServerPoolApplication : Entity, IRoutingConfigEntity
{
    public Guid PoolId { get; set; }
    public ServerPool? Pool { get; set; }
    public Guid ApplicationId { get; set; }
    public Application? Application { get; set; }
}

public class ServerPoolMember : Entity, IRoutingConfigEntity
{
    public Guid PoolId { get; set; }
    public ServerPool? Pool { get; set; }
    public Guid ServerId { get; set; }
    public Server? Server { get; set; }
    public int? WeightOverride { get; set; }
    public int Priority { get; set; }
    /// <summary>False temporarily removes the server from new allocations in this pool.</summary>
    public bool AcceptingNewSessions { get; set; } = true;
    public DateTimeOffset AddedAt { get; set; }
}

// ------------------------------------------------------------------------------------------------------------------
// Assignments (effective-dated: changes add rows and close the previous one, which gives history and rollback)
// ------------------------------------------------------------------------------------------------------------------

public abstract class AssignmentBase : Entity, IConcurrencyStamped, IRoutingConfigEntity
{
    public Guid CompanyId { get; set; }
    public Company? Company { get; set; }
    public Guid? ApplicationId { get; set; }
    public Application? Application { get; set; }
    public DeploymentEnvironment Environment { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset EffectiveFrom { get; set; }
    public DateTimeOffset? EffectiveTo { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public Guid? SupersededById { get; set; }
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}

public class CompanyServerAssignment : AssignmentBase
{
    public Guid PrimaryServerId { get; set; }
    public Server? PrimaryServer { get; set; }
    public Guid? SecondaryServerId { get; set; }
    public Server? SecondaryServer { get; set; }
    public Guid? ClusterPoolId { get; set; }
    public ServerPool? ClusterPool { get; set; }
    public ClusterMode ClusterMode { get; set; }
    public Guid? FailoverPolicyId { get; set; }
    public FailoverPolicy? FailoverPolicy { get; set; }
}

public class UserServerAssignment : AssignmentBase
{
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public Guid PrimaryServerId { get; set; }
    public Server? PrimaryServer { get; set; }
    public Guid? SecondaryServerId { get; set; }
    public Server? SecondaryServer { get; set; }
    public Guid? FailoverPolicyId { get; set; }
    public FailoverPolicy? FailoverPolicy { get; set; }
    public bool AllowCompanyRouteFallback { get; set; }
}

public class SharedPoolAssignment : AssignmentBase
{
    public Guid PoolId { get; set; }
    public ServerPool? Pool { get; set; }
    public int Priority { get; set; }
}

public class FailoverPolicy : Entity, IConcurrencyStamped, IRoutingConfigEntity
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool FailoverEnabled { get; set; }
    public Guid? FailoverPoolId { get; set; }
    public ServerPool? FailoverPool { get; set; }
    public FailbackPolicy FailbackPolicy { get; set; }
    public int MaxFailoverAttempts { get; set; } = 3;
    public int FailoverPriority { get; set; }
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}

public class RoutingPolicy : Entity, IConcurrencyStamped, IRoutingConfigEntity
{
    public required string Name { get; set; }
    public bool IsDefault { get; set; }
    public bool AllowDegradedServers { get; set; } = true;
    public AffinityMode DefaultAffinity { get; set; } = AffinityMode.PerSession;
    public double CpuWeight { get; set; } = 0.30;
    public double MemoryWeight { get; set; } = 0.20;
    public double ActiveRequestsWeight { get; set; } = 0.25;
    public double ResponseTimeWeight { get; set; } = 0.15;
    public double ErrorRateWeight { get; set; } = 0.10;
    public double ResponseTimeCeilingMs { get; set; } = 2000;
    public int SessionIdleTimeoutMinutes { get; set; } = 30;
    public int SessionAbsoluteTimeoutHours { get; set; } = 12;
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}

/// <summary>
/// Explicit grant that a server may hold/serve a company's data. Company assignments, cluster pools, failover pools
/// and shared pools assigned to the company are implicitly authorised; user-level assignments must stay inside the
/// authorised set.
/// </summary>
public class CompanyServerAuthorization : Entity, IRoutingConfigEntity
{
    public Guid CompanyId { get; set; }
    public Company? Company { get; set; }
    public Guid ServerId { get; set; }
    public Server? Server { get; set; }
    public string? Notes { get; set; }
    public string? GrantedBy { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
}

public class CapacityReservation : Entity, IRoutingConfigEntity
{
    public Guid ServerId { get; set; }
    public Server? Server { get; set; }
    public Guid CompanyId { get; set; }
    public Company? Company { get; set; }
    public int ReservedSessions { get; set; }
}

// ------------------------------------------------------------------------------------------------------------------
// Health, metrics, sessions, maintenance
// ------------------------------------------------------------------------------------------------------------------

public class ServerHealthHistory : Entity
{
    public Guid ServerId { get; set; }
    public HealthState FromState { get; set; }
    public HealthState ToState { get; set; }
    public DateTimeOffset At { get; set; }
    public string? Reason { get; set; }
    public HealthCheckSource Source { get; set; }
    public string? ReportedBy { get; set; }
}

/// <summary>Current health per server, written by the monitor leader and read by gateways (not per request).</summary>
public class ServerHealthCurrent
{
    public Guid ServerId { get; set; }
    public HealthState State { get; set; }
    public DateTimeOffset? StateChangedAt { get; set; }
    public DateTimeOffset? LastCheckAt { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public string? LastReason { get; set; }
    public int ConsecutiveFailures { get; set; }
    public double? ResponseTimeMs { get; set; }
    public double? CpuPercent { get; set; }
    public double? MemoryPercent { get; set; }
    public double? DiskPercent { get; set; }
    public int? ActiveConnections { get; set; }
    public int? ActiveRequests { get; set; }
    public int? QueueLength { get; set; }
    public double? ErrorRatePercent { get; set; }
    public int? ActiveSessions { get; set; }
    public bool? DatabaseHealthy { get; set; }
    public string? ReportedVersion { get; set; }
    public bool AwaitingManualRestore { get; set; }
}

public class ServerMetric : Entity
{
    public Guid ServerId { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public double? CpuPercent { get; set; }
    public double? MemoryPercent { get; set; }
    public double? DiskPercent { get; set; }
    public int? ActiveConnections { get; set; }
    public int? ActiveRequests { get; set; }
    public int? QueueLength { get; set; }
    public double? ErrorRatePercent { get; set; }
    public double? ResponseTimeMs { get; set; }
    public int? ActiveSessions { get; set; }
    public bool? DatabaseHealthy { get; set; }
}

public class RoutingDecisionLog : Entity
{
    public DateTimeOffset At { get; set; }
    public string? CorrelationId { get; set; }
    public string? GatewayInstance { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? ApplicationId { get; set; }
    public DeploymentEnvironment? Environment { get; set; }
    public RoutingOutcome Outcome { get; set; }
    public Guid? ServerId { get; set; }
    public RoutingSource Source { get; set; }
    public Guid? PoolId { get; set; }
    public bool IsFailover { get; set; }
    public bool IsFailback { get; set; }
    public bool AffinityApplied { get; set; }
    public string? Reason { get; set; }
    public long ConfigVersion { get; set; }
    public double DecisionMicroseconds { get; set; }
    public string? SessionKey { get; set; }
    public string? EventKind { get; set; }
}

/// <summary>One row per (session, application). Written asynchronously by gateways.</summary>
public class ActiveSession : Entity
{
    public required string SessionKey { get; set; }
    public Guid CompanyId { get; set; }
    public Guid UserId { get; set; }
    public Guid ApplicationId { get; set; }
    public Guid? ServerId { get; set; }
    public string? GatewayInstance { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? EndReason { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
    public string? ClientAddress { get; set; }
    public string? UserAgent { get; set; }
}

public class MaintenanceWindow : Entity, IConcurrencyStamped
{
    public Guid ServerId { get; set; }
    public Server? Server { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public int DrainTimeoutMinutes { get; set; } = 30;
    public bool AutoRestore { get; set; } = true;
    public MaintenanceStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}

// ------------------------------------------------------------------------------------------------------------------
// Audit, administration, settings, operations
// ------------------------------------------------------------------------------------------------------------------

public class AuditLog : Entity
{
    public DateTimeOffset At { get; set; }
    public required string Actor { get; set; }
    public string ActorType { get; set; } = "System";
    public required string EventType { get; set; }
    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? ServerId { get; set; }
    public string? PreviousValue { get; set; }
    public string? NewValue { get; set; }
    public string? Reason { get; set; }
    public string? CorrelationId { get; set; }
    public string? SourceIp { get; set; }
    public AlertSeverity Severity { get; set; }
}

public class Administrator : Entity, IConcurrencyStamped, ITracked
{
    public required string Username { get; set; }
    public string NormalizedUsername { get; set; } = "";
    public required string DisplayName { get; set; }
    public string? Email { get; set; }
    public string? PasswordHash { get; set; }
    public bool MfaEnabled { get; set; }
    public string? MfaSecretProtected { get; set; }
    public AccountStatus Status { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutEndAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public string? ModifiedBy { get; set; }
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
    public List<AdministratorRole> Roles { get; set; } = [];
}

public class Role : Entity
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public List<RolePermission> Permissions { get; set; } = [];
}

public class Permission : Entity
{
    public required string Code { get; set; }
    public string? Description { get; set; }
}

public class AdministratorRole : Entity
{
    public Guid AdministratorId { get; set; }
    public Administrator? Administrator { get; set; }
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
}

public class RolePermission : Entity
{
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
    public Guid PermissionId { get; set; }
    public Permission? Permission { get; set; }
}

public class SystemSetting : Entity, IConcurrencyStamped
{
    public required string Key { get; set; }
    public required string Value { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public string? ModifiedBy { get; set; }
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}

public class Alert : Entity
{
    public required string Type { get; set; }
    public AlertSeverity Severity { get; set; }
    public AlertStatus Status { get; set; }
    public required string DedupKey { get; set; }
    public required string Title { get; set; }
    public string? Message { get; set; }
    public Guid? ServerId { get; set; }
    public Guid? CompanyId { get; set; }
    public DateTimeOffset FirstRaisedAt { get; set; }
    public DateTimeOffset LastRaisedAt { get; set; }
    public int Occurrences { get; set; } = 1;
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
    public string? ResolutionNote { get; set; }
}

public class NotificationChannel : Entity, IConcurrencyStamped
{
    public required string Name { get; set; }
    public NotificationChannelType Type { get; set; }
    /// <summary>Email address list or webhook URL. Webhook signing secrets come from configuration, not the database.</summary>
    public required string Target { get; set; }
    public AlertSeverity MinimumSeverity { get; set; } = AlertSeverity.Warning;
    public bool IsEnabled { get; set; } = true;
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}

public class GatewayInstance : Entity
{
    public required string InstanceName { get; set; }
    public string? Host { get; set; }
    public string? Version { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastHeartbeatAt { get; set; }
    public long ConfigVersion { get; set; }
    public double RequestsPerSecond { get; set; }
    public double AverageLatencyMs { get; set; }
    public double AverageDecisionMicroseconds { get; set; }
    public long FailedRoutingLastInterval { get; set; }
    public long FailoversLastInterval { get; set; }
    public int ActiveRequests { get; set; }
    public bool RedisAvailable { get; set; }
    public bool DatabaseAvailable { get; set; }
    public bool ConfigurationStale { get; set; }
}

/// <summary>Traffic totals reported by each gateway every heartbeat (reports: requests processed, latency).</summary>
public class GatewayTrafficSample : Entity
{
    public required string GatewayInstance { get; set; }
    public DateTimeOffset At { get; set; }
    public int IntervalSeconds { get; set; }
    public long Requests { get; set; }
    public double AverageLatencyMs { get; set; }
    public double AverageDecisionMicroseconds { get; set; }
    public long FailedRouting { get; set; }
    public long Failovers { get; set; }
}

/// <summary>Leader lease, so exactly one monitor instance runs active checks and the drain controller.</summary>
public class ServiceLease : Entity, IConcurrencyStamped
{
    public required string Name { get; set; }
    public required string Holder { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}

/// <summary>
/// Append-only log of routing configuration versions. The newest row is the current version. Inserted in the same
/// transaction as every routing-relevant change, so a gateway can never see a change without a new version.
/// </summary>
public class ConfigurationVersion : Entity
{
    public long Version { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
    public string? ChangedBy { get; set; }
    public string? Summary { get; set; }
}
