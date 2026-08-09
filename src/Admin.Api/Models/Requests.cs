using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Admin.Models;

public record AccountCreateRequest(
    string Name, string Platform, string Type, string BaseUrl,
    int Priority, int Concurrency, int LoadFactor, decimal RateMultiplier,
    bool Schedulable, Dictionary<string, string> Credentials,
    Dictionary<string, string> ModelMapping, string[] SupportedModels,
    string? ProxyUrl, bool TlsFingerprint,
    ProviderOAuthCredential? OAuth = null);

public record GroupCreateRequest(
    string Platform, decimal RateMultiplier, bool IsExclusive,
    decimal? DailyLimitUsd, bool ClaudeCodeOnly, long? FallbackGroupId,
    bool ModelRoutingEnabled, Dictionary<string, long[]> ModelRouting,
    long[] MemberAccountIds, int RpmLimit,
    decimal? PeakMultiplier, int? PeakStartHour, int? PeakEndHour);

public record UserCreateRequest(
    string Role, int Concurrency,
    int RpmLimit, long[] AllowedGroups);

public record ApiKeyCreateRequest(
    long UserId, long GroupId, decimal Quota,
    long? ExpiresAt, string[] IpWhitelist, string[] IpBlacklist,
    decimal RateLimit5h, decimal RateLimit1d, decimal RateLimit7d,
    string[]? Scopes = null);

public record StatusRequest(string Status);
public record BalanceRequest(decimal Delta, string Reason);
public record ConfigUpdateRequest(string Key, string Value);
