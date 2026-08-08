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

    private sealed class RecordingGarnet : IGarnetService
    {
        public List<string> Deleted { get; } = [];

        public void Set(string key, string value, TimeSpan? ttl = null) { }
        public string? Get(string key) => null;
        public void Delete(string key) => Deleted.Add(key);
        public long Increment(string key) => 1;
        public bool Ping() => true;
    }
}
