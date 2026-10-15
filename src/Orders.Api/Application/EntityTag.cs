using System.Globalization;

namespace Orders.Api.Application;

/// <summary>Strong ETags carrying the order's row version.</summary>
internal static class EntityTag
{
    public static string Format(uint version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>
    /// Parses a single strong entity tag. Weak tags, lists and "*" are rejected:
    /// a transition must name the exact version the client read.
    /// </summary>
    public static bool TryParse(string? value, out uint version)
    {
        version = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length < 3 || trimmed[0] != '"' || trimmed[^1] != '"')
        {
            return false;
        }

        return uint.TryParse(trimmed.AsSpan(1, trimmed.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }
}
