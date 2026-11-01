using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wings.IntegrationTests.Infrastructure;
using Wings.LoadBalancer.Data.Entities;
using Wings.LoadBalancer.Infrastructure.Configuration;
using Wings.LoadBalancer.Infrastructure.Hosting;
using Wings.Routing.Model;

namespace Wings.IntegrationTests;

/// <summary>Scenarios D (failover), E (live reassignment), I (draining), backend failures and failback.</summary>
public sealed class FailoverAndMaintenanceTests : IAsyncLifetime
{
    private WingsTestEnvironment _env = null!;

    public async Task InitializeAsync() => _env = await WingsTestEnvironment.StartAsync();
    public async Task DisposeAsync() => await _env.DisposeAsync();

    [Fact]
    public async Task ScenarioD_dedicated_user_without_failover_gets_controlled_503()
    {
        using var client = _env.CreateClient();
        await client.LoginOrThrowAsync("ABC", "user1");
        Assert.Equal("APP-SERVER-01", (await client.WhoAmIAsync()).Server);

        await _env.SetBackendHealthAsync(SampleIds.Server01, false);
        await _env.WaitForAsync([SampleIds.Server01], HealthState.Unhealthy);

        var response = await client.Http.GetAsync("/api/whoami");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("APP-SERVER", body); // no topology leak
        Assert.DoesNotContain("localhost:", body);
    }

    [Fact]
    public async Task ScenarioD_company_failover_to_permitted_secondary_then_automatic_failback()
    {
        using var client = _env.CreateClient();
        await client.LoginOrThrowAsync("ABC", "manager");
        Assert.Equal("APP-SERVER-02", (await client.WhoAmIAsync()).Server);

        await _env.SetBackendHealthAsync(SampleIds.Server02, false);
        await _env.WaitForAsync([SampleIds.Server02], HealthState.Unhealthy);
        Assert.Equal("APP-SERVER-06", (await client.WhoAmIAsync()).Server);

        await _env.SetBackendHealthAsync(SampleIds.Server02, true);
        await _env.WaitForHealthyAsync(SampleIds.Server02);
        Assert.Equal("APP-SERVER-02", (await client.WhoAmIAsync()).Server); // policy: automatic failback
    }

