using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wings.Gateway;
using Wings.LoadBalancer.Data;
using Wings.LoadBalancer.Infrastructure.Configuration;
using Wings.LoadBalancer.Infrastructure.Health;
using Wings.LoadBalancer.Infrastructure.Hosting;
using Wings.MockBackend;
using Wings.Routing.Model;

namespace Wings.IntegrationTests.Infrastructure;

/// <summary>
/// A complete local deployment for one test class: SQLite database seeded with the sample tenants, eight mock
/// application servers on real Kestrel ports, and one or more gateway instances (real Kestrel) sharing the database
/// and Data Protection keys.
/// </summary>
public sealed class WingsTestEnvironment : IAsyncDisposable
{
    public const string MasterKey = "dGVzdC1tYXN0ZXIta2V5LWZvci1pbnRlZ3JhdGlvbi10ZXN0cy0zMg==";
    public const string UserPassword = "Sample-Passw0rd!";
    public const string AdminPassword = "Admin-Passw0rd!";

    public static readonly (Guid Id, string Name)[] ServerDefinitions =
    [
        (SampleIds.Server01, "APP-SERVER-01"), (SampleIds.Server02, "APP-SERVER-02"), (SampleIds.Server03, "APP-SERVER-03"),
        (SampleIds.Server04, "APP-SERVER-04"), (SampleIds.Server05, "APP-SERVER-05"), (SampleIds.Server06, "APP-SERVER-06"),
        (SampleIds.Server07, "APP-SERVER-07"), (SampleIds.Staging01, "STAGING-01")
    ];

