using Orleans.TestingHost;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Grains.Tests;

[Collection("Cluster")]
public class UserGrainTests
{
    private readonly TestCluster _cluster;

    public UserGrainTests(ClusterFixture fixture) => _cluster = fixture.Cluster;

    private IUserGrain GetGrain(long id) => _cluster.GrainFactory.GetGrain<IUserGrain>(id);

    [Fact]
    public async Task TryAcquireSlot_WithinLimit_Succeeds()
    {
        var grain = GetGrain(7001);
        await grain.Create(new UserCreate("user", 3, 0, []));

        var result = await grain.TryAcquireSlot("req-1");
        Assert.True(result.Acquired);
        Assert.Equal("req-1", result.LeaseToken);
        Assert.Equal(1, result.CurrentLoad);
        Assert.Equal(3, result.MaxConcurrency);
    }

    [Fact]
    public async Task TryAcquireSlot_ExceedsLimit_Fails()
    {
        var grain = GetGrain(7002);
        await grain.Create(new UserCreate("user", 1, 0, []));

        Assert.True((await grain.TryAcquireSlot("req-a")).Acquired);
        var second = await grain.TryAcquireSlot("req-b");

        Assert.False(second.Acquired);
        Assert.Equal(1, second.CurrentLoad);
    }

    [Fact]
    public async Task ReleaseSlot_FreesCapacity()
    {
        var grain = GetGrain(7003);
        await grain.Create(new UserCreate("user", 1, 0, []));
        await grain.TryAcquireSlot("req-x");

        await grain.ReleaseSlot("req-x");

        Assert.True((await grain.TryAcquireSlot("req-y")).Acquired);
    }

    [Fact]
    public async Task FinalizeLease_FreesCapacityAndIsIdempotent()
    {
        var grain = GetGrain(7004);
        await grain.Create(new UserCreate("user", 1, 0, []));
        await grain.TryAcquireSlot("lease-a");

        await grain.FinalizeLease("lease-a", "request-a");
        await grain.FinalizeLease("lease-a", "request-a");

        Assert.True((await grain.TryAcquireSlot("lease-b")).Acquired);
    }

    [Fact]
    public async Task Create_InitializesZeroVersionZeroBalance()
    {
        var grain = GetGrain(7005);
        await grain.Create(new UserCreate("user", 1, 0, []));

        Assert.Equal(new BalanceProjection(0, 0m), await grain.GetBalanceProjection());
    }

    [Fact]
    public async Task ApplyBalanceSnapshot_RejectsStaleVersion()
    {
        var grain = GetGrain(7006);
        await grain.Create(new UserCreate("user", 1, 0, []));

        await grain.ApplyBalanceSnapshot(2, 80m);
        await grain.ApplyBalanceSnapshot(1, 125m);

        Assert.Equal(new BalanceProjection(2, 80m), await grain.GetBalanceProjection());
    }

    [Fact]
    public async Task ApplyBalanceSnapshot_SameVersionRepairsProjectionDrift()
    {
        var grain = GetGrain(7007);
        await grain.Create(new UserCreate("user", 1, 0, []));
        await grain.ApplyBalanceSnapshot(3, 25m);

        await grain.ApplyBalanceSnapshot(3, 24.5m);

        Assert.Equal(new BalanceProjection(3, 24.5m), await grain.GetBalanceProjection());
    }

    [Fact]
    public async Task SetStatus_ChangesProjection()
    {
        var grain = GetGrain(7008);
        await grain.Create(new UserCreate("user", 1, 0, []));

        await grain.SetStatus("suspended");

        Assert.Equal("suspended", (await grain.GetAuthProjection()).Status);
    }

    [Fact]
    public async Task UpdateChangesConfigurationWithoutMutatingBalance()
    {
        var grain = GetGrain(7009);
        await grain.Create(new UserCreate("user", 1, 0, []));
        await grain.ApplyBalanceSnapshot(1, 25m);

        await grain.Update(new UserConfiguration("user", 4, 60, [41]));

        var projection = await grain.GetAuthProjection();
        Assert.Equal(25m, projection.Balance);
        Assert.Equal(4, projection.Concurrency);
        Assert.Equal(60, projection.RpmLimit);
        Assert.Equal([41], projection.AllowedGroups);
    }

    [Fact]
    public async Task GetAuthProjection_ReflectsConfiguration()
    {
        var grain = GetGrain(7010);
        await grain.Create(new UserCreate("admin", 5, 120, [1, 2, 3]));

        var projection = await grain.GetAuthProjection();

        Assert.Equal(5, projection.Concurrency);
        Assert.Equal(120, projection.RpmLimit);
        Assert.Equal([1, 2, 3], projection.AllowedGroups);
        Assert.Equal("admin", projection.Role);
    }
}
