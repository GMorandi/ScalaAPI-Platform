using Orleans;
using Orleans.Runtime;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Grains;

[GenerateSerializer]
public class GroupState
{
    [Id(0)] public long Id { get; set; }
    [Id(1)] public string Platform { get; set; } = "anthropic";
    [Id(2)] public string Status { get; set; } = "active";
    [Id(3)] public decimal RateMultiplier { get; set; } = 1.0m;
    [Id(4)] public bool IsExclusive { get; set; }
    [Id(5)] public decimal? DailyLimitUsd { get; set; }
    [Id(6)] public bool ClaudeCodeOnly { get; set; }
    [Id(7)] public long? FallbackGroupId { get; set; }
    [Id(8)] public bool ModelRoutingEnabled { get; set; }
    [Id(9)] public Dictionary<string, long[]> ModelRouting { get; set; } = new();
    [Id(10)] public long[] MemberAccountIds { get; set; } = [];
    [Id(11)] public int RpmLimit { get; set; }
    [Id(12)] public decimal? PeakMultiplier { get; set; }
    [Id(13)] public int? PeakStartHour { get; set; }
    [Id(14)] public int? PeakEndHour { get; set; }
    [Id(15)] public decimal DailySpendUsd { get; set; }
    [Id(16)] public string DailySpendDate { get; set; } = "";
    [Id(17)] public HashSet<string> AppliedLeases { get; set; } = [];
    [Id(18)] public long RpmWindowStart { get; set; }
    [Id(19)] public int RpmCount { get; set; }
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

