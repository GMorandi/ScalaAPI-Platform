using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Sub2Api.Grains.Interfaces;

namespace Sub2Api.Grains;

[GenerateSerializer]
public class ApiKeyState
{
    [Id(0)] public long ApiKeyId { get; set; }
    [Id(1)] public long UserId { get; set; }
    [Id(2)] public long GroupId { get; set; }
    [Id(3)] public string Status { get; set; } = "active";
    [Id(4)] public double Quota { get; set; }
    [Id(5)] public double QuotaUsed { get; set; }
    [Id(6)] public long? ExpiresAt { get; set; }
    [Id(7)] public string[] IpWhitelist { get; set; } = [];
    [Id(8)] public string[] IpBlacklist { get; set; } = [];
    [Id(9)] public double RateLimit5h { get; set; }
    [Id(10)] public double RateLimit1d { get; set; }
    [Id(11)] public double RateLimit7d { get; set; }
    [Id(12)] public long Version { get; set; }
}

public class ApiKeyGrain : Grain, IApiKeyGrain
{
    private readonly IPersistentState<ApiKeyState> _state;
    private readonly ILogger<ApiKeyGrain> _logger;

    public ApiKeyGrain(
        [PersistentState("apiKey", "postgres")] IPersistentState<ApiKeyState> state,
        ILogger<ApiKeyGrain> logger)
    {
        _state = state;
        _logger = logger;
    }

    public async Task<AuthResult> Validate(AuthRequest req)
    {
        var s = _state.State;

        if (s.Status != "active")
            throw new InvalidOperationException("API key is not active");

        if (s.ExpiresAt.HasValue && s.ExpiresAt.Value < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            throw new InvalidOperationException("API key expired");

        if (s.IpBlacklist.Contains(req.ClientIp))
            throw new InvalidOperationException("IP blacklisted");

        if (s.IpWhitelist.Length > 0 && !s.IpWhitelist.Contains(req.ClientIp))
            throw new InvalidOperationException("IP not in whitelist");

        var userGrain = GrainFactory.GetGrain<IUserGrain>(s.UserId);
        var user = await userGrain.GetAuthProjection();

        var groupGrain = GrainFactory.GetGrain<IGroupGrain>(s.GroupId);
        var group = await groupGrain.GetAuthProjection();

        return new AuthResult(
            s.ApiKeyId, s.UserId, s.GroupId, group.Platform, s.Status,
            s.Quota, s.QuotaUsed, group.RateMultiplier,
            user.Concurrency, user.RpmLimit, s.Version, user, group);
    }

    public Task<long> GetVersion() => Task.FromResult(_state.State.Version);

    public async Task AddUsage(decimal usd)
    {
        _state.State.QuotaUsed += (double)usd;
        _state.State.Version++;
        await _state.WriteStateAsync();
    }

    public async Task Create(ApiKeyUpsert input)
    {
        var s = _state.State;
        s.UserId = input.UserId;
        s.GroupId = input.GroupId;
        s.Quota = input.Quota;
        s.ExpiresAt = input.ExpiresAt;
        s.IpWhitelist = input.IpWhitelist;
        s.IpBlacklist = input.IpBlacklist;
        s.RateLimit5h = input.RateLimit5h;
        s.RateLimit1d = input.RateLimit1d;
        s.RateLimit7d = input.RateLimit7d;
        s.Version = 1;
        await _state.WriteStateAsync();
    }

    public async Task Update(ApiKeyUpsert input)
    {
        var s = _state.State;
        s.UserId = input.UserId;
        s.GroupId = input.GroupId;
        s.Quota = input.Quota;
        s.ExpiresAt = input.ExpiresAt;
        s.IpWhitelist = input.IpWhitelist;
        s.IpBlacklist = input.IpBlacklist;
        s.RateLimit5h = input.RateLimit5h;
        s.RateLimit1d = input.RateLimit1d;
        s.RateLimit7d = input.RateLimit7d;
        s.Version++;
        await _state.WriteStateAsync();
    }

    public async Task Revoke()
    {
        _state.State.Status = "revoked";
        _state.State.Version++;
        await _state.WriteStateAsync();
    }
}
