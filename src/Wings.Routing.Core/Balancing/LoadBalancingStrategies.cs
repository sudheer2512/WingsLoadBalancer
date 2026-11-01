using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Wings.Routing.Model;
using Wings.Routing.Runtime;

namespace Wings.Routing.Balancing;

/// <summary>A server that has already passed every eligibility check for the current request.</summary>
public readonly record struct Candidate(ServerInfo Server, ServerRuntimeState State, int Weight, int Priority);

/// <param name="ScopeId">Pool or cluster id; strategies keep per-scope counters.</param>
public readonly record struct SelectionContext(Guid ScopeId, Guid CompanyId, Guid UserId, RoutingPolicyInfo Policy);

public interface ILoadBalancingStrategy
{
    LoadBalancingAlgorithm Algorithm { get; }

    /// <summary>Selects one of the non-empty <paramref name="candidates"/>.</summary>
    Candidate Select(IReadOnlyList<Candidate> candidates, in SelectionContext context);

    string Describe(in Candidate chosen, IReadOnlyList<Candidate> candidates);
}

public interface ILoadBalancingStrategyRegistry
{
    ILoadBalancingStrategy Get(LoadBalancingAlgorithm algorithm);
}

public sealed class LoadBalancingStrategyRegistry : ILoadBalancingStrategyRegistry
{
    private readonly Dictionary<LoadBalancingAlgorithm, ILoadBalancingStrategy> _strategies;

    public LoadBalancingStrategyRegistry() : this(
    [
        new RoundRobinStrategy(), new WeightedRoundRobinStrategy(), new LeastRequestsStrategy(),
        new WeightedLeastRequestsStrategy(), new LowestUtilizationStrategy(),
        new ConsistentHashStrategy(LoadBalancingAlgorithm.ConsistentHashCompany),
        new ConsistentHashStrategy(LoadBalancingAlgorithm.ConsistentHashCompanyUser)
    ])
    {
    }

    public LoadBalancingStrategyRegistry(IEnumerable<ILoadBalancingStrategy> strategies)
    {
        _strategies = strategies.ToDictionary(s => s.Algorithm);
    }

    public ILoadBalancingStrategy Get(LoadBalancingAlgorithm algorithm) =>
        _strategies.TryGetValue(algorithm, out var strategy)
            ? strategy
            : throw new NotSupportedException($"No strategy registered for {algorithm}");
}

public sealed class RoundRobinStrategy : ILoadBalancingStrategy
{
    private readonly ConcurrentDictionary<Guid, StrongBox<long>> _counters = new();
    public LoadBalancingAlgorithm Algorithm => LoadBalancingAlgorithm.RoundRobin;

    public Candidate Select(IReadOnlyList<Candidate> candidates, in SelectionContext context)
    {
        var counter = _counters.GetOrAdd(context.ScopeId, _ => new StrongBox<long>(-1));
        var next = Interlocked.Increment(ref counter.Value);
        return candidates[(int)((ulong)next % (ulong)candidates.Count)];
    }

    public string Describe(in Candidate chosen, IReadOnlyList<Candidate> candidates) =>
        $"Round robin across {candidates.Count} eligible servers";
}

public sealed class WeightedRoundRobinStrategy : ILoadBalancingStrategy
{
    private readonly ConcurrentDictionary<Guid, StrongBox<long>> _counters = new();
    public LoadBalancingAlgorithm Algorithm => LoadBalancingAlgorithm.WeightedRoundRobin;

    public Candidate Select(IReadOnlyList<Candidate> candidates, in SelectionContext context)
    {
        long total = 0;
        foreach (var c in candidates) total += Math.Max(c.Weight, 0);
        if (total == 0) return candidates[0];

        var counter = _counters.GetOrAdd(context.ScopeId, _ => new StrongBox<long>(-1));
        var slot = (long)((ulong)Interlocked.Increment(ref counter.Value) % (ulong)total);
        foreach (var c in candidates)
        {
            slot -= Math.Max(c.Weight, 0);
            if (slot < 0) return c;
        }
        return candidates[^1];
    }

    public string Describe(in Candidate chosen, IReadOnlyList<Candidate> candidates) =>
        $"Weighted round robin (weight {chosen.Weight} of {candidates.Sum(c => c.Weight)})";
}

public sealed class LeastRequestsStrategy : ILoadBalancingStrategy
{
    public LoadBalancingAlgorithm Algorithm => LoadBalancingAlgorithm.LeastRequests;

    public Candidate Select(IReadOnlyList<Candidate> candidates, in SelectionContext context)
    {
        var best = candidates[0];
        for (var i = 1; i < candidates.Count; i++)
        {
            var c = candidates[i];
            if (c.State.EffectiveActiveRequests < best.State.EffectiveActiveRequests) best = c;
        }
        return best;
    }

