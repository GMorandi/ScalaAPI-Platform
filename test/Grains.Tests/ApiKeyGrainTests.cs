using NSubstitute;
using Orleans.TestingHost;
using ScalaAPI.Grains.Interfaces;
using ScalaAPI.Grains;

namespace ScalaAPI.Grains.Tests;

[Collection("Cluster")]
public class ApiKeyGrainTests
{
    private readonly TestCluster _cluster;

    public ApiKeyGrainTests(ClusterFixture fixture) => _cluster = fixture.Cluster;

    private IApiKeyGrain GetGrain(long id) => _cluster.GrainFactory.GetGrain<IApiKeyGrain>(id.ToString());

    private static ApiKeyUpsert DefaultUpsert(long userId = 1, long groupId = 1) => new(
        userId, groupId, 100.0m, null, [], [], 0, 0, 0);

    [Fact]
    public void QuotaPolicy_AbsoluteQuotaWinsOverRollingWindows()
    {
        var state = new ApiKeyQuotaState(
            10m, 10m, 5m, 8m, 9m, 5m, 8m, 9m, 1, 1, 1);

        var decision = ApiKeyQuotaPolicy.Evaluate(state, 2);

        Assert.False(decision.Allowed);
        Assert.Equal("Quota exhausted", decision.RejectionReason);
    }

    [Fact]
    public void QuotaPolicy_UsesShortestWindowForStablePrecedence()
    {
        var state = new ApiKeyQuotaState(
            100m, 1m, 5m, 8m, 9m, 5m, 8m, 9m, 1, 1, 1);

        var decision = ApiKeyQuotaPolicy.Evaluate(state, 2);

        Assert.False(decision.Allowed);
        Assert.Equal("Rate limit exceeded (5h)", decision.RejectionReason);
    }

    [Fact]
    public void QuotaPolicy_ResetsEachExpiredWindowIndependently()
    {
        var now = 10_000_000L;
        var state = new ApiKeyQuotaState(
            100m, 20m, 10m, 30m, 40m, 10m, 30m, 40m,
            now - (5 * 3600 * 1000L), now - 1, now - (7 * 24 * 3600 * 1000L));

        var normalized = ApiKeyQuotaPolicy.Normalize(state, now);

        Assert.Equal(0m, normalized.Usage5h);
        Assert.Equal(30m, normalized.Usage1d);
        Assert.Equal(0m, normalized.Usage7d);
        Assert.Equal(now, normalized.Window5hStart);
        Assert.Equal(now - 1, normalized.Window1dStart);
        Assert.Equal(now, normalized.Window7dStart);
    }

    [Fact]
    public void QuotaPolicy_ZeroLimitsRemainUnlimited()
    {
        var state = new ApiKeyQuotaState(
            0m, 1_000_000m, 0m, 0m, 0m, 1_000_000m, 1_000_000m, 1_000_000m,
            10_000_000, 10_000_000, 10_000_000);

        Assert.True(ApiKeyQuotaPolicy.Evaluate(state, 10_000_001).Allowed);
    }

    [Fact]
    public async Task Create_SetsVersionToOne()
    {
        var grain = GetGrain(1001);
        await grain.Create(DefaultUpsert());
        Assert.Equal(1, await grain.GetVersion());
    }

    [Fact]
    public async Task Update_BumpsVersion()
    {
        var grain = GetGrain(1002);
        await grain.Create(DefaultUpsert());
        await grain.Update(DefaultUpsert());
        Assert.Equal(2, await grain.GetVersion());
    }

    [Fact]
    public async Task AddUsage_IncrementsVersionAndQuota()
    {
        var grain = GetGrain(1003);
        await grain.Create(DefaultUpsert());
        await grain.AddUsage(1.5m);
        Assert.Equal(2, await grain.GetVersion());
    }

