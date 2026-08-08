namespace ScalaAPI.Admin.Models;

public record AccountCreateRequest(
    string Name, string Platform, string Type, string BaseUrl,
    int Priority, int Concurrency, int LoadFactor, double RateMultiplier,
    bool Schedulable, Dictionary<string, string> Credentials,
    Dictionary<string, string> ModelMapping, string[] SupportedModels,
    string? ProxyUrl, bool TlsFingerprint);

public record GroupCreateRequest(
    string Platform, double RateMultiplier, bool IsExclusive,
    double? DailyLimitUsd, bool ClaudeCodeOnly, long? FallbackGroupId,
    bool ModelRoutingEnabled, Dictionary<string, long[]> ModelRouting,
    long[] MemberAccountIds, int RpmLimit,
    double? PeakMultiplier, int? PeakStartHour, int? PeakEndHour);

public record UserCreateRequest(
    string Role, double Balance, int Concurrency,
    int RpmLimit, long[] AllowedGroups);

public record ApiKeyCreateRequest(
    long UserId, long GroupId, double Quota,
    long? ExpiresAt, string[] IpWhitelist, string[] IpBlacklist,
    double RateLimit5h, double RateLimit1d, double RateLimit7d);

public record StatusRequest(string Status);
public record BalanceRequest(double Delta);
public record ConfigUpdateRequest(string Key, string Value);
