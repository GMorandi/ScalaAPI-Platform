namespace Sub2Api.Grains.Interfaces;

public record AuthRequest(string ClientIp, string RequestId);

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

public record UserProjection(
    long Id, string Status, string Role, double Balance,
    int Concurrency, long[] AllowedGroups, int RpmLimit);

public record GroupProjection(
    long Id, string Platform, bool IsExclusive, string Status,
    double RateMultiplier, double? DailyLimitUsd, bool ClaudeCodeOnly,
    long? FallbackGroupId, bool ModelRoutingEnabled);

public interface IApiKeyGrain : IGrainWithStringKey
{
    Task<AuthResult> Validate(AuthRequest req);
    Task<long> GetVersion();
    Task AddUsage(decimal usd);
}