        return Task.FromResult(FindMatchingRoute(s.ModelRouting, model)?.Value ?? []);
    }

    public Task<long[]> GetMemberAccountIds() => Task.FromResult(_state.State.MemberAccountIds);

    public Task<CompositeRouteDecision?> ResolveCompositeRoute(string model, string endpoint)
    {
        var s = _state.State;
        if (string.IsNullOrEmpty(s.Platform))
            return Task.FromResult<CompositeRouteDecision?>(null);

        var upstreamModel = model;
        if (s.ModelRoutingEnabled)
        {
            var match = FindMatchingRoute(s.ModelRouting, model);
            if (match is not null)
            {
                var mapped = match.Value.Key.Contains(':')
                    ? match.Value.Key[(match.Value.Key.IndexOf(':') + 1)..]
                    : model;
                upstreamModel = mapped;
            }
        }

        var targetEndpoint = endpoint;
        if (string.IsNullOrEmpty(targetEndpoint))
        {
            targetEndpoint = s.Platform switch
            {
                "anthropic" or "claude" => "/v1/messages",
                "gemini" or "google" => $"/v1beta/models/{upstreamModel}:generateContent",
                _ => "/v1/chat/completions"
            };
        }

        return Task.FromResult<CompositeRouteDecision?>(
            new CompositeRouteDecision(s.Platform, upstreamModel, targetEndpoint));
    }

    public Task<decimal> GetEffectiveMultiplier(DateTimeOffset now)
    {
        var s = _state.State;
        if (s.PeakMultiplier.HasValue && s.PeakStartHour.HasValue && s.PeakEndHour.HasValue)
        {
            var hour = now.Hour;
            var start = s.PeakStartHour.Value;
            var end = s.PeakEndHour.Value;
            var inPeak = start < end
                ? hour >= start && hour < end
                : start > end && (hour >= start || hour < end);
            if (inPeak)
                return Task.FromResult(s.PeakMultiplier.Value);
        }
        return Task.FromResult(s.RateMultiplier);
    }

    public Task<decimal> GetDailySpend()
    {
        var s = _state.State;
        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        if (s.DailySpendDate != today)
            return Task.FromResult(0m);
        return Task.FromResult(s.DailySpendUsd);
    }

    public async Task<bool> CheckAndRecordRpm()
    {
        var s = _state.State;
        var limit = s.RpmLimit;
        if (limit <= 0) return true;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (s.RpmWindowStart <= 0 || now - s.RpmWindowStart >= 60_000)
        {
            s.RpmCount = 0;
            s.RpmWindowStart = now;
        }
        if (s.RpmCount >= limit) return false;
        s.RpmCount++;
        await _state.WriteStateAsync();
        return true;
    }

    public async Task RecordSpend(decimal amount)
    {
        amount = Math.Max(0m, amount);
        var s = _state.State;
        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        if (s.DailySpendDate != today)
        {
            s.DailySpendDate = today;
            s.DailySpendUsd = 0;
        }
        s.DailySpendUsd += amount;
        await _state.WriteStateAsync();
    }

    public async Task RecordLeaseSpend(string leaseToken, decimal amount)
    {
        amount = Math.Max(0m, amount);
        if (!_state.State.AppliedLeases.Add(leaseToken)) return;
        var beforeAmount = _state.State.DailySpendUsd;
        var beforeDate = _state.State.DailySpendDate;
        try
        {
            var s = _state.State;
            var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
            if (s.DailySpendDate != today)
            {
                s.DailySpendDate = today;
                s.DailySpendUsd = 0;
            }
            s.DailySpendUsd += amount;
            await _state.WriteStateAsync();
        }
        catch
        {
            _state.State.AppliedLeases.Remove(leaseToken);
            _state.State.DailySpendUsd = beforeAmount;
            _state.State.DailySpendDate = beforeDate;
            throw;
        }
    }

    private static KeyValuePair<string, long[]>? FindMatchingRoute(
        IReadOnlyDictionary<string, long[]> routes, string model)
    {
        return routes
            .Where(entry => MatchesPattern(model, entry.Key))
            .OrderByDescending(entry => PatternRank(entry.Key))
            .ThenByDescending(entry => entry.Key.Length)
            .Select(entry => (KeyValuePair<string, long[]>?)entry)
            .FirstOrDefault();
    }

    private static int PatternRank(string pattern) =>
        pattern == "*" ? 0 : pattern.EndsWith('*') ? 1 : 2;

    private static bool MatchesPattern(string model, string pattern)
    {
        if (pattern == "*") return true;
        if (pattern.EndsWith('*'))
            return model.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        return string.Equals(model, pattern, StringComparison.OrdinalIgnoreCase);
    }

    public async Task Create(GroupUpsert input)
    {
        var s = _state.State;
        s.Id = this.GetPrimaryKeyLong();
        s.Platform = input.Platform;
        s.RateMultiplier = input.RateMultiplier;
        s.IsExclusive = input.IsExclusive;
        s.DailyLimitUsd = input.DailyLimitUsd;
        s.ClaudeCodeOnly = input.ClaudeCodeOnly;
        s.FallbackGroupId = input.FallbackGroupId;
        s.ModelRoutingEnabled = input.ModelRoutingEnabled;
        s.ModelRouting = input.ModelRouting;
        s.MemberAccountIds = input.MemberAccountIds;
        s.RpmLimit = input.RpmLimit;
        s.PeakMultiplier = input.PeakMultiplier;
        s.PeakStartHour = input.PeakStartHour;
        s.PeakEndHour = input.PeakEndHour;
        await _state.WriteStateAsync();
    }

    public async Task Update(GroupUpsert input)
    {
        var s = _state.State;
        s.Platform = input.Platform;
        s.RateMultiplier = input.RateMultiplier;
        s.IsExclusive = input.IsExclusive;
        s.DailyLimitUsd = input.DailyLimitUsd;
        s.ClaudeCodeOnly = input.ClaudeCodeOnly;
        s.FallbackGroupId = input.FallbackGroupId;
        s.ModelRoutingEnabled = input.ModelRoutingEnabled;
        s.ModelRouting = input.ModelRouting;
        s.MemberAccountIds = input.MemberAccountIds;
        s.RpmLimit = input.RpmLimit;
        s.PeakMultiplier = input.PeakMultiplier;
        s.PeakStartHour = input.PeakStartHour;
        s.PeakEndHour = input.PeakEndHour;
        await _state.WriteStateAsync();
    }

    public async Task SetStatus(string status)
    {
        _state.State.Status = status;
        await _state.WriteStateAsync();
    }

    public async Task Delete()
    {
        await _state.ClearStateAsync();
    }
}
