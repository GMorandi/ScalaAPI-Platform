using ScalaAPI.Host.Services;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class GarnetKeyspaceTests
{
    [Fact]
    public void KeysUseVersionedProductNamespace()
    {
        Assert.Equal("scalaapi:v1:auth:key-hash", GarnetKeyspace.Auth("key-hash"));
        Assert.Equal("scalaapi:v1:acct:42:proj", GarnetKeyspace.AccountProjection(42));
        Assert.Equal("scalaapi:v1:group:7:routes", GarnetKeyspace.GroupRoutes(7));
        Assert.Equal("scalaapi:v1:group:7:config", GarnetKeyspace.GroupConfig(7));
        Assert.Equal("scalaapi:v1:sticky:7:session", GarnetKeyspace.StickySession(7, "session"));
        Assert.Equal("scalaapi:v1:invalidation:version", GarnetKeyspace.InvalidationVersion);
        Assert.Equal("scalaapi:v1:content-policy:revision",
            GarnetKeyspace.ContentPolicyRevision);
    }

    [Fact]
    public void KeyBuildersDoNotProduceLegacyUnversionedNames()
    {
        var keys = new[]
        {
            GarnetKeyspace.Auth("hash"),
            GarnetKeyspace.AccountProjection(1),
            GarnetKeyspace.GroupRoutes(1),
            GarnetKeyspace.GroupConfig(1),
            GarnetKeyspace.StickySession(1, "session"),
            GarnetKeyspace.InvalidationVersion,
            GarnetKeyspace.ContentPolicyRevision,
        };

        Assert.All(keys, key => Assert.StartsWith("scalaapi:v1:", key));
        Assert.DoesNotContain(keys, key => key.StartsWith("auth:", StringComparison.Ordinal));
        Assert.DoesNotContain(keys, key => key.StartsWith("invalidation:", StringComparison.Ordinal));
    }

    [Fact]
    public void AuthProjectionEvictionUsesVersionedKey()
    {
        var garnet = new RecordingGarnet();
        var writer = new GarnetWriteThroughService(garnet);

        writer.EvictAuthSnapshot("hash");

        Assert.Equal(["scalaapi:v1:auth:hash"], garnet.Deleted);
    }

    [Fact]
    public void ContentPolicyRevisionProjectionIsDurableAndInvalidates()
    {
        var garnet = new RecordingGarnet();
        var writer = new GarnetWriteThroughService(garnet);

        var invalidationVersion = writer.PublishContentPolicyRevision(42);

        Assert.Equal(1, invalidationVersion);
        var write = Assert.Single(garnet.SetCalls);
        Assert.Equal(GarnetKeyspace.ContentPolicyRevision, write.Key);
        Assert.Equal("42", write.Value);
        Assert.Null(write.Ttl);
        Assert.Equal([GarnetKeyspace.InvalidationVersion], garnet.Increments);
    }

    [Fact]
    public void ContentPolicyRevisionPublicationIsMonotonicAndReplaySafe()
    {
        var garnet = new RecordingGarnet();
        var writer = new GarnetWriteThroughService(garnet);

        var first = writer.PublishContentPolicyRevision(42);
        var replay = writer.PublishContentPolicyRevision(42);
        var stale = writer.PublishContentPolicyRevision(41);
        var newer = writer.PublishContentPolicyRevision(43);

        Assert.Equal(1, first);
        Assert.Equal(first, replay);
        Assert.Equal(first, stale);
        Assert.Equal(2, newer);
        Assert.Equal(["42", "43"], garnet.SetCalls
            .Where(call => call.Key == GarnetKeyspace.ContentPolicyRevision)
            .Select(call => call.Value));
        Assert.Equal(2, garnet.Increments.Count);
    }

    private sealed class RecordingGarnet : IGarnetService
    {
        public List<string> Deleted { get; } = [];
        public List<(string Key, string Value, TimeSpan? Ttl)> SetCalls { get; } = [];
        public List<string> Increments { get; } = [];

        public void Set(string key, string value, TimeSpan? ttl = null) =>
            SetCalls.Add((key, value, ttl));
        public string? Get(string key) => null;
        public void Delete(string key) => Deleted.Add(key);
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