    private readonly List<WebApplication> _gateways = [];
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "wings-it-" + Guid.NewGuid().ToString("N")[..8]);
    public string DatabasePath => Path.Combine(Directory, "wings.db");
    public Dictionary<Guid, (WebApplication App, string Url)> Backends { get; } = new();
    public IReadOnlyList<WebApplication> Gateways => _gateways;
    public WebApplication Gateway => _gateways[0];
    public string GatewayUrl => Url(_gateways[0]);

    public static async Task<WingsTestEnvironment> StartAsync(int gateways = 1, Action<Dictionary<string, string?>>? configure = null)
    {
        var env = new WingsTestEnvironment();
        System.IO.Directory.CreateDirectory(env.Directory);
        foreach (var (id, name) in ServerDefinitions) await env.StartBackendAsync(id, name);
        for (var i = 0; i < gateways; i++) await env.StartGatewayAsync($"Gateway-{i + 1:00}", seed: i == 0, configure);
        await env.WaitForHealthyAsync(ServerDefinitions.Select(s => s.Id).ToArray());
        return env;
    }

    public async Task StartBackendAsync(Guid id, string name)
    {
        var app = MockBackendApp.Build(["--urls", "http://127.0.0.1:0"], s =>
        {
            s.ServerName = name;
            s.ServerId = id;
            s.MasterKey = MasterKey;
            s.EnableTestControls = true;
            s.Version = name.StartsWith("STAGING") ? "2.2.0" : "2.1.0";
        });
        await app.StartAsync();
        Backends[id] = (app, Url(app));
    }

    public async Task<WebApplication> StartGatewayAsync(string instance, bool seed, Action<Dictionary<string, string?>>? configure = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["ConnectionStrings:Wings"] = $"Data Source={DatabasePath};Default Timeout=30",
            ["Seed:RunOnGateway"] = seed ? "true" : "false",
            ["Seed:ApplyMigrations"] = "true",
            ["Seed:SampleData"] = "true",
            ["Seed:SampleUserPassword"] = UserPassword,
            ["Seed:AdminPassword"] = AdminPassword,
            ["Authentication:PasswordIterations"] = "1000",
            ["Gateway:InstanceName"] = instance,
            ["Gateway:CookieName"] = "Wings.Session",
            ["Gateway:RequireHttpsCookies"] = "false",
            ["Gateway:LoginPermitsPerMinutePerIp"] = "1000",
            ["Health:GatewayMode"] = "Local",
            ["Health:IntervalSeconds"] = "1",
            ["Health:TimeoutSeconds"] = "2",
            ["Health:FailuresToUnhealthy"] = "2",
            ["Health:SuccessesToRecover"] = "2",
            ["Health:StabilizationSeconds"] = "1",
            ["Health:PassiveFailuresToQuarantine"] = "1",
            ["ConfigurationSync:VersionPollSeconds"] = "1",
            ["ConfigurationSync:LastKnownGoodPath"] = Path.Combine(Directory, $"lkg-{instance}.bin"),
            ["DestinationPolicy:AllowInsecureBackends"] = "true",
            ["DestinationPolicy:AllowLoopback"] = "true",
            ["BackendTrust:MasterKey"] = MasterKey,
            ["DataProtection:KeyPath"] = Path.Combine(Directory, "dp-keys"),
            ["Redis:Configuration"] = "",
            ["Logging:LogLevel:Default"] = "Warning",
        };
        configure?.Invoke(settings);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ApplicationName = typeof(GatewayEntryPoint).Assembly.GetName().Name,
            ContentRootPath = SourcePath("src", "Wings.Gateway"),
            WebRootPath = SourcePath("src", "Wings.Gateway", "wwwroot")
        });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Host.UseDefaultServiceProvider(o => { o.ValidateScopes = true; o.ValidateOnBuild = true; });
        GatewayApp.ConfigureServices(builder);
        var app = builder.Build();
        await GatewayApp.ConfigurePipelineAsync(app);

        if (seed) await PointServersAtBackendsAsync(app.Services);
        await app.StartAsync();
        _gateways.Add(app);
        await app.Services.GetRequiredService<RoutingSnapshotService>().WaitForFirstLoadAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));
        return app;
    }

    private async Task PointServersAtBackendsAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WingsDbContext>();
        foreach (var server in await db.Servers.ToListAsync())
        {
            if (!Backends.TryGetValue(server.Id, out var backend)) continue;
            server.BaseUrl = backend.Url;
            server.HealthCheckUrl = backend.Url + "/health/ready";
        }
        await db.SaveChangesAsync();
    }

    public async Task WithDbAsync(Func<WingsDbContext, Task> action)
    {
        using var scope = Gateway.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<WingsDbContext>());
    }

    public async Task<T> WithDbAsync<T>(Func<WingsDbContext, Task<T>> action)
    {
        using var scope = Gateway.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<WingsDbContext>());
    }

    /// <summary>Forces every gateway to pick up the latest configuration now instead of waiting for the poll.</summary>
    public async Task RefreshConfigurationAsync()
    {
        foreach (var g in _gateways)
            await g.Services.GetRequiredService<RoutingSnapshotService>().RefreshAsync(false, CancellationToken.None);
    }

    public async Task SetBackendHealthAsync(Guid serverId, bool healthy)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{Backends[serverId].Url}/_test/health?healthy={healthy}", null)).EnsureSuccessStatusCode();
    }

    public Task WaitForHealthyAsync(params Guid[] servers) => WaitForAsync(servers, HealthState.Healthy);

    public async Task WaitForAsync(Guid[] servers, HealthState state, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            var all = _gateways.All(g =>
            {
                var store = g.Services.GetRequiredService<ServerStateStore>();
                return servers.All(s => store.Get(s).Health == state);
            });
            if (all) return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Servers did not reach {state}: " + string.Join(", ", servers.Select(s =>
                    $"{s}={_gateways[0].Services.GetRequiredService<ServerStateStore>().Get(s).Health}")));
            await Task.Delay(100);
        }
    }

    public WingsClient CreateClient(WebApplication? gateway = null) => new(Url(gateway ?? Gateway));

    public WebApplication? Admin { get; private set; }

    /// <summary>Starts the admin API (with the leader-elected health monitor) against the same database.</summary>
    public async Task<WebApplication> StartAdminAsync(Action<Dictionary<string, string?>>? configure = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["ConnectionStrings:Wings"] = $"Data Source={DatabasePath};Default Timeout=30",
            ["Seed:RunOnAdmin"] = "false",
            ["Authentication:PasswordIterations"] = "1000",
            ["Admin:CookieName"] = "Wings.Admin",
            ["Admin:RequireHttpsCookies"] = "false",
            ["Admin:LoginPermitsPerMinute"] = "1000",
            ["Admin:GatewayUrl"] = GatewayUrl,
            ["Monitor:RunInAdmin"] = "true",
            ["Health:IntervalSeconds"] = "1",
            ["Health:FailuresToUnhealthy"] = "2",
            ["Health:SuccessesToRecover"] = "1",
            ["Health:StabilizationSeconds"] = "0",
            ["Operations:DrainCheckSeconds"] = "1",
            ["DestinationPolicy:AllowInsecureBackends"] = "true",
            ["DestinationPolicy:AllowLoopback"] = "true",
            ["BackendTrust:MasterKey"] = MasterKey,
            ["DataProtection:KeyPath"] = Path.Combine(Directory, "dp-keys"),
            ["Redis:Configuration"] = "",
            ["Logging:LogLevel:Default"] = "Warning",
        };
        configure?.Invoke(settings);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ApplicationName = typeof(Wings.Admin.Api.AdminEntryPoint).Assembly.GetName().Name,
            ContentRootPath = SourcePath("src", "Wings.Admin.Api"),
        });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Host.UseDefaultServiceProvider(o => { o.ValidateScopes = true; o.ValidateOnBuild = true; });
        Wings.Admin.Api.AdminApp.ConfigureServices(builder);
        var app = builder.Build();
        await Wings.Admin.Api.AdminApp.ConfigurePipelineAsync(app);
        await app.StartAsync();
        Admin = app;
        return app;
    }

    public async Task<AdminClient> CreateAdminClientAsync(string username = "admin", string password = AdminPassword)
    {
        var client = new AdminClient(Url(Admin!));
        await client.LoginAsync(username, password);
        return client;
    }

    public static string Url(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return address.Replace("127.0.0.1", "localhost");
    }

    private static string SourcePath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Wings.LoadBalancer.slnx"))) dir = dir.Parent;
        return Path.Combine([dir!.FullName, .. parts]);
    }

    public async ValueTask DisposeAsync()
    {
        if (Admin is not null) await StopQuietly(Admin);
        foreach (var g in _gateways) await StopQuietly(g);
        foreach (var (_, (app, _)) in Backends) await StopQuietly(app);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task StopQuietly(WebApplication app)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(cts.Token);
            await app.DisposeAsync();
        }
        catch
        {
            // ignored during teardown
        }
    }
}

