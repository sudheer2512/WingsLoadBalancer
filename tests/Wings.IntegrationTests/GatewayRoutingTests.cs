using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wings.IntegrationTests.Infrastructure;
using Wings.LoadBalancer.Data.Entities;
using Wings.LoadBalancer.Infrastructure.Hosting;
using Wings.Routing.Model;

namespace Wings.IntegrationTests;

/// <summary>Acceptance scenarios A, B, C, F and H plus proxy behaviour, against real mock backends.</summary>
public sealed class GatewayRoutingTests : IAsyncLifetime
{
    private WingsTestEnvironment _env = null!;

    public async Task InitializeAsync() => _env = await WingsTestEnvironment.StartAsync();
    public async Task DisposeAsync() => await _env.DisposeAsync();

    [Fact]
    public async Task ScenarioA_user_dedicated_server_handles_every_request()
    {
        using var client = _env.CreateClient();
        await client.LoginOrThrowAsync("ABC", "user1");
        for (var i = 0; i < 5; i++)
        {
            var who = await client.WhoAmIAsync();
            Assert.Equal("APP-SERVER-01", who.Server);
            Assert.True(who.SignatureVerified);
            Assert.Equal(SampleIds.AbcMotors.ToString(), who.CompanyId);
            Assert.Equal(SampleIds.AbcUser1.ToString(), who.UserId);
        }
    }

    [Fact]
    public async Task ScenarioB_user_without_assignment_uses_company_server()
    {
        using var client = _env.CreateClient();
        await client.LoginOrThrowAsync("abc motors", "USER2"); // alias + case-insensitive username
        Assert.Equal("APP-SERVER-02", (await client.WhoAmIAsync()).Server);
    }

    [Fact]
    public async Task ScenarioC_shared_pool_company_with_company_affinity()
    {
        var servers = new HashSet<string>();
        foreach (var user in new[] { "manager", "user1" })
        {
            using var client = _env.CreateClient();
            await client.LoginOrThrowAsync("XYZ", user);
            for (var i = 0; i < 3; i++) servers.Add((await client.WhoAmIAsync()).Server);
        }
        // Pool A uses per-company affinity: every XYZ user lands on the same pool member.
        var only = Assert.Single(servers);
        Assert.Contains(only, new[] { "APP-SERVER-03", "APP-SERVER-04", "APP-SERVER-05" });
    }

    [Fact]
    public async Task ScenarioF_identical_usernames_in_two_companies_stay_independent()
    {
        using var abc = _env.CreateClient();
        using var xyz = _env.CreateClient();
        await abc.LoginOrThrowAsync("ABC", "manager");
        await xyz.LoginOrThrowAsync("XYZ", "manager");
        var a = await abc.WhoAmIAsync();
        var x = await xyz.WhoAmIAsync();
        Assert.Equal("APP-SERVER-02", a.Server);
        Assert.NotEqual("APP-SERVER-02", x.Server);
        Assert.NotEqual(a.UserId, x.UserId);
        Assert.Equal("ABC", a.CompanyCode);
        Assert.Equal("XYZ", x.CompanyCode);
    }

    [Fact]
    public async Task Login_failures_are_indistinguishable()
    {
        using var client = _env.CreateClient();
        var responses = new[]
        {
            await client.LoginAsync("NO-SUCH-COMPANY", "user1"),
            await client.LoginAsync("ABC", "no-such-user"),
            await client.LoginAsync("ABC", "user1", "wrong-password"),
            await client.LoginAsync("ABC", "former.employee"),
        };
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
        Assert.Single(bodies.Distinct());
    }

