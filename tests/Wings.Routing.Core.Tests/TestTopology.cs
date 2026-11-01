using Wings.Routing.Engine;
using Wings.Routing.Model;
using Wings.Routing.Runtime;

namespace Wings.Routing.Core.Tests;

/// <summary>
/// ABC Motors: user1 on SERVER-01, company on SERVER-02 (secondary SERVER-06 when failover allowed).
/// XYZ Motors: shared Pool A (SERVER-03/04/05). Both companies have a user called "manager".
/// </summary>
internal sealed class TestTopology
{
    public static readonly Guid Erp = G("a0000000-0000-0000-0000-000000000001");
    public static readonly Guid Hr = G("a0000000-0000-0000-0000-000000000002");

    public static readonly Guid Abc = G("c0000000-0000-0000-0000-000000000001");
    public static readonly Guid Xyz = G("c0000000-0000-0000-0000-000000000002");
    public static readonly Guid Def = G("c0000000-0000-0000-0000-000000000003");

    public static readonly Guid AbcUser1 = G("d0000000-0000-0000-0000-000000000001");
    public static readonly Guid AbcUser2 = G("d0000000-0000-0000-0000-000000000002");
    public static readonly Guid AbcManager = G("d0000000-0000-0000-0000-000000000003");
    public static readonly Guid XyzManager = G("d0000000-0000-0000-0000-000000000004");

    public static readonly Guid S1 = G("e0000000-0000-0000-0000-000000000001");
    public static readonly Guid S2 = G("e0000000-0000-0000-0000-000000000002");
    public static readonly Guid S3 = G("e0000000-0000-0000-0000-000000000003");
    public static readonly Guid S4 = G("e0000000-0000-0000-0000-000000000004");
    public static readonly Guid S5 = G("e0000000-0000-0000-0000-000000000005");
    public static readonly Guid S6 = G("e0000000-0000-0000-0000-000000000006");
    public static readonly Guid S7Def = G("e0000000-0000-0000-0000-000000000007");
    public static readonly Guid SStaging = G("e0000000-0000-0000-0000-000000000008");

    public static readonly Guid PoolA = G("f0000000-0000-0000-0000-000000000001");
    public static readonly Guid FailoverPool = G("f0000000-0000-0000-0000-000000000002");

    public static readonly Guid FailoverOn = G("b0000000-0000-0000-0000-000000000001");
    public static readonly Guid FailoverOff = G("b0000000-0000-0000-0000-000000000002");
    public static readonly Guid FailoverOnEndOfSession = G("b0000000-0000-0000-0000-000000000003");

    public static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    public List<CompanyInfo> Companies { get; } =
    [
        new(Abc, "ABC", "ABC Motors", CompanyStatus.Active, CompanyRoutingMode.Dedicated, "Enterprise", null, null),
        new(Xyz, "XYZ", "XYZ Motors", CompanyStatus.Active, CompanyRoutingMode.SharedPool, "Standard", null, null),
        new(Def, "DEF", "DEF Trucks", CompanyStatus.Active, CompanyRoutingMode.Dedicated, "Enterprise", null, null),
    ];

    public List<ServerInfo> Servers { get; } =
    [
        Server(S1, "SERVER-01", ServerType.Dedicated),
        Server(S2, "SERVER-02", ServerType.Dedicated),
        Server(S3, "SERVER-03", ServerType.Shared),
        Server(S4, "SERVER-04", ServerType.Shared),
        Server(S5, "SERVER-05", ServerType.Shared),
        Server(S6, "SERVER-06", ServerType.Hybrid),
        Server(S7Def, "SERVER-07", ServerType.Dedicated),
        Server(SStaging, "STAGING-01", ServerType.Shared) with { Environment = DeploymentEnvironment.Staging },
    ];

    public List<PoolInfo> Pools { get; } =
    [
        new()
        {
            Id = PoolA, Name = "Shared Pool A", Algorithm = LoadBalancingAlgorithm.RoundRobin, Affinity = AffinityMode.PerSession,
            Members = [new(S3, null, 1, true), new(S4, null, 2, true), new(S5, null, 3, true)]
        },
        new()
        {
            Id = FailoverPool, Name = "Failover Pool", Algorithm = LoadBalancingAlgorithm.RoundRobin,
            // SERVER-07 is dedicated to DEF Trucks, so it must never be used for ABC even though it is listed here.
            Members = [new(S7Def, null, 0, true), new(S6, null, 1, true)]
        }
    ];

    public List<UserAssignmentInfo> UserAssignments { get; } =
    [
        new(G("10000000-0000-0000-0000-000000000001"), Abc, AbcUser1, null, DeploymentEnvironment.Production, S1, null,
            null, false, true, Now.AddDays(-30), null),
    ];

    public List<CompanyAssignmentInfo> CompanyAssignments { get; } =
    [
        new(G("20000000-0000-0000-0000-000000000001"), Abc, null, DeploymentEnvironment.Production, S2, S6, null,
            ClusterMode.PrimaryPreferred, FailoverOn, true, Now.AddDays(-30), null),
        new(G("20000000-0000-0000-0000-000000000002"), Def, null, DeploymentEnvironment.Production, S7Def, null, null,
            ClusterMode.PrimaryPreferred, null, true, Now.AddDays(-30), null),
    ];

