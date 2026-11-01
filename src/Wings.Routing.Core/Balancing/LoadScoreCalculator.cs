using Wings.Routing.Model;
using Wings.Routing.Runtime;

namespace Wings.Routing.Balancing;

/// <summary>
/// Load score in [0, 1] (lower is less loaded). Missing metrics count as 0.5 so a server that reports nothing is
/// neither preferred nor avoided.
/// </summary>
public static class LoadScoreCalculator
{
    public static double Calculate(ServerInfo server, ServerRuntimeState state, RoutingPolicyInfo policy)
    {
        var w = policy.Weights;
        var total = w.Cpu + w.Memory + w.ActiveRequests + w.ResponseTime + w.ErrorRate;
        if (total <= 0) return 0;

        var m = state.Metrics;
        var cpu = Fraction(m?.CpuPercent, 100);
        var memory = Fraction(m?.MemoryPercent, 100);
        var requests = server.MaxCapacity > 0
            ? Math.Clamp(state.EffectiveActiveRequests / (double)server.MaxCapacity, 0, 1)
            : Fraction(state.EffectiveActiveRequests, 1000);
        var responseTime = Fraction(m?.ResponseTimeMs, policy.ResponseTimeCeilingMs);
        var errors = Fraction(m?.ErrorRatePercent, 100);

        return (w.Cpu * cpu + w.Memory * memory + w.ActiveRequests * requests + w.ResponseTime * responseTime
                + w.ErrorRate * errors) / total;
    }

    private static double Fraction(double? value, double ceiling) =>
        value is null ? 0.5 : Math.Clamp(value.Value / ceiling, 0, 1);
}