    [Fact]
    public async Task ScenarioH_tampered_cookie_is_rejected()
    {
        using var client = _env.CreateClient();
        await client.LoginOrThrowAsync("ABC", "user1");
        var cookie = client.SessionCookie!;
        var tampered = cookie[..^4] + (cookie[^4] == 'A' ? "B" : "A") + cookie[^3..];

        using var attacker = new HttpClient { BaseAddress = new Uri(_env.GatewayUrl) };
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        request.Headers.Add("Cookie", $"Wings.Session={tampered}");
        var response = await attacker.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ScenarioH_spoofed_identity_and_forwarding_headers_are_replaced()
    {
        using var client = _env.CreateClient();
        await client.LoginOrThrowAsync("XYZ", "manager");
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        request.Headers.Add("X-Wings-Company-Id", SampleIds.AbcMotors.ToString());
        request.Headers.Add("X-Wings-User-Id", SampleIds.AbcUser1.ToString());
        request.Headers.Add("X-Company-Id", SampleIds.AbcMotors.ToString());
        request.Headers.Add("X-User-Id", "admin");
        request.Headers.Add("X-Forwarded-For", "10.9.9.9");
        request.Headers.Add("X-Forwarded-Host", "app-server-01.internal");
        var response = await client.Http.SendAsync(request);
        var who = JsonSerializer.Deserialize<WhoAmI>(await response.Content.ReadAsStringAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(SampleIds.XyzMotors.ToString(), who.CompanyId);
        Assert.Equal(SampleIds.XyzManager.ToString(), who.UserId);
        Assert.True(who.SignatureVerified);
        Assert.Empty(who.SuspiciousHeaders);
        Assert.False(who.ForwardedFor!.Contains("10.9.9.9"), $"XFF={who.ForwardedFor} XFH={who.ForwardedHost}");
        Assert.DoesNotContain("app-server-01", who.ForwardedHost);
        Assert.DoesNotContain("Wings.Session", who.Cookies);
    }

    [Fact]
    public async Task Direct_backend_access_without_gateway_signature_is_refused()
    {
        using var http = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get, _env.Backends[SampleIds.Server01].Url + "/api/whoami");
        request.Headers.Add("X-Wings-Company-Id", SampleIds.AbcMotors.ToString());
        request.Headers.Add("X-Wings-User-Id", SampleIds.AbcUser1.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_requests_are_challenged()
    {
        using var client = _env.CreateClient();
        var api = await client.Http.GetAsync("/api/whoami");
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);

        var page = new HttpRequestMessage(HttpMethod.Get, "/orders?id=5");
        page.Headers.Accept.ParseAdd("text/html");
        var redirect = await client.Http.SendAsync(page);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("/_wings/login?returnUrl=%2Forders%3Fid%3D5", redirect.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Unknown_host_is_not_routed()
    {
        using var client = _env.CreateClient();
        await client.LoginOrThrowAsync("ABC", "user1");
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        request.Headers.Host = "attacker.example";
        Assert.Equal(HttpStatusCode.NotFound, (await client.Http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Return_url_cannot_redirect_off_site()
    {
        using var client = _env.CreateClient();
        var response = await client.LoginAsync("ABC", "user1", returnUrl: "//evil.example/steal");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("/", body.GetProperty("redirect").GetString());
    }

    [Fact]
    public async Task Logout_revokes_the_session_even_if_cookie_is_replayed()
    {
        using var client = _env.CreateClient();
        await client.LoginOrThrowAsync("ABC", "user2");
        var cookie = client.SessionCookie!;
        var csrf = await client.Http.GetFromJsonAsync<JsonElement>("/_wings/auth/csrf");
        var logout = new HttpRequestMessage(HttpMethod.Post, "/_wings/auth/logout");
        logout.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
        (await client.Http.SendAsync(logout)).EnsureSuccessStatusCode();

        using var replay = new HttpClient { BaseAddress = new Uri(_env.GatewayUrl) };
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        request.Headers.Add("Cookie", $"Wings.Session={cookie}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Login_requires_antiforgery_token()
    {
        using var client = _env.CreateClient();
        var response = await client.Http.PostAsJsonAsync("/_wings/auth/login",
            new { company = "ABC", username = "user1", password = WingsTestEnvironment.UserPassword });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Path_binding_routes_second_application_with_same_assignment()
    {
        using var client = _env.CreateClient();
        await client.LoginOrThrowAsync("ABC", "user1");
        var who = await client.WhoAmIAsync("/hr/api/whoami");
        Assert.Equal("APP-SERVER-01", who.Server);
        Assert.Equal("HR", who.Application);
    }

    [Fact]
    public async Task Environment_comes_from_host_binding()
    {
        using var client = _env.CreateClient();
        await client.LoginOrThrowAsync("XYZ", "manager");
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        request.Headers.Host = "staging.localhost:" + new Uri(_env.GatewayUrl).Port;
        request.Headers.Add("Cookie", $"Wings.Session={client.SessionCookie}");
        using var raw = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(_env.GatewayUrl) };
        var response = await raw.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        var who = JsonSerializer.Deserialize<WhoAmI>(await response.Content.ReadAsStringAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("STAGING-01", who.Server);
        Assert.Equal("Staging", who.Environment);
    }

    [Fact]
    public async Task Large_upload_and_download_stream_through()
    {
        using var client = _env.CreateClient();
        await client.LoginOrThrowAsync("ABC", "user1");
        var payload = RandomNumberGenerator.GetBytes(24 * 1024 * 1024);
        var upload = await client.Http.PostAsync("/api/upload", new ByteArrayContent(payload));
        var result = await upload.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(payload.Length, result.GetProperty("bytes").GetInt64());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)), result.GetProperty("sha256").GetString());
        Assert.Equal("APP-SERVER-01", result.GetProperty("server").GetString());

        var download = await client.Http.GetAsync("/api/download?size=10485760", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(10_485_760, (await download.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task Websocket_connections_are_proxied_to_the_assigned_server()
    {
        using var client = _env.CreateClient();
        await client.LoginOrThrowAsync("ABC", "user2");
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Cookie", $"Wings.Session={client.SessionCookie}");
        await ws.ConnectAsync(new Uri(_env.GatewayUrl.Replace("http", "ws") + "/ws/echo"), CancellationToken.None);
        await ws.SendAsync(Encoding.UTF8.GetBytes("hello"), WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[256];
        var received = await ws.ReceiveAsync(buffer, CancellationToken.None);
        Assert.Equal("APP-SERVER-02:hello", Encoding.UTF8.GetString(buffer, 0, received.Count));
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Response_headers_hide_backend_details()
    {
        using var client = _env.CreateClient();
        await client.LoginOrThrowAsync("ABC", "user1");
        var cookie = await client.Http.GetAsync("/api/set-cookie");
        Assert.False(cookie.Headers.Contains("Server"));
        Assert.False(cookie.Headers.Contains("X-Powered-By"));
        var setCookie = Assert.Single(cookie.Headers.GetValues("Set-Cookie"));
        Assert.DoesNotContain("domain=", setCookie, StringComparison.OrdinalIgnoreCase);

        var redirect = await client.Http.GetAsync("/api/internal-redirect");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.StartsWith(_env.GatewayUrl, redirect.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Routing_decisions_are_logged_with_config_version()
    {
        using var client = _env.CreateClient();
        await client.LoginOrThrowAsync("ABC", "user1");
        await client.WhoAmIAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        RoutingDecisionLog? log = null;
        while (log is null && DateTime.UtcNow < deadline)
        {
            log = await _env.WithDbAsync(db => db.RoutingDecisionLogs.AsNoTracking()
                .FirstOrDefaultAsync(d => d.UserId == SampleIds.AbcUser1 && d.EventKind == "NewSession"));
            if (log is null) await Task.Delay(200);
        }
        Assert.NotNull(log);
        Assert.Equal(SampleIds.Server01, log!.ServerId);
        Assert.Equal(RoutingSource.UserAssignment, log.Source);
        Assert.True(log.ConfigVersion > 0);
    }

    [Fact]
    public async Task Readiness_and_liveness_endpoints()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_env.GatewayUrl) };
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/_wings/health/live")).StatusCode);
        var ready = await http.GetFromJsonAsync<JsonElement>("/_wings/health/ready");
        Assert.Equal("ready", ready.GetProperty("status").GetString());
    }
}
