using Wings.Routing.Model;

namespace Wings.Routing.Runtime;

/// <summary>Latest metrics reported by a server's health endpoint.</summary>
public sealed record ServerMetricsSample(
    DateTimeOffset CapturedAt,
    double? CpuPercent,
    double? MemoryPercent,
    double? DiskPercent,
    int? ActiveConnections,
    int? ActiveRequests,
    int? QueueLength,
    double? ErrorRatePercent,
    double? ResponseTimeMs,
    int? ActiveSessions,
    bool? DatabaseHealthy,
    string? ApplicationVersion);

/// <summary>Runtime view of one server, combining active checks, passive checks and local request tracking.</summary>
public sealed record ServerRuntimeState(
    Guid ServerId,
    HealthState Health,
    DateTimeOffset? LastHeartbeat,
    DateTimeOffset? HealthChangedAt,
    ServerMetricsSample? Metrics,
    int LocalActiveRequests,
    int ActiveSessions,
    /// <summary>Set when a recovered server must wait for an administrator (manual failback).</summary>
    bool AwaitingManualRestore = false)
{
    public static ServerRuntimeState Unknown(Guid serverId) => new(serverId, HealthState.Unknown, null, null, null, 0, 0);

    public int EffectiveActiveRequests => Math.Max(LocalActiveRequests, Metrics?.ActiveRequests ?? 0);
}

public interface IServerStateView
{
    ServerRuntimeState Get(Guid serverId);
}

/// <summary>Simple dictionary-backed state view, used by the simulator, tests and benchmarks.</summary>
public sealed class StaticServerStateView(IReadOnlyDictionary<Guid, ServerRuntimeState> states) : IServerStateView
{
    public ServerRuntimeState Get(Guid serverId) =>
        states.TryGetValue(serverId, out var state) ? state : ServerRuntimeState.Unknown(serverId);
}
