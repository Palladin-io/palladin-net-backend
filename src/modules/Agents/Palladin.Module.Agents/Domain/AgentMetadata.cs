using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Palladin.Module.Agents.Domain;

internal static class AgentMetadata
{
    internal const int MaxDisplayNameLength = 64;
    internal const int MaxAgentTypeLength = 100;

    internal static bool TryNormalizeDisplayName(string? value, out string? normalized) =>
        TryNormalizeOptional(value, MaxDisplayNameLength, out normalized);

    internal static bool TryNormalizeType(string? value, out string? normalized) =>
        TryNormalizeOptional(value, MaxAgentTypeLength, out normalized);

    internal static bool TryNormalizeRequiredDisplayName(string? value, out string normalized)
    {
        if (TryNormalizeDisplayName(value, out var candidate) && candidate is not null)
        {
            normalized = candidate;
            return true;
        }

        normalized = string.Empty;
        return false;
    }

    internal static string DisplayNameReservationKey(string normalizedDisplayName)
    {
        var comparisonBytes = Encoding.UTF8.GetBytes(normalizedDisplayName.ToUpperInvariant());
        try
        {
            return Convert.ToHexString(SHA256.HashData(comparisonBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(comparisonBytes);
        }
    }

    private static bool TryNormalizeOptional(string? value, int maximumRunes, out string? normalized)
    {
        normalized = null;
        if (value is null)
        {
            return true;
        }

        var candidate = value.Trim().Normalize(NormalizationForm.FormC);
        if (candidate.Length == 0)
        {
            return true;
        }

        var runeCount = 0;
        foreach (var rune in candidate.EnumerateRunes())
        {
            runeCount++;
            if (runeCount > maximumRunes || IsForbidden(rune))
            {
                return false;
            }
        }

        normalized = candidate;
        return true;
    }

    private static bool IsForbidden(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.Surrogate;
    }
}
