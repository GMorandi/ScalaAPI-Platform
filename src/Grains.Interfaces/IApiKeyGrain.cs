namespace ScalaAPI.Grains.Interfaces;

[GenerateSerializer]
public record AuthRequest(string ClientIp, string RequestId);

[GenerateSerializer]
public record AuthResult(
    long ApiKeyId,
    long UserId,
    long GroupId,
    string Platform,
    string Status,
    decimal Quota,
    decimal QuotaUsed,
    decimal RateMultiplier,
    int Concurrency,
    int RpmLimit,
    long Version,
    UserProjection User,
    GroupProjection Group);

[GenerateSerializer]
public record UserProjection(
    long Id, string Status, string Role, decimal Balance,
    int Concurrency, long[] AllowedGroups, int RpmLimit);

[GenerateSerializer]
public record GroupProjection(
    long Id, string Platform, bool IsExclusive, string Status,
    decimal RateMultiplier, decimal? DailyLimitUsd, bool ClaudeCodeOnly,
    long? FallbackGroupId, bool ModelRoutingEnabled);

[GenerateSerializer]
public record ApiKeyUpsert(
    long UserId, long GroupId, decimal Quota,
    long? ExpiresAt, string[] IpWhitelist, string[] IpBlacklist,
    decimal RateLimit5h, decimal RateLimit1d, decimal RateLimit7d);

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
