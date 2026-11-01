using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wings.IntegrationTests.Infrastructure;
using Wings.LoadBalancer.Infrastructure.Hosting;
using Wings.Routing.Model;

namespace Wings.IntegrationTests;

/// <summary>Administration API against the live sample deployment (gateway + backends + monitor).</summary>
public sealed class AdminApiTests : IAsyncLifetime
{
    private WingsTestEnvironment _env = null!;
    private AdminClient _admin = null!;

    public async Task InitializeAsync()
    {
        _env = await WingsTestEnvironment.StartAsync();
        await _env.StartAdminAsync();
        _admin = await _env.CreateAdminClientAsync();
        // The simulator uses the monitor's health; wait until it has checked every server once.
        foreach (var (id, _) in WingsTestEnvironment.ServerDefinitions) await WaitForMonitorAsync(id);
    }

    public async Task DisposeAsync()
    {
        _admin.Dispose();
        await _env.DisposeAsync();
    }

    [Fact]
    public async Task Admin_login_returns_permissions_and_endpoints_require_authentication()
    {
        var me = await _admin.GetJsonAsync("/api/auth/me");
        Assert.True(me.GetProperty("authenticated").GetBoolean());
        Assert.Contains("assignments.write", me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));

        using var anonymous = new HttpClient { BaseAddress = _admin.Http.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/servers")).StatusCode);
    }