    [Fact]
    public async Task Backend_connection_failure_retries_safe_request_on_permitted_server()
    {
        using var client = _env.CreateClient();
        await client.LoginOrThrowAsync("ABC", "user2");
        Assert.Equal("APP-SERVER-02", (await client.WhoAmIAsync()).Server);

        await _env.Backends[SampleIds.Server02].App.StopAsync();
        // The connect failure is detected on this request and retried on the secondary server.
        Assert.Equal("APP-SERVER-06", (await client.WhoAmIAsync()).Server);

        // Non-replayable requests are not retried: they fail with a controlled error, or (once the passive check
        // has quarantined the server) are routed to the permitted secondary.
        var post = await client.Http.PostAsync("/api/upload", new ByteArrayContent(new byte[10]));
        Assert.Contains(post.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.BadGateway });
    }

    [Fact]
    public async Task Backend_failure_for_tenant_without_failover_never_reaches_other_servers()
    {
        using var client = _env.CreateClient();
        await client.LoginOrThrowAsync("GFL", "manager");
        Assert.Equal("APP-SERVER-07", (await client.WhoAmIAsync()).Server);
        await _env.Backends[SampleIds.Server07].App.StopAsync();
        var response = await client.Http.GetAsync("/api/whoami");
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable });
    }

    [Fact]
    public async Task ScenarioE_assignment_change_applies_to_new_sessions_without_restart()
    {
        using var before = _env.CreateClient();
        await before.LoginOrThrowAsync("ABC", "accounts");
        Assert.Equal("APP-SERVER-01", (await before.WhoAmIAsync()).Server);

        await _env.WithDbAsync(async db =>
        {
            var current = await db.UserServerAssignments.SingleAsync(a => a.UserId == SampleIds.AbcAccounts && a.EffectiveTo == null);
            var now = DateTimeOffset.UtcNow;
            current.EffectiveTo = now;
            db.UserServerAssignments.Add(new UserServerAssignment
            {
                CompanyId = SampleIds.AbcMotors, UserId = SampleIds.AbcAccounts, Environment = DeploymentEnvironment.Production,
                PrimaryServerId = SampleIds.Server06, EffectiveFrom = now, CreatedAt = now, CreatedBy = "test"
            });
            await db.SaveChangesAsync();
        });

        // The gateway polls the configuration version every second; no restart or manual refresh.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        string server;
        using var after = _env.CreateClient();
        await after.LoginOrThrowAsync("ABC", "accounts");
        do
        {
            server = (await after.WhoAmIAsync()).Server;
            if (server == "APP-SERVER-06") break;
            await Task.Delay(250);
        } while (DateTime.UtcNow < deadline);
        Assert.Equal("APP-SERVER-06", server);
    }

    [Fact]
    public async Task ScenarioI_draining_server_keeps_existing_sessions_and_refuses_new_ones()
    {
        using var existing = _env.CreateClient();
        await existing.LoginOrThrowAsync("XYZ", "manager");
        var pinned = (await existing.WhoAmIAsync()).Server;
        var pinnedId = WingsTestEnvironment.ServerDefinitions.Single(s => s.Name == pinned).Id;

        await _env.WithDbAsync(async db =>
        {
            var server = await db.Servers.SingleAsync(s => s.Id == pinnedId);
            server.AdminState = ServerAdminState.Draining;
            server.DrainStartedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        });
        await _env.RefreshConfigurationAsync();

        Assert.Equal(pinned, (await existing.WhoAmIAsync()).Server);

        using var fresh = _env.CreateClient();
        await fresh.LoginOrThrowAsync("XYZ", "user1");
        var newServer = (await fresh.WhoAmIAsync()).Server;
        Assert.NotEqual(pinned, newServer);
        Assert.Contains(newServer, new[] { "APP-SERVER-03", "APP-SERVER-04", "APP-SERVER-05" });

        // Maintenance: existing sessions move to another permitted pool member.
        await _env.WithDbAsync(async db =>
        {
            (await db.Servers.SingleAsync(s => s.Id == pinnedId)).AdminState = ServerAdminState.Maintenance;
            await db.SaveChangesAsync();
        });
        await _env.RefreshConfigurationAsync();
        Assert.NotEqual(pinned, (await existing.WhoAmIAsync()).Server);
    }

    [Fact]
    public async Task Disabling_a_user_cuts_off_existing_sessions()
    {
        using var client = _env.CreateClient();
        await client.LoginOrThrowAsync("XYZ", "user1");
        await client.WhoAmIAsync();
        await _env.WithDbAsync(async db =>
        {
            var m = await db.UserCompanyMemberships.SingleAsync(x => x.UserId == SampleIds.XyzUser1);
            m.Status = AccountStatus.Disabled;
            await db.SaveChangesAsync();
        });
        await _env.RefreshConfigurationAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.Http.GetAsync("/api/whoami")).StatusCode);
    }
}

/// <summary>Scenario G: several gateway instances share configuration, keys and decisions.</summary>
public sealed class MultiGatewayTests : IAsyncLifetime
{
    private WingsTestEnvironment _env = null!;

    public async Task InitializeAsync() => _env = await WingsTestEnvironment.StartAsync(gateways: 2);
    public async Task DisposeAsync() => await _env.DisposeAsync();

    [Fact]
    public async Task ScenarioG_session_from_one_gateway_is_valid_on_another_with_same_destination()
    {
        using var gw1 = _env.CreateClient(_env.Gateways[0]);
        await gw1.LoginOrThrowAsync("ABC", "user1");
        Assert.Equal("APP-SERVER-01", (await gw1.WhoAmIAsync()).Server);

        using var gw2 = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(WingsTestEnvironment.Url(_env.Gateways[1])) };
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        request.Headers.Add("Cookie", $"Wings.Session={gw1.SessionCookie}");
        var response = await gw2.SendAsync(request);
        var who = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("APP-SERVER-01", who.GetProperty("server").GetString());
    }

    [Fact]
    public async Task ScenarioG_shared_pool_decisions_are_identical_on_every_gateway()
    {
        var picks = new List<string>();
        foreach (var gateway in _env.Gateways)
        {
            foreach (var user in new[] { "manager", "user1" })
            {
                using var client = _env.CreateClient(gateway);
                await client.LoginOrThrowAsync("XYZ", user);
                picks.Add((await client.WhoAmIAsync()).Server);
            }
        }
        Assert.Single(picks.Distinct());
    }

    [Fact]
    public async Task Configuration_versions_converge_on_all_gateways()
    {
        await _env.WithDbAsync(async db =>
        {
            (await db.Companies.SingleAsync(c => c.Id == SampleIds.GlobalFreight)).Notes = "touched";
            await db.SaveChangesAsync();
        });
        var expected = await _env.WithDbAsync(db => db.ConfigurationVersions.OrderByDescending(v => v.ChangedAt).Select(v => v.Version).FirstAsync());
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && _env.Gateways.Any(g => g.Services.GetRequiredService<IRoutingSnapshotProvider>().Current!.Version != expected))
            await Task.Delay(200);
        Assert.All(_env.Gateways, g => Assert.Equal(expected, g.Services.GetRequiredService<IRoutingSnapshotProvider>().Current!.Version));
    }
}

