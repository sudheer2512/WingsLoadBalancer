namespace Wings.Gateway;

public sealed class GatewayOptions
{
    public const string Section = "Gateway";

    /// <summary>Logical instance name (Gateway-01, Gateway-02 …). Defaults to the machine name.</summary>
    public string? InstanceName { get; set; }

    /// <summary>Session cookie. Keep the <c>__Host-</c> prefix in production (HTTPS, no Domain, Path=/).</summary>
    public string CookieName { get; set; } = "__Host-Wings.Session";
    public bool RequireHttpsCookies { get; set; } = true;
    public int SessionIdleMinutes { get; set; } = 30;
    public int SessionAbsoluteHours { get; set; } = 12;

    /// <summary>Default max request body (bytes) when an application binding does not set one.</summary>
    public long MaxRequestBodyBytes { get; set; } = 100 * 1024 * 1024;

    /// <summary>Retry a safe, body-less request once on another permitted server after a connect failure.</summary>
    public bool RetryIdempotentRequests { get; set; } = true;

    /// <summary>Extra inbound headers to strip before forwarding (all X-Wings-* are always stripped).</summary>
    public List<string> StripRequestHeaders { get; set; } =
    [
        "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Forwarded-Prefix", "Forwarded", "X-Real-IP",
        "X-Original-URL", "X-Original-Host", "X-Rewrite-URL", "X-Company-Id", "X-User-Id", "X-Tenant-Id",
        "X-Remote-User", "Remote-User", "X-Authenticated-User", "X-MS-CLIENT-PRINCIPAL", "X-MS-CLIENT-PRINCIPAL-ID",
        "X-MS-CLIENT-PRINCIPAL-NAME"
    ];

    /// <summary>Response headers removed to avoid exposing backend technology or topology.</summary>
    public List<string> StripResponseHeaders { get; set; } =
        ["Server", "X-Powered-By", "X-AspNet-Version", "X-AspNetMvc-Version", "X-SourceFiles", "X-Backend-Server"];

    /// <summary>Trusted reverse proxies / NLB addresses whose X-Forwarded-For is honoured.</summary>
    public List<string> KnownProxies { get; set; } = [];
    public List<string> KnownNetworks { get; set; } = [];

    public int LoginPermitsPerMinutePerIp { get; set; } = 20;
    public int ProxyPermitsPerMinutePerIp { get; set; } = 6000;

    /// <summary>Lifetime of the one-time token used by redirect-mode applications.</summary>
    public int HandoffTokenSeconds { get; set; } = 60;
}

public sealed class OidcOptions
{
    public const string Section = "Oidc";
    public string? Authority { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string SubjectClaim { get; set; } = "sub";
    public bool Enabled => !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(ClientId);
}