    [Fact]
    public async Task AddUsage_NotifiesAuthProjectionInvalidation()
    {
        ClusterFixture.InvalidationService.ClearReceivedCalls();
        var grain = GetGrain(1030);
        await grain.Create(DefaultUpsert());
        ClusterFixture.InvalidationService.ClearReceivedCalls();

        await grain.AddUsage(0.25m);

        ClusterFixture.InvalidationService.Received(1)
            .NotifyChange("apiKey", Arg.Any<string>());
    }

    [Fact]
    public async Task Validate_ActiveKey_ReturnsAuthResult()
    {
        var userGrain = _cluster.GrainFactory.GetGrain<IUserGrain>(2001);
        await userGrain.Create(new UserCreate("user", 50.0m, 2, 60, [3001]));

        var groupGrain = _cluster.GrainFactory.GetGrain<IGroupGrain>(3001);
        await groupGrain.Create(new GroupUpsert("anthropic", 1.0m, false, null, false, null, false, new(), [1], 0, null, null, null));

        var grain = GetGrain(1004);
        await grain.Create(DefaultUpsert(2001, 3001));

        var result = await grain.Validate(new AuthRequest("10.0m.0.1", "req-1"));
        Assert.Equal(1004, result.ApiKeyId);
        Assert.Equal(2001, result.UserId);
        Assert.Equal(3001, result.GroupId);
        Assert.Equal("anthropic", result.Platform);
        Assert.Equal("active", result.Status);
    }

    [Fact]
    public async Task Validate_NormalizesOmittedIpLists()
    {
        var userGrain = _cluster.GrainFactory.GetGrain<IUserGrain>(2050);
        await userGrain.Create(new UserCreate("user", 50.0m, 1, 0, []));
        var groupGrain = _cluster.GrainFactory.GetGrain<IGroupGrain>(3050);
        await groupGrain.Create(new GroupUpsert("openai", 1.0m, false, null, false,
            null, false, new(), [], 0, null, null, null));

        var grain = GetGrain(1050);
        await grain.Create(new ApiKeyUpsert(
            2050, 3050, 10.0m, null, null!, null!, 0, 0, 0));

        var result = await grain.Validate(new AuthRequest("10.0.0.1", "req-null-lists"));
        Assert.Equal(1050, result.ApiKeyId);
    }

    [Fact]
    public async Task Validate_PreservesDecimalPrecisionAcrossProjections()
    {
        var user = _cluster.GrainFactory.GetGrain<IUserGrain>(2020);
        await user.Create(new UserCreate("user", 123.45678901m, 1, 0, [3020]));

        var group = _cluster.GrainFactory.GetGrain<IGroupGrain>(3020);
        await group.Create(new GroupUpsert(
            "openai", 1.23456789m, false, 9876.54321098m, false, null,
            false, new(), [], 0, null, null, null));

        var grain = GetGrain(1020);
        await grain.Create(new ApiKeyUpsert(
            2020, 3020, 987.65432109m, null, [], [],
            0.12345678m, 1.23456789m, 12.34567890m));

        var result = await grain.Validate(new AuthRequest("10.0.0.1", "req-decimal"));

        Assert.Equal(987.65432109m, result.Quota);
        Assert.Equal(0m, result.QuotaUsed);
        Assert.Equal(123.45678901m, result.User.Balance);
        Assert.Equal(1.23456789m, result.Group.RateMultiplier);
        Assert.Equal(9876.54321098m, result.Group.DailyLimitUsd);
    }

