using Microsoft.EntityFrameworkCore;
using Wings.LoadBalancer.Data;
using Wings.LoadBalancer.Infrastructure.Monitoring;
using Wings.Routing;
using Wings.Routing.Balancing;
using Wings.Routing.Engine;
using Wings.Routing.Model;
using Wings.Routing.Runtime;

namespace Wings.Admin.Api.Services;

public sealed record SimulationRequest(string Company, string Username, string? Application, DeploymentEnvironment Environment = DeploymentEnvironment.Production);

public sealed record ServerSummary(Guid Id, string Name, string AdminState, string Health, string Type, string Environment);

public sealed record SimulationResult
{
    public required string Outcome { get; init; }
    public required string Reason { get; init; }
    public long ConfigVersion { get; init; }
    public Guid? CompanyId { get; init; }
    public string? CompanyCode { get; init; }
    public string? CompanyName { get; init; }
    public string? CompanyStatus { get; init; }
    public Guid? UserId { get; init; }
    public string? MembershipStatus { get; init; }
    public Guid? ApplicationId { get; init; }
    public string? ApplicationCode { get; init; }
    public object? UserAssignment { get; init; }
    public object? CompanyAssignment { get; init; }
    public IReadOnlyList<object> EligiblePools { get; init; } = [];
    public string? RoutingPolicy { get; init; }
    public string? FailoverPolicy { get; init; }
    public string? Source { get; init; }
    public ServerSummary? ChosenServer { get; init; }
    public ServerSummary? FailoverServer { get; init; }
    public bool IsFailover { get; init; }
    public string? SelectionDetail { get; init; }
    public IReadOnlyList<string> Trace { get; init; } = [];
}

