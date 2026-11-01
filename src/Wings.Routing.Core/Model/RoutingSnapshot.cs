using System.Collections.Frozen;

namespace Wings.Routing.Model;

/// <summary>
/// Immutable, indexed view of one <see cref="RoutingConfiguration"/> version. A routing decision reads exactly one
/// snapshot, so it always sees a consistent policy.
/// </summary>
public sealed class RoutingSnapshot
{
    private static readonly IReadOnlyList<UserAssignmentInfo> NoUserAssignments = [];
    private static readonly IReadOnlyList<CompanyAssignmentInfo> NoCompanyAssignments = [];
    private static readonly IReadOnlyList<SharedPoolAssignmentInfo> NoPoolAssignments = [];
    private static readonly IReadOnlySet<Guid> NoOwners = FrozenSet<Guid>.Empty;

    public RoutingSnapshot(RoutingConfiguration configuration, DateTimeOffset loadedAt)
    {
        Configuration = configuration;
        Version = configuration.Version;
        LoadedAt = loadedAt;
        LastValidatedAt = loadedAt;

        Companies = configuration.Companies.ToFrozenDictionary(c => c.Id);

        var lookup = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var company in configuration.Companies)
        {
            lookup.TryAdd(IdentifierNormalizer.Normalize(company.Code), company.Id);
        }
        foreach (var alias in configuration.CompanyAliases)
        {
            lookup.TryAdd(IdentifierNormalizer.Normalize(alias.Alias), alias.CompanyId);
        }
        CompanyLookup = lookup.ToFrozenDictionary(StringComparer.Ordinal);

        BlockedUsers = configuration.BlockedUsers.ToFrozenSet();
        Applications = configuration.Applications.ToFrozenDictionary(a => a.Id);
        Bindings = configuration.ApplicationBindings
            .Where(b => b.IsEnabled)
            .GroupBy(b => IdentifierNormalizer.NormalizeHost(b.PublicHost))
            .ToFrozenDictionary(
                g => g.Key,
                g => (IReadOnlyList<ApplicationBindingInfo>)g.OrderByDescending(b => b.PathPrefix.Length).ToArray(),
                StringComparer.Ordinal);
        PublishedApplications = configuration.ApplicationBindings
            .Where(b => b.IsEnabled)
            .Select(b => (b.ApplicationId, b.Environment))
            .ToFrozenSet();
        Servers = configuration.Servers.ToFrozenDictionary(s => s.Id);
        Pools = configuration.Pools.ToFrozenDictionary(p => p.Id);
        FailoverPolicies = configuration.FailoverPolicies.ToFrozenDictionary(f => f.Id);
        RoutingPolicies = configuration.RoutingPolicies.ToFrozenDictionary(p => p.Id);
        DefaultRoutingPolicy = configuration.RoutingPolicies.FirstOrDefault(p => p.IsDefault)
            ?? new RoutingPolicyInfo { Id = Guid.Empty, Name = "Built-in default", IsDefault = true };

        UserAssignments = configuration.UserAssignments
            .GroupBy(a => new UserKey(a.CompanyId, a.UserId))
            .ToFrozenDictionary(g => g.Key, g => (IReadOnlyList<UserAssignmentInfo>)g.ToArray());
        CompanyAssignments = configuration.CompanyAssignments
            .GroupBy(a => a.CompanyId)
            .ToFrozenDictionary(g => g.Key, g => (IReadOnlyList<CompanyAssignmentInfo>)g.ToArray());
        SharedPoolAssignments = configuration.SharedPoolAssignments
            .GroupBy(a => a.CompanyId)
            .ToFrozenDictionary(g => g.Key, g => (IReadOnlyList<SharedPoolAssignmentInfo>)g.ToArray());

