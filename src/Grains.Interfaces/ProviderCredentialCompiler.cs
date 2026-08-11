namespace ScalaAPI.Grains.Interfaces;

public sealed class ProviderCredentialContractException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// Converts encrypted-account semantic fields into the bounded header set sent
/// to Gateway. The input map is a product credential document, not an HTTP map.
/// </summary>
public static class ProviderCredentialCompiler
{
    public const string DefaultAnthropicVersion = "2023-06-01";

    private const int MaxMaterialLength = 4096;
    private const int MaxVersionLength = 32;
    private const int MaxBetaLength = 256;
    private const int MaxHeaders = 16;

    public static Dictionary<string, string> CompileStatic(
        string platform,
        string accountType,
        IReadOnlyDictionary<string, string> credentials)
    {
        if (credentials.Count > MaxHeaders)
            throw Invalid("provider_credentials_too_many");

        var normalizedPlatform = NormalizePlatform(platform);
        var normalizedType = accountType.Trim().ToLowerInvariant();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in credentials)
        {
            if (!IsCredentialKey(entry.Key) || !IsMaterial(entry.Value))
                throw Invalid("provider_credential_invalid");
            if (!values.TryAdd(entry.Key, entry.Value))
                throw Invalid("provider_credential_duplicate");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var apiKey = Take(values, "api_key");
        var isOAuth = normalizedType == "oauth";
        if (normalizedPlatform is "anthropic" or "claude")
        {
            if (apiKey is null && !isOAuth)
                throw Invalid("provider_api_key_missing");
            if (apiKey is not null)
                AddHeader(headers, "x-api-key", apiKey);
            AddOptionalHeader(headers, "anthropic-version",
                Take(values, "anthropic_version") ?? DefaultAnthropicVersion,
                MaxVersionLength);
            AddOptionalHeader(headers, "anthropic-beta", Take(values, "anthropic_beta"),
                MaxBetaLength);
        }
        else if (normalizedPlatform is "gemini" or "google")
        {
            if (apiKey is null && !isOAuth)
                throw Invalid("provider_api_key_missing");
            if (apiKey is not null)
                AddHeader(headers, "x-goog-api-key", apiKey);
        }
        else if (apiKey is not null)
        {
            AddHeader(headers, "Authorization", $"Bearer {apiKey}");
        }

        foreach (var entry in values)
        {
            if (entry.Key is "api_key" or "anthropic_version" or "anthropic_beta")
                continue;
            var key = entry.Key.Equals("provider_scenario", StringComparison.OrdinalIgnoreCase)
                ? "X-Provider-Scenario" : entry.Key;
            AddHeader(headers, key, entry.Value);
        }
        if (headers.Count > MaxHeaders)
            throw Invalid("provider_credentials_too_many");
        return headers;
    }

    public static string NormalizePlatform(string platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
            throw Invalid("provider_platform_missing");
        return platform.Trim().ToLowerInvariant();
    }

    private static string? Take(Dictionary<string, string> values, string key)
    {
        if (!values.Remove(key, out var value)) return null;
        return value;
    }

    private static void AddOptionalHeader(Dictionary<string, string> headers,
        string key, string? value, int maxLength)
    {
        if (value is null) return;
        if (value.Length > maxLength || value.IndexOfAny(['\r', '\n']) >= 0)
            throw Invalid("provider_credential_invalid");
        AddHeader(headers, key, value);
    }

    private static void AddHeader(Dictionary<string, string> headers, string key, string value)
    {
        if (!headers.TryAdd(key, value))
            throw Invalid("provider_credential_header_collision");
    }

    private static bool IsMaterial(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaxMaterialLength
        && value.IndexOfAny(['\r', '\n']) < 0;

    private static bool IsCredentialKey(string key) =>
        !string.IsNullOrWhiteSpace(key)
        && key.Length <= 64
        && key.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    private static ProviderCredentialContractException Invalid(string code) =>
        new(code);
}
