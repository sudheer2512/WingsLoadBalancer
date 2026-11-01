using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Wings.Admin.Api.Services;
using Wings.Gateway.Proxy;
using Wings.LoadBalancer.Infrastructure.Monitoring;
using Wings.LoadBalancer.Infrastructure.Security;
using Wings.MockBackend;
using Wings.Routing.Model;

namespace Wings.IntegrationTests;

/// <summary>Fast tests for security building blocks (no servers started).</summary>
public sealed class SecurityPrimitiveTests
{
    private static DestinationValidator Validator(Action<DestinationPolicyOptions>? configure = null)
    {
        var options = new DestinationPolicyOptions
        {
            AllowedBackendHosts = ["*.wings.internal", "app-server-*"],
            AllowedBackendNetworks = ["10.20.0.0/16"],
            AllowedRedirectHosts = ["*.erp.example.com"]
        };
        configure?.Invoke(options);
        return new DestinationValidator(new StaticOptionsMonitor<DestinationPolicyOptions>(options));
    }

    [Theory]
    [InlineData("https://app-01.wings.internal", true)]
    [InlineData("https://app-server-07:8443/base", true)]
    [InlineData("https://10.20.4.5", true)]
    [InlineData("https://10.21.4.5", false)]
    [InlineData("http://app-01.wings.internal", false)]
    [InlineData("https://169.254.169.254/latest/meta-data", false)]
    [InlineData("https://localhost", false)]
    [InlineData("https://127.0.0.1", false)]
    [InlineData("https://user:pass@app-01.wings.internal", false)]
    [InlineData("https://evil.example.com", false)]
    [InlineData("https://wings.internal.evil.com", false)]
    [InlineData("not a url", false)]
    public void Backend_urls_must_match_the_allow_list(string url, bool valid) =>
        Assert.Equal(valid, Validator().ValidateBackendUrl(url).IsValid);

    [Fact]
    public void Health_url_must_be_on_the_backend_host()
    {
        var v = Validator();
        Assert.True(v.ValidateBackendUrl("https://app-01.wings.internal/health/ready", "https://app-01.wings.internal").IsValid);
        Assert.False(v.ValidateBackendUrl("https://app-02.wings.internal/health/ready", "https://app-01.wings.internal").IsValid);
    }

    [Fact]
    public void Connect_time_address_check_blocks_metadata_and_loopback()
    {
        var policy = new DestinationPolicyOptions();
        Assert.False(DestinationValidator.IsAddressPermitted(IPAddress.Parse("169.254.169.254"), policy));
        Assert.False(DestinationValidator.IsAddressPermitted(IPAddress.Loopback, policy));
        Assert.False(DestinationValidator.IsAddressPermitted(IPAddress.Parse("0.0.0.0"), policy));
        Assert.True(DestinationValidator.IsAddressPermitted(IPAddress.Parse("10.20.1.1"), policy));
        Assert.True(DestinationValidator.IsAddressPermitted(IPAddress.Loopback, new DestinationPolicyOptions { AllowLoopback = true }));
    }

    [Theory]
    [InlineData("https://abc.erp.example.com/app", true)]
    [InlineData("http://abc.erp.example.com/app", false)]
    [InlineData("https://evil.com", false)]
    public void Redirect_targets_are_allow_listed(string url, bool valid) =>
        Assert.Equal(valid, Validator().ValidateRedirectUrl(url).IsValid);

    [Theory]
    [InlineData(null, "/")]
    [InlineData("/orders?id=1", "/orders?id=1")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData("/_wings/auth/logout", "/")]
    public void Login_return_url_must_be_local(string? input, string expected) =>
        Assert.Equal(expected, GatewayResponses.SafeReturnUrl(input));

