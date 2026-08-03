namespace Sub2Api.Grains.Interfaces;

[GenerateSerializer]
public record AuthRequest(string ClientIp, string RequestId);

[GenerateSerializer]
public record AuthResult(
    long ApiKeyId,
    long UserId,
    long GroupId,
    string Platform,
    string Status,
    double Quota,
    double QuotaUsed,
    double RateMultiplier,
    int Concurrency,
    int RpmLimit,
    long Version,
    UserProjection User,
    GroupProjection Group);

[GenerateSerializer]
public record UserProjection(
    long Id, string Status, string Role, double Balance,
    int Concurrency, long[] AllowedGroups, int RpmLimit);

[GenerateSerializer]
public record GroupProjection(
    long Id, string Platform, bool IsExclusive, string Status,
    double RateMultiplier, double? DailyLimitUsd, bool ClaudeCodeOnly,
    long? FallbackGroupId, bool ModelRoutingEnabled);

[GenerateSerializer]
public record ApiKeyUpsert(
    long UserId, long GroupId, double Quota,
    long? ExpiresAt, string[] IpWhitelist, string[] IpBlacklist,
    double RateLimit5h, double RateLimit1d, double RateLimit7d);

public interface IApiKeyGrain : IGrainWithStringKey
{
    Task<AuthResult> Validate(AuthRequest req);
    Task<long> GetVersion();
    Task AddUsage(decimal usd);
    Task AddLeaseUsage(string leaseToken, decimal usd);

    Task Create(ApiKeyUpsert input, long apiKeyId = 0);
    Task Update(ApiKeyUpsert input);
    Task Revoke();
}
