using Orleans.TestingHost;
using Sub2Api.Grains.Interfaces;

namespace Sub2Api.Grains.Tests;

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
        await grain.Create(new UserUpsert("user", 100.0, 3, 0, []));

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
        await grain.Create(new UserUpsert("user", 100.0, 1, 0, []));

        var first = await grain.TryAcquireSlot("req-a");
        Assert.True(first.Acquired);

        var second = await grain.TryAcquireSlot("req-b");
        Assert.False(second.Acquired);
        Assert.Equal(1, second.CurrentLoad);
    }

    [Fact]
    public async Task ReleaseSlot_FreesCapacity()
    {
        var grain = GetGrain(7003);
        await grain.Create(new UserUpsert("user", 100.0, 1, 0, []));

        await grain.TryAcquireSlot("req-x");
        await grain.ReleaseSlot("req-x");

        var result = await grain.TryAcquireSlot("req-y");
        Assert.True(result.Acquired);
    }

    [Fact]
    public async Task ReserveBalance_Sufficient_ReturnsHandle()
    {
        var grain = GetGrain(7004);
        await grain.Create(new UserUpsert("user", 50.0, 1, 0, []));

        var handle = await grain.ReserveBalance(10.0m);
        Assert.NotNull(handle);
        Assert.Equal(10.0m, handle.Amount);
    }

    [Fact]
    public async Task ReserveBalance_Insufficient_ReturnsNull()
    {
        var grain = GetGrain(7005);
        await grain.Create(new UserUpsert("user", 5.0, 1, 0, []));

        var handle = await grain.ReserveBalance(10.0m);
        Assert.Null(handle);
    }

    [Fact]
    public async Task CommitUsage_DeductsBalance()
    {
        var grain = GetGrain(7006);
        await grain.Create(new UserUpsert("user", 100.0, 1, 0, []));

        var handle = await grain.ReserveBalance(20.0m);
        Assert.NotNull(handle);

        await grain.CommitUsage(handle, 15.0m);
        var canAfford = await grain.CheckBalance(86.0m);
        Assert.False(canAfford);
    }

    [Fact]
    public async Task ReleaseHold_UnfreezesBalance()
    {
        var grain = GetGrain(7007);
        await grain.Create(new UserUpsert("user", 100.0, 1, 0, []));

        var handle = await grain.ReserveBalance(80.0m);
        Assert.NotNull(handle);

        var blocked = await grain.CheckBalance(30.0m);
        Assert.False(blocked);

        await grain.ReleaseHold(handle);
        var freed = await grain.CheckBalance(30.0m);
        Assert.True(freed);
    }

    [Fact]
    public async Task SetStatus_ChangesProjection()
    {
        var grain = GetGrain(7008);
        await grain.Create(new UserUpsert("user", 100.0, 1, 0, []));

        await grain.SetStatus("suspended");
        var proj = await grain.GetAuthProjection();
        Assert.Equal("suspended", proj.Status);
    }

    [Fact]
    public async Task AdjustBalance_ModifiesBalance()
    {
        var grain = GetGrain(7009);
        await grain.Create(new UserUpsert("user", 100.0, 1, 0, []));

        await grain.AdjustBalance(-30.0);
        var proj = await grain.GetAuthProjection();
        Assert.Equal(70.0, proj.Balance);
    }

    [Fact]
    public async Task GetAuthProjection_ReflectsConcurrency()
    {
        var grain = GetGrain(7010);
        await grain.Create(new UserUpsert("admin", 200.0, 5, 120, [1, 2, 3]));

        var proj = await grain.GetAuthProjection();
        Assert.Equal(5, proj.Concurrency);
        Assert.Equal(120, proj.RpmLimit);
        Assert.Equal([1, 2, 3], proj.AllowedGroups);
        Assert.Equal("admin", proj.Role);
    }

    [Fact]
    public async Task MigrationMetadataAndRelationEventsPreserveAllowedGroups()
    {
        var grain = GetGrain(7011);
        await grain.UpsertMetadata(new UserMetadataUpsert("user", 25.0, 2, 30));
        await grain.AddAllowedGroup(10);
        await grain.AddAllowedGroup(10);
        await grain.UpsertMetadata(new UserMetadataUpsert("admin", 30.0, 4, 60));

        var projection = await grain.GetAuthProjection();
        Assert.Equal([10], projection.AllowedGroups);
        Assert.Equal("admin", projection.Role);

        await grain.RemoveAllowedGroup(10);
        await grain.RemoveAllowedGroup(10);
        Assert.Empty((await grain.GetAuthProjection()).AllowedGroups);
    }
}
