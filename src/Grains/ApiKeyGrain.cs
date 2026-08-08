using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Grains;

[GenerateSerializer]
public class ApiKeyState
{
    [Id(0)] public long ApiKeyId { get; set; }
    [Id(1)] public long UserId { get; set; }
    [Id(2)] public long GroupId { get; set; }
    [Id(3)] public string Status { get; set; } = "active";
    [Id(4)] public decimal Quota { get; set; }
    [Id(5)] public decimal QuotaUsed { get; set; }
    [Id(6)] public long? ExpiresAt { get; set; }
    [Id(7)] public string[] IpWhitelist { get; set; } = [];
    [Id(8)] public string[] IpBlacklist { get; set; } = [];
    [Id(9)] public decimal RateLimit5h { get; set; }
    [Id(10)] public decimal RateLimit1d { get; set; }
    [Id(11)] public decimal RateLimit7d { get; set; }
    [Id(12)] public long Version { get; set; }
    [Id(13)] public decimal Usage5h { get; set; }
    [Id(14)] public decimal Usage1d { get; set; }
    [Id(15)] public decimal Usage7d { get; set; }
    [Id(16)] public long Window5hStart { get; set; }
    [Id(17)] public long Window1dStart { get; set; }
    [Id(18)] public long Window7dStart { get; set; }
    [Id(19)] public HashSet<string> AppliedLeases { get; set; } = [];
}

public class ApiKeyGrain : Grain, IApiKeyGrain
{
    private readonly IPersistentState<ApiKeyState> _state;
    private readonly ILogger<ApiKeyGrain> _logger;
    private readonly IInvalidationService _invalidation;

    public ApiKeyGrain(
        [PersistentState("apiKey", "postgres")] IPersistentState<ApiKeyState> state,
        ILogger<ApiKeyGrain> logger,
        IInvalidationService invalidation)
    {
        _state = state;
        _logger = logger;
        _invalidation = invalidation;
    }