    [Fact]
    public async Task Unsafe_requests_without_xsrf_token_are_rejected()
    {
        // Same authenticated cookie jar, but no X-XSRF-TOKEN header.
        using var noXsrf = new HttpClient(new HttpClientHandler { CookieContainer = _admin.Cookies, UseCookies = true }) { BaseAddress = _admin.Http.BaseAddress };
        Assert.Equal(HttpStatusCode.OK, (await noXsrf.GetAsync("/api/servers")).StatusCode);
        var response = await noXsrf.PostAsync($"/api/servers/{SampleIds.Server05}/drain", JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("csrf", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Read_only_monitor_role_cannot_change_configuration()
    {
        var create = await _admin.Http.PostAsJsonAsync("/api/admins", new
        {
            username = "watcher", displayName = "Watcher", password = "Watcher-Passw0rd!", status = "Active", roles = new[] { "ReadOnlyMonitor" }
        });
        create.EnsureSuccessStatusCode();
        using var watcher = await _env.CreateAdminClientAsync("watcher", "Watcher-Passw0rd!");
        Assert.Equal(HttpStatusCode.OK, (await watcher.Http.GetAsync("/api/servers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await watcher.Http.PostAsync($"/api/servers/{SampleIds.Server05}/drain", JsonContent.Create(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await watcher.Http.GetAsync("/api/audit")).StatusCode);
    }

    [Fact]
    public async Task Simulator_explains_each_routing_level_without_side_effects()
    {
        var a = await Simulate("ABC", "user1");
        Assert.Equal("Routed", a.GetProperty("outcome").GetString());
        Assert.Equal("APP-SERVER-01", a.GetProperty("chosenServer").GetProperty("name").GetString());
        Assert.Equal("UserAssignment", a.GetProperty("source").GetString());
        Assert.Equal(SampleIds.AbcUser1, a.GetProperty("userId").GetGuid());

        var b = await Simulate("abc motors", "user2");
        Assert.Equal("APP-SERVER-02", b.GetProperty("chosenServer").GetProperty("name").GetString());
        Assert.Equal("APP-SERVER-06", b.GetProperty("failoverServer").GetProperty("name").GetString());

        var c = await Simulate("XYZ", "manager");
        Assert.Equal("SharedPool", c.GetProperty("source").GetString());
        Assert.NotEmpty(c.GetProperty("eligiblePools").EnumerateArray());
        Assert.NotEmpty(c.GetProperty("trace").EnumerateArray());

        var unknown = await Simulate("NOPE", "user1");
        Assert.Equal("Denied", unknown.GetProperty("outcome").GetString());

        var sessions = await _env.WithDbAsync(db => db.ActiveSessions.CountAsync());
        Assert.Equal(0, sessions); // the simulator never creates sessions
    }

    [Fact]
    public async Task User_assignment_validation_blocks_isolation_and_unauthorised_servers()
    {
        var stamp = await MembershipStamp(SampleIds.AbcUser2);
        var toOtherTenant = await PutAssignment(SampleIds.AbcUser2, SampleIds.Server07, stamp);
        Assert.Equal(HttpStatusCode.BadRequest, toOtherTenant.StatusCode);
        Assert.Contains("isolation", await toOtherTenant.Content.ReadAsStringAsync());

        var toUnauthorised = await PutAssignment(SampleIds.AbcUser2, SampleIds.Server03, stamp);
        Assert.Equal(HttpStatusCode.BadRequest, toUnauthorised.StatusCode);
        Assert.Contains("not authorised", await toUnauthorised.Content.ReadAsStringAsync());

        var wrongEnvironment = await PutAssignment(SampleIds.AbcUser2, SampleIds.Staging01, stamp);
        Assert.Equal(HttpStatusCode.BadRequest, wrongEnvironment.StatusCode);
    }

    [Fact]
    public async Task User_assignment_change_is_applied_by_the_gateway_and_can_be_rolled_back()
    {
        using var before = _env.CreateClient();
        await before.LoginOrThrowAsync("ABC", "user2");
        Assert.Equal("APP-SERVER-02", (await before.WhoAmIAsync()).Server);

        var put = await PutAssignment(SampleIds.AbcUser2, SampleIds.Server01, await MembershipStamp(SampleIds.AbcUser2));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        await _env.RefreshConfigurationAsync();

        using var after = _env.CreateClient();
        await after.LoginOrThrowAsync("ABC", "user2");
        Assert.Equal("APP-SERVER-01", (await after.WhoAmIAsync()).Server);

        var history = await _admin.GetJsonAsync($"/api/users/{SampleIds.AbcUser2}/assignment/history?companyId={SampleIds.AbcMotors}");
        Assert.Single(history.EnumerateArray());

        var remove = await _admin.Http.DeleteAsync($"/api/users/{SampleIds.AbcUser2}/assignment?companyId={SampleIds.AbcMotors}");
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        var rollback = await _admin.Http.PostAsJsonAsync($"/api/users/{SampleIds.AbcUser2}/assignment/rollback",
            new { assignmentId = history[0].GetProperty("id").GetGuid(), reason = "test rollback" });
        Assert.Equal(HttpStatusCode.OK, rollback.StatusCode);
        var current = await _admin.GetJsonAsync($"/api/users/{SampleIds.AbcUser2}/assignment?companyId={SampleIds.AbcMotors}");
        Assert.Equal("APP-SERVER-01", current.GetProperty("assignments")[0].GetProperty("primaryServer").GetString());

        var audit = await _admin.GetJsonAsync("/api/audit?eventType=UserAssignmentChanged");
        Assert.True(audit.GetProperty("total").GetInt32() >= 2);
    }

    [Fact]
    public async Task Concurrent_assignment_changes_with_same_stamp_conflict()
    {
        var stamp = await MembershipStamp(SampleIds.AbcManager);
        var results = await Task.WhenAll(
            PutAssignment(SampleIds.AbcManager, SampleIds.Server01, stamp),
            PutAssignment(SampleIds.AbcManager, SampleIds.Server06, stamp));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Scheduled_assignment_does_not_apply_before_its_start()
    {
        var put = await _admin.Http.PutAsJsonAsync($"/api/users/{SampleIds.AbcAccounts}/assignment", new
        {
            companyId = SampleIds.AbcMotors, primaryServerId = SampleIds.Server06, environment = "Production",
            effectiveFrom = DateTimeOffset.UtcNow.AddDays(1), reason = "Planned move"
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var sim = await Simulate("ABC", "accounts");
        Assert.Equal("APP-SERVER-01", sim.GetProperty("chosenServer").GetProperty("name").GetString());
        var current = await _admin.GetJsonAsync($"/api/users/{SampleIds.AbcAccounts}/assignment?companyId={SampleIds.AbcMotors}");
        Assert.Contains(current.GetProperty("assignments").EnumerateArray(), a => a.GetProperty("state").GetString() == "Scheduled");
    }

    [Fact]
    public async Task Preview_shows_before_and_after_without_saving()
    {
        var response = await _admin.Http.PostAsJsonAsync($"/api/users/{SampleIds.AbcUser2}/assignment/preview", new
        {
            assignment = new { companyId = SampleIds.AbcMotors, primaryServerId = SampleIds.Server01, environment = "Production" }
        });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var result = body.GetProperty("preview").GetProperty("results")[0];
        Assert.Equal("APP-SERVER-02", result.GetProperty("before").GetProperty("server").GetString());
        Assert.Equal("APP-SERVER-01", result.GetProperty("after").GetProperty("server").GetString());
        Assert.Equal("APP-SERVER-02", (await Simulate("ABC", "user2")).GetProperty("chosenServer").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Bulk_import_dry_run_reports_each_row_then_commits()
    {
        var csv = "Company,Username,Action,PrimaryServer,Environment,Reason\n" +
                  "ABC,manager,Assign,APP-SERVER-01,Production,bulk\n" +
                  "ABC,ghost,Assign,APP-SERVER-01,Production,bulk\n" +
                  "XYZ,manager,Assign,APP-SERVER-02,Production,bulk\n";
        var dry = await Upload(csv, dryRun: true);
        Assert.False(dry.GetProperty("valid").GetBoolean());
        var rows = dry.GetProperty("rows").EnumerateArray().ToList();
        Assert.True(rows[0].GetProperty("valid").GetBoolean());
        Assert.False(rows[1].GetProperty("valid").GetBoolean());
        Assert.False(rows[2].GetProperty("valid").GetBoolean()); // XYZ is not authorised for ABC's dedicated server

        var commitInvalid = await Upload(csv, dryRun: false);
        Assert.Equal(0, commitInvalid.GetProperty("applied").GetInt32());

        var ok = await Upload("Company,Username,Action,PrimaryServer\nABC,manager,Assign,APP-SERVER-01\n", dryRun: false);
        Assert.Equal(1, ok.GetProperty("applied").GetInt32());
        Assert.Equal("APP-SERVER-01", (await Simulate("ABC", "manager")).GetProperty("chosenServer").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Company_transfer_validates_before_cutover()
    {
        var blocked = await _admin.Http.PostAsJsonAsync($"/api/companies/{SampleIds.GlobalFreight}/transfer",
            new { targetServerId = SampleIds.Server02, mode = "Immediate", validateOnly = true });
        var report = await blocked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(report.GetProperty("passed").GetBoolean()); // APP-SERVER-02 is dedicated to ABC

        await _env.WaitForHealthyAsync(SampleIds.Server06);
        await WaitForMonitorAsync(SampleIds.Server06);
        var transfer = await _admin.Http.PostAsJsonAsync($"/api/companies/{SampleIds.GlobalFreight}/transfer",
            new { targetServerId = SampleIds.Server06, mode = "Immediate", reason = "Hardware refresh" });
        var result = await transfer.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("applied").GetBoolean(), result.ToString());
        Assert.Equal("APP-SERVER-06", (await Simulate("GFL", "manager")).GetProperty("chosenServer").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Server_with_active_assignments_cannot_be_retired_or_deleted()
    {
        var retire = await _admin.Http.PostAsync($"/api/servers/{SampleIds.Server01}/retire", null);
        Assert.Equal(HttpStatusCode.Conflict, retire.StatusCode);
        var delete = await _admin.Http.DeleteAsync($"/api/servers/{SampleIds.Server01}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
    }

    [Fact]
    public async Task Ssrf_protection_rejects_metadata_and_disallowed_backend_urls()
    {
        var response = await _admin.Http.PostAsJsonAsync("/api/servers", new
        {
            name = "EVIL", hostname = "metadata", baseUrl = "http://169.254.169.254/latest", environment = "Production", serverType = "Shared", maxCapacity = 10
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Drain_moves_server_to_maintenance_when_sessions_end()
    {
        var drain = await _admin.Http.PostAsJsonAsync($"/api/servers/{SampleIds.Server05}/drain", new { timeoutMinutes = 5 });
        Assert.Equal(HttpStatusCode.OK, drain.StatusCode);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        string state;
        do
        {
            state = (await _admin.GetJsonAsync($"/api/servers/{SampleIds.Server05}")).GetProperty("adminState").GetString()!;
            if (state == "Maintenance") break;
            await Task.Delay(300);
        } while (DateTime.UtcNow < deadline);
        Assert.Equal("Maintenance", state);

        // The automatic transition is attributed to the system and records enum names, not numbers.
        var audit = await _admin.GetJsonAsync($"/api/audit?eventType=Server.Modified&serverId={SampleIds.Server05}");
        var automatic = audit.GetProperty("rows").EnumerateArray()
            .First(r => r.GetProperty("newValue").GetString()!.Contains("\"AdminState\":\"Maintenance\""));
        Assert.Equal("system", automatic.GetProperty("actor").GetString());

        Assert.Equal(HttpStatusCode.OK, (await _admin.Http.PostAsync($"/api/servers/{SampleIds.Server05}/restore", null)).StatusCode);
    }

    [Fact]
    public async Task Health_monitor_leader_records_current_health_and_metrics()
    {
        await WaitForMonitorAsync(SampleIds.Server01);
        var servers = await _admin.GetJsonAsync("/api/servers");
        var s1 = servers.EnumerateArray().First(s => s.GetProperty("name").GetString() == "APP-SERVER-01");
        Assert.Equal("Healthy", s1.GetProperty("health").GetString());
        Assert.Equal("Online", s1.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Dashboard_reports_and_exports_respond()
    {
        var summary = await _admin.GetJsonAsync("/api/dashboard/summary");
        Assert.Equal(8, summary.GetProperty("servers").GetProperty("total").GetInt32());
        Assert.Equal(3, summary.GetProperty("companies").GetProperty("total").GetInt32());
        Assert.NotEmpty((await _admin.GetJsonAsync("/api/dashboard/topology")).EnumerateArray());

        foreach (var report in (await _admin.GetJsonAsync("/api/reports")).EnumerateArray())
        {
            var name = report.GetProperty("name").GetString();
            var table = await _admin.GetJsonAsync($"/api/reports/{name}");
            Assert.True(table.GetProperty("columns").GetArrayLength() > 0, name);
        }
        var xlsx = await _admin.Http.GetAsync("/api/reports/company-allocation?format=xlsx");
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsx.Content.Headers.ContentType!.MediaType);
        var csv = await (await _admin.Http.GetAsync("/api/reports/company-allocation?format=csv")).Content.ReadAsStringAsync();
        Assert.Contains("ABC", csv);
    }

    [Fact]
    public async Task Configuration_endpoint_reports_validation_issues_and_gateway_sync()
    {
        var config = await _admin.GetJsonAsync("/api/routing/config");
        Assert.True(config.GetProperty("version").GetInt64() > 0);
        Assert.DoesNotContain(config.GetProperty("issues").EnumerateArray(), i => i.GetProperty("severity").GetString() == "Error");
    }

    [Fact]
    public async Task End_to_end_test_signs_in_through_the_gateway()
    {
        var result = await _admin.Http.PostAsJsonAsync("/api/routing/e2e", new
        {
            company = "ABC", username = "user1", password = WingsTestEnvironment.UserPassword, path = "/api/whoami"
        });
        var body = await result.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("success").GetBoolean(), body.ToString());
        Assert.Equal("APP-SERVER-01", body.GetProperty("steps")[1].GetProperty("servedBy").GetString());
        Assert.DoesNotContain(WingsTestEnvironment.UserPassword, body.ToString());
    }

    [Fact]
    public async Task Application_codes_are_unique_regardless_of_case()
    {
        Assert.Equal(HttpStatusCode.Conflict, (await _admin.Http.PostAsJsonAsync("/api/applications", new { code = "erp", name = "Duplicate" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.Http.PostAsJsonAsync($"/api/companies/{Guid.NewGuid()}/assignment/preview",
            new { assignment = new { primaryServerId = SampleIds.Server01, environment = "Production" } })).StatusCode);
    }

    [Fact]
    public async Task Admin_login_bookkeeping_is_not_audited_as_anonymous()
    {
        var audit = await _admin.GetJsonAsync("/api/audit?eventType=Administrator.Modified");
        Assert.DoesNotContain(audit.GetProperty("rows").EnumerateArray(), r => r.GetProperty("actor").GetString() == "anonymous");
    }

    [Fact]
    public async Task OpenApi_document_is_published()
    {
        using var anonymous = new HttpClient { BaseAddress = _admin.Http.BaseAddress };
        var doc = await anonymous.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        Assert.True(doc.GetProperty("paths").TryGetProperty("/api/routing/simulate", out _));
    }

    private async Task<JsonElement> Simulate(string company, string username)
    {
        var response = await _admin.Http.PostAsJsonAsync("/api/routing/simulate", new { company, username, environment = "Production" });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<Guid> MembershipStamp(Guid userId) =>
        (await _admin.GetJsonAsync($"/api/users/{userId}/assignment?companyId={SampleIds.AbcMotors}")).GetProperty("membershipStamp").GetGuid();

    private Task<HttpResponseMessage> PutAssignment(Guid userId, Guid serverId, Guid stamp) =>
        _admin.Http.PutAsJsonAsync($"/api/users/{userId}/assignment", new
        {
            companyId = SampleIds.AbcMotors, primaryServerId = serverId, environment = "Production", reason = "test", expectedStamp = stamp
        });

    private async Task<JsonElement> Upload(string csv, bool dryRun)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "assignments.csv");
        var response = await _admin.Http.PostAsync($"/api/users/bulk-import?dryRun={dryRun}", form);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task WaitForMonitorAsync(Guid serverId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var state = await _env.WithDbAsync(db => db.ServerHealthCurrent.AsNoTracking().Where(h => h.ServerId == serverId)
                .Select(h => (HealthState?)h.State).FirstOrDefaultAsync());
            if (state == HealthState.Healthy) return;
            await Task.Delay(200);
        }
        throw new TimeoutException("Monitor did not report the server healthy");
    }
}