        // Owners of each server through dedicated (user or company) assignments, used for isolation checks.
        var owners = new Dictionary<Guid, HashSet<Guid>>();
        void AddOwner(Guid serverId, Guid companyId)
        {
            if (!owners.TryGetValue(serverId, out var set)) owners[serverId] = set = [];
            set.Add(companyId);
        }
        foreach (var a in configuration.CompanyAssignments.Where(a => a.IsEnabled))
        {
            AddOwner(a.PrimaryServerId, a.CompanyId);
            if (a.SecondaryServerId is { } s) AddOwner(s, a.CompanyId);
            if (a.ClusterPoolId is { } poolId && Pools.TryGetValue(poolId, out var cluster))
            {
                foreach (var m in cluster.Members) AddOwner(m.ServerId, a.CompanyId);
            }
        }
        foreach (var a in configuration.UserAssignments.Where(a => a.IsEnabled))
        {
            AddOwner(a.PrimaryServerId, a.CompanyId);
            if (a.SecondaryServerId is { } s) AddOwner(s, a.CompanyId);
        }
        DedicatedOwners = owners.ToFrozenDictionary(kv => kv.Key, kv => (IReadOnlySet<Guid>)kv.Value.ToFrozenSet());

        // Servers authorised to hold each company's data: explicit grants plus everything its own company-level
        // assignments, cluster pools, failover pools and shared pools name.
        var authorized = new Dictionary<Guid, HashSet<Guid>>();
        void Authorize(Guid companyId, Guid serverId)
        {
            if (!authorized.TryGetValue(companyId, out var set)) authorized[companyId] = set = [];
            set.Add(serverId);
        }
        void AuthorizePool(Guid companyId, Guid? poolId)
        {
            if (poolId is { } id && Pools.TryGetValue(id, out var pool))
                foreach (var m in pool.Members) Authorize(companyId, m.ServerId);
        }
        void AuthorizeFailover(Guid companyId, Guid? policyId)
        {
            if (policyId is { } id && configuration.FailoverPolicies.FirstOrDefault(f => f.Id == id) is { } policy)
                AuthorizePool(companyId, policy.FailoverPoolId);
        }
        foreach (var g in configuration.ServerAuthorizations) Authorize(g.CompanyId, g.ServerId);
        foreach (var a in configuration.CompanyAssignments.Where(a => a.IsEnabled))
        {
            Authorize(a.CompanyId, a.PrimaryServerId);
            if (a.SecondaryServerId is { } s) Authorize(a.CompanyId, s);
            AuthorizePool(a.CompanyId, a.ClusterPoolId);
            AuthorizeFailover(a.CompanyId, a.FailoverPolicyId);
        }
        foreach (var a in configuration.SharedPoolAssignments.Where(a => a.IsEnabled))
        {
            AuthorizePool(a.CompanyId, a.PoolId);
            if (Pools.TryGetValue(a.PoolId, out var pool)) AuthorizeFailover(a.CompanyId, pool.FailoverPolicyId);
        }
        foreach (var a in configuration.UserAssignments.Where(a => a.IsEnabled))
            AuthorizeFailover(a.CompanyId, a.FailoverPolicyId);
        AuthorizedServers = authorized.ToFrozenDictionary(kv => kv.Key, kv => (IReadOnlySet<Guid>)kv.Value.ToFrozenSet());

