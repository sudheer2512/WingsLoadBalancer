using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Wings.Routing.Balancing;
using Wings.Routing.Engine;
using Wings.Routing.Model;
using Wings.Routing.Runtime;

// Wings routing benchmarks.
//   dotnet run -c Release --project tests/Wings.Benchmarks                     → routing engine micro-benchmark
//   dotnet run -c Release --project tests/Wings.Benchmarks -- load --gateway http://localhost:5280 \
//        --company ABC --username user1 --password ... --concurrency 64 --seconds 30 [--path /api/whoami]
//                                                                             → end-to-end load test through a gateway
if (args.Length > 0 && args[0] == "load") return await LoadTest.RunAsync(args[1..]);
EngineBenchmark.Run(args);
return 0;

static class EngineBenchmark
{
    public static void Run(string[] args)
    {
        var companies = Arg(args, "--companies", 5_000);
        var usersPerCompany = Arg(args, "--users", 40);
        var servers = Arg(args, "--servers", 200);
        var iterations = Arg(args, "--iterations", 2_000_000);
        Console.WriteLine($"Building configuration: {companies:N0} companies, {companies * usersPerCompany:N0} users, {servers} servers…");
        var (snapshot, state, requests) = Build(companies, usersPerCompany, servers);
        var engine = new RoutingEngine(new LoadBalancingStrategyRegistry(), TimeProvider.System, new RoutingEngineOptions { LastKnownGoodMaxAge = null });

        // Warm-up (JIT, tiered compilation).
        for (var i = 0; i < 200_000; i++) engine.Route(snapshot, state, requests[i % requests.Length]);

        var samples = new double[iterations];
        var routed = 0;
        var total = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            var t = Stopwatch.GetTimestamp();
            var d = engine.Route(snapshot, state, requests[i % requests.Length]);
            samples[i] = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (d.IsRouted) routed++;
        }
        total.Stop();
        Array.Sort(samples);
        double P(double p) => samples[Math.Min(samples.Length - 1, (int)(samples.Length * p))];
        Console.WriteLine($"Single thread: {iterations:N0} decisions in {total.Elapsed.TotalSeconds:0.00}s → {iterations / total.Elapsed.TotalSeconds:N0}/s, routed {routed * 100.0 / iterations:0.0}%");
        Console.WriteLine($"Decision latency µs: p50 {P(0.50):0.00}  p95 {P(0.95):0.00}  p99 {P(0.99):0.00}  p99.9 {P(0.999):0.00}  max {samples[^1]:0.0}");
        var target = 5000.0;
        Console.WriteLine($"Target: p99 < {target / 1000:0} ms (cached decision) → {(P(0.99) < target ? "PASS" : "FAIL")}");

        var threads = Environment.ProcessorCount;
        var perThread = iterations / threads;
        total.Restart();
        Parallel.For(0, threads, t =>
        {
            for (var i = 0; i < perThread; i++) engine.Route(snapshot, state, requests[(i * 7 + t) % requests.Length]);
        });
        total.Stop();
        Console.WriteLine($"{threads} threads: {perThread * threads / total.Elapsed.TotalSeconds:N0} decisions/s");
    }

    private static (RoutingSnapshot, IServerStateView, RoutingRequest[]) Build(int companyCount, int usersPerCompany, int serverCount)
    {
        var app = Guid.NewGuid();
        var serverList = Enumerable.Range(0, serverCount).Select(i => new ServerInfo
        {
            Id = Guid.NewGuid(), Name = $"APP-{i:000}", BaseUrl = $"https://app-{i:000}.internal", Type = i < serverCount / 2 ? ServerType.Dedicated : ServerType.Shared,
            AdminState = ServerAdminState.Active, Weight = 100, MaxCapacity = 5000, ApplicationIds = [app], Environment = DeploymentEnvironment.Production
        }).ToList();
        var shared = serverList.Where(s => s.Type == ServerType.Shared).ToList();
        var dedicated = serverList.Where(s => s.Type == ServerType.Dedicated).ToList();
        var pools = Enumerable.Range(0, 10).Select(p => new PoolInfo
        {
            Id = Guid.NewGuid(), Name = $"Pool-{p}", Algorithm = (LoadBalancingAlgorithm)(p % 7), Affinity = p % 2 == 0 ? AffinityMode.PerSession : AffinityMode.PerUser,
            Members = shared.Where((_, i) => i % 10 == p).Select(s => new PoolMemberInfo(s.Id, null, 0, true)).ToArray()
        }).ToList();
        var failover = new FailoverPolicyInfo(Guid.NewGuid(), "fo", true, pools[0].Id, FailbackPolicy.Automatic, 3, 0);
        var companies = new List<CompanyInfo>();
        var companyAssignments = new List<CompanyAssignmentInfo>();
        var poolAssignments = new List<SharedPoolAssignmentInfo>();
        var userAssignments = new List<UserAssignmentInfo>();
        var authorizations = new List<CompanyServerAuthorizationInfo>();
        var requests = new List<RoutingRequest>();
        var rnd = new Random(42);
        var since = DateTimeOffset.UtcNow.AddDays(-1);
        for (var c = 0; c < companyCount; c++)
        {
            var id = Guid.NewGuid();
            companies.Add(new CompanyInfo(id, $"C{c}", $"Company {c}", CompanyStatus.Active, CompanyRoutingMode.Dedicated, "Standard", null, null));
            var isDedicated = c % 3 == 0;
            Guid? dedicatedServer = null;
            if (isDedicated)
            {
                dedicatedServer = dedicated[c % dedicated.Count].Id;
                companyAssignments.Add(new CompanyAssignmentInfo(Guid.NewGuid(), id, null, DeploymentEnvironment.Production, dedicatedServer.Value,
                    null, null, ClusterMode.PrimaryPreferred, failover.Id, true, since, null));
            }
            else
            {
                poolAssignments.Add(new SharedPoolAssignmentInfo(Guid.NewGuid(), id, pools[c % pools.Count].Id, null, DeploymentEnvironment.Production, 0, true, since, null));
            }
            for (var u = 0; u < usersPerCompany; u++)
            {
                var user = Guid.NewGuid();
                if (isDedicated && u % 10 == 0)
                {
                    userAssignments.Add(new UserAssignmentInfo(Guid.NewGuid(), id, user, null, DeploymentEnvironment.Production, dedicatedServer!.Value,
                        null, null, false, true, since, null));
                }
                if (u < 4) requests.Add(new RoutingRequest
                {
                    CompanyId = id, UserId = user, ApplicationId = app, Environment = DeploymentEnvironment.Production,
                    AffinityServerId = rnd.Next(3) == 0 ? dedicatedServer : null
                });
            }
        }
        var snapshot = new RoutingSnapshot(new RoutingConfiguration
        {
            Version = 1, GeneratedAt = DateTimeOffset.UtcNow, Companies = companies, Applications = [new ApplicationInfo(app, "ERP", "ERP", true, null)],
            ApplicationBindings = [new ApplicationBindingInfo(Guid.NewGuid(), app, DeploymentEnvironment.Production, "erp.example.com", "/", ApplicationRoutingMode.Proxy, true, 100, null)],
            Servers = serverList, Pools = pools, CompanyAssignments = companyAssignments, SharedPoolAssignments = poolAssignments,
            UserAssignments = userAssignments, FailoverPolicies = [failover], ServerAuthorizations = authorizations,
            RoutingPolicies = [new RoutingPolicyInfo { Id = Guid.NewGuid(), Name = "Default", IsDefault = true }]
        }, DateTimeOffset.UtcNow);
        var state = new StaticServerStateView(serverList.ToDictionary(s => s.Id, s => new ServerRuntimeState(s.Id,
            rnd.Next(50) == 0 ? HealthState.Unhealthy : HealthState.Healthy, DateTimeOffset.UtcNow, null,
            new ServerMetricsSample(DateTimeOffset.UtcNow, rnd.Next(5, 90), rnd.Next(20, 90), 30, 10, rnd.Next(0, 200), 0, rnd.NextDouble(), rnd.Next(10, 400), rnd.Next(0, 3000), true, "2.1"),
            rnd.Next(0, 50), rnd.Next(0, 3000))));
        return (snapshot, state, requests.ToArray());
    }

    private static int Arg(string[] args, string name, int fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
    }
}

