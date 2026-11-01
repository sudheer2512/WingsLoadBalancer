using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using OtpNet;

namespace Wings.LoadBalancer.Infrastructure.Security;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string? hash);

    /// <summary>Burns the same CPU as a real verification, so unknown accounts are not distinguishable by timing.</summary>
    void VerifyDummy(string password);
}

/// <summary>PBKDF2-HMAC-SHA512. Format: <c>pbkdf2-sha512$iterations$salt$hash</c> (base64).</summary>
public sealed class Pbkdf2PasswordHasher(int iterations = 210_000) : IPasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private readonly string _dummyHash = Create("dummy-password-for-timing", iterations);

    public string Hash(string password) => Create(password, iterations);

    public bool Verify(string password, string? hash)
    {
        if (string.IsNullOrEmpty(hash)) return false;
        var parts = hash.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha512" || !int.TryParse(parts[1], out var iter)) return false;
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iter, HashAlgorithmName.SHA512, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public void VerifyDummy(string password) => Verify(password, _dummyHash);

    private static string Create(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, HashSize);
        return $"pbkdf2-sha512${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }
}

public interface ITotpService
{
    /// <summary>Creates a new secret; returns the protected value to store and the provisioning URI to show once.</summary>
    (string ProtectedSecret, string ProvisioningUri) CreateSecret(string issuer, string account);
    bool Verify(string? protectedSecret, string? code);
}

public sealed class TotpService(IDataProtectionProvider dataProtection, TimeProvider time) : ITotpService
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("Wings.Mfa.Totp.v1");

    public (string ProtectedSecret, string ProvisioningUri) CreateSecret(string issuer, string account)
    {
        var secret = KeyGeneration.GenerateRandomKey(20);
        var base32 = Base32Encoding.ToString(secret);
        var uri = new OtpUri(OtpType.Totp, base32, account, issuer).ToString();
        return (_protector.Protect(base32), uri);
    }

    public bool Verify(string? protectedSecret, string? code)
    {
        if (string.IsNullOrEmpty(protectedSecret) || string.IsNullOrWhiteSpace(code)) return false;
        string base32;
        try
        {
            base32 = _protector.Unprotect(protectedSecret);
        }
        catch (CryptographicException)
        {
            return false;
        }
        var totp = new Totp(Base32Encoding.ToBytes(base32));
        return totp.VerifyTotp(time.GetUtcNow().UtcDateTime, code.Trim(), out _, new VerificationWindow(1, 1));
    }
}