        CapacityReservations = configuration.CapacityReservations
            .GroupBy(r => r.ServerId)
            .ToFrozenDictionary(g => g.Key, g => (IReadOnlyList<CapacityReservationInfo>)g.ToArray());
    }

    public RoutingConfiguration Configuration { get; }
    public long Version { get; }
    public DateTimeOffset LoadedAt { get; }

    /// <summary>Last time the authoritative store confirmed this version is current. Used for last-known-good limits.</summary>
    public DateTimeOffset LastValidatedAt { get; private set; }

    public FrozenDictionary<Guid, CompanyInfo> Companies { get; }
    public FrozenDictionary<string, Guid> CompanyLookup { get; }
    public FrozenSet<UserKey> BlockedUsers { get; }
    public FrozenDictionary<Guid, ApplicationInfo> Applications { get; }
    public FrozenDictionary<string, IReadOnlyList<ApplicationBindingInfo>> Bindings { get; }
    /// <summary>Application/environment pairs that have at least one enabled public binding.</summary>
    public FrozenSet<(Guid ApplicationId, DeploymentEnvironment Environment)> PublishedApplications { get; }
    public FrozenDictionary<Guid, ServerInfo> Servers { get; }
    public FrozenDictionary<Guid, PoolInfo> Pools { get; }
    public FrozenDictionary<Guid, FailoverPolicyInfo> FailoverPolicies { get; }
    public FrozenDictionary<Guid, RoutingPolicyInfo> RoutingPolicies { get; }
    public RoutingPolicyInfo DefaultRoutingPolicy { get; }
    public FrozenDictionary<UserKey, IReadOnlyList<UserAssignmentInfo>> UserAssignments { get; }
    public FrozenDictionary<Guid, IReadOnlyList<CompanyAssignmentInfo>> CompanyAssignments { get; }
    public FrozenDictionary<Guid, IReadOnlyList<SharedPoolAssignmentInfo>> SharedPoolAssignments { get; }
    public FrozenDictionary<Guid, IReadOnlySet<Guid>> DedicatedOwners { get; }
    public FrozenDictionary<Guid, IReadOnlyList<CapacityReservationInfo>> CapacityReservations { get; }
    public FrozenDictionary<Guid, IReadOnlySet<Guid>> AuthorizedServers { get; }

    public bool IsAuthorizedFor(Guid companyId, Guid serverId) =>
        AuthorizedServers.TryGetValue(companyId, out var set) && set.Contains(serverId);

    public void MarkValidated(DateTimeOffset at)
    {
        if (at > LastValidatedAt) LastValidatedAt = at;
    }

    public bool TryResolveCompany(string codeOrAlias, out CompanyInfo company)
    {
        company = null!;
        return CompanyLookup.TryGetValue(IdentifierNormalizer.Normalize(codeOrAlias), out var id)
               && Companies.TryGetValue(id, out company!);
    }

    /// <summary>Finds the application binding for a public host and request path (longest prefix wins).</summary>
    public ApplicationBindingInfo? ResolveBinding(string host, string path)
    {
        if (!Bindings.TryGetValue(IdentifierNormalizer.NormalizeHost(host), out var candidates)) return null;
        foreach (var binding in candidates)
        {
            if (PathMatches(path, binding.PathPrefix)) return binding;
        }
        return null;
    }

    public IReadOnlyList<UserAssignmentInfo> GetUserAssignments(Guid companyId, Guid userId) =>
        UserAssignments.TryGetValue(new UserKey(companyId, userId), out var list) ? list : NoUserAssignments;

    public IReadOnlyList<CompanyAssignmentInfo> GetCompanyAssignments(Guid companyId) =>
        CompanyAssignments.TryGetValue(companyId, out var list) ? list : NoCompanyAssignments;

    public IReadOnlyList<SharedPoolAssignmentInfo> GetSharedPoolAssignments(Guid companyId) =>
        SharedPoolAssignments.TryGetValue(companyId, out var list) ? list : NoPoolAssignments;

    public IReadOnlySet<Guid> GetDedicatedOwners(Guid serverId) =>
        DedicatedOwners.TryGetValue(serverId, out var set) ? set : NoOwners;

    public RoutingPolicyInfo GetRoutingPolicy(CompanyInfo company) =>
        company.RoutingPolicyId is { } id && RoutingPolicies.TryGetValue(id, out var policy) ? policy : DefaultRoutingPolicy;

    internal static bool PathMatches(string path, string prefix)
    {
        if (prefix.Length == 0 || prefix == "/") return true;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        return path.Length == prefix.Length || path[prefix.Length] == '/' || prefix.EndsWith('/');
    }
}