/// <summary>
/// What-if routing over the current database configuration and the monitor's latest health. Never writes sessions,
/// affinity or assignments, and never handles credentials.
/// </summary>
public sealed class SimulationService(WingsDbContext db, IRoutingConfigurationSource source, ILoadBalancingStrategyRegistry strategies,
    TimeProvider time)
{
    public async Task<RoutingSnapshot> LoadSnapshotAsync(CancellationToken ct) => new(await source.LoadAsync(ct), time.GetUtcNow());

    public async Task<IServerStateView> LoadStateAsync(CancellationToken ct)
    {
        var rows = await db.ServerHealthCurrent.AsNoTracking().ToListAsync(ct);
        return new StaticServerStateView(rows.ToDictionary(r => r.ServerId, r => new ServerRuntimeState(r.ServerId, r.State,
            r.LastHeartbeatAt, r.StateChangedAt,
            new ServerMetricsSample(r.LastCheckAt ?? time.GetUtcNow(), r.CpuPercent, r.MemoryPercent, r.DiskPercent, r.ActiveConnections,
                r.ActiveRequests, r.QueueLength, r.ErrorRatePercent, r.ResponseTimeMs, r.ActiveSessions, r.DatabaseHealthy, r.ReportedVersion),
            0, r.ActiveSessions ?? 0, r.AwaitingManualRestore)));
    }

    public async Task<SimulationResult> SimulateAsync(SimulationRequest request, CancellationToken ct, RoutingSnapshot? snapshot = null)
    {
        snapshot ??= await LoadSnapshotAsync(ct);
        var state = await LoadStateAsync(ct);
        if (!snapshot.TryResolveCompany(request.Company, out var company))
            return new SimulationResult { Outcome = nameof(RoutingOutcome.Denied), Reason = "Company not found (code, name or alias)", ConfigVersion = snapshot.Version };

        var normalized = IdentifierNormalizer.Normalize(request.Username);
        var membership = await db.UserCompanyMemberships.AsNoTracking().Include(m => m.User)
            .FirstOrDefaultAsync(m => m.CompanyId == company.Id && m.NormalizedUsername == normalized, ct);
        var app = request.Application is { Length: > 0 } code
            ? snapshot.Applications.Values.FirstOrDefault(a => a.Code.Equals(code, StringComparison.OrdinalIgnoreCase) || a.Id.ToString() == code)
            : snapshot.Applications.Values.OrderBy(a => a.Code).FirstOrDefault();

        var baseResult = new SimulationResult
        {
            Outcome = nameof(RoutingOutcome.Denied), Reason = "", ConfigVersion = snapshot.Version, CompanyId = company.Id,
            CompanyCode = company.Code, CompanyName = company.Name, CompanyStatus = company.Status.ToString(),
            UserId = membership?.UserId, MembershipStatus = membership is null ? "NotFound" : $"{membership.Status}/{membership.User?.Status}",
            ApplicationId = app?.Id, ApplicationCode = app?.Code, RoutingPolicy = snapshot.GetRoutingPolicy(company).Name
        };
        if (membership is null) return baseResult with { Reason = "User is not a member of this company" };
        if (app is null) return baseResult with { Reason = "Application not found" };

        var engine = new RoutingEngine(strategies, time, new RoutingEngineOptions { LastKnownGoodMaxAge = null });
        var decision = engine.Route(snapshot, state, new RoutingRequest
        {
            CompanyId = company.Id, UserId = membership.UserId, ApplicationId = app.Id, Environment = request.Environment, CollectTrace = true
        });

        var now = time.GetUtcNow();
        bool Current(IScopedAssignment a) => a.IsEnabled && a.Environment == request.Environment
            && (a.ApplicationId is null || a.ApplicationId == app.Id) && a.EffectiveFrom <= now && (a.EffectiveTo is null || a.EffectiveTo > now);
        var userAssignment = snapshot.GetUserAssignments(company.Id, membership.UserId).Where(Current).OrderByDescending(a => a.ApplicationId.HasValue).FirstOrDefault();
        var companyAssignment = snapshot.GetCompanyAssignments(company.Id).Where(Current).OrderByDescending(a => a.ApplicationId.HasValue).FirstOrDefault();
        var pools = snapshot.GetSharedPoolAssignments(company.Id).Where(Current)
            .Select(a => snapshot.Pools.TryGetValue(a.PoolId, out var p)
                ? (object)new { p.Id, p.Name, Algorithm = p.Algorithm.ToString(), Affinity = p.Affinity.ToString(), a.Priority, Members = p.Members.Count }
                : new { Id = a.PoolId, Name = "(missing pool)" })
            .ToList();
        var failoverPolicyId = decision.FailoverPolicyId ?? userAssignment?.FailoverPolicyId ?? companyAssignment?.FailoverPolicyId;

        ServerSummary? Summary(Guid? id) => id is { } sid && snapshot.Servers.TryGetValue(sid, out var s)
            ? new ServerSummary(s.Id, s.Name, s.AdminState.ToString(), state.Get(s.Id).Health.ToString(), s.Type.ToString(), s.Environment.ToString())
            : null;

        return baseResult with
        {
            Outcome = decision.Outcome.ToString(),
            Reason = decision.Reason,
            Source = decision.Source.ToString(),
            UserAssignment = userAssignment is null ? null : new
            {
                userAssignment.Id, Primary = Summary(userAssignment.PrimaryServerId), Secondary = Summary(userAssignment.SecondaryServerId),
                userAssignment.EffectiveFrom, userAssignment.EffectiveTo, userAssignment.AllowCompanyRouteFallback
            },
            CompanyAssignment = companyAssignment is null ? null : new
            {
                companyAssignment.Id, Primary = Summary(companyAssignment.PrimaryServerId), Secondary = Summary(companyAssignment.SecondaryServerId),
                companyAssignment.ClusterPoolId, ClusterMode = companyAssignment.ClusterMode.ToString(), companyAssignment.EffectiveFrom, companyAssignment.EffectiveTo
            },
            EligiblePools = pools,
            FailoverPolicy = failoverPolicyId is { } fp && snapshot.FailoverPolicies.TryGetValue(fp, out var policy)
                ? $"{policy.Name} ({(policy.FailoverEnabled ? "enabled" : "disabled")}, failback {policy.Failback})"
                : "None",
            ChosenServer = Summary(decision.ServerId),
            FailoverServer = Summary(decision.FailoverCandidateId),
            IsFailover = decision.IsFailover,
            SelectionDetail = decision.SelectionDetail,
            Trace = decision.Trace
        };
    }

    /// <summary>Before/after comparison for a proposed configuration change (nothing is saved).</summary>
    public async Task<object> PreviewAsync(Func<RoutingConfiguration, RoutingConfiguration> change, IEnumerable<SimulationRequest> requests,
        CancellationToken ct)
    {
        var configuration = await source.LoadAsync(ct);
        var before = new RoutingSnapshot(configuration, time.GetUtcNow());
        var after = new RoutingSnapshot(change(configuration) with { Version = configuration.Version + 1 }, time.GetUtcNow());
        var results = new List<object>();
        foreach (var r in requests)
        {
            var b = await SimulateAsync(r, ct, before);
            var a = await SimulateAsync(r, ct, after);
            results.Add(new
            {
                r.Company, r.Username, r.Application, Environment = r.Environment.ToString(),
                Before = new { b.Outcome, Server = b.ChosenServer?.Name, b.Reason },
                After = new { a.Outcome, Server = a.ChosenServer?.Name, a.Reason },
                Changed = b.ChosenServer?.Id != a.ChosenServer?.Id || b.Outcome != a.Outcome
            });
        }
        var issues = ConfigurationValidator.Validate(after, time.GetUtcNow());
        return new { results, issues = issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message) };
    }
}
