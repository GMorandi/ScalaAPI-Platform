namespace ScalaAPI.Grains.Interfaces;

public static class ApiKeyScopes
{
    public const string Wildcard = "*";

    public static readonly string[] All =
    [
        "messages", "chat_completions", "responses", "responses_subpath",
        "count_tokens", "models", "search", "embeddings", "images_sync",
        "images_async", "images_batch", "videos", "realtime", "gemini_models",
        "gemini_generate", "antigravity",
    ];

    public static string[] Normalize(IEnumerable<string>? scopes)
    {
        var values = (scopes ?? [Wildcard])
            .Select(scope => scope.Trim().ToLowerInvariant())
            .Where(scope => scope.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (values.Length == 0) return [Wildcard];
        if (values.Contains(Wildcard, StringComparer.Ordinal)) return [Wildcard];

        var invalid = values.FirstOrDefault(scope => !All.Contains(scope, StringComparer.Ordinal));
        if (invalid is not null)
            throw new ArgumentException($"Unknown API key scope '{invalid}'", nameof(scopes));
        return values;
    }

    public static bool Allows(IEnumerable<string>? scopes, string capability)
    {
        var normalized = Normalize(scopes);
        return normalized.Contains(Wildcard, StringComparer.Ordinal)
            || normalized.Contains(capability.Trim().ToLowerInvariant(), StringComparer.Ordinal);
    }
}
