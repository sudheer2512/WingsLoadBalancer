using Wings.Routing.Balancing;
using Wings.Routing.Health;
using Wings.Routing.Model;
using Wings.Routing.Runtime;

namespace Wings.Routing.Core.Tests;

public class BalancingTests
{
    private static Candidate C(string name, int weight = 100, int requests = 0, double? cpu = null) =>
        new(new ServerInfo { Id = Guid.NewGuid(), Name = name, BaseUrl = "https://x", Weight = weight, MaxCapacity = 100 },
            new ServerRuntimeState(Guid.NewGuid(), HealthState.Healthy, null, null,
                cpu is null ? null : new ServerMetricsSample(DateTimeOffset.UtcNow, cpu, cpu, null, null, null, null, 0, 100, null, true, null),
                requests, 0),
            weight, 0);

    private static readonly SelectionContext Ctx = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        new RoutingPolicyInfo { Id = Guid.Empty, Name = "p" });

    [Fact]
    public void Round_robin_cycles()
    {
        var s = new RoundRobinStrategy();
        var list = new[] { C("a"), C("b"), C("c") };
        var picks = Enumerable.Range(0, 6).Select(_ => s.Select(list, Ctx).Server.Name);
        Assert.Equal(new[] { "a", "b", "c", "a", "b", "c" }, picks);
    }

    [Fact]
    public void Weighted_round_robin_respects_weights()
    {
        var s = new WeightedRoundRobinStrategy();
        var list = new[] { C("a", 300), C("b", 100) };
        var picks = Enumerable.Range(0, 400).Select(_ => s.Select(list, Ctx).Server.Name).ToList();
        Assert.Equal(300, picks.Count(p => p == "a"));
    }

    [Fact]
    public void Least_requests_and_weighted_least_requests()
    {
        var list = new[] { C("a", 100, 10), C("b", 100, 3), C("c", 400, 8) };
        Assert.Equal("b", new LeastRequestsStrategy().Select(list, Ctx).Server.Name);
        Assert.Equal("c", new WeightedLeastRequestsStrategy().Select(list, Ctx).Server.Name);
    }

    [Fact]
    public void Lowest_utilisation_uses_load_score()
    {
        var list = new[] { C("busy", cpu: 90), C("idle", cpu: 10) };
        Assert.Equal("idle", new LowestUtilizationStrategy().Select(list, Ctx).Server.Name);
    }

    [Fact]
    public void Rendezvous_hash_moves_only_keys_of_removed_server()
    {
        var servers = Enumerable.Range(0, 5).Select(i => C($"s{i}")).ToList();
        var company = Guid.NewGuid();
        var users = Enumerable.Range(0, 500).Select(_ => Guid.NewGuid()).ToArray();
        var before = users.ToDictionary(u => u, u => RendezvousHash.Pick(servers, company, u).Server.Name);
        var removed = servers[2];
        servers.RemoveAt(2);
        foreach (var u in users)
        {
            var after = RendezvousHash.Pick(servers, company, u).Server.Name;
            if (before[u] != removed.Server.Name) Assert.Equal(before[u], after);
        }
        // Roughly uniform spread with equal weights.
        Assert.All(before.Values.GroupBy(v => v), g => Assert.InRange(g.Count(), 60, 140));
    }

    [Fact]
    public void Load_score_combines_weighted_metrics()
    {
        var server = new ServerInfo { Id = Guid.NewGuid(), Name = "s", BaseUrl = "https://x", MaxCapacity = 100 };
        var state = new ServerRuntimeState(server.Id, HealthState.Healthy, null, null,
            new ServerMetricsSample(DateTimeOffset.UtcNow, 50, 40, null, null, 20, null, 10, 1000, null, true, null), 20, 0);
        var policy = new RoutingPolicyInfo { Id = Guid.Empty, Name = "p", ResponseTimeCeilingMs = 2000 };
        // 0.30*0.5 + 0.20*0.4 + 0.25*0.2 + 0.15*0.5 + 0.10*0.1 = 0.365
        Assert.Equal(0.365, LoadScoreCalculator.Calculate(server, state, policy), 3);
    }
}

public class HealthStateMachineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Full_transition_cycle_with_stabilisation()
    {
        var m = new HealthStateMachine(new HealthThresholds { FailuresToUnhealthy = 3, SuccessesToRecover = 3, StabilizationPeriod = TimeSpan.FromSeconds(20) });
        Assert.Equal(HealthState.Healthy, m.Observe(new(T0, true))!.To);
        Assert.Equal(HealthState.Degraded, m.Observe(new(T0.AddSeconds(5), false, Failure: "timeout"))!.To);
        Assert.Null(m.Observe(new(T0.AddSeconds(10), false)));
        Assert.Equal(HealthState.Unhealthy, m.Observe(new(T0.AddSeconds(15), false))!.To);
        Assert.Equal(HealthState.Recovering, m.Observe(new(T0.AddSeconds(20), true))!.To);
        Assert.Null(m.Observe(new(T0.AddSeconds(25), true)));
        Assert.Null(m.Observe(new(T0.AddSeconds(30), true))); // 3 successes but only 10 s stable
        Assert.Equal(HealthState.Healthy, m.Observe(new(T0.AddSeconds(40), true))!.To);
    }

    [Fact]
    public void Failure_during_recovery_returns_to_unhealthy()
    {
        var m = new HealthStateMachine(new HealthThresholds(), HealthState.Unhealthy);
        m.Observe(new(T0, true));
        Assert.Equal(HealthState.Recovering, m.State);
        Assert.Equal(HealthState.Unhealthy, m.Observe(new(T0.AddSeconds(1), false))!.To);
    }

    [Fact]
    public void Slow_or_overloaded_server_is_degraded()
    {
        var m = new HealthStateMachine(new HealthThresholds { DegradedResponseTimeMs = 500 });
        m.Observe(new(T0, true, 50));
        Assert.Equal(HealthState.Degraded, m.Observe(new(T0.AddSeconds(1), true, 900))!.To);
        Assert.Equal(HealthState.Healthy, m.Observe(new(T0.AddSeconds(2), true, 50))!.To);
    }

    [Fact]
    public void Passive_success_never_promotes_but_passive_failures_count()
    {
        var m = new HealthStateMachine(new HealthThresholds { FailuresToUnhealthy = 2 }, HealthState.Unhealthy);
        Assert.Null(m.Observe(new(T0, true, Passive: true)));
        Assert.Equal(HealthState.Unhealthy, m.State);

        var healthy = new HealthStateMachine(new HealthThresholds { FailuresToUnhealthy = 2 }, HealthState.Healthy);
        healthy.Observe(new(T0, false, Passive: true));
        Assert.Equal(HealthState.Unhealthy, healthy.Observe(new(T0.AddSeconds(1), false, Passive: true))!.To);
    }
}