static class LoadTest
{
    public static async Task<int> RunAsync(string[] args)
    {
        string A(string name, string? fallback = null) =>
            Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : fallback ?? throw new ArgumentException($"{name} is required");
        var gateway = A("--gateway");
        var concurrency = int.Parse(A("--concurrency", "32"));
        var seconds = int.Parse(A("--seconds", "20"));
        var path = A("--path", "/api/whoami");

        var cookies = new CookieContainer();
        var handler = new SocketsHttpHandler { CookieContainer = cookies, MaxConnectionsPerServer = concurrency * 2, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(gateway) };
        var csrf = await client.GetFromJsonAsync<JsonElement>("/_wings/auth/csrf");
        using var login = new HttpRequestMessage(HttpMethod.Post, "/_wings/auth/login")
        {
            Content = JsonContent.Create(new { company = A("--company"), username = A("--username"), password = A("--password") })
        };
        login.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        var loginResponse = await client.SendAsync(login);
        if (!loginResponse.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"Login failed: {(int)loginResponse.StatusCode}");
            return 1;
        }

        var latencies = new System.Collections.Concurrent.ConcurrentBag<double>();
        var errors = 0;
        var statuses = new System.Collections.Concurrent.ConcurrentDictionary<int, int>();
        var servers = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var stop = DateTime.UtcNow.AddSeconds(seconds);
        var sw = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
        {
            while (DateTime.UtcNow < stop)
            {
                var t = Stopwatch.GetTimestamp();
                try
                {
                    using var response = await client.GetAsync(path);
                    var body = await response.Content.ReadAsStringAsync();
                    latencies.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                    statuses.AddOrUpdate((int)response.StatusCode, 1, (_, n) => n + 1);
                    if (!response.IsSuccessStatusCode) Interlocked.Increment(ref errors);
                    else if (body.Contains("\"server\""))
                        servers.AddOrUpdate(JsonDocument.Parse(body).RootElement.GetProperty("server").GetString() ?? "?", 1, (_, n) => n + 1);
                }
                catch
                {
                    Interlocked.Increment(ref errors);
                }
            }
        }));
        sw.Stop();
        var sorted = latencies.OrderBy(x => x).ToArray();
        double P(double p) => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * p))];
        Console.WriteLine($"{sorted.Length:N0} requests in {sw.Elapsed.TotalSeconds:0.0}s → {sorted.Length / sw.Elapsed.TotalSeconds:N0} req/s, errors {errors}");
        Console.WriteLine($"Latency ms: p50 {P(0.5):0.00}  p95 {P(0.95):0.00}  p99 {P(0.99):0.00}  max {(sorted.Length > 0 ? sorted[^1] : 0):0.0}");
        Console.WriteLine("Status codes: " + string.Join(", ", statuses.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value:N0}")));
        Console.WriteLine("Served by: " + string.Join(", ", servers.Select(kv => $"{kv.Key}={kv.Value:N0}")));
        return errors == 0 ? 0 : 2;
    }
}
