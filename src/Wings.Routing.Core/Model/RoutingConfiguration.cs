namespace Wings.Routing.Model;

/// <summary>
/// Serializable, flat routing configuration. Produced from the database (or a cache) and indexed into an immutable
/// <see cref="RoutingSnapshot"/>. Contains no credentials or tokens.
/// </summary>
public sealed record RoutingConfiguration
{
    public long Version { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
    public IReadOnlyList<CompanyInfo> Companies { get; init; } = [];
    public IReadOnlyList<CompanyAliasInfo> CompanyAliases { get; init; } = [];
    /// <summary>Users that must be refused even with a valid session (disabled/locked users or memberships).</summary>
    public IReadOnlyList<UserKey> BlockedUsers { get; init; } = [];
    public IReadOnlyList<ApplicationInfo> Applications { get; init; } = [];
    public IReadOnlyList<ApplicationBindingInfo> ApplicationBindings { get; init; } = [];
    public IReadOnlyList<ServerInfo> Servers { get; init; } = [];
    public IReadOnlyList<PoolInfo> Pools { get; init; } = [];
    public IReadOnlyList<UserAssignmentInfo> UserAssignments { get; init; } = [];
    public IReadOnlyList<CompanyAssignmentInfo> CompanyAssignments { get; init; } = [];
    public IReadOnlyList<SharedPoolAssignmentInfo> SharedPoolAssignments { get; init; } = [];
    public IReadOnlyList<FailoverPolicyInfo> FailoverPolicies { get; init; } = [];
    public IReadOnlyList<RoutingPolicyInfo> RoutingPolicies { get; init; } = [];
    public IReadOnlyList<CapacityReservationInfo> CapacityReservations { get; init; } = [];
    /// <summary>Explicit company → server data authorisations (in addition to implicit ones from assignments).</summary>
    public IReadOnlyList<CompanyServerAuthorizationInfo> ServerAuthorizations { get; init; } = [];
}

public readonly record struct CompanyServerAuthorizationInfo(Guid CompanyId, Guid ServerId);

public readonly record struct UserKey(Guid CompanyId, Guid UserId);

public sealed record CompanyInfo(
    Guid Id,
    string Code,
    string Name,
    CompanyStatus Status,
    CompanyRoutingMode RoutingMode,
    string? ServicePlan,
    int? MaxConcurrentUsers,
    Guid? RoutingPolicyId);

public sealed record CompanyAliasInfo(Guid CompanyId, string Alias);

public sealed record ApplicationInfo(
    Guid Id,
    string Code,
    string Name,
    bool IsEnabled,
    string? MinimumServerVersion);

/// <summary>Maps a public host (and optional path prefix) to an application environment.</summary>
public sealed record ApplicationBindingInfo(
    Guid Id,
    Guid ApplicationId,
    DeploymentEnvironment Environment,
    string PublicHost,
    string PathPrefix,
    ApplicationRoutingMode RoutingMode,
    bool IsEnabled,
    int RequestTimeoutSeconds,
    long? MaxRequestBodyBytes);

public sealed record ServerInfo
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string BaseUrl { get; init; }
    /// <summary>Public URL used only by redirect-mode applications. Must be allow-listed.</summary>
    public string? PublicUrl { get; init; }
    public string? HealthCheckUrl { get; init; }
    /// <summary>Client certificate (thumbprint in the machine/user store) presented for mutual TLS.</summary>
    public string? ClientCertificateThumbprint { get; init; }
    /// <summary>Optional pinned backend certificate thumbprint, checked in addition to chain validation.</summary>
    public string? BackendCertificateThumbprint { get; init; }
    public DeploymentEnvironment Environment { get; init; }
    public string? Region { get; init; }
    public ServerType Type { get; init; }
    public ServerAdminState AdminState { get; init; }
    public int Priority { get; init; }
    public int Weight { get; init; } = 100;
    public int MaxCapacity { get; init; }
    public string? ApplicationVersion { get; init; }
    public bool RequireManualRestore { get; init; }
    public IReadOnlyList<Guid> ApplicationIds { get; init; } = [];
}

public sealed record PoolInfo
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public DeploymentEnvironment Environment { get; init; }
    public string? Region { get; init; }
    public LoadBalancingAlgorithm Algorithm { get; init; }
    public AffinityMode Affinity { get; init; }
    public Guid? FailoverPolicyId { get; init; }
    /// <summary>Service plans allowed to use this pool. Empty means any explicitly assigned company.</summary>
    public IReadOnlyList<string> EligiblePlans { get; init; } = [];
    public IReadOnlyList<Guid> ApplicationIds { get; init; } = [];
    public IReadOnlyList<PoolMemberInfo> Members { get; init; } = [];
}

public sealed record PoolMemberInfo(Guid ServerId, int? WeightOverride, int Priority, bool AcceptingNewSessions);

public interface IEffectiveDated
{
    DateTimeOffset EffectiveFrom { get; }
    DateTimeOffset? EffectiveTo { get; }
    bool IsEnabled { get; }
}

public interface IScopedAssignment : IEffectiveDated
{
    Guid Id { get; }
    Guid? ApplicationId { get; }
    DeploymentEnvironment Environment { get; }
}

public sealed record UserAssignmentInfo(
    Guid Id,
    Guid CompanyId,
    Guid UserId,
    Guid? ApplicationId,
    DeploymentEnvironment Environment,
    Guid PrimaryServerId,
    Guid? SecondaryServerId,
    Guid? FailoverPolicyId,
    bool AllowCompanyRouteFallback,
    bool IsEnabled,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveTo) : IScopedAssignment;

public sealed record CompanyAssignmentInfo(
    Guid Id,
    Guid CompanyId,
    Guid? ApplicationId,
    DeploymentEnvironment Environment,
    Guid PrimaryServerId,
    Guid? SecondaryServerId,
    Guid? ClusterPoolId,
    ClusterMode ClusterMode,
    Guid? FailoverPolicyId,
    bool IsEnabled,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveTo) : IScopedAssignment;

public sealed record SharedPoolAssignmentInfo(
    Guid Id,
    Guid CompanyId,
    Guid PoolId,
    Guid? ApplicationId,
    DeploymentEnvironment Environment,
    int Priority,
    bool IsEnabled,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveTo) : IScopedAssignment;

public sealed record FailoverPolicyInfo(
    Guid Id,
    string Name,
    bool FailoverEnabled,
    Guid? FailoverPoolId,
    FailbackPolicy Failback,
    int MaxFailoverAttempts,
    int FailoverPriority);

public sealed record RoutingPolicyInfo
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public bool IsDefault { get; init; }
    public bool AllowDegradedServers { get; init; } = true;
    public AffinityMode DefaultAffinity { get; init; } = AffinityMode.PerSession;
    public LoadScoreWeights Weights { get; init; } = LoadScoreWeights.Default;
    /// <summary>Response time (ms) treated as fully loaded when computing the load score.</summary>
    public double ResponseTimeCeilingMs { get; init; } = 2000;
}

public sealed record LoadScoreWeights(double Cpu, double Memory, double ActiveRequests, double ResponseTime, double ErrorRate)
{
    public static LoadScoreWeights Default { get; } = new(0.30, 0.20, 0.25, 0.15, 0.10);
}

public sealed record CapacityReservationInfo(Guid ServerId, Guid CompanyId, int ReservedSessions);