/// <summary>Scenario J and dependency outages.</summary>
public sealed class DependencyOutageTests
{
    [Fact]
    public async Task ScenarioJ_database_outage_keeps_authorised_traffic_on_last_known_good_configuration()
    {
        await using var env = await WingsTestEnvironment.StartAsync();
        using var client = env.CreateClient();
        await client.LoginOrThrowAsync("ABC", "user1");
        Assert.Equal("APP-SERVER-01", (await client.WhoAmIAsync()).Server);

        // Take the database away.
        SqliteConnection.ClearAllPools();
        File.Move(env.DatabasePath, env.DatabasePath + ".offline");
        var provider = env.Gateway.Services.GetRequiredService<IRoutingSnapshotProvider>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (provider.DatabaseAvailable && DateTime.UtcNow < deadline) await Task.Delay(200);
        Assert.False(provider.DatabaseAvailable);

        // Existing authorised session keeps routing to the same server, and tenant isolation still holds.
        Assert.Equal("APP-SERVER-01", (await client.WhoAmIAsync()).Server);

        // New sign-ins fail gracefully rather than bypassing authentication.
        using var newcomer = env.CreateClient();
        var login = await newcomer.LoginAsync("ABC", "user2");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, login.StatusCode);
        var ready = await newcomer.Http.GetFromJsonAsync<JsonElement>("/_wings/health/ready");
        Assert.False(ready.GetProperty("database").GetBoolean());
    }

    [Fact]
    public async Task ScenarioJ_last_known_good_expiry_fails_closed()
    {
        await using var env = await WingsTestEnvironment.StartAsync(configure: s => s["Routing:LastKnownGoodMaxAgeMinutes"] = "1");
        using var client = env.CreateClient();
        await client.LoginOrThrowAsync("ABC", "user1");
        await client.WhoAmIAsync();
        SqliteConnection.ClearAllPools();
        File.Move(env.DatabasePath, env.DatabasePath + ".offline");
        var snapshot = env.Gateway.Services.GetRequiredService<IRoutingSnapshotProvider>().Current!;
        // Simulate the passage of time by back-dating the last validation beyond the one-minute limit.
        typeof(RoutingSnapshot).GetProperty(nameof(RoutingSnapshot.LastValidatedAt))!
            .SetValue(snapshot, DateTimeOffset.UtcNow.AddMinutes(-5));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (env.Gateway.Services.GetRequiredService<IRoutingSnapshotProvider>().DatabaseAvailable && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.Http.GetAsync("/api/whoami")).StatusCode);
    }

    [Fact]
    public async Task Redis_unavailable_falls_back_to_in_memory_state()
    {
        await using var env = await WingsTestEnvironment.StartAsync(configure: s =>
            s["Redis:Configuration"] = "127.0.0.1:1,connectTimeout=200,abortConnect=false");
        using var client = env.CreateClient();
        await client.LoginOrThrowAsync("ABC", "user2");
        Assert.Equal("APP-SERVER-02", (await client.WhoAmIAsync()).Server);
        Assert.False(env.Gateway.Services.GetRequiredService<Wings.LoadBalancer.Infrastructure.Caching.IStateStore>().IsDistributed);
    }

    [Fact]
    public async Task Gateway_restart_without_database_uses_protected_last_known_good_file()
    {
        await using var env = await WingsTestEnvironment.StartAsync();
        SqliteConnection.ClearAllPools();
        File.Move(env.DatabasePath, env.DatabasePath + ".offline");
        var second = await env.StartGatewayAsync("Gateway-01", seed: false, configure: s => s["Seed:ApplyMigrations"] = "false");
        var snapshot = second.Services.GetRequiredService<IRoutingSnapshotProvider>().Current;
        Assert.NotNull(snapshot);
        Assert.True(snapshot!.Companies.ContainsKey(SampleIds.AbcMotors));
    }
}
