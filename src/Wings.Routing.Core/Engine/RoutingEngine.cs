using Wings.Routing.Balancing;
using Wings.Routing.Model;
using Wings.Routing.Runtime;

namespace Wings.Routing.Engine;

/// <summary>
/// Deterministic tenant routing policy engine. Independent of any transport: the gateway (YARP), the simulator and
/// tests all call the same <see cref="Route"/> method.
/// </summary>
public sealed class RoutingEngine(
    ILoadBalancingStrategyRegistry strategies,
    TimeProvider time,
    RoutingEngineOptions options) : IRoutingEngine
{
    public RoutingEngine() : this(new LoadBalancingStrategyRegistry(), TimeProvider.System, new RoutingEngineOptions())
    {
    }

    public RoutingDecision Route(RoutingSnapshot snapshot, IServerStateView state, RoutingRequest request)
    {
        var ctx = new Context(snapshot, state, request, time.GetUtcNow(), request.CollectTrace ? [] : null);

        if (options.LastKnownGoodMaxAge is { } maxAge && ctx.Now - snapshot.LastValidatedAt > maxAge)
        {
            return ctx.Fail(RoutingOutcome.ConfigurationStale,
                $"Configuration v{snapshot.Version} not confirmed for {(ctx.Now - snapshot.LastValidatedAt).TotalMinutes:0} min (limit {maxAge.TotalMinutes:0} min)");
        }

        if (!snapshot.Companies.TryGetValue(request.CompanyId, out var company))
            return ctx.Fail(RoutingOutcome.Denied, "Unknown company");
        if (company.Status != CompanyStatus.Active)
            return ctx.Fail(RoutingOutcome.Denied, $"Company is {company.Status}");
        if (snapshot.BlockedUsers.Contains(new UserKey(request.CompanyId, request.UserId)))
            return ctx.Fail(RoutingOutcome.Denied, "User or membership is disabled");
        if (!snapshot.Applications.TryGetValue(request.ApplicationId, out var application) || !application.IsEnabled)
            return ctx.Fail(RoutingOutcome.Denied, "Application is unknown or disabled");
        if (!snapshot.PublishedApplications.Contains((request.ApplicationId, request.Environment)))
            return ctx.Fail(RoutingOutcome.Denied, $"Application is not published in {request.Environment}");

        ctx.Company = company;
        ctx.Application = application;
        ctx.Policy = snapshot.GetRoutingPolicy(company);
        ctx.Log($"Company {company.Code} ({company.Id}) active; policy '{ctx.Policy.Name}'");

        // 1. User-level assignment.
        var userAssignment = SelectEffective(snapshot.GetUserAssignments(company.Id, request.UserId), ctx);
        if (userAssignment is not null)
        {
            ctx.Log($"User assignment {userAssignment.Id} → primary {ServerLabel(snapshot, userAssignment.PrimaryServerId)}");
            var decision = RouteDedicated(ctx, RoutingSource.UserAssignment, userAssignment.Id,
                userAssignment.PrimaryServerId, userAssignment.SecondaryServerId, null, ClusterMode.PrimaryPreferred,
                userAssignment.FailoverPolicyId);
            if (decision.IsRouted || !userAssignment.AllowCompanyRouteFallback) return decision;
            ctx.Log("User assignment has no healthy destination; assignment permits the company route");
        }
        else
        {
            ctx.Log("No effective user-level assignment");
        }

        // 2. Company-level assignment (single server or cluster).
        var companyAssignment = SelectEffective(snapshot.GetCompanyAssignments(company.Id), ctx);
        if (companyAssignment is not null)
        {
            ctx.Log($"Company assignment {companyAssignment.Id} → primary {ServerLabel(snapshot, companyAssignment.PrimaryServerId)}");
            PoolInfo? cluster = null;
            if (companyAssignment.ClusterPoolId is { } clusterId && !snapshot.Pools.TryGetValue(clusterId, out cluster))
                ctx.Log($"Cluster pool {clusterId} not found; using primary/secondary only");
            return RouteDedicated(ctx,
                cluster is null ? RoutingSource.CompanyAssignment : RoutingSource.CompanyCluster,
                companyAssignment.Id, companyAssignment.PrimaryServerId, companyAssignment.SecondaryServerId,
                cluster, companyAssignment.ClusterMode, companyAssignment.FailoverPolicyId);
        }
        ctx.Log("No effective company-level assignment");

        // 3. Shared pools explicitly assigned to the company, in priority order.
        RoutingDecision? lastPoolDecision = null;
        foreach (var poolAssignment in EffectiveAll(snapshot.GetSharedPoolAssignments(company.Id), ctx).OrderBy(a => a.Priority))
        {
            if (!snapshot.Pools.TryGetValue(poolAssignment.PoolId, out var pool))
            {
                ctx.Log($"Shared pool {poolAssignment.PoolId} not found");
                continue;
            }
            var poolEligibility = CheckPoolEligibility(pool, ctx);
            if (poolEligibility is not null)
            {
                ctx.Log($"Pool '{pool.Name}' not eligible: {poolEligibility}");
                continue;
            }
            ctx.Log($"Shared pool assignment {poolAssignment.Id} → pool '{pool.Name}' ({pool.Algorithm}, affinity {pool.Affinity})");
            lastPoolDecision = RoutePool(ctx, pool, poolAssignment.Id);
            if (lastPoolDecision.IsRouted) return lastPoolDecision;
        }
        if (lastPoolDecision is not null) return lastPoolDecision;

        // Never treat a missing assignment as permission to use any server.
        return ctx.Fail(RoutingOutcome.NoAssignment, "No user, company or shared-pool assignment covers this tenant");
    }

    public EligibilityResult CheckEligibility(RoutingSnapshot snapshot, IServerStateView state, RoutingRequest request,
        Guid serverId, bool forNewAllocation)
    {
        var ctx = new Context(snapshot, state, request, time.GetUtcNow(), null);
        if (!snapshot.Companies.TryGetValue(request.CompanyId, out var company))
            return new EligibilityResult(false, "Unknown company");
        if (!snapshot.Applications.TryGetValue(request.ApplicationId, out var app))
            return new EligibilityResult(false, "Unknown application");
        ctx.Company = company;
        ctx.Application = app;
        ctx.Policy = snapshot.GetRoutingPolicy(company);
        var reason = Ineligibility(ctx, serverId, forNewAllocation);
        return new EligibilityResult(reason is null, reason ?? "Eligible");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Dedicated (user or company) routing
    // ---------------------------------------------------------------------------------------------------------------

    private RoutingDecision RouteDedicated(Context ctx, RoutingSource source, Guid assignmentId, Guid primaryId,
        Guid? secondaryId, PoolInfo? cluster, ClusterMode clusterMode, Guid? failoverPolicyId)
    {
        var snapshot = ctx.Snapshot;
        FailoverPolicyInfo? failover = null;
        if (failoverPolicyId is { } fpId && !snapshot.FailoverPolicies.TryGetValue(fpId, out failover))
            ctx.Log($"Failover policy {fpId} not found; failover disabled");
        var failoverEnabled = failover?.FailoverEnabled == true;

        // Defence in depth: a user-level assignment may only point at servers authorised for the company's data.
        if (source == RoutingSource.UserAssignment)
        {
            foreach (var id in secondaryId is { } sid ? new[] { primaryId, sid } : [primaryId])
            {
                if (!snapshot.IsAuthorizedFor(ctx.Request.CompanyId, id))
                {
                    ctx.Log($"{ServerLabel(snapshot, id)} is not authorised for this company's data");
                    return ctx.Fail(RoutingOutcome.Denied, "User assignment points to a server not authorised for the company",
                        source, assignmentId);
                }
            }
        }

        var balanced = cluster is not null && clusterMode == ClusterMode.Balanced;
        var primaryGroup = balanced
            ? cluster!.Members.Select(m => m.ServerId).Append(primaryId).Distinct().ToArray()
            : [primaryId];
        var alternates = BuildAlternates(ctx, primaryGroup, secondaryId, balanced ? null : cluster, failover);

        RoutingDecision Routed(Guid serverId, string reason, bool isFailover = false, bool isFailback = false,
            bool affinity = false, string? detail = null) =>
            ctx.Success(serverId, reason, source, assignmentId, cluster?.Id, failover?.Id, isFailover, isFailback,
                affinity, AffinityMode.PerSession, detail, ctx.Tracing ? NextAlternate(ctx, serverId, alternates) : null);

        // Existing session affinity, honoured only while the server is still permitted and eligible.
        if (ctx.Request.AffinityServerId is { } pinned)
        {
            if (primaryGroup.Contains(pinned))
            {
                var why = Ineligibility(ctx, pinned, forNewAllocation: false);
                if (why is null) return Routed(pinned, "Session affinity to assigned server", affinity: true);
                ctx.Log($"Affinity server {ServerLabel(snapshot, pinned)} not usable: {why}");
            }
            else if (failoverEnabled && alternates.Contains(pinned))
            {
                var why = Ineligibility(ctx, pinned, forNewAllocation: false);
                var primaryBack = !balanced && Ineligibility(ctx, primaryId, forNewAllocation: true) is null;
                if (primaryBack && failover!.Failback == FailbackPolicy.Automatic)
                    return Routed(primaryId, "Primary recovered; automatic failback", isFailback: true);
                if (why is null)
                    return Routed(pinned, $"Session stays on failover server ({failover!.Failback} failback)",
                        isFailover: true, affinity: true);
                ctx.Log($"Failover affinity server {ServerLabel(snapshot, pinned)} not usable: {why}");
            }
            else
            {
                ctx.Log($"Affinity server {ServerLabel(snapshot, pinned)} is not permitted by the current assignment; ignored");
            }
        }

        if (balanced)
        {
            var candidates = Candidates(ctx, cluster!.Members.Where(m => m.AcceptingNewSessions || m.ServerId == primaryId)
                .Select(m => (m.ServerId, m.WeightOverride, m.Priority)));
            if (candidates.Count > 0)
            {
                var strategy = strategies.Get(cluster.Algorithm);
                var chosen = strategy.Select(candidates, new SelectionContext(cluster.Id, ctx.Request.CompanyId, ctx.Request.UserId, ctx.Policy));
                return Routed(chosen.Server.Id, $"Cluster '{cluster.Name}' balanced selection",
                    detail: strategy.Describe(chosen, candidates));
            }
            ctx.Log($"No eligible member in cluster '{cluster.Name}'");
        }
        else
        {
            var why = Ineligibility(ctx, primaryId, forNewAllocation: true);
            if (why is null) return Routed(primaryId, source == RoutingSource.UserAssignment
                ? "User-dedicated server"
                : "Company-dedicated server");
            ctx.Log($"Primary {ServerLabel(snapshot, primaryId)} not usable: {why}");
        }

        if (!failoverEnabled)
        {
            return ctx.Fail(RoutingOutcome.NoHealthyDestination,
                failover is null ? "Assigned server unavailable and no failover policy is attached"
                    : $"Assigned server unavailable and failover is disabled by policy '{failover.Name}'",
                source, assignmentId);
        }

        var attempts = 0;
        var maxAttempts = failover!.MaxFailoverAttempts <= 0 ? int.MaxValue : failover.MaxFailoverAttempts;
        foreach (var alternate in alternates)
        {
            if (attempts++ >= maxAttempts)
            {
                ctx.Log($"Maximum failover attempts ({failover.MaxFailoverAttempts}) reached");
                break;
            }
            var why = Ineligibility(ctx, alternate, forNewAllocation: true);
            if (why is null) return Routed(alternate, $"Failover to permitted server under policy '{failover.Name}'", isFailover: true);
            ctx.Log($"Failover candidate {ServerLabel(snapshot, alternate)} rejected: {why}");
        }

        return ctx.Fail(RoutingOutcome.NoHealthyDestination,
            $"No permitted failover destination is available under policy '{failover.Name}'", source, assignmentId);
    }

    /// <summary>Permitted alternates in order: secondary, cluster members (primary-preferred), failover pool.</summary>
    private static List<Guid> BuildAlternates(Context ctx, Guid[] primaryGroup, Guid? secondaryId, PoolInfo? cluster,
        FailoverPolicyInfo? failover)
    {
        var list = new List<Guid>();
        void Add(Guid id)
        {
            if (!primaryGroup.Contains(id) && !list.Contains(id)) list.Add(id);
        }

        if (secondaryId is { } s) Add(s);
        if (cluster is not null)
        {
            foreach (var m in cluster.Members.OrderBy(m => m.Priority)) Add(m.ServerId);
        }
        if (failover is { FailoverEnabled: true, FailoverPoolId: { } poolId } && ctx.Snapshot.Pools.TryGetValue(poolId, out var pool))
        {
            foreach (var m in OrderFailoverMembers(ctx, pool)) Add(m.ServerId);
        }
        return list;
    }

    private static IEnumerable<PoolMemberInfo> OrderFailoverMembers(Context ctx, PoolInfo pool) =>
        pool.Members
            .OrderBy(m => m.Priority)
            .ThenBy(m => ctx.Snapshot.Servers.TryGetValue(m.ServerId, out var s)
                ? LoadScoreCalculator.Calculate(s, ctx.State.Get(s.Id), ctx.Policy)
                : double.MaxValue)
            .ThenBy(m => m.ServerId);

    private Guid? NextAlternate(Context ctx, Guid chosen, IEnumerable<Guid> alternates)
    {
        foreach (var id in alternates)
        {
            if (id != chosen && Ineligibility(ctx, id, forNewAllocation: true) is null) return id;
        }
        return null;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Shared pool routing
    // ---------------------------------------------------------------------------------------------------------------

    private string? CheckPoolEligibility(PoolInfo pool, Context ctx)
    {
        if (pool.Environment != ctx.Request.Environment) return $"pool is for {pool.Environment}";
        if (pool.ApplicationIds.Count > 0 && !pool.ApplicationIds.Contains(ctx.Request.ApplicationId))
            return "pool does not serve this application";
        if (pool.EligiblePlans.Count > 0 &&
            (ctx.Company!.ServicePlan is null ||
             !pool.EligiblePlans.Contains(ctx.Company.ServicePlan, StringComparer.OrdinalIgnoreCase)))
            return $"service plan '{ctx.Company!.ServicePlan}' is not eligible";
        return null;
    }

    private RoutingDecision RoutePool(Context ctx, PoolInfo pool, Guid assignmentId)
    {
        var snapshot = ctx.Snapshot;
        var affinity = pool.Affinity;
        FailoverPolicyInfo? failover = null;
        if (pool.FailoverPolicyId is { } fpId) snapshot.FailoverPolicies.TryGetValue(fpId, out failover);

        RoutingDecision Routed(Guid serverId, string reason, bool isFailover = false, bool affinityApplied = false,
            string? detail = null) =>
            ctx.Success(serverId, reason, RoutingSource.SharedPool, assignmentId, pool.Id, failover?.Id, isFailover,
                false, affinityApplied, affinity, detail, null);

        if (affinity != AffinityMode.None && ctx.Request.AffinityServerId is { } pinned)
        {
            var isMember = pool.Members.Any(m => m.ServerId == pinned);
            var isFailoverMember = !isMember && failover is { FailoverEnabled: true, FailoverPoolId: { } fp }
                                   && snapshot.Pools.TryGetValue(fp, out var fpPool)
                                   && fpPool.Members.Any(m => m.ServerId == pinned);
            if (isMember || isFailoverMember)
            {
                var why = Ineligibility(ctx, pinned, forNewAllocation: false);
                if (why is null)
                    return Routed(pinned, "Session affinity within shared pool", isFailover: isFailoverMember, affinityApplied: true);
                ctx.Log($"Affinity server {ServerLabel(snapshot, pinned)} not usable: {why}");
            }
            else
            {
                ctx.Log($"Affinity server {ServerLabel(snapshot, pinned)} is not in pool '{pool.Name}'; ignored");
            }
        }

        var candidates = Candidates(ctx, pool.Members.Where(m => m.AcceptingNewSessions)
            .Select(m => (m.ServerId, m.WeightOverride, m.Priority)));
        if (candidates.Count > 0)
        {
            var selection = new SelectionContext(pool.Id, ctx.Request.CompanyId, ctx.Request.UserId, ctx.Policy);
            Candidate chosen;
            string detail;
            switch (affinity)
            {
                case AffinityMode.PerCompany:
                    chosen = RendezvousHash.Pick(candidates, ctx.Request.CompanyId, Guid.Empty);
                    detail = "Company affinity (consistent hash on company)";
                    break;
                case AffinityMode.PerUser:
                    chosen = RendezvousHash.Pick(candidates, ctx.Request.CompanyId, ctx.Request.UserId);
                    detail = "User affinity (consistent hash on company and user)";
                    break;
                default:
                    var strategy = strategies.Get(pool.Algorithm);
                    chosen = strategy.Select(candidates, selection);
                    detail = strategy.Describe(chosen, candidates);
                    break;
            }
            ctx.Log($"Selected {chosen.Server.Name} from {candidates.Count} eligible: {detail}");
            return Routed(chosen.Server.Id, $"Shared pool '{pool.Name}'", detail: detail);
        }
        ctx.Log($"No eligible server in pool '{pool.Name}'");

        if (failover is { FailoverEnabled: true, FailoverPoolId: { } failoverPoolId }
            && snapshot.Pools.TryGetValue(failoverPoolId, out var failoverPool))
        {
            var attempts = 0;
            var maxAttempts = failover.MaxFailoverAttempts <= 0 ? int.MaxValue : failover.MaxFailoverAttempts;
            foreach (var member in OrderFailoverMembers(ctx, failoverPool))
            {
                if (attempts++ >= maxAttempts) break;
                var why = Ineligibility(ctx, member.ServerId, forNewAllocation: true);
                if (why is null)
                    return Routed(member.ServerId, $"Failover from pool '{pool.Name}' to '{failoverPool.Name}'", isFailover: true);
                ctx.Log($"Failover candidate {ServerLabel(snapshot, member.ServerId)} rejected: {why}");
            }
        }

        return ctx.Fail(RoutingOutcome.NoHealthyDestination, $"No eligible server in shared pool '{pool.Name}'",
            RoutingSource.SharedPool, assignmentId);
    }

    private List<Candidate> Candidates(Context ctx, IEnumerable<(Guid ServerId, int? WeightOverride, int Priority)> members)
    {
        var list = new List<Candidate>();
        foreach (var (serverId, weightOverride, priority) in members)
        {
            var why = Ineligibility(ctx, serverId, forNewAllocation: true);
            if (why is not null)
            {
                ctx.Log($"  {ServerLabel(ctx.Snapshot, serverId)} skipped: {why}");
                continue;
            }
            var server = ctx.Snapshot.Servers[serverId];
            list.Add(new Candidate(server, ctx.State.Get(serverId), weightOverride ?? server.Weight, priority));
        }
        return list;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Eligibility
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Returns null when eligible, otherwise the reason.</summary>
    private string? Ineligibility(Context ctx, Guid serverId, bool forNewAllocation)
    {
        var snapshot = ctx.Snapshot;
        var request = ctx.Request;
        if (!snapshot.Servers.TryGetValue(serverId, out var server)) return "server not registered";
        if (request.ExcludedServerIds.Contains(serverId)) return "excluded after a failure on this request";
        if (server.Environment != request.Environment) return $"server is in {server.Environment}";
        if (!server.ApplicationIds.Contains(request.ApplicationId)) return "server does not host this application";
        if (ctx.Application?.MinimumServerVersion is { } minimum && !VersionAtLeast(server.ApplicationVersion, minimum))
            return $"application version {server.ApplicationVersion ?? "unknown"} below required {minimum}";

        switch (server.AdminState)
        {
            case ServerAdminState.Retired:
            case ServerAdminState.Disabled:
            case ServerAdminState.Maintenance:
                return $"server is {server.AdminState}";
            case ServerAdminState.Draining when forNewAllocation:
                return "server is draining (no new sessions)";
        }

        if (server.Type == ServerType.Dedicated)
        {
            var owners = snapshot.GetDedicatedOwners(serverId);
            if (owners.Count > 0 && !owners.Contains(request.CompanyId))
                return "dedicated to another tenant (isolation)";
        }

        var runtime = ctx.State.Get(serverId);
        if (runtime.AwaitingManualRestore) return "recovered but awaiting manual restore";
        switch (runtime.Health)
        {
            case HealthState.Healthy:
                break;
            case HealthState.Degraded when ctx.Policy.AllowDegradedServers:
                break;
            case HealthState.Unknown when options.TreatUnknownHealthAsHealthy:
                break;
            default:
                return $"health is {runtime.Health}";
        }

        if (forNewAllocation && server.MaxCapacity > 0)
        {
            var reservedForOthers = 0;
            if (snapshot.CapacityReservations.TryGetValue(serverId, out var reservations))
            {
                foreach (var r in reservations)
                {
                    if (r.CompanyId != request.CompanyId) reservedForOthers += r.ReservedSessions;
                }
            }
            var limit = server.MaxCapacity - reservedForOthers;
            if (runtime.ActiveSessions >= limit)
                return $"at capacity ({runtime.ActiveSessions}/{limit} sessions available to this tenant)";
        }

        return null;
    }

    private static bool VersionAtLeast(string? actual, string minimum) =>
        Version.TryParse(actual, out var a) && Version.TryParse(minimum, out var m) && a >= m;

    private static T? SelectEffective<T>(IReadOnlyList<T> assignments, Context ctx) where T : class, IScopedAssignment =>
        EffectiveAll(assignments, ctx)
            .OrderByDescending(a => a.ApplicationId.HasValue)
            .ThenByDescending(a => a.EffectiveFrom)
            .FirstOrDefault();

    private static IEnumerable<T> EffectiveAll<T>(IReadOnlyList<T> assignments, Context ctx) where T : IScopedAssignment
    {
        foreach (var a in assignments)
        {
            if (!a.IsEnabled || a.Environment != ctx.Request.Environment) continue;
            if (a.ApplicationId is { } appId && appId != ctx.Request.ApplicationId) continue;
            if (a.EffectiveFrom > ctx.Now) continue;
            if (a.EffectiveTo is { } to && to <= ctx.Now) continue;
            yield return a;
        }
    }

    private static string ServerLabel(RoutingSnapshot snapshot, Guid id) =>
        snapshot.Servers.TryGetValue(id, out var s) ? s.Name : id.ToString();

    private sealed class Context(RoutingSnapshot snapshot, IServerStateView state, RoutingRequest request,
        DateTimeOffset now, List<string>? trace)
    {
        public RoutingSnapshot Snapshot { get; } = snapshot;
        public IServerStateView State { get; } = state;
        public RoutingRequest Request { get; } = request;
        public DateTimeOffset Now { get; } = now;
        public CompanyInfo? Company { get; set; }
        public ApplicationInfo? Application { get; set; }
        public RoutingPolicyInfo Policy { get; set; } = snapshot.DefaultRoutingPolicy;
        public bool Tracing => trace is not null;

        public void Log(string message) => trace?.Add(message);

        public RoutingDecision Fail(RoutingOutcome outcome, string reason, RoutingSource source = RoutingSource.None,
            Guid? assignmentId = null)
        {
            Log($"{outcome}: {reason}");
            return new RoutingDecision
            {
                Outcome = outcome,
                ConfigVersion = Snapshot.Version,
                Reason = reason,
                Source = source,
                AssignmentId = assignmentId,
                Trace = (IReadOnlyList<string>?)trace ?? []
            };
        }

        public RoutingDecision Success(Guid serverId, string reason, RoutingSource source, Guid assignmentId,
            Guid? poolId, Guid? failoverPolicyId, bool isFailover, bool isFailback, bool affinityApplied,
            AffinityMode affinity, string? detail, Guid? failoverCandidate)
        {
            var server = Snapshot.Servers[serverId];
            Log($"Routed to {server.Name}: {reason}");
            return new RoutingDecision
            {
                Outcome = RoutingOutcome.Routed,
                ConfigVersion = Snapshot.Version,
                Reason = reason,
                ServerId = serverId,
                ServerName = server.Name,
                Source = source,
                AssignmentId = assignmentId,
                PoolId = poolId,
                FailoverPolicyId = failoverPolicyId,
                IsFailover = isFailover,
                IsFailback = isFailback,
                AffinityApplied = affinityApplied,
                Affinity = affinity,
                SelectionDetail = detail,
                FailoverCandidateId = failoverCandidate,
                Trace = (IReadOnlyList<string>?)trace ?? []
            };
        }
    }
}
