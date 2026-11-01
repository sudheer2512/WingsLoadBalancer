using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Wings.LoadBalancer.Data.Entities;
using Wings.Routing.Health;
using Wings.Routing.Model;
using Wings.Routing.Runtime;

namespace Wings.LoadBalancer.Infrastructure.Health;

public enum GatewayHealthMode
{
    /// <summary>Use health published by the central monitor; passive failures quarantine locally.</summary>
    Shared = 0,
    /// <summary>This process runs its own active checks (single node, development, monitor outage).</summary>
    Local = 1
}

public sealed class HealthOptions
{
    public const string Section = "Health";

    public GatewayHealthMode GatewayMode { get; set; } = GatewayHealthMode.Shared;
    public int IntervalSeconds { get; set; } = 10;
    public int TimeoutSeconds { get; set; } = 5;
    public int FeedPollSeconds { get; set; } = 3;
    public int MetricsIntervalSeconds { get; set; } = 60;
    public int MaxParallelChecks { get; set; } = 16;
    public int FailuresToUnhealthy { get; set; } = 3;
    public int SuccessesToRecover { get; set; } = 3;
    public int StabilizationSeconds { get; set; } = 30;
    public double DegradedResponseTimeMs { get; set; } = 1500;
    public double DegradedCpuPercent { get; set; } = 90;
    public double DegradedMemoryPercent { get; set; } = 92;
    public double DegradedErrorRatePercent { get; set; } = 5;
    /// <summary>Consecutive transport failures that quarantine a server on a gateway (passive check).</summary>
    public int PassiveFailuresToQuarantine { get; set; } = 3;
    public int PassiveQuarantineSeconds { get; set; } = 30;
    /// <summary>Shared health older than this is treated as Unknown (monitor outage).</summary>
    public int SharedStateMaxAgeSeconds { get; set; } = 120;

    public HealthThresholds ToThresholds() => new()
    {
        FailuresToUnhealthy = FailuresToUnhealthy,
        SuccessesToRecover = SuccessesToRecover,
        StabilizationPeriod = TimeSpan.FromSeconds(StabilizationSeconds),
        DegradedResponseTimeMs = DegradedResponseTimeMs,
        DegradedCpuPercent = DegradedCpuPercent,
        DegradedMemoryPercent = DegradedMemoryPercent,
        DegradedErrorRatePercent = DegradedErrorRatePercent
    };
}

/// <summary>
/// Thread-safe runtime state of every server in this process: shared (monitor) health, local active checks, passive
/// failure quarantine, in-flight request counts and reported metrics. Implements the engine's <see cref="IServerStateView"/>.
/// </summary>
public sealed class ServerStateStore(IOptionsMonitor<HealthOptions> options, TimeProvider time) : IServerStateView
{
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public ServerRuntimeState Get(Guid serverId)
    {
        if (!_entries.TryGetValue(serverId, out var e)) return ServerRuntimeState.Unknown(serverId);
        var now = time.GetUtcNow();
        var o = options.CurrentValue;
        HealthState health;
        lock (e)
        {
            if (o.GatewayMode == GatewayHealthMode.Local || e.SharedAt is null)
                health = e.Machine?.State ?? HealthState.Unknown;
            else
                health = now - e.SharedAt.Value > TimeSpan.FromSeconds(o.SharedStateMaxAgeSeconds)
                    ? HealthState.Unknown
                    : e.SharedState;
            if (e.QuarantinedUntil is { } until && until > now) health = HealthState.Unhealthy;
        }
        return new ServerRuntimeState(serverId, health, e.LastHeartbeat, e.ChangedAt, e.Metrics,
            Volatile.Read(ref e.ActiveRequests), e.ActiveSessions, e.AwaitingManualRestore);
    }

    public IReadOnlyCollection<Guid> KnownServers => _entries.Keys.ToArray();

    /// <summary>Applies health published by the monitor leader.</summary>
    public void ApplyShared(ServerHealthCurrent row)
    {
        var e = _entries.GetOrAdd(row.ServerId, _ => new Entry());
        lock (e)
        {
            e.SharedState = row.State;
            e.SharedAt = row.LastCheckAt ?? time.GetUtcNow();
            e.ChangedAt = row.StateChangedAt;
            e.LastHeartbeat = row.LastHeartbeatAt;
            e.ActiveSessions = row.ActiveSessions ?? 0;
            e.AwaitingManualRestore = row.AwaitingManualRestore;
            e.Metrics = new ServerMetricsSample(row.LastCheckAt ?? time.GetUtcNow(), row.CpuPercent, row.MemoryPercent,
                row.DiskPercent, row.ActiveConnections, row.ActiveRequests, row.QueueLength, row.ErrorRatePercent,
                row.ResponseTimeMs, row.ActiveSessions, row.DatabaseHealthy, row.ReportedVersion);
        }
    }

