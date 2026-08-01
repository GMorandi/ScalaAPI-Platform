using NSubstitute;
using Orleans.TestingHost;
using Sub2Api.Grains.Interfaces;

namespace Sub2Api.Grains.Tests;

[Collection("Cluster")]
public class ApiKeyGrainTests
{
    private readonly TestCluster _cluster;

    public ApiKeyGrainTests(ClusterFixture fixture) => _cluster = fixture.Cluster;

    private IApiKeyGrain GetGrain(long id) => _cluster.GrainFactory.GetGrain<IApiKeyGrain>(id.ToString());

    private static ApiKeyUpsert DefaultUpsert(long userId = 1, long groupId = 1) => new(
        userId, groupId, 100.0, null, [], [], 0, 0, 0);

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
    public async Task Validate_ActiveKey_ReturnsAuthResult()
    {
        var userGrain = _cluster.GrainFactory.GetGrain<IUserGrain>(2001);
        await userGrain.Create(new UserUpsert("user", 50.0, 2, 60, [1]));

        var groupGrain = _cluster.GrainFactory.GetGrain<IGroupGrain>(3001);
        await groupGrain.Create(new GroupUpsert("anthropic", 1.0, false, null, false, null, false, new(), [1], 0, null, null, null));

        var grain = GetGrain(1004);
        await grain.Create(DefaultUpsert(2001, 3001));

        var result = await grain.Validate(new AuthRequest("10.0.0.1", "req-1"));
        Assert.Equal(2001, result.UserId);
        Assert.Equal(3001, result.GroupId);
        Assert.Equal("anthropic", result.Platform);
        Assert.Equal("active", result.Status);
    }

    [Fact]
    public async Task Validate_RevokedKey_Throws()
    {
        var grain = GetGrain(1005);
        await grain.Create(DefaultUpsert());
        await grain.Revoke();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.Validate(new AuthRequest("10.0.0.1", "req-2")));
    }

    [Fact]
    public async Task Validate_ExpiredKey_Throws()
    {
        var grain = GetGrain(1006);
        var expired = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
        await grain.Create(new ApiKeyUpsert(1, 1, 100.0, expired, [], [], 0, 0, 0));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.Validate(new AuthRequest("10.0.0.1", "req-3")));
    }

    [Fact]
    public async Task Validate_BlacklistedIp_Throws()
    {
        var grain = GetGrain(1007);
        await grain.Create(new ApiKeyUpsert(1, 1, 100.0, null, [], ["192.168.1.1"], 0, 0, 0));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.Validate(new AuthRequest("192.168.1.1", "req-4")));
    }

    [Fact]
    public async Task Validate_WhitelistEnforced_Throws()
    {
        var grain = GetGrain(1008);
        await grain.Create(new ApiKeyUpsert(1, 1, 100.0, null, ["10.0.0.1"], [], 0, 0, 0));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.Validate(new AuthRequest("10.0.0.2", "req-5")));
    }

    [Fact]
    public async Task Validate_WhitelistedIp_Succeeds()
    {
        var userGrain = _cluster.GrainFactory.GetGrain<IUserGrain>(2002);
        await userGrain.Create(new UserUpsert("user", 50.0, 1, 0, []));

        var groupGrain = _cluster.GrainFactory.GetGrain<IGroupGrain>(3002);
        await groupGrain.Create(new GroupUpsert("openai", 1.0, false, null, false, null, false, new(), [], 0, null, null, null));

        var grain = GetGrain(1009);
        await grain.Create(new ApiKeyUpsert(2002, 3002, 100.0, null, ["10.0.0.1"], [], 0, 0, 0));

        var result = await grain.Validate(new AuthRequest("10.0.0.1", "req-6"));
        Assert.Equal("active", result.Status);
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
