using Orleans;
using Orleans.Runtime;
using Sub2Api.Grains.Interfaces;

namespace Sub2Api.Grains;

[GenerateSerializer]
public class GroupState
{
    [Id(0)] public long Id { get; set; }
    [Id(1)] public string Platform { get; set; } = "anthropic";
    [Id(2)] public string Status { get; set; } = "active";
    [Id(3)] public double RateMultiplier { get; set; } = 1.0;
    [Id(4)] public bool IsExclusive { get; set; }
    [Id(5)] public double? DailyLimitUsd { get; set; }
    [Id(6)] public bool ClaudeCodeOnly { get; set; }
    [Id(7)] public long? FallbackGroupId { get; set; }
    [Id(8)] public bool ModelRoutingEnabled { get; set; }
    [Id(9)] public Dictionary<string, long[]> ModelRouting { get; set; } = new();
    [Id(10)] public long[] MemberAccountIds { get; set; } = [];
    [Id(11)] public int RpmLimit { get; set; }
    [Id(12)] public double? PeakMultiplier { get; set; }
    [Id(13)] public int? PeakStartHour { get; set; }
    [Id(14)] public int? PeakEndHour { get; set; }
}

public class GroupGrain : Grain, IGroupGrain
{
    private readonly IPersistentState<GroupState> _state;

    public GroupGrain([PersistentState("group", "postgres")] IPersistentState<GroupState> state)
    {
        _state = state;
    }

    public Task<GroupConfig> GetConfig()
    {
        var s = _state.State;
        return Task.FromResult(new GroupConfig(
            s.Id, s.Platform, s.RateMultiplier, s.ModelRoutingEnabled,
            s.ClaudeCodeOnly, s.FallbackGroupId, s.RpmLimit,
            s.PeakMultiplier, s.PeakStartHour, s.PeakEndHour));
    }

    public Task<GroupProjection> GetAuthProjection()
    {
        var s = _state.State;
        return Task.FromResult(new GroupProjection(
            s.Id, s.Platform, s.IsExclusive, s.Status, s.RateMultiplier,
            s.DailyLimitUsd, s.ClaudeCodeOnly, s.FallbackGroupId, s.ModelRoutingEnabled));
    }

    public Task<long[]> GetRoutingAccountIds(string model)
    {
        var s = _state.State;
        if (!s.ModelRoutingEnabled) return Task.FromResult(Array.Empty<long>());

        foreach (var (pattern, ids) in s.ModelRouting)
        {
            if (MatchesPattern(model, pattern))
                return Task.FromResult(ids);
        }
        return Task.FromResult(Array.Empty<long>());
    }

    public Task<long[]> GetMemberAccountIds() => Task.FromResult(_state.State.MemberAccountIds);

    public Task<CompositeRouteDecision?> ResolveCompositeRoute(string model, string endpoint)
    {
        return Task.FromResult<CompositeRouteDecision?>(null);
    }

    public Task<double> GetEffectiveMultiplier(DateTimeOffset now)
    {
        var s = _state.State;
        if (s.PeakMultiplier.HasValue && s.PeakStartHour.HasValue && s.PeakEndHour.HasValue)
        {
            var hour = now.Hour;
            if (hour >= s.PeakStartHour.Value && hour < s.PeakEndHour.Value)
                return Task.FromResult(s.PeakMultiplier.Value);
        }
        return Task.FromResult(s.RateMultiplier);
    }

    private static bool MatchesPattern(string model, string pattern)
    {
        if (pattern == "*") return true;
        if (pattern.EndsWith('*'))
            return model.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        return string.Equals(model, pattern, StringComparison.OrdinalIgnoreCase);
    }
}