/// <summary>Browser-like client: cookie jar, no automatic redirects, helper for the login flow.</summary>
public sealed class WingsClient : IDisposable
{
    public CookieContainer Cookies { get; } = new();
    public HttpClient Http { get; }
    public string BaseUrl { get; }

    public WingsClient(string baseUrl)
    {
        BaseUrl = baseUrl;
        Http = new HttpClient(new HttpClientHandler { CookieContainer = Cookies, AllowAutoRedirect = false, UseCookies = true })
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(60)
        };
    }

    public async Task<HttpResponseMessage> LoginAsync(string company, string username, string password = WingsTestEnvironment.UserPassword,
        string? returnUrl = null, string? mfa = null)
    {
        var csrf = await Http.GetFromJsonAsync<JsonElement>("/_wings/auth/csrf");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/_wings/auth/login")
        {
            Content = JsonContent.Create(new { company, username, password, mfaCode = mfa, returnUrl })
        };
        request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        return await Http.SendAsync(request);
    }

    public async Task LoginOrThrowAsync(string company, string username)
    {
        var response = await LoginAsync(company, username);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Login {company}/{username} failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    public async Task<WhoAmI> WhoAmIAsync(string path = "/api/whoami")
    {
        var response = await Http.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{(int)response.StatusCode}: {body}", null, response.StatusCode);
        return JsonSerializer.Deserialize<WhoAmI>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    public string? SessionCookie => Cookies.GetCookies(new Uri(BaseUrl))["Wings.Session"]?.Value;

    public void Dispose() => Http.Dispose();
}

public sealed record WhoAmI(string Server, Guid ServerId, string CompanyId, string? CompanyCode, string UserId, string? Username,
    string? Application, string? Environment, bool SignatureVerified, string? Path, string? ForwardedFor, string? ForwardedHost,
    string[] Cookies, string[] WingsHeaders, string[] SuspiciousHeaders);

/// <summary>Admin API client: cookie session + XSRF header echo, like the Angular app.</summary>
public sealed class AdminClient : IDisposable
{
    private readonly CookieContainer _cookies = new();
    private readonly Uri _base;
    public CookieContainer Cookies => _cookies;
    public HttpClient Http { get; }

    public AdminClient(string baseUrl)
    {
        _base = new Uri(baseUrl);
        Http = new HttpClient(new XsrfHandler(_cookies, _base) { InnerHandler = new HttpClientHandler { CookieContainer = _cookies, UseCookies = true } })
        {
            BaseAddress = _base
        };
    }

    public async Task<HttpResponseMessage> LoginAsync(string username, string password)
    {
        await Http.GetAsync("/api/auth/csrf");
        var response = await Http.PostAsJsonAsync("/api/auth/login", new { username, password });
        await Http.GetAsync("/api/auth/me"); // refresh XSRF token for the authenticated identity
        return response;
    }

    public async Task<JsonElement> GetJsonAsync(string url)
    {
        var response = await Http.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{url} → {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public void Dispose() => Http.Dispose();

    private sealed class XsrfHandler(CookieContainer cookies, Uri baseUri) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get && cookies.GetCookies(baseUri)["XSRF-TOKEN"] is { } token)
                request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", token.Value);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
