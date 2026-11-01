using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Wings.MockBackend;

/// <summary>
/// Reference verifier for the identity headers the Wings gateway injects. Copy this into a Wings application server
/// (or reference it) and trust X-Wings-* headers only when <see cref="Verify"/> succeeds.
/// Keys are derived from the gateway master key with HKDF-SHA256 using the server id as salt.
/// </summary>
public sealed class GatewayIdentity
{
    public required string CompanyId { get; init; }
    public required string CompanyCode { get; init; }
    public required string UserId { get; init; }
    public required string Username { get; init; }
    public required string Session { get; init; }
    public required string Application { get; init; }
    public required string Environment { get; init; }
    public required string RequestId { get; init; }

    public static byte[] DeriveSigningKey(byte[] masterKey, Guid serverId) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, serverId.ToByteArray(), "wings-backend-signing-v1"u8.ToArray());

    public static string DeriveHealthKey(byte[] masterKey, Guid serverId) =>
        Convert.ToBase64String(HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 24, serverId.ToByteArray(),
            "wings-backend-health-v1"u8.ToArray()));

    /// <summary>Returns the verified identity, or null when headers are missing, stale or the signature is wrong.</summary>
    public static GatewayIdentity? Verify(HttpRequest request, byte[] signingKey, TimeSpan lifetime)
    {
        string H(string name) => request.Headers[name].ToString();
        var timestamp = H("X-Wings-Timestamp");
        var signature = H("X-Wings-Signature");
        if (signature.Length == 0 || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var unix))
            return null;
        if ((DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unix)).Duration() > lifetime) return null;

        var canonical = string.Join('\n', H("X-Wings-Company-Id"), H("X-Wings-User-Id"), H("X-Wings-Session"),
            H("X-Wings-Application"), H("X-Wings-Environment"), H("X-Wings-Request-Id"), timestamp,
            request.Method.ToUpperInvariant(), request.Path + request.QueryString);
        var expected = Convert.ToBase64String(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(canonical)));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature)))
            return null;

        return new GatewayIdentity
        {
            CompanyId = H("X-Wings-Company-Id"),
            CompanyCode = H("X-Wings-Company-Code"),
            UserId = H("X-Wings-User-Id"),
            Username = Uri.UnescapeDataString(H("X-Wings-Username")),
            Session = H("X-Wings-Session"),
            Application = H("X-Wings-Application"),
            Environment = H("X-Wings-Environment"),
            RequestId = H("X-Wings-Request-Id")
        };
    }
}
