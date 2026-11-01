namespace Wings.Routing.Model;

public enum DeploymentEnvironment
{
    Production = 0,
    Staging = 1,
    Test = 2,
    Development = 3
}

public enum ServerType
{
    Dedicated = 0,
    Shared = 1,
    Hybrid = 2
}

/// <summary>Administrative lifecycle state, set by administrators or the drain controller.</summary>
public enum ServerAdminState
{
    Active = 0,
    Draining = 1,
    Maintenance = 2,
    Disabled = 3,
    Retired = 4
}

/// <summary>Observed health, produced by active and passive checks.</summary>
public enum HealthState
{
    Unknown = 0,
    Healthy = 1,
    Degraded = 2,
    Unhealthy = 3,
    Recovering = 4
}

public enum CompanyStatus
{
    Active = 0,
    Suspended = 1,
    Disabled = 2
}

public enum CompanyRoutingMode
{
    /// <summary>Company uses explicit dedicated server assignments.</summary>
    Dedicated = 0,
    /// <summary>Company uses a shared pool.</summary>
    SharedPool = 1,
    /// <summary>Company is assigned to a cluster of permitted servers with a designated primary.</summary>
    Cluster = 2
}

public enum ClusterMode
{
    /// <summary>Always use the primary; other cluster members are failover candidates.</summary>
    PrimaryPreferred = 0,
    /// <summary>Spread sessions across all healthy cluster members using the cluster pool's algorithm.</summary>
    Balanced = 1
}

public enum LoadBalancingAlgorithm
{
    RoundRobin = 0,
    WeightedRoundRobin = 1,
    LeastRequests = 2,
    WeightedLeastRequests = 3,
    LowestUtilization = 4,
    ConsistentHashCompany = 5,
    ConsistentHashCompanyUser = 6
}

public enum AffinityMode
{
    None = 0,
    PerUser = 1,
    PerCompany = 2,
    PerSession = 3
}

public enum FailbackPolicy
{
    /// <summary>Return sessions to the primary as soon as it is healthy and stabilised.</summary>
    Automatic = 0,
    /// <summary>Keep failed-over sessions on the failover server until they end.</summary>
    EndOfSession = 1,
    /// <summary>As EndOfSession, and a recovered primary stays out of rotation until an administrator restores it.</summary>
    Manual = 2
}

public enum ApplicationRoutingMode
{
    /// <summary>Transparent reverse proxy (preferred).</summary>
    Proxy = 0,
    /// <summary>HTTP redirect to an allow-listed public server URL with a secure handoff token.</summary>
    Redirect = 1
}

public enum RoutingOutcome
{
    Routed = 0,
    /// <summary>The tenant/user/application combination is not authorised.</summary>
    Denied = 1,
    /// <summary>No assignment covers the tenant; fail closed.</summary>
    NoAssignment = 2,
    /// <summary>An assignment exists but no permitted destination is eligible and healthy.</summary>
    NoHealthyDestination = 3,
    /// <summary>The configuration snapshot is older than the last-known-good limit.</summary>
    ConfigurationStale = 4
}

public enum RoutingSource
{
    None = 0,
    UserAssignment = 1,
    CompanyAssignment = 2,
    CompanyCluster = 3,
    SharedPool = 4
}
