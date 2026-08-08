namespace ScalaAPI.Grains.Interfaces;

[GenerateSerializer]
public record GroupConfig(
    long Id, string Platform, decimal RateMultiplier,
    bool ModelRoutingEnabled, bool ClaudeCodeOnly,
    long? FallbackGroupId, int RpmLimit,
    decimal? PeakMultiplier, int? PeakStartHour, int? PeakEndHour);

[GenerateSerializer]
public record ModelRoute(string Pattern, long[] AccountIds);

[GenerateSerializer]
public record CompositeRouteDecision(
    string TargetPlatform, string UpstreamModel, string Endpoint);

[GenerateSerializer]
public record GroupUpsert(
    string Platform, decimal RateMultiplier, bool IsExclusive,
    decimal? DailyLimitUsd, bool ClaudeCodeOnly, long? FallbackGroupId,
    bool ModelRoutingEnabled, Dictionary<string, long[]> ModelRouting,
    long[] MemberAccountIds, int RpmLimit,
    decimal? PeakMultiplier, int? PeakStartHour, int? PeakEndHour);

public interface IGroupGrain : IGrainWithIntegerKey
{
    Task<GroupConfig> GetConfig();
    Task<GroupProjection> GetAuthProjection();
    Task<long[]> GetRoutingAccountIds(string model);
    Task<long[]> GetMemberAccountIds();
    Task<CompositeRouteDecision?> ResolveCompositeRoute(string model, string endpoint);
    Task<decimal> GetEffectiveMultiplier(DateTimeOffset now);
    Task<decimal> GetDailySpend();
    Task<bool> CheckAndRecordRpm();
    Task RecordSpend(decimal amount);
    Task RecordLeaseSpend(string leaseToken, decimal amount);

    Task Create(GroupUpsert input);
    Task Update(GroupUpsert input);
    Task SetStatus(string status);
    Task Delete();
}