    public async Task<AuthResult> Validate(AuthRequest req)
    {
        var s = _state.State;

        if (s.ApiKeyId <= 0)
            throw new InvalidOperationException("API key does not exist");
        if (s.Status != "active")
            throw new InvalidOperationException("API key is not active");

        if (s.ExpiresAt.HasValue && s.ExpiresAt.Value < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            throw new InvalidOperationException("API key expired");

        if (s.IpBlacklist.Contains(req.ClientIp))
            throw new InvalidOperationException("IP blacklisted");

        if (s.IpWhitelist.Length > 0 && !s.IpWhitelist.Contains(req.ClientIp))
            throw new InvalidOperationException("IP not in whitelist");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ResetWindowIfExpired(s, now);

        if (s.RateLimit5h > 0 && s.Usage5h >= s.RateLimit5h)
            throw new InvalidOperationException("Rate limit exceeded (5h)");
        if (s.RateLimit1d > 0 && s.Usage1d >= s.RateLimit1d)
            throw new InvalidOperationException("Rate limit exceeded (1d)");
        if (s.RateLimit7d > 0 && s.Usage7d >= s.RateLimit7d)
            throw new InvalidOperationException("Rate limit exceeded (7d)");

        var userGrain = GrainFactory.GetGrain<IUserGrain>(s.UserId);
        var user = await userGrain.GetAuthProjection();
        if (user.Status != "active")
            throw new InvalidOperationException("User is not active");

        var groupGrain = GrainFactory.GetGrain<IGroupGrain>(s.GroupId);
        var group = await groupGrain.GetAuthProjection();
        if (group.Status != "active")
            throw new InvalidOperationException("Group is not active");
        if (user.AllowedGroups.Length > 0 && !user.AllowedGroups.Contains(s.GroupId))
            throw new InvalidOperationException("User is not allowed to use this group");
        if (s.Quota > 0 && s.QuotaUsed >= s.Quota)
            throw new InvalidOperationException("API key quota exhausted");

        return new AuthResult(
            s.ApiKeyId, s.UserId, s.GroupId, group.Platform, s.Status,
            (double)s.Quota, (double)s.QuotaUsed, group.RateMultiplier,
            user.Concurrency, user.RpmLimit, s.Version, user, group);
    }

    public Task<long> GetVersion() => Task.FromResult(_state.State.Version);

    public async Task AddUsage(decimal usd)
    {
        var s = _state.State;
        var amount = Math.Max(0m, usd);
        s.QuotaUsed += amount;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ResetWindowIfExpired(s, now);
        s.Usage5h += amount;
        s.Usage1d += amount;
        s.Usage7d += amount;

        s.Version++;
        await _state.WriteStateAsync();
    }

    public async Task AddLeaseUsage(string leaseToken, decimal usd)
    {
        if (!_state.State.AppliedLeases.Add(leaseToken)) return;
        var s = _state.State;
        var before = (s.QuotaUsed, s.Usage5h, s.Usage1d, s.Usage7d,
            s.Window5hStart, s.Window1dStart, s.Window7dStart, s.Version);
        try
        {
            await AddUsage(usd);
        }
        catch
        {
            s.AppliedLeases.Remove(leaseToken);
            (s.QuotaUsed, s.Usage5h, s.Usage1d, s.Usage7d,
                s.Window5hStart, s.Window1dStart, s.Window7dStart, s.Version) = before;
            throw;
        }
    }

    private static void ResetWindowIfExpired(ApiKeyState s, long nowMs)
    {
        const long fiveHoursMs = 5 * 3600 * 1000L;
        const long oneDayMs = 24 * 3600 * 1000L;
        const long sevenDaysMs = 7 * 24 * 3600 * 1000L;

        if (nowMs - s.Window5hStart >= fiveHoursMs)
        {
            s.Usage5h = 0;
            s.Window5hStart = nowMs;
        }
        if (nowMs - s.Window1dStart >= oneDayMs)
        {
            s.Usage1d = 0;
            s.Window1dStart = nowMs;
        }
        if (nowMs - s.Window7dStart >= sevenDaysMs)
        {
            s.Usage7d = 0;
            s.Window7dStart = nowMs;
        }
    }

    public async Task Create(ApiKeyUpsert input, long apiKeyId = 0)
    {
        var s = _state.State;
        if (apiKeyId <= 0 && long.TryParse(this.GetPrimaryKeyString(), out var parsedId))
            apiKeyId = parsedId;
        if (apiKeyId <= 0)
            throw new ArgumentOutOfRangeException(nameof(apiKeyId), "A positive API key ID is required");
        s.ApiKeyId = apiKeyId;
        s.UserId = input.UserId;
        s.GroupId = input.GroupId;
        s.Quota = (decimal)input.Quota;
        s.ExpiresAt = input.ExpiresAt;
        s.IpWhitelist = input.IpWhitelist;
        s.IpBlacklist = input.IpBlacklist;
        s.RateLimit5h = (decimal)input.RateLimit5h;
        s.RateLimit1d = (decimal)input.RateLimit1d;
        s.RateLimit7d = (decimal)input.RateLimit7d;
        s.Version = 1;
        await _state.WriteStateAsync();
        _invalidation.NotifyChange("apiKey", this.GetPrimaryKeyString());
    }

    public async Task Update(ApiKeyUpsert input)
    {
        var s = _state.State;
        s.UserId = input.UserId;
        s.GroupId = input.GroupId;
        s.Quota = (decimal)input.Quota;
        s.ExpiresAt = input.ExpiresAt;
        s.IpWhitelist = input.IpWhitelist;
        s.IpBlacklist = input.IpBlacklist;
        s.RateLimit5h = (decimal)input.RateLimit5h;
        s.RateLimit1d = (decimal)input.RateLimit1d;
        s.RateLimit7d = (decimal)input.RateLimit7d;
        s.Version++;
        await _state.WriteStateAsync();
        _invalidation.NotifyChange("apiKey", this.GetPrimaryKeyString());
    }

    public async Task Revoke()
    {
        _state.State.Status = "revoked";
        _state.State.Version++;
        await _state.WriteStateAsync();
        _invalidation.NotifyChange("apiKey", this.GetPrimaryKeyString());
    }
}