    [Fact]
    public void Gateway_signature_verifies_with_the_backend_reference_implementation()
    {
        var master = Convert.FromBase64String("dGVzdC1tYXN0ZXIta2V5LWZvci1pbnRlZ3JhdGlvbi10ZXN0cy0zMg==");
        var serverId = Guid.NewGuid();
        var keys = new BackendKeyProvider(Options.Create(new BackendTrustOptions { MasterKey = Convert.ToBase64String(master) }));

        // Both sides derive the same per-server keys.
        Assert.Equal(GatewayIdentity.DeriveSigningKey(master, serverId), keys.GetSigningKey(serverId));
        Assert.Equal(GatewayIdentity.DeriveHealthKey(master, serverId), keys.GetHealthKey(serverId));
        Assert.NotEqual(keys.GetSigningKey(serverId), keys.GetSigningKey(Guid.NewGuid()));

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/orders";
        context.Request.QueryString = new QueryString("?page=2");
        var h = context.Request.Headers;
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        h[WingsHeaders.CompanyId] = "c1";
        h[WingsHeaders.UserId] = "u1";
        h[WingsHeaders.Session] = "s1";
        h[WingsHeaders.Application] = "ERP";
        h[WingsHeaders.Environment] = "Production";
        h[WingsHeaders.RequestId] = "r1";
        h[WingsHeaders.Timestamp] = ts;
        h[WingsHeaders.CompanyCode] = "ABC";
        h[WingsHeaders.Username] = "user1";
        var canonical = IdentitySignature.Canonical("c1", "u1", "s1", "ERP", "Production", "r1", ts, "GET", "/api/orders?page=2");
        h[WingsHeaders.Signature] = IdentitySignature.Sign(keys.GetSigningKey(serverId), canonical);

        Assert.NotNull(GatewayIdentity.Verify(context.Request, keys.GetSigningKey(serverId), TimeSpan.FromMinutes(2)));
        Assert.Null(GatewayIdentity.Verify(context.Request, keys.GetSigningKey(Guid.NewGuid()), TimeSpan.FromMinutes(2)));

        h[WingsHeaders.CompanyId] = "c2"; // tamper
        Assert.Null(GatewayIdentity.Verify(context.Request, keys.GetSigningKey(serverId), TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void Stale_signature_is_rejected()
    {
        var key = new byte[32];
        var old = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds().ToString();
        var canonical = IdentitySignature.Canonical("c", "u", "s", "a", "e", "r", old, "GET", "/");
        Assert.False(IdentitySignature.Verify(key, canonical, IdentitySignature.Sign(key, canonical), old, TimeSpan.FromMinutes(2), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Password_hashes_verify_and_resist_tampering()
    {
        var hasher = new Pbkdf2PasswordHasher(1000);
        var hash = hasher.Hash("correct horse battery staple");
        Assert.True(hasher.Verify("correct horse battery staple", hash));
        Assert.False(hasher.Verify("Correct horse battery staple", hash));
        Assert.False(hasher.Verify("x", hash.Replace("pbkdf2-sha512", "md5")));
        Assert.False(hasher.Verify("x", null));
        Assert.NotEqual(hash, hasher.Hash("correct horse battery staple")); // salted
    }

    [Fact]
    public void Set_cookie_domain_is_removed()
    {
        Assert.Equal("a=b; Path=/; HttpOnly", WingsTransformer.StripCookieDomain("a=b; Domain=app-server-01.internal; Path=/; HttpOnly"));
    }

    [Fact]
    public void Exports_neutralise_spreadsheet_formulas()
    {
        var table = new ReportTable("t", "T", ["Name", "Value"], [["=HYPERLINK(\"http://x\")", -5], ["plain", 3]]);
        var csv = Encoding.UTF8.GetString(ReportService.ToCsv(table));
        Assert.Contains("\"'=HYPERLINK(\"\"http://x\"\")\",-5", csv);
    }

    [Fact]
    public void Csv_parser_handles_quotes_commas_and_newlines()
    {
        var rows = BulkImportService.ParseCsv("﻿Company,Reason\nABC,\"Moved, finally\"\nXYZ,\"Line1\nLine2\"\n");
        Assert.Equal(["Company", "Reason"], rows[0]);
        Assert.Equal("Moved, finally", rows[1][1]);
        Assert.Equal("Line1\nLine2", rows[2][1]);
    }

    [Fact]
    public void Configuration_validator_flags_shared_dedicated_server_and_environment_mismatch()
    {
        var server = Guid.NewGuid();
        var staging = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var since = DateTimeOffset.UtcNow.AddDays(-1);
        var snapshot = new RoutingSnapshot(new RoutingConfiguration
        {
            Version = 1,
            Companies =
            [
                new CompanyInfo(a, "A", "A", CompanyStatus.Active, CompanyRoutingMode.Dedicated, null, null, null),
                new CompanyInfo(b, "B", "B", CompanyStatus.Active, CompanyRoutingMode.Dedicated, null, null, null)
            ],
            Servers =
            [
                new ServerInfo { Id = server, Name = "S1", BaseUrl = "https://s1", Type = ServerType.Dedicated },
                new ServerInfo { Id = staging, Name = "ST", BaseUrl = "https://st", Type = ServerType.Shared, Environment = DeploymentEnvironment.Staging }
            ],
            CompanyAssignments =
            [
                new CompanyAssignmentInfo(Guid.NewGuid(), a, null, DeploymentEnvironment.Production, server, null, null, ClusterMode.PrimaryPreferred, null, true, since, null),
                new CompanyAssignmentInfo(Guid.NewGuid(), b, null, DeploymentEnvironment.Production, server, staging, null, ClusterMode.PrimaryPreferred, null, true, since, null)
            ]
        }, DateTimeOffset.UtcNow);
        var issues = ConfigurationValidator.Validate(snapshot, DateTimeOffset.UtcNow);
        Assert.Contains(issues, i => i.Code == "DedicatedServerShared");
        Assert.Contains(issues, i => i.Code == "EnvironmentMismatch");
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
