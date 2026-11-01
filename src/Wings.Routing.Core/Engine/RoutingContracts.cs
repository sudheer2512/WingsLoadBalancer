using Wings.Routing.Model;
using Wings.Routing.Runtime;

namespace Wings.Routing.Engine;

/// <summary>
/// Input to the engine. Every field must come from trusted server-side context (authenticated session, host binding,
/// session store) — never from client-supplied headers, query strings or unprotected cookies.
/// </summary>
public sealed record RoutingRequest
{
    public required Guid CompanyId { get; init; }
    public required Guid UserId { get; init; }
    public required Guid ApplicationId { get; init; }
    public required DeploymentEnvironment Environment { get; init; }

    /// <summary>Server this session is currently pinned to for the application, if any.</summary>
    public Guid? AffinityServerId { get; init; }

    /// <summary>Servers to skip, e.g. after a transport failure on this request.</summary>
    public IReadOnlyCollection<Guid> ExcludedServerIds { get; init; } = [];

    /// <summary>Collect a human-readable trace (simulator, diagnostics). Off on the hot path.</summary>
    public bool CollectTrace { get; init; }
}

public sealed record RoutingDecision
{
    public required RoutingOutcome Outcome { get; init; }
    public required long ConfigVersion { get; init; }
    public required string Reason { get; init; }
    public Guid? ServerId { get; init; }
    public string? ServerName { get; init; }
    public RoutingSource Source { get; init; }
    public Guid? AssignmentId { get; init; }
    public Guid? PoolId { get; init; }
    public Guid? FailoverPolicyId { get; init; }
    public bool IsFailover { get; init; }
    public bool IsFailback { get; init; }
    public bool AffinityApplied { get; init; }
    public AffinityMode Affinity { get; init; }
    public string? SelectionDetail { get; init; }

    /// <summary>Next permitted alternative after the chosen server (only computed when tracing).</summary>
    public Guid? FailoverCandidateId { get; init; }
    public IReadOnlyList<string> Trace { get; init; } = [];

    public bool IsRouted => Outcome == RoutingOutcome.Routed && ServerId.HasValue;
}

public interface IRoutingEngine
{
    RoutingDecision Route(RoutingSnapshot snapshot, IServerStateView state, RoutingRequest request);

    /// <summary>Explains why a single server is or is not eligible for the request (simulator, validation).</summary>
    EligibilityResult CheckEligibility(RoutingSnapshot snapshot, IServerStateView state, RoutingRequest request,
        Guid serverId, bool forNewAllocation);
}

public readonly record struct EligibilityResult(bool Eligible, string Reason);

public sealed record RoutingEngineOptions
{
    /// <summary>Snapshots not confirmed by the authoritative store for longer than this fail closed. Null disables.</summary>
    public TimeSpan? LastKnownGoodMaxAge { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Allow servers with no health data yet. Keep false in production.</summary>
    public bool TreatUnknownHealthAsHealthy { get; init; }
}
