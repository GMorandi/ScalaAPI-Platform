using Orleans.TestingHost;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Grains.Tests;

[Collection("Cluster")]
public class GroupGrainTests
{
    private readonly TestCluster _cluster;

    public GroupGrainTests(ClusterFixture fixture) => _cluster = fixture.Cluster;

    private IGroupGrain GetGrain(long id) => _cluster.GrainFactory.GetGrain<IGroupGrain>(id);

    [Fact]
    public async Task GetConfig_ReturnsCreatedValues()
    {
        var grain = GetGrain(4001);
        await grain.Create(new GroupUpsert(
            "anthropic", 1.5m, true, 50.0m, true, null, true,
            new() { ["claude*"] = [1, 2] }, [1, 2, 3], 100, 2.0m, 9, 18));

        var config = await grain.GetConfig();
        Assert.Equal("anthropic", config.Platform);
        Assert.Equal(1.5m, config.RateMultiplier);
        Assert.True(config.ModelRoutingEnabled);
        Assert.True(config.ClaudeCodeOnly);
        Assert.Equal(100, config.RpmLimit);
        Assert.Equal(2.0m, config.PeakMultiplier);
        Assert.Equal(9, config.PeakStartHour);
        Assert.Equal(18, config.PeakEndHour);
    }

    [Fact]
    public async Task GetRoutingAccountIds_MatchesWildcard()
    {
        var grain = GetGrain(4002);
        await grain.Create(new GroupUpsert(
            "anthropic", 1.0m, false, null, false, null, true,
            new() { ["claude*"] = [10, 20], ["gpt*"] = [30] }, [10, 20, 30], 0, null, null, null));

        var ids = await grain.GetRoutingAccountIds("claude-sonnet-4-20250514");
        Assert.Equal([10, 20], ids);
    }

    [Fact]
    public async Task GetRoutingAccountIds_ExactMatch()
    {
        var grain = GetGrain(4003);
        await grain.Create(new GroupUpsert(
            "openai", 1.0m, false, null, false, null, true,
            new() { ["gpt-4o"] = [40, 50] }, [40, 50], 0, null, null, null));

        var ids = await grain.GetRoutingAccountIds("gpt-4o");
        Assert.Equal([40, 50], ids);
    }

    [Fact]
    public async Task GetRoutingAccountIds_Disabled_ReturnsEmpty()
    {
        var grain = GetGrain(4004);
        await grain.Create(new GroupUpsert(
            "anthropic", 1.0m, false, null, false, null, false,
            new() { ["claude*"] = [10] }, [10], 0, null, null, null));

        var ids = await grain.GetRoutingAccountIds("claude-sonnet-4-20250514");
        Assert.Empty(ids);
    }

    [Fact]
    public async Task GetEffectiveMultiplier_PeakHours()
    {
        var grain = GetGrain(4005);
        await grain.Create(new GroupUpsert(
            "anthropic", 1.0m, false, null, false, null, false, new(), [], 0, 2.5m, 9, 18));

        var peakTime = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var offPeakTime = new DateTimeOffset(2026, 1, 15, 22, 0, 0, TimeSpan.Zero);

        Assert.Equal(2.5m, await grain.GetEffectiveMultiplier(peakTime));
        Assert.Equal(1.0m, await grain.GetEffectiveMultiplier(offPeakTime));
    }

    [Fact]
    public async Task ResolveCompositeRoute_AnthropicDefault()
    {
        var grain = GetGrain(4006);
        await grain.Create(new GroupUpsert(
            "anthropic", 1.0m, false, null, false, null, false, new(), [], 0, null, null, null));

        var route = await grain.ResolveCompositeRoute("claude-sonnet-4-20250514", "");
        Assert.NotNull(route);
        Assert.Equal("anthropic", route.TargetPlatform);
        Assert.Equal("/v1/messages", route.Endpoint);
        Assert.Equal("claude-sonnet-4-20250514", route.UpstreamModel);
    }

    [Fact]
    public async Task ResolveCompositeRoute_GeminiDefault()
    {
        var grain = GetGrain(4007);
        await grain.Create(new GroupUpsert(
            "gemini", 1.0m, false, null, false, null, false, new(), [], 0, null, null, null));

        var route = await grain.ResolveCompositeRoute("gemini-2.0m-flash", "");
        Assert.NotNull(route);
        Assert.Equal("gemini", route.TargetPlatform);
        Assert.Contains("generateContent", route.Endpoint);
    }

    [Fact]
    public async Task ResolveCompositeRoute_ExplicitEndpoint()
    {
        var grain = GetGrain(4008);
        await grain.Create(new GroupUpsert(
            "openai", 1.0m, false, null, false, null, false, new(), [], 0, null, null, null));

        var route = await grain.ResolveCompositeRoute("gpt-4o", "/v1/responses");
        Assert.NotNull(route);
        Assert.Equal("/v1/responses", route.Endpoint);
    }

    [Fact]
    public async Task SetStatus_ChangesProjection()
    {
        var grain = GetGrain(4009);
        await grain.Create(new GroupUpsert(
            "anthropic", 1.0m, false, null, false, null, false, new(), [], 0, null, null, null));

        await grain.SetStatus("disabled");
        var proj = await grain.GetAuthProjection();
        Assert.Equal("disabled", proj.Status);
    }

    [Fact]
    public async Task GetMemberAccountIds_ReturnsCreated()
    {
        var grain = GetGrain(4010);
        await grain.Create(new GroupUpsert(
            "anthropic", 1.0m, false, null, false, null, false, new(), [100, 200, 300], 0, null, null, null));

        var ids = await grain.GetMemberAccountIds();
        Assert.Equal([100, 200, 300], ids);
    }

}
