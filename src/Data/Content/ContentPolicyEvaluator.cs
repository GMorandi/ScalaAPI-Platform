using System.Globalization;
using System.Text;

namespace ScalaAPI.Data.Content;

/// <summary>
/// Versioned, source-owned normalization used by every content-policy surface.
/// Compatibility normalization (NFKC), case folding, format-character removal,
/// and the bounded confusable map make rule matching deterministic without an
/// external service.
/// </summary>
public static class ContentPolicyEvaluator
{
    public const string Version = "unicode-confusable-v1";

    public static string Normalize(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var normalized = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format
                || rune.Value is 0x200B or 0x200C or 0x200D or 0xFEFF)
                continue;

            builder.Append(MapConfusable(rune));
        }

        return builder.ToString();
    }

    public static bool Contains(string content, string pattern)
    {
        var normalizedPattern = Normalize(pattern);
        return normalizedPattern.Length > 0
            && Normalize(content).Contains(normalizedPattern, StringComparison.Ordinal);
    }

    public static bool IsSupported(string version) =>
        string.Equals(version, Version, StringComparison.Ordinal);

    private static string MapConfusable(Rune rune) => rune.Value switch
    {
        // Cyrillic letters commonly substituted for Latin ASCII.
        0x0430 or 0x03B1 => "a", // a / alpha
        0x0435 or 0x03B5 => "e", // e / epsilon
        0x043E or 0x03BF => "o", // o / omicron
        0x0440 or 0x03C1 => "p", // p / rho
        0x0441 => "c",
        0x0445 or 0x03C7 => "x",
        0x0443 => "y",
        0x0455 => "s",
        0x03C4 => "t",
        0x03BD => "v",
        0x0456 => "i",
        0x04CF => "l",
        _ => rune.ToString(),
    };
}
