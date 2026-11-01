using System.Globalization;
using System.Text;

namespace Wings.Routing;

/// <summary>
/// One normalisation rule for company codes, aliases and usernames everywhere (login, admin, import, cache keys):
/// Unicode NFKC, trimmed, internal whitespace collapsed, upper-cased with the invariant culture.
/// </summary>
public static class IdentifierNormalizer
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim();
        var builder = new StringBuilder(normalized.Length);
        var previousWasSpace = false;
        foreach (var ch in normalized)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!previousWasSpace) builder.Append(' ');
                previousWasSpace = true;
                continue;
            }
            previousWasSpace = false;
            builder.Append(char.ToUpper(ch, CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    /// <summary>Lower-cased host without port or trailing dot.</summary>
    public static string NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return string.Empty;
        var value = host.Trim().ToLowerInvariant().TrimEnd('.');
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            return close > 0 ? value[..(close + 1)] : value;
        }
        var colon = value.LastIndexOf(':');
        return colon > 0 && value.IndexOf(':') == colon ? value[..colon] : value;
    }
}
