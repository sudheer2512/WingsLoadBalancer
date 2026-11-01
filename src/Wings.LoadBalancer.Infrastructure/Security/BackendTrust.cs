using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Wings.LoadBalancer.Infrastructure.Security;

public sealed class BackendTrustOptions
{
    public const string Section = "BackendTrust";

    /// <summary>
    /// Base64 master key (≥ 32 bytes) from a secret store (environment variable, user secrets, Key Vault, DPAPI-protected
    /// config). Per-server signing and health keys are derived from it, so no backend secrets are stored in the database.
    /// </summary>
    public string? MasterKey { get; set; }

    /// <summary>Maximum clock skew accepted by backends when verifying the identity signature.</summary>
    public int SignatureLifetimeSeconds { get; set; } = 120;
}

/// <summary>Well-known identity headers. Inbound copies are always stripped; only the gateway sets them.</summary>
public static class WingsHeaders
{
    public const string Prefix = "X-Wings-";
    public const string CompanyId = "X-Wings-Company-Id";
    public const string CompanyCode = "X-Wings-Company-Code";
    public const string UserId = "X-Wings-User-Id";
    public const string Username = "X-Wings-Username";
    public const string Session = "X-Wings-Session";
    public const string Application = "X-Wings-Application";
    public const string Environment = "X-Wings-Environment";
    public const string RequestId = "X-Wings-Request-Id";
    public const string Timestamp = "X-Wings-Timestamp";
    public const string Signature = "X-Wings-Signature";
    public const string HealthKey = "X-Wings-Health-Key";
    public const string ServerId = "X-Wings-Server-Id";
}

public interface IBackendKeyProvider
{
    byte[] GetSigningKey(Guid serverId);
    string GetHealthKey(Guid serverId);
}

/// <summary>HKDF-SHA256 derivation of per-server keys from the master key.</summary>
public sealed class BackendKeyProvider : IBackendKeyProvider
{
    private readonly byte[] _master;

    public BackendKeyProvider(IOptions<BackendTrustOptions> options)
    {
        var configured = options.Value.MasterKey;
        if (string.IsNullOrWhiteSpace(configured))
        {
            // Development fallback only: random per process means backends cannot verify signatures.
            _master = RandomNumberGenerator.GetBytes(32);
            IsEphemeral = true;
        }
        else
        {
            _master = Convert.FromBase64String(configured);
            if (_master.Length < 32) throw new InvalidOperationException("BackendTrust:MasterKey must be at least 32 bytes");
        }
    }

    public bool IsEphemeral { get; }

    public byte[] GetSigningKey(Guid serverId) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _master, 32, serverId.ToByteArray(), "wings-backend-signing-v1"u8.ToArray());

    public string GetHealthKey(Guid serverId) =>
        Convert.ToBase64String(HKDF.DeriveKey(HashAlgorithmName.SHA256, _master, 24, serverId.ToByteArray(),
            "wings-backend-health-v1"u8.ToArray()));
}

/// <summary>
/// Canonical signature over the identity headers. Backends recompute it with their key (see
/// samples/Wings.MockBackend/GatewayIdentity.cs for a reference verifier).
/// </summary>
public static class IdentitySignature
{
    public static string Canonical(string companyId, string userId, string session, string application,
        string environment, string requestId, string timestamp, string method, string pathAndQuery) =>
        string.Join('\n', companyId, userId, session, application, environment, requestId, timestamp,
            method.ToUpperInvariant(), pathAndQuery);

    public static string Sign(byte[] key, string canonical) =>
        Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical)));

    public static bool Verify(byte[] key, string canonical, string? signature, string timestamp, TimeSpan lifetime,
        DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(signature)) return false;
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var unix)) return false;
        if ((now - DateTimeOffset.FromUnixTimeSeconds(unix)).Duration() > lifetime) return false;
        var expected = Encoding.ASCII.GetBytes(Sign(key, canonical));
        var actual = Encoding.ASCII.GetBytes(signature);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
