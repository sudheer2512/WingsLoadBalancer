using Wings.Routing.Model;
using Wings.Routing.Runtime;

namespace Wings.Routing.Health;

public sealed record HealthThresholds
{
    public int FailuresToUnhealthy { get; init; } = 3;
    public int SuccessesToRecover { get; init; } = 3;
    public TimeSpan StabilizationPeriod { get; init; } = TimeSpan.FromSeconds(30);
    public double DegradedResponseTimeMs { get; init; } = 1500;
    public double DegradedCpuPercent { get; init; } = 90;
    public double DegradedMemoryPercent { get; init; } = 92;
    public double DegradedErrorRatePercent { get; init; } = 5;
}

/// <summary>One probe result (active check) or request outcome (passive check).</summary>
public sealed record HealthObservation(
    DateTimeOffset At,
    bool Success,
    double? ResponseTimeMs = null,
    ServerMetricsSample? Metrics = null,
    string? Failure = null,
    bool Passive = false);

public sealed record HealthTransition(HealthState From, HealthState To, DateTimeOffset At, string Reason);

/// <summary>
/// Healthy → Degraded → Unhealthy → Recovering → Healthy. A server only becomes healthy again after
/// <see cref="HealthThresholds.SuccessesToRecover"/> consecutive successes spread over the stabilisation period.
/// Not thread-safe; callers serialise access per server.
/// </summary>
public sealed class HealthStateMachine(HealthThresholds thresholds, HealthState initial = HealthState.Unknown)
{
    private int _consecutiveFailures;
    private int _consecutiveSuccesses;
    private DateTimeOffset? _recoveringSince;

    public HealthState State { get; private set; } = initial;
    public DateTimeOffset? ChangedAt { get; private set; }
    public string? LastReason { get; private set; }
    public int ConsecutiveFailures => _consecutiveFailures;

    public HealthTransition? Observe(HealthObservation observation)
    {
        // Passive (request-level) successes only clear the failure streak; promotion needs active checks.
        if (observation is { Passive: true, Success: true })
        {
            _consecutiveFailures = 0;
            return null;
        }

        var previous = State;
        string reason;

        if (!observation.Success)
        {
            _consecutiveSuccesses = 0;
            _consecutiveFailures++;
            _recoveringSince = null;
            reason = observation.Failure ?? "Health check failed";

            if (_consecutiveFailures >= thresholds.FailuresToUnhealthy)
            {
                State = HealthState.Unhealthy;
                reason = $"{_consecutiveFailures} consecutive failures: {reason}";
            }
            else if (State is HealthState.Healthy)
            {
                State = HealthState.Degraded;
            }
            else if (State is HealthState.Recovering or HealthState.Unknown)
            {
                State = HealthState.Unhealthy;
            }
        }
        else
        {
            _consecutiveFailures = 0;
            _consecutiveSuccesses++;
            var degradedReason = DegradationReason(observation);
            reason = degradedReason ?? "Health check passed";

            switch (State)
            {
                case HealthState.Unknown:
                    State = degradedReason is null ? HealthState.Healthy : HealthState.Degraded;
                    break;
                case HealthState.Unhealthy:
                    State = HealthState.Recovering;
                    _recoveringSince = observation.At;
                    _consecutiveSuccesses = 1;
                    reason = "First successful check after failure";
                    break;
                case HealthState.Recovering:
                    _recoveringSince ??= observation.At;
                    if (_consecutiveSuccesses >= thresholds.SuccessesToRecover
                        && observation.At - _recoveringSince.Value >= thresholds.StabilizationPeriod)
                    {
                        State = degradedReason is null ? HealthState.Healthy : HealthState.Degraded;
                        reason = $"Stable for {thresholds.StabilizationPeriod.TotalSeconds:0}s";
                        _recoveringSince = null;
                    }
                    break;
                case HealthState.Healthy when degradedReason is not null:
                    State = HealthState.Degraded;
                    break;
                case HealthState.Degraded when degradedReason is null:
                    State = HealthState.Healthy;
                    break;
            }
        }

        LastReason = reason;
        if (previous == State) return null;
        ChangedAt = observation.At;
        return new HealthTransition(previous, State, observation.At, reason);
    }

    /// <summary>Forces a state (administrator restore, or seeding from shared state).</summary>
    public void Reset(HealthState state, DateTimeOffset at)
    {
        State = state;
        ChangedAt = at;
        _consecutiveFailures = 0;
        _consecutiveSuccesses = 0;
        _recoveringSince = state == HealthState.Recovering ? at : null;
    }

    private string? DegradationReason(HealthObservation o)
    {
        var m = o.Metrics;
        var rt = o.ResponseTimeMs ?? m?.ResponseTimeMs;
        if (rt > thresholds.DegradedResponseTimeMs) return $"Response time {rt:0} ms above {thresholds.DegradedResponseTimeMs:0} ms";
        if (m?.CpuPercent > thresholds.DegradedCpuPercent) return $"CPU {m.CpuPercent:0}% above {thresholds.DegradedCpuPercent:0}%";
        if (m?.MemoryPercent > thresholds.DegradedMemoryPercent) return $"Memory {m.MemoryPercent:0}% above {thresholds.DegradedMemoryPercent:0}%";
        if (m?.ErrorRatePercent > thresholds.DegradedErrorRatePercent) return $"Error rate {m.ErrorRatePercent:0.0}% above {thresholds.DegradedErrorRatePercent:0.0}%";
        if (m?.DatabaseHealthy == false) return "Database connectivity degraded";
        return null;
    }
}
