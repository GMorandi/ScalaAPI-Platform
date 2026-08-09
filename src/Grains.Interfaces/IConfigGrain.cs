namespace ScalaAPI.Grains.Interfaces;

[GenerateSerializer]
public record ConfigSnapshot(
    Dictionary<string, string> Settings,
    long Version);

public interface IConfigGrain : IGrainWithStringKey
{
    Task<Dictionary<string, string>> Get();
    Task<ConfigSnapshot> GetSnapshot();
    Task<ConfigSnapshot> Update(string key, string value, long? expectedVersion = null);
    Task<long> GetVersion();
}

public static class ConfigValidation
{
    public static void Validate(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128
            || key.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '.' or ':' or '_' or '-')))
            throw new ArgumentException("Configuration key is invalid", nameof(key));
        if (value is null || value.Length > 4096 || value.Contains('\0'))
            throw new ArgumentException("Configuration value is invalid", nameof(value));
        if (key.StartsWith("feature.", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("true", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("false", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Feature flags must be true or false", nameof(value));
        if (key.StartsWith("security:", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("connectionstrings:", StringComparison.OrdinalIgnoreCase)
            || key.Contains("password", StringComparison.OrdinalIgnoreCase)
            || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || key.Contains("masterkey", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Sensitive configuration cannot be stored at runtime", nameof(key));
    }
}