    /// <summary>Records an active-check result in local mode. Returns the transition, if any.</summary>
    public HealthTransition? ObserveActive(Guid serverId, HealthObservation observation)
    {
        var e = _entries.GetOrAdd(serverId, _ => new Entry());
        lock (e)
        {
            e.Machine ??= new HealthStateMachine(options.CurrentValue.ToThresholds());
            if (observation.Success) e.LastHeartbeat = observation.At;
            if (observation.Metrics is not null) e.Metrics = observation.Metrics;
            if (observation.Metrics?.ActiveSessions is { } sessions) e.ActiveSessions = Math.Max(e.ActiveSessions, sessions);
            var transition = e.Machine.Observe(observation);
            if (transition is not null) e.ChangedAt = transition.At;
            return transition;
        }
    }

    /// <summary>Records a proxied request outcome. Returns true when the server has just been quarantined.</summary>
    public bool ObservePassive(Guid serverId, bool success, string? failure = null)
    {
        var e = _entries.GetOrAdd(serverId, _ => new Entry());
        var o = options.CurrentValue;
        lock (e)
        {
            if (success)
            {
                e.PassiveFailures = 0;
                return false;
            }
            e.PassiveFailures++;
            e.LastPassiveFailure = failure;
            if (o.GatewayMode == GatewayHealthMode.Local)
            {
                e.Machine ??= new HealthStateMachine(o.ToThresholds());
                e.Machine.Observe(new HealthObservation(time.GetUtcNow(), false, Failure: failure, Passive: true));
            }
            if (e.PassiveFailures >= o.PassiveFailuresToQuarantine && (e.QuarantinedUntil is null || e.QuarantinedUntil <= time.GetUtcNow()))
            {
                e.QuarantinedUntil = time.GetUtcNow().AddSeconds(o.PassiveQuarantineSeconds);
                e.PassiveFailures = 0;
                return true;
            }
            return false;
        }
    }

    /// <summary>Quarantine announced by another gateway instance.</summary>
    public void Quarantine(Guid serverId, DateTimeOffset until)
    {
        var e = _entries.GetOrAdd(serverId, _ => new Entry());
        lock (e)
        {
            if (e.QuarantinedUntil is null || e.QuarantinedUntil < until) e.QuarantinedUntil = until;
        }
    }

    public DateTimeOffset? QuarantinedUntil(Guid serverId) =>
        _entries.TryGetValue(serverId, out var e) ? e.QuarantinedUntil : null;

    /// <summary>Administrator restore: clears quarantine, manual-restore flags and local failure history.</summary>
    public void Reset(Guid serverId, HealthState state)
    {
        var e = _entries.GetOrAdd(serverId, _ => new Entry());
        lock (e)
        {
            e.QuarantinedUntil = null;
            e.PassiveFailures = 0;
            e.AwaitingManualRestore = false;
            e.Machine?.Reset(state, time.GetUtcNow());
        }
    }

    public void BeginRequest(Guid serverId) =>
        Interlocked.Increment(ref _entries.GetOrAdd(serverId, _ => new Entry()).ActiveRequests);

    public void EndRequest(Guid serverId)
    {
        if (_entries.TryGetValue(serverId, out var e)) Interlocked.Decrement(ref e.ActiveRequests);
    }

    public int TotalActiveRequests => _entries.Values.Sum(e => Volatile.Read(ref e.ActiveRequests));

    private sealed class Entry
    {
        public HealthStateMachine? Machine;
        public HealthState SharedState;
        public DateTimeOffset? SharedAt;
        public DateTimeOffset? ChangedAt;
        public DateTimeOffset? LastHeartbeat;
        public ServerMetricsSample? Metrics;
        public int ActiveRequests;
        public int ActiveSessions;
        public bool AwaitingManualRestore;
        public int PassiveFailures;
        public string? LastPassiveFailure;
        public DateTimeOffset? QuarantinedUntil;
    }
}
