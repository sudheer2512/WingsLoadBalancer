using Microsoft.EntityFrameworkCore;
using Wings.LoadBalancer.Data.Entities;
using Wings.Routing.Model;

namespace Wings.LoadBalancer.Data;

public interface IRoutingConfigurationSource
{
    /// <summary>Current configuration version (cheap query).</summary>
    Task<long> GetCurrentVersionAsync(CancellationToken cancellationToken);

    /// <summary>Loads the full routing configuration in one consistent read.</summary>
    Task<RoutingConfiguration> LoadAsync(CancellationToken cancellationToken);
}

public sealed class DbRoutingConfigurationSource(WingsDbContext db, TimeProvider time) : IRoutingConfigurationSource
{
    public async Task<long> GetCurrentVersionAsync(CancellationToken cancellationToken) =>
        await db.ConfigurationVersions.AsNoTracking()
            .OrderByDescending(v => v.ChangedAt)
            .Select(v => v.Version)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<RoutingConfiguration> LoadAsync(CancellationToken cancellationToken) =>
        // Retrying execution strategies (SQL Server) require user transactions to run inside the strategy.
        db.Database.CreateExecutionStrategy().ExecuteAsync(() => LoadCoreAsync(cancellationToken));

    private async Task<RoutingConfiguration> LoadCoreAsync(CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var now = time.GetUtcNow();
        // Read everything in one transaction so the snapshot is internally consistent.
        var isolation = db.Database.IsSqlServer()
            ? System.Data.IsolationLevel.RepeatableRead
            : System.Data.IsolationLevel.Serializable;
        await using var tx = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(isolation, cancellationToken)
            : null;

        var version = await GetCurrentVersionAsync(cancellationToken);
        var companies = await db.Companies.AsNoTracking().ToListAsync(cancellationToken);
        var aliases = await db.CompanyAliases.AsNoTracking().ToListAsync(cancellationToken);

        // Only disabled identities are needed per request; active users are validated at login.
        var blocked = await db.UserCompanyMemberships.AsNoTracking()
            .Where(m => m.Status != AccountStatus.Active || m.User!.Status != AccountStatus.Active)
            .Select(m => new { m.CompanyId, m.UserId })
            .ToListAsync(cancellationToken);

        var applications = await db.Applications.AsNoTracking().ToListAsync(cancellationToken);
        var bindings = await db.ApplicationEnvironments.AsNoTracking().ToListAsync(cancellationToken);
        var servers = await db.Servers.AsNoTracking()
            .Where(s => s.AdminState != ServerAdminState.Retired)
            .Include(s => s.Applications)
            .ToListAsync(cancellationToken);
        var pools = await db.ServerPools.AsNoTracking()
            .Include(p => p.Members)
            .Include(p => p.Applications)
            .ToListAsync(cancellationToken);

        // Current and future assignments; expired rows are history only.
        var userAssignments = await db.UserServerAssignments.AsNoTracking()
            .Where(a => a.IsEnabled && (a.EffectiveTo == null || a.EffectiveTo > now))
            .ToListAsync(cancellationToken);
        var companyAssignments = await db.CompanyServerAssignments.AsNoTracking()
            .Where(a => a.IsEnabled && (a.EffectiveTo == null || a.EffectiveTo > now))
            .ToListAsync(cancellationToken);
        var poolAssignments = await db.SharedPoolAssignments.AsNoTracking()
            .Where(a => a.IsEnabled && (a.EffectiveTo == null || a.EffectiveTo > now))
            .ToListAsync(cancellationToken);
        var failoverPolicies = await db.FailoverPolicies.AsNoTracking().ToListAsync(cancellationToken);
        var routingPolicies = await db.RoutingPolicies.AsNoTracking().ToListAsync(cancellationToken);
        var reservations = await db.CapacityReservations.AsNoTracking().ToListAsync(cancellationToken);
        var authorizations = await db.CompanyServerAuthorizations.AsNoTracking()
            .Select(a => new CompanyServerAuthorizationInfo(a.CompanyId, a.ServerId)).ToListAsync(cancellationToken);

        if (tx is not null) await tx.CommitAsync(cancellationToken);

        return new RoutingConfiguration
        {
            Version = version,
            GeneratedAt = now,
            Companies = companies.Select(ToInfo).ToArray(),
            CompanyAliases = aliases.Select(a => new CompanyAliasInfo(a.CompanyId, a.Alias)).ToArray(),
            BlockedUsers = blocked.Select(b => new UserKey(b.CompanyId, b.UserId)).Distinct().ToArray(),
            Applications = applications.Select(a => new ApplicationInfo(a.Id, a.Code, a.Name, a.IsEnabled, a.MinimumServerVersion)).ToArray(),
            ApplicationBindings = bindings.Select(ToInfo).ToArray(),
            Servers = servers.Select(ToInfo).ToArray(),
            Pools = pools.Select(ToInfo).ToArray(),
            UserAssignments = userAssignments.Select(ToInfo).ToArray(),
            CompanyAssignments = companyAssignments.Select(ToInfo).ToArray(),
            SharedPoolAssignments = poolAssignments.Select(ToInfo).ToArray(),
            FailoverPolicies = failoverPolicies.Select(ToInfo).ToArray(),
            RoutingPolicies = routingPolicies.Select(ToInfo).ToArray(),
            CapacityReservations = reservations.Select(r => new CapacityReservationInfo(r.ServerId, r.CompanyId, r.ReservedSessions)).ToArray(),
            ServerAuthorizations = authorizations
        };
    }