    [Fact]
    public async Task Validate_RevokedKey_Throws()
    {
        var grain = GetGrain(1005);
        await grain.Create(DefaultUpsert());
        await grain.Revoke();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.Validate(new AuthRequest("10.0m.0.1", "req-2")));
    }

    [Fact]
    public async Task Validate_ExpiredKey_Throws()
    {
        var grain = GetGrain(1006);
        var expired = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
        await grain.Create(new ApiKeyUpsert(1, 1, 100.0m, expired, [], [], 0, 0, 0));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.Validate(new AuthRequest("10.0m.0.1", "req-3")));
    }

    [Fact]
    public async Task Validate_BlacklistedIp_Throws()
    {
        var grain = GetGrain(1007);
        await grain.Create(new ApiKeyUpsert(1, 1, 100.0m, null, [], ["192.168m.1.1"], 0, 0, 0));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.Validate(new AuthRequest("192.168m.1.1", "req-4")));
    }

    [Fact]
    public async Task Validate_WhitelistEnforced_Throws()
    {
        var grain = GetGrain(1008);
        await grain.Create(new ApiKeyUpsert(1, 1, 100.0m, null, ["10.0m.0.1"], [], 0, 0, 0));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.Validate(new AuthRequest("10.0m.0.2", "req-5")));
    }

    [Fact]
    public async Task Validate_WhitelistedIp_Succeeds()
    {
        var userGrain = _cluster.GrainFactory.GetGrain<IUserGrain>(2002);
        await userGrain.Create(new UserCreate("user", 50.0m, 1, 0, []));

        var groupGrain = _cluster.GrainFactory.GetGrain<IGroupGrain>(3002);
        await groupGrain.Create(new GroupUpsert("openai", 1.0m, false, null, false, null, false, new(), [], 0, null, null, null));

        var grain = GetGrain(1009);
        await grain.Create(new ApiKeyUpsert(2002, 3002, 100.0m, null, ["10.0m.0.1"], [], 0, 0, 0));

        var result = await grain.Validate(new AuthRequest("10.0m.0.1", "req-6"));
        Assert.Equal("active", result.Status);
    }

    [Fact]
    public async Task Validate_ExhaustedQuota_Throws()
    {
        var user = _cluster.GrainFactory.GetGrain<IUserGrain>(2010);
        await user.Create(new UserCreate("user", 50.0m, 1, 0, []));
        var group = _cluster.GrainFactory.GetGrain<IGroupGrain>(3010);
        await group.Create(new GroupUpsert("openai", 1.0m, false, null, false,
            null, false, new(), [], 0, null, null, null));
        var grain = GetGrain(1012);
        await grain.Create(new ApiKeyUpsert(2010, 3010, 1.0m, null, [], [], 0, 0, 0));
        await grain.AddUsage(1.0m);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.Validate(new AuthRequest("10.0m.0.1", "req-quota")));
    }

    [Fact]
    public async Task Validate_DisabledUser_Throws()
    {
        var user = _cluster.GrainFactory.GetGrain<IUserGrain>(2011);
        await user.Create(new UserCreate("user", 50.0m, 1, 0, []));
        await user.SetStatus("disabled");
        var group = _cluster.GrainFactory.GetGrain<IGroupGrain>(3011);
        await group.Create(new GroupUpsert("openai", 1.0m, false, null, false,
            null, false, new(), [], 0, null, null, null));
        var grain = GetGrain(1013);
        await grain.Create(new ApiKeyUpsert(2011, 3011, 10.0m, null, [], [], 0, 0, 0));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.Validate(new AuthRequest("10.0m.0.1", "req-disabled")));
    }

    [Fact]
    public async Task Validate_UnknownKey_Throws()
    {
        var grain = GetGrain(1099);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.Validate(new AuthRequest("10.0m.0.1", "req-unknown")));
    }

    [Fact]
    public async Task Revoke_NotifiesInvalidation()
    {
        ClusterFixture.InvalidationService.ClearReceivedCalls();
        var grain = GetGrain(1010);
        await grain.Create(DefaultUpsert());
        await grain.Revoke();

        ClusterFixture.InvalidationService.Received(2)
            .NotifyChange("apiKey", Arg.Any<string>());
    }

    [Fact]
    public async Task Create_NotifiesInvalidation()
    {
        ClusterFixture.InvalidationService.ClearReceivedCalls();
        var grain = GetGrain(1011);
        await grain.Create(DefaultUpsert());

        ClusterFixture.InvalidationService.Received(1)
            .NotifyChange("apiKey", Arg.Any<string>());
    }
}
