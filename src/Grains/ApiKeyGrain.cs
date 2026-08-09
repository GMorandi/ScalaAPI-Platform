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
    [Id(20)] public string[] Scopes { get; set; } = [ApiKeyScopes.Wildcard];
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

        var ipBlacklist = s.IpBlacklist ?? [];
        var ipWhitelist = s.IpWhitelist ?? [];
        if (ipBlacklist.Contains(req.ClientIp))
            throw new InvalidOperationException("IP blacklisted");

        if (ipWhitelist.Length > 0 && !ipWhitelist.Contains(req.ClientIp))
            throw new InvalidOperationException("IP not in whitelist");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var quota = ApiKeyQuotaPolicy.Evaluate(ToQuotaState(s), now);
        ApplyQuotaState(s, quota.State);
        if (!quota.Allowed)
            throw new InvalidOperationException(quota.RejectionReason);

        var userGrain = GrainFactory.GetGrain<IUserGrain>(s.UserId);
        var user = await userGrain.GetAuthProjection();
        if (user.Status != "active")
            throw new InvalidOperationException("User is not active");

        var groupGrain = GrainFactory.GetGrain<IGroupGrain>(s.GroupId);
        var group = await groupGrain.GetAuthProjection();
        if (group.Status != "active")
            throw new InvalidOperationException("Group is not active");
        var allowedGroups = user.AllowedGroups ?? [];
        if (allowedGroups.Length > 0 && !allowedGroups.Contains(s.GroupId))
            throw new InvalidOperationException("User is not allowed to use this group");
        return new AuthResult(
            s.ApiKeyId, s.UserId, s.GroupId, group.Platform, s.Status,
            s.Quota, s.QuotaUsed, group.RateMultiplier,
            user.Concurrency, user.RpmLimit, s.Version,
            ApiKeyScopes.Normalize(s.Scopes), user, group);
    }

    public Task<long> GetVersion() => Task.FromResult(_state.State.Version);

    public Task<ApiKeyConfig> GetConfig()
    {
        var s = _state.State;
        return Task.FromResult(new ApiKeyConfig(
            s.UserId, s.GroupId, s.Quota, s.ExpiresAt,
            s.IpWhitelist, s.IpBlacklist,
            s.RateLimit5h, s.RateLimit1d, s.RateLimit7d,
            ApiKeyScopes.Normalize(s.Scopes)));
    }

    public Task<ApiKeyProjection> GetProjection()
    {
        var s = _state.State;
        return Task.FromResult(new ApiKeyProjection(
            s.ApiKeyId, s.UserId, s.GroupId, s.Status, s.Version,
            s.Quota, s.QuotaUsed, s.ExpiresAt, ApiKeyScopes.Normalize(s.Scopes)));
    }

    public async Task AddUsage(decimal usd)
    {
        var s = _state.State;
        var amount = Math.Max(0m, usd);
        s.QuotaUsed += amount;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ApplyQuotaState(s, ApiKeyQuotaPolicy.Normalize(ToQuotaState(s), now));
        s.Usage5h += amount;
        s.Usage1d += amount;
        s.Usage7d += amount;

        s.Version++;
        await _state.WriteStateAsync();
        _invalidation.NotifyChange("apiKey", this.GetPrimaryKeyString());
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

    private static ApiKeyQuotaState ToQuotaState(ApiKeyState s) => new(
        s.Quota, s.QuotaUsed, s.RateLimit5h, s.RateLimit1d, s.RateLimit7d,
        s.Usage5h, s.Usage1d, s.Usage7d,
        s.Window5hStart, s.Window1dStart, s.Window7dStart);

    private static void ApplyQuotaState(ApiKeyState target, ApiKeyQuotaState source)
    {
        target.QuotaUsed = source.QuotaUsed;
        target.Usage5h = source.Usage5h;
        target.Usage1d = source.Usage1d;
        target.Usage7d = source.Usage7d;
        target.Window5hStart = source.Window5hStart;
        target.Window1dStart = source.Window1dStart;
        target.Window7dStart = source.Window7dStart;
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
        s.Quota = input.Quota;
        s.ExpiresAt = input.ExpiresAt;
        s.IpWhitelist = input.IpWhitelist ?? [];
        s.IpBlacklist = input.IpBlacklist ?? [];
        s.RateLimit5h = input.RateLimit5h;
        s.RateLimit1d = input.RateLimit1d;
        s.RateLimit7d = input.RateLimit7d;
        s.Scopes = ApiKeyScopes.Normalize(input.Scopes);
        s.Version = 1;
        await _state.WriteStateAsync();
        _invalidation.NotifyChange("apiKey", this.GetPrimaryKeyString());
    }

    public async Task Update(ApiKeyUpsert input)
    {
        var s = _state.State;
        s.UserId = input.UserId;
        s.GroupId = input.GroupId;
        s.Quota = input.Quota;
        s.ExpiresAt = input.ExpiresAt;
        s.IpWhitelist = input.IpWhitelist ?? [];
        s.IpBlacklist = input.IpBlacklist ?? [];
        s.RateLimit5h = input.RateLimit5h;
        s.RateLimit1d = input.RateLimit1d;
        s.RateLimit7d = input.RateLimit7d;
        s.Scopes = ApiKeyScopes.Normalize(input.Scopes);
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
