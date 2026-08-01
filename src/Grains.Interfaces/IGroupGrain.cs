namespace Sub2Api.Grains.Interfaces;

[GenerateSerializer]
public record GroupConfig(
    long Id, string Platform, double RateMultiplier,
    bool ModelRoutingEnabled, bool ClaudeCodeOnly,
    long? FallbackGroupId, int RpmLimit,
    double? PeakMultiplier, int? PeakStartHour, int? PeakEndHour);

[GenerateSerializer]
public record ModelRoute(string Pattern, long[] AccountIds);

[GenerateSerializer]
public record CompositeRouteDecision(
    string TargetPlatform, string UpstreamModel, string Endpoint);

[GenerateSerializer]
public record GroupUpsert(
    string Platform, double RateMultiplier, bool IsExclusive,
    double? DailyLimitUsd, bool ClaudeCodeOnly, long? FallbackGroupId,
    bool ModelRoutingEnabled, Dictionary<string, long[]> ModelRouting,
    long[] MemberAccountIds, int RpmLimit,
    double? PeakMultiplier, int? PeakStartHour, int? PeakEndHour);

public interface IGroupGrain : IGrainWithIntegerKey
{
    Task<GroupConfig> GetConfig();
    Task<GroupProjection> GetAuthProjection();
    Task<long[]> GetRoutingAccountIds(string model);
    Task<long[]> GetMemberAccountIds();
    Task<CompositeRouteDecision?> ResolveCompositeRoute(string model, string endpoint);
    Task<double> GetEffectiveMultiplier(DateTimeOffset now);

    Task Create(GroupUpsert input);
    Task Update(GroupUpsert input);
    Task SetStatus(string status);
    Task Delete();
}
