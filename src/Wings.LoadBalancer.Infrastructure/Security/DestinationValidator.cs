using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Wings.LoadBalancer.Infrastructure.Security;

public sealed class DestinationPolicyOptions
{
    public const string Section = "DestinationPolicy";

    /// <summary>Host patterns allowed for backend and health URLs, e.g. <c>*.internal</c>, <c>app-server-*.corp.local</c>.</summary>
    public List<string> AllowedBackendHosts { get; set; } = [];

    /// <summary>CIDR ranges allowed for IP-literal backends, e.g. <c>10.20.0.0/16</c>.</summary>
    public List<string> AllowedBackendNetworks { get; set; } = [];

    /// <summary>Host patterns allowed for redirect-mode public URLs.</summary>
    public List<string> AllowedRedirectHosts { get; set; } = [];

    /// <summary>Permit http:// backends (development only).</summary>
    public bool AllowInsecureBackends { get; set; }

    /// <summary>Permit loopback backends (development and tests only).</summary>
    public bool AllowLoopback { get; set; }
}

public interface IDestinationValidator
{
    /// <summary>Validates an administrator-supplied backend or health URL (SSRF protection).</summary>
    ValidationResult ValidateBackendUrl(string? url, string? mustMatchHostOf = null);

    /// <summary>Validates a redirect target (open-redirect protection).</summary>
    ValidationResult ValidateRedirectUrl(string? url);
}

public readonly record struct ValidationResult(bool IsValid, string? Error)
{
    public static ValidationResult Ok => new(true, null);
    public static ValidationResult Fail(string error) => new(false, error);
}

public sealed class DestinationValidator(IOptionsMonitor<DestinationPolicyOptions> options) : IDestinationValidator
{
    public ValidationResult ValidateBackendUrl(string? url, string? mustMatchHostOf = null)
    {
        var policy = options.CurrentValue;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return ValidationResult.Fail("URL must be absolute");
        if (uri.Scheme != Uri.UriSchemeHttps && !(policy.AllowInsecureBackends && uri.Scheme == Uri.UriSchemeHttp))
            return ValidationResult.Fail("Backend URLs must use https");
        if (!string.IsNullOrEmpty(uri.UserInfo)) return ValidationResult.Fail("Credentials in URLs are not allowed");
        if (!string.IsNullOrEmpty(uri.Fragment)) return ValidationResult.Fail("Fragments are not allowed");

        if (mustMatchHostOf is not null && Uri.TryCreate(mustMatchHostOf, UriKind.Absolute, out var other)
            && !string.Equals(other.Host, uri.Host, StringComparison.OrdinalIgnoreCase))
            return ValidationResult.Fail("Health check URL must use the same host as the backend URL");

        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip))
        {
            if (IsAlwaysBlocked(ip)) return ValidationResult.Fail("Link-local, metadata and unspecified addresses are blocked");
            if (IPAddress.IsLoopback(ip)) return policy.AllowLoopback ? ValidationResult.Ok : ValidationResult.Fail("Loopback is not allowed");
            return policy.AllowedBackendNetworks.Any(cidr => InNetwork(ip, cidr))
                ? ValidationResult.Ok
                : ValidationResult.Fail("IP address is outside the allowed backend networks");
        }

        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return policy.AllowLoopback ? ValidationResult.Ok : ValidationResult.Fail("Loopback is not allowed");

        return policy.AllowedBackendHosts.Any(p => HostMatches(uri.Host, p))
            ? ValidationResult.Ok
            : ValidationResult.Fail($"Host '{uri.Host}' is not in the allowed backend host list");
    }

    public ValidationResult ValidateRedirectUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return ValidationResult.Fail("URL must be absolute");
        if (uri.Scheme != Uri.UriSchemeHttps) return ValidationResult.Fail("Redirect targets must use https");
        if (!string.IsNullOrEmpty(uri.UserInfo)) return ValidationResult.Fail("Credentials in URLs are not allowed");
        return options.CurrentValue.AllowedRedirectHosts.Any(p => HostMatches(uri.Host, p))
            ? ValidationResult.Ok
            : ValidationResult.Fail($"Host '{uri.Host}' is not an allowed redirect target");
    }

    /// <summary>Resolves a host at connection time and re-checks the addresses (DNS rebinding protection).</summary>
    public static bool IsAddressPermitted(IPAddress address, DestinationPolicyOptions policy) =>
        !IsAlwaysBlocked(address) && (!IPAddress.IsLoopback(address) || policy.AllowLoopback);

    internal static bool HostMatches(string host, string pattern)
    {
        pattern = pattern.Trim();
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
            return host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase) && host.Length > pattern.Length - 1;
        if (pattern.Contains('*'))
        {
            var parts = pattern.Split('*');
            return host.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase)
                   && host.EndsWith(parts[^1], StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAlwaysBlocked(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 169 && b[1] == 254 // link-local, cloud metadata
                   || b[0] == 0
                   || b[0] >= 224; // multicast / reserved
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6Multicast;
    }

    internal static bool InNetwork(IPAddress ip, string cidr)
    {
        var slash = cidr.IndexOf('/');
        if (slash < 0 || !IPAddress.TryParse(cidr[..slash], out var network) || !int.TryParse(cidr[(slash + 1)..], out var bits))
            return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (network.AddressFamily != ip.AddressFamily) return false;
        var a = ip.GetAddressBytes();
        var n = network.GetAddressBytes();
        for (var i = 0; i < a.Length && bits > 0; i++, bits -= 8)
        {
            var mask = bits >= 8 ? 0xFF : (byte)(0xFF << (8 - bits));
            if ((a[i] & mask) != (n[i] & mask)) return false;
        }
        return true;
    }
}
