namespace Sub2Api.Grains.Interfaces;

public record GroupConfig(
    long Id, string Platform, double RateMultiplier,
    bool ModelRoutingEnabled, bool ClaudeCodeOnly,
    long? FallbackGroupId, int RpmLimit,
    double? PeakMultiplier, int? PeakStartHour, int? PeakEndHour);

public record ModelRoute(string Pattern, long[] AccountIds);

public record CompositeRouteDecision(
    string TargetPlatform, string UpstreamModel, string Endpoint);

public interface IGroupGrain : IGrainWithIntegerKey
{
    Task<GroupConfig> GetConfig();
    Task<GroupProjection> GetAuthProjection();
    Task<long[]> GetRoutingAccountIds(string model);
    Task<long[]> GetMemberAccountIds();
    Task<CompositeRouteDecision?> ResolveCompositeRoute(string model, string endpoint);
    Task<double> GetEffectiveMultiplier(DateTimeOffset now);
}