    public List<SharedPoolAssignmentInfo> PoolAssignments { get; } =
    [
        new(G("30000000-0000-0000-0000-000000000001"), Xyz, PoolA, null, DeploymentEnvironment.Production, 0, true,
            Now.AddDays(-30), null),
    ];

    public List<FailoverPolicyInfo> FailoverPolicies { get; } =
    [
        new(FailoverOn, "Failover to secondary then pool", true, FailoverPool, FailbackPolicy.Automatic, 3, 0),
        new(FailoverOff, "No failover", false, null, FailbackPolicy.Manual, 0, 0),
        new(FailoverOnEndOfSession, "Failover, stay until session ends", true, FailoverPool, FailbackPolicy.EndOfSession, 3, 0),
    ];

    public List<UserKey> Blocked { get; } = [];
    public List<CompanyServerAuthorizationInfo> Authorizations { get; } = [new(Abc, S1)];
    public List<CapacityReservationInfo> Reservations { get; } = [];
    public Dictionary<Guid, ServerRuntimeState> States { get; } = new();
    public long Version { get; set; } = 1;

    public TestTopology()
    {
        foreach (var s in Servers) SetHealth(s.Id, HealthState.Healthy);
    }

    public TestTopology SetHealth(Guid id, HealthState health, int sessions = 0, int requests = 0)
    {
        States[id] = new ServerRuntimeState(id, health, Now, Now, null, requests, sessions);
        return this;
    }

    public TestTopology UpdateServer(Guid id, Func<ServerInfo, ServerInfo> update)
    {
        var index = Servers.FindIndex(s => s.Id == id);
        Servers[index] = update(Servers[index]);
        return this;
    }

    public RoutingSnapshot Snapshot(DateTimeOffset? loadedAt = null) => new(new RoutingConfiguration
    {
        Version = Version,
        GeneratedAt = Now,
        Companies = Companies,
        CompanyAliases = [new(Abc, "ABC Motors"), new(Abc, "abc-motors"), new(Xyz, "XYZ Motors")],
        BlockedUsers = Blocked,
        Applications = [new(Erp, "ERP", "Wings ERP", true, null), new(Hr, "HR", "Wings HR", true, "2.0")],
        ApplicationBindings =
        [
            new(Guid.NewGuid(), Erp, DeploymentEnvironment.Production, "erp.example.com", "/", ApplicationRoutingMode.Proxy, true, 100, null),
            new(Guid.NewGuid(), Hr, DeploymentEnvironment.Production, "erp.example.com", "/hr", ApplicationRoutingMode.Proxy, true, 100, null),
            new(Guid.NewGuid(), Erp, DeploymentEnvironment.Staging, "staging-erp.example.com", "/", ApplicationRoutingMode.Proxy, true, 100, null),
        ],
        Servers = Servers,
        Pools = Pools,
        UserAssignments = UserAssignments,
        CompanyAssignments = CompanyAssignments,
        SharedPoolAssignments = PoolAssignments,
        FailoverPolicies = FailoverPolicies,
        RoutingPolicies = [new RoutingPolicyInfo { Id = Guid.NewGuid(), Name = "Default", IsDefault = true, AllowDegradedServers = true }],
        CapacityReservations = Reservations,
        ServerAuthorizations = Authorizations,
    }, loadedAt ?? Now);

    public IServerStateView StateView => new StaticServerStateView(States);

    public static RoutingEngine CreateEngine(DateTimeOffset? now = null, RoutingEngineOptions? options = null) =>
        new(new Balancing.LoadBalancingStrategyRegistry(), new FixedTime(now ?? Now), options ?? new RoutingEngineOptions());

    public RoutingDecision Route(Guid company, Guid user, Guid? affinity = null, Guid? app = null,
        DeploymentEnvironment env = DeploymentEnvironment.Production, RoutingEngine? engine = null,
        IReadOnlyCollection<Guid>? excluded = null) =>
        (engine ?? CreateEngine()).Route(Snapshot(), StateView, new RoutingRequest
        {
            CompanyId = company,
            UserId = user,
            ApplicationId = app ?? Erp,
            Environment = env,
            AffinityServerId = affinity,
            ExcludedServerIds = excluded ?? [],
            CollectTrace = true
        });

    private static ServerInfo Server(Guid id, string name, ServerType type) => new()
    {
        Id = id, Name = name, BaseUrl = $"https://{name.ToLowerInvariant()}.internal", Type = type,
        Environment = DeploymentEnvironment.Production, AdminState = ServerAdminState.Active, Weight = 100,
        MaxCapacity = 100, ApplicationVersion = "2.1.0", ApplicationIds = [Erp, Hr]
    };

    private static Guid G(string s) => Guid.Parse(s);
}

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Value { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Value;
}
