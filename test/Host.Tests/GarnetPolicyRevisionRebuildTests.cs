using Npgsql;
using Microsoft.Extensions.Configuration;
using ScalaAPI.Host.Services;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class GarnetPolicyRevisionRebuildTests
{
    [Fact]
    public async Task RebuildPublishesAuthoritativeRevisionWithoutExpiry()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var revisionCommand = dataSource.CreateCommand(
            "SELECT revision FROM content_policy_state WHERE id = 1");
        var expectedRevision = (long)(await revisionCommand.ExecuteScalarAsync())!;
        var garnet = new RecordingGarnet();
        var service = new GarnetPolicyRevisionRebuildService(
            dataSource, new GarnetWriteThroughService(garnet));

        var result = await service.RebuildAsync();

        Assert.Equal(expectedRevision, result.PolicyRevision);
        Assert.Equal(1, result.InvalidationVersion);
        var write = Assert.Single(garnet.SetCalls);
        Assert.Equal(GarnetKeyspace.ContentPolicyRevision, write.Key);
        Assert.Equal(expectedRevision.ToString(), write.Value);
        Assert.Null(write.Ttl);
        Assert.Equal([GarnetKeyspace.InvalidationVersion], garnet.Increments);
    }

    [Fact]
    public async Task DedicatedRemoteGarnetRestoresRevisionAfterKeyLoss()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        var garnetHost = Environment.GetEnvironmentVariable("GREENFIELD_GARNET_HOST");
        if (string.IsNullOrWhiteSpace(connectionString)
            || string.IsNullOrWhiteSpace(garnetHost)) return;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Garnet:Host"] = garnetHost,
                ["Garnet:Port"] = Environment.GetEnvironmentVariable(
                    "GREENFIELD_GARNET_PORT") ?? "6379",
                ["Garnet:Password"] = Environment.GetEnvironmentVariable(
                    "GREENFIELD_GARNET_PASSWORD"),
                ["Garnet:TimeoutMs"] = "2000",
            }).Build();
        using var garnet = new RemoteGarnetService(configuration);
        garnet.Delete(GarnetKeyspace.ContentPolicyRevision);
        garnet.Delete(GarnetKeyspace.InvalidationVersion);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var service = new GarnetPolicyRevisionRebuildService(
            dataSource, new GarnetWriteThroughService(garnet));

        var result = await service.RebuildAsync();

        Assert.Equal(result.PolicyRevision.ToString(),
            garnet.Get(GarnetKeyspace.ContentPolicyRevision));
        Assert.Equal(result.InvalidationVersion.ToString(),
            garnet.Get(GarnetKeyspace.InvalidationVersion));
        Assert.True(result.InvalidationVersion > 0);

        var replayVersion = await service.RebuildAsync();
        Assert.Equal(result.InvalidationVersion, replayVersion.InvalidationVersion);
        Assert.Equal(result.PolicyRevision.ToString(),
            garnet.Get(GarnetKeyspace.ContentPolicyRevision));

        var staleVersion = garnet.PublishMonotonicRevision(
            GarnetKeyspace.ContentPolicyRevision,
            Math.Max(1, result.PolicyRevision - 1),
            GarnetKeyspace.InvalidationVersion);
        Assert.Equal(result.InvalidationVersion, staleVersion);
        Assert.Equal(result.PolicyRevision.ToString(),
            garnet.Get(GarnetKeyspace.ContentPolicyRevision));
    }

    private sealed class RecordingGarnet : IGarnetService
    {
        public List<(string Key, string Value, TimeSpan? Ttl)> SetCalls { get; } = [];
        public List<string> Increments { get; } = [];

        public void Set(string key, string value, TimeSpan? ttl = null) =>
            SetCalls.Add((key, value, ttl));

        public string? Get(string key) => null;
        public void Delete(string key) { }

        public long Increment(string key)
        {
            Increments.Add(key);
            return Increments.Count;
        }

        public long PublishMonotonicRevision(string revisionKey, long revision,
            string invalidationKey)
        {
            var current = SetCalls.LastOrDefault(call => call.Key == revisionKey).Value;
            if (long.TryParse(current, out var currentRevision) && currentRevision >= revision)
                return Increments.Count;
            Set(revisionKey, revision.ToString());
            return Increment(invalidationKey);
        }

        public bool Ping() => true;
    }
}
