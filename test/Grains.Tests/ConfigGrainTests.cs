using Orleans.TestingHost;
using ScalaAPI.Grains.Interfaces;
using Xunit;

namespace ScalaAPI.Grains.Tests;

[Collection("Cluster")]
public sealed class ConfigGrainTests
{
    private readonly TestCluster cluster;

    public ConfigGrainTests(ClusterFixture fixture) => cluster = fixture.Cluster;

    [Fact]
    public async Task UpdateUsesVersionAndReturnsIndependentSnapshot()
    {
        var grain = cluster.GrainFactory.GetGrain<IConfigGrain>(
            $"config-test-{Guid.NewGuid():N}");
        var initial = await grain.GetSnapshot();

        var updated = await grain.Update("feature.responses", "true", initial.Version);

        Assert.Equal(initial.Version + 1, updated.Version);
        Assert.Equal("true", updated.Settings["feature.responses"]);
        updated.Settings["feature.responses"] = "false";
        Assert.Equal("true", (await grain.GetSnapshot()).Settings["feature.responses"]);
    }

    [Fact]
    public async Task RuntimeConfigRejectsSensitiveAndMalformedValues()
    {
        Assert.Throws<ArgumentException>(() =>
            ConfigValidation.Validate("Security:MasterKey", "not-a-secret"));
        Assert.Throws<ArgumentException>(() =>
            ConfigValidation.Validate("feature.realtime", "enabled"));
        Assert.Throws<ArgumentException>(() =>
            ConfigValidation.Validate("bad key", "value"));
    }
}
