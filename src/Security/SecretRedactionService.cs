using System.Text.RegularExpressions;
using System.Text;

namespace ScalaAPI.Admin.Security;

/// <summary>
/// Recursively redacts secret patterns from strings, objects, and structured data.
/// Used to sanitize error messages, log entries, and metric payloads before they
/// reach any external surface (API response, log file, Cap'n Proto dump).
/// </summary>
public sealed partial class SecretRedactionService
{
    // Patterns that look like secrets in free-form text
    private static readonly (string Label, Regex Pattern)[] Patterns =
    [
        ("api_key", ApiKeyRegex()),
        ("bearer", BearerTokenRegex()),
        ("basic", BasicAuthRegex()),
        ("password_kv", PasswordKvRegex()),
        ("secret_kv", SecretKvRegex()),
    ];

    // JSON property names whose values should always be redacted
    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "secret", "token", "authorization", "api_key", "apikey",
        "access_token", "refresh_token", "private_key", "master_key",
        "connection_string", "connectionstring",
    };

    public string Redact(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var result = input;
        foreach (var (label, pattern) in Patterns)
        {
            result = pattern.Replace(result, $"[{label}:redacted]");
        }
        return result;
    }

    /// <summary>
    /// Recursively redact a JSON string, replacing values of sensitive properties.
    /// </summary>
    public string RedactJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            using var ms = new MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(ms))
            {
                WriteRedacted(doc.RootElement, writer);
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }
        catch (System.Text.Json.JsonException)
        {
            return Redact(json);
        }
    }

    /// <summary>
    /// Redact an exception message recursively, including inner exceptions.
    /// </summary>
    public string RedactException(Exception? ex)
    {
        if (ex is null) return string.Empty;
        var parts = new List<string>();
        var current = ex;
        while (current is not null)
        {
            parts.Add(Redact(current.Message));
            current = current.InnerException;
        }
        return string.Join(" -> ", parts);
    }

    private static void WriteRedacted(System.Text.Json.JsonElement element, System.Text.Json.Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject())
                {
                    writer.WritePropertyName(prop.Name);
                    if (IsSensitiveName(prop.Name))
                        writer.WriteStringValue("[redacted]");
                    else
                        WriteRedacted(prop.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case System.Text.Json.JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var child in element.EnumerateArray())
                    WriteRedacted(child, writer);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsSensitiveName(string name)
    {
        if (SensitiveNames.Contains(name)) return true;
        if (name.EndsWith("_key", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith("_secret", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith("_token", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    [GeneratedRegex(@"(?:api[_-]?key|apikey)\s*[:=]\s*\S+", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyRegex();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"Basic\s+[A-Za-z0-9+/]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex BasicAuthRegex();

    [GeneratedRegex(@"(?:password|passwd|pwd)\s*[:=]\s*\S+", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordKvRegex();

    [GeneratedRegex(@"(?:secret)\s*[:=]\s*\S+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretKvRegex();
}