    public string Describe(in Candidate chosen, IReadOnlyList<Candidate> candidates) =>
        $"Least active requests ({chosen.State.EffectiveActiveRequests})";
}

public sealed class WeightedLeastRequestsStrategy : ILoadBalancingStrategy
{
    public LoadBalancingAlgorithm Algorithm => LoadBalancingAlgorithm.WeightedLeastRequests;

    public Candidate Select(IReadOnlyList<Candidate> candidates, in SelectionContext context)
    {
        var best = candidates[0];
        var bestRatio = Ratio(best);
        for (var i = 1; i < candidates.Count; i++)
        {
            var ratio = Ratio(candidates[i]);
            if (ratio < bestRatio)
            {
                best = candidates[i];
                bestRatio = ratio;
            }
        }
        return best;
    }

    private static double Ratio(in Candidate c) => (c.State.EffectiveActiveRequests + 1) / (double)Math.Max(c.Weight, 1);

    public string Describe(in Candidate chosen, IReadOnlyList<Candidate> candidates) =>
        $"Weighted least requests ({chosen.State.EffectiveActiveRequests} active, weight {chosen.Weight})";
}

public sealed class LowestUtilizationStrategy : ILoadBalancingStrategy
{
    public LoadBalancingAlgorithm Algorithm => LoadBalancingAlgorithm.LowestUtilization;

    public Candidate Select(IReadOnlyList<Candidate> candidates, in SelectionContext context)
    {
        var best = candidates[0];
        var bestScore = Score(best, context.Policy);
        for (var i = 1; i < candidates.Count; i++)
        {
            var score = Score(candidates[i], context.Policy);
            if (score < bestScore)
            {
                best = candidates[i];
                bestScore = score;
            }
        }
        return best;
    }

    // Weight scales the score so a heavier server absorbs proportionally more load.
    private static double Score(in Candidate c, RoutingPolicyInfo policy) =>
        LoadScoreCalculator.Calculate(c.Server, c.State, policy) / Math.Max(c.Weight, 1) * 100;

    public string Describe(in Candidate chosen, IReadOnlyList<Candidate> candidates) =>
        $"Lowest utilisation score among {candidates.Count} servers";
}

/// <summary>Weighted rendezvous (highest random weight) hashing: stable across gateway instances and restarts.</summary>
public sealed class ConsistentHashStrategy(LoadBalancingAlgorithm algorithm) : ILoadBalancingStrategy
{
    public LoadBalancingAlgorithm Algorithm { get; } = algorithm is LoadBalancingAlgorithm.ConsistentHashCompany
        or LoadBalancingAlgorithm.ConsistentHashCompanyUser
        ? algorithm
        : throw new ArgumentOutOfRangeException(nameof(algorithm));

    public Candidate Select(IReadOnlyList<Candidate> candidates, in SelectionContext context)
    {
        var userId = Algorithm == LoadBalancingAlgorithm.ConsistentHashCompanyUser ? context.UserId : Guid.Empty;
        return RendezvousHash.Pick(candidates, context.CompanyId, userId);
    }

    public string Describe(in Candidate chosen, IReadOnlyList<Candidate> candidates) =>
        Algorithm == LoadBalancingAlgorithm.ConsistentHashCompany
            ? "Consistent hash on company"
            : "Consistent hash on company and user";
}

public static class RendezvousHash
{
    public static Candidate Pick(IReadOnlyList<Candidate> candidates, Guid companyId, Guid userId)
    {
        var best = candidates[0];
        var bestScore = double.NegativeInfinity;
        foreach (var c in candidates)
        {
            var score = Score(companyId, userId, c.Server.Id, Math.Max(c.Weight, 1));
            if (score > bestScore || (score == bestScore && c.Server.Id.CompareTo(best.Server.Id) < 0))
            {
                best = c;
                bestScore = score;
            }
        }
        return best;
    }

    internal static double Score(Guid companyId, Guid userId, Guid serverId, int weight)
    {
        var hash = Hash(companyId, userId, serverId);
        // Map to (0, 1); the -w / ln(u) form gives weight-proportional selection.
        var unit = ((hash >> 11) + 0.5) / (1UL << 53);
        return -weight / Math.Log(unit);
    }

    internal static ulong Hash(Guid a, Guid b, Guid c)
    {
        Span<byte> buffer = stackalloc byte[48];
        a.TryWriteBytes(buffer[..16]);
        b.TryWriteBytes(buffer.Slice(16, 16));
        c.TryWriteBytes(buffer.Slice(32, 16));

        // FNV-1a 64 followed by a SplitMix64 finaliser for avalanche.
        var h = 14695981039346656037UL;
        foreach (var x in buffer)
        {
            h ^= x;
            h *= 1099511628211UL;
        }
        h ^= h >> 30;
        h *= 0xbf58476d1ce4e5b9UL;
        h ^= h >> 27;
        h *= 0x94d049bb133111ebUL;
        h ^= h >> 31;
        return h;
    }
}
