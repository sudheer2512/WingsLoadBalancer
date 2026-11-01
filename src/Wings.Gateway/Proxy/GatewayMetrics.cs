using System.Diagnostics.Metrics;
using Wings.Routing.Engine;

namespace Wings.Gateway.Proxy;

/// <summary>
/// OpenTelemetry-compatible instruments (meter <c>Wings.Gateway</c>) plus rolling totals for the heartbeat that feeds
/// the admin dashboard.
/// </summary>
public sealed class GatewayMetrics : IDisposable
{
    public const string MeterName = "Wings.Gateway";
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _decisions;
    private readonly Histogram<double> _decisionMicros;
    private readonly Histogram<double> _proxyMillis;
    private readonly Counter<long> _proxyErrors;
    private readonly UpDownCounter<long> _activeRequests;

    private long _requests;
    private long _failedRouting;
    private long _failovers;
    private double _latencyTotalMs;
    private double _decisionTotalMicros;
    private long _decisionCount;

    public GatewayMetrics()
    {
        _decisions = _meter.CreateCounter<long>("wings.routing.decisions", description: "Routing decisions by outcome");
        _decisionMicros = _meter.CreateHistogram<double>("wings.routing.decision.duration", "us", "Routing engine decision time");
        _proxyMillis = _meter.CreateHistogram<double>("wings.proxy.duration", "ms", "End-to-end proxied request time");
        _proxyErrors = _meter.CreateCounter<long>("wings.proxy.errors", description: "Forwarding errors by type");
        _activeRequests = _meter.CreateUpDownCounter<long>("wings.proxy.active_requests", description: "In-flight proxied requests");
    }

    public void RecordDecision(RoutingDecision decision, double micros)
    {
        _decisions.Add(1, new KeyValuePair<string, object?>("outcome", decision.Outcome.ToString()),
            new KeyValuePair<string, object?>("source", decision.Source.ToString()),
            new KeyValuePair<string, object?>("failover", decision.IsFailover));
        _decisionMicros.Record(micros);
        Interlocked.Increment(ref _decisionCount);
        AddDouble(ref _decisionTotalMicros, micros);
        if (!decision.IsRouted) Interlocked.Increment(ref _failedRouting);
        if (decision.IsFailover) Interlocked.Increment(ref _failovers);
    }

    public void RecordProxy(double millis, string? error)
    {
        Interlocked.Increment(ref _requests);
        AddDouble(ref _latencyTotalMs, millis);
        _proxyMillis.Record(millis);
        if (error is not null) _proxyErrors.Add(1, new KeyValuePair<string, object?>("error", error));
    }

    public void RequestStarted() => _activeRequests.Add(1);
    public void RequestEnded() => _activeRequests.Add(-1);

    /// <summary>Returns totals since the previous call and resets them.</summary>
    public (long Requests, double AvgLatencyMs, double AvgDecisionMicros, long FailedRouting, long Failovers) Drain()
    {
        var requests = Interlocked.Exchange(ref _requests, 0);
        var latency = Interlocked.Exchange(ref _latencyTotalMs, 0);
        var decisions = Interlocked.Exchange(ref _decisionCount, 0);
        var decisionMicros = Interlocked.Exchange(ref _decisionTotalMicros, 0);
        return (requests, requests == 0 ? 0 : latency / requests, decisions == 0 ? 0 : decisionMicros / decisions,
            Interlocked.Exchange(ref _failedRouting, 0), Interlocked.Exchange(ref _failovers, 0));
    }

    private static void AddDouble(ref double target, double value)
    {
        double initial, computed;
        do
        {
            initial = Volatile.Read(ref target);
            computed = initial + value;
        } while (Interlocked.CompareExchange(ref target, computed, initial) != initial);
    }

    public void Dispose() => _meter.Dispose();
}