    public static CompanyInfo ToInfo(Company c) =>
        new(c.Id, c.Code, c.Name, c.Status, c.RoutingMode, c.ServicePlan, c.MaxConcurrentUsers, c.RoutingPolicyId);

    public static ApplicationBindingInfo ToInfo(ApplicationEnvironment b) =>
        new(b.Id, b.ApplicationId, b.Environment, b.PublicHost, b.PathPrefix, b.RoutingMode, b.IsEnabled,
            b.RequestTimeoutSeconds, b.MaxRequestBodyBytes);

    public static ServerInfo ToInfo(Server s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        BaseUrl = s.BaseUrl,
        PublicUrl = s.PublicUrl,
        HealthCheckUrl = s.HealthCheckUrl,
        ClientCertificateThumbprint = s.ClientCertificateThumbprint,
        BackendCertificateThumbprint = s.BackendCertificateThumbprint,
        Environment = s.Environment,
        Region = s.Region,
        Type = s.ServerType,
        AdminState = s.AdminState,
        Priority = s.Priority,
        Weight = s.Weight,
        MaxCapacity = s.MaxCapacity,
        ApplicationVersion = s.ApplicationVersion,
        RequireManualRestore = s.RequireManualRestore,
        ApplicationIds = s.Applications.Select(a => a.ApplicationId).ToArray()
    };

    public static PoolInfo ToInfo(ServerPool p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Environment = p.Environment,
        Region = p.Region,
        Algorithm = p.Algorithm,
        Affinity = p.Affinity,
        FailoverPolicyId = p.FailoverPolicyId,
        EligiblePlans = string.IsNullOrWhiteSpace(p.EligiblePlans)
            ? []
            : p.EligiblePlans.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        ApplicationIds = p.Applications.Select(a => a.ApplicationId).ToArray(),
        Members = p.Members.Select(m => new PoolMemberInfo(m.ServerId, m.WeightOverride, m.Priority, m.AcceptingNewSessions)).ToArray()
    };

    public static UserAssignmentInfo ToInfo(UserServerAssignment a) =>
        new(a.Id, a.CompanyId, a.UserId, a.ApplicationId, a.Environment, a.PrimaryServerId, a.SecondaryServerId,
            a.FailoverPolicyId, a.AllowCompanyRouteFallback, a.IsEnabled, a.EffectiveFrom, a.EffectiveTo);

    public static CompanyAssignmentInfo ToInfo(CompanyServerAssignment a) =>
        new(a.Id, a.CompanyId, a.ApplicationId, a.Environment, a.PrimaryServerId, a.SecondaryServerId, a.ClusterPoolId,
            a.ClusterMode, a.FailoverPolicyId, a.IsEnabled, a.EffectiveFrom, a.EffectiveTo);

    public static SharedPoolAssignmentInfo ToInfo(SharedPoolAssignment a) =>
        new(a.Id, a.CompanyId, a.PoolId, a.ApplicationId, a.Environment, a.Priority, a.IsEnabled, a.EffectiveFrom, a.EffectiveTo);

    public static FailoverPolicyInfo ToInfo(FailoverPolicy f) =>
        new(f.Id, f.Name, f.FailoverEnabled, f.FailoverPoolId, f.FailbackPolicy, f.MaxFailoverAttempts, f.FailoverPriority);

    public static RoutingPolicyInfo ToInfo(RoutingPolicy p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        IsDefault = p.IsDefault,
        AllowDegradedServers = p.AllowDegradedServers,
        DefaultAffinity = p.DefaultAffinity,
        Weights = new LoadScoreWeights(p.CpuWeight, p.MemoryWeight, p.ActiveRequestsWeight, p.ResponseTimeWeight, p.ErrorRateWeight),
        ResponseTimeCeilingMs = p.ResponseTimeCeilingMs
    };
}
