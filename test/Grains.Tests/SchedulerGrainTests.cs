using Orleans.TestingHost;
using Sub2Api.Grains.Interfaces;

namespace Sub2Api.Grains.Tests;

[Collection("Cluster")]
public class SchedulerGrainTests
{
    private readonly TestCluster _cluster;

    public SchedulerGrainTests(ClusterFixture fixture) => _cluster = fixture.Cluster;

    private async Task SetupGroupWithAccounts(long groupId, long[] accountIds, string platform = "anthropic")
    {
        var groupGrain = _cluster.GrainFactory.GetGrain<IGroupGrain>(groupId);
        await groupGrain.Create(new GroupUpsert(
            platform, 1.0, false, null, false, null, false, new(), accountIds, 0, null, null, null));

        foreach (var id in accountIds)
        {
            var acctGrain = _cluster.GrainFactory.GetGrain<IAccountGrain>(id);
            await acctGrain.Create(new AccountUpsert(
                $"acct-{id}", platform, "api_key", "https://api.example.com",
                1, 5, 100, 1.0, true, new(), new(), [], null, false));
        }
    }

    [Fact]
    public async Task Select_AvailableAccount_ReturnsOk()
    {
        await SetupGroupWithAccounts(5001, [6001, 6002]);
        var scheduler = _cluster.GrainFactory.GetGrain<ISchedulerGrain>(5001);

        var result = await scheduler.Select(new SelectRequest(
            "claude-sonnet-4-20250514", "sess-1", "req-1", null, [], "/v1/messages"));

        Assert.Equal(SelectionOutcome.Ok, result.Outcome);
        Assert.NotNull(result.AccountId);
        Assert.NotNull(result.LeaseToken);
    }

    [Fact]
    public async Task Select_AllExcluded_ReturnsRejected()
    {
        await SetupGroupWithAccounts(5002, [6003]);
        var scheduler = _cluster.GrainFactory.GetGrain<ISchedulerGrain>(5002);

        var result = await scheduler.Select(new SelectRequest(
            "claude-sonnet-4-20250514", "sess-2", "req-2", null, [6003], "/v1/messages"));

        Assert.Equal(SelectionOutcome.Rejected, result.Outcome);
        Assert.Equal("No available accounts", result.RejectReason);
    }

    [Fact]
    public async Task Select_StickySession_ReusesAccount()
    {
        await SetupGroupWithAccounts(5003, [6004, 6005]);
        var scheduler = _cluster.GrainFactory.GetGrain<ISchedulerGrain>(5003);

        var first = await scheduler.Select(new SelectRequest(
            "claude-sonnet-4-20250514", "sticky-hash-1", "req-3", null, [], "/v1/messages"));
        Assert.Equal(SelectionOutcome.Ok, first.Outcome);

        var second = await scheduler.Select(new SelectRequest(
            "claude-sonnet-4-20250514", "sticky-hash-1", "req-4", null, [], "/v1/messages"));
        Assert.Equal(SelectionOutcome.Ok, second.Outcome);
        Assert.Equal(first.AccountId, second.AccountId);
    }

    [Fact]
    public async Task BindSticky_ThenGet_ReturnsAccount()
    {
        var scheduler = _cluster.GrainFactory.GetGrain<ISchedulerGrain>(5004);
        await scheduler.BindSticky("hash-x", 9999, TimeSpan.FromHours(1));

        var result = await scheduler.GetStickyAccount("hash-x");
        Assert.Equal(9999, result);
    }

    [Fact]
    public async Task GetStickyAccount_Expired_ReturnsNull()
    {
        var scheduler = _cluster.GrainFactory.GetGrain<ISchedulerGrain>(5005);
        await scheduler.BindSticky("hash-expired", 8888, TimeSpan.FromMilliseconds(-1));

        var result = await scheduler.GetStickyAccount("hash-expired");
        Assert.Null(result);
    }

    [Fact]
    public async Task ClearSticky_RemovesBinding()
    {
        var scheduler = _cluster.GrainFactory.GetGrain<ISchedulerGrain>(5006);
        await scheduler.BindSticky("hash-clear", 7777, TimeSpan.FromHours(1));
        await scheduler.ClearSticky("hash-clear");

        var result = await scheduler.GetStickyAccount("hash-clear");
        Assert.Null(result);
    }

    [Fact]
    public async Task Select_ExcludedAccount_SkipsToNext()
    {
        await SetupGroupWithAccounts(5007, [6006, 6007]);
        var scheduler = _cluster.GrainFactory.GetGrain<ISchedulerGrain>(5007);

        var result = await scheduler.Select(new SelectRequest(
            "claude-sonnet-4-20250514", "sess-excl", "req-5", null, [6006], "/v1/messages"));

        Assert.Equal(SelectionOutcome.Ok, result.Outcome);
        Assert.Equal(6007, result.AccountId);
    }

    [Fact]
    public async Task Select_ConcurrencyExhausted_ReturnsWaitOrRejected()
    {
        await SetupGroupWithAccounts(5008, [6008]);
        var scheduler = _cluster.GrainFactory.GetGrain<ISchedulerGrain>(5008);

        // Exhaust all 5 slots
        for (int i = 0; i < 5; i++)
        {
            var r = await scheduler.Select(new SelectRequest(
                "claude-sonnet-4-20250514", $"sess-fill-{i}", $"req-fill-{i}", null, [], "/v1/messages"));
            Assert.Equal(SelectionOutcome.Ok, r.Outcome);
        }

        var overflow = await scheduler.Select(new SelectRequest(
            "claude-sonnet-4-20250514", "sess-overflow", "req-overflow", null, [], "/v1/messages"));

        Assert.True(overflow.Outcome is SelectionOutcome.Wait or SelectionOutcome.Rejected);
    }
}
