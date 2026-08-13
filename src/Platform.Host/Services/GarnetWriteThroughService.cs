namespace ScalaAPI.Host.Services;

public class GarnetWriteThroughService
{
    private readonly IGarnetService _garnet;

    public GarnetWriteThroughService(IGarnetService garnet)
    {
        _garnet = garnet;
    }

    public void WriteAuthSnapshot(string keyHash, string serializedSnapshot, TimeSpan? ttl = null)
    {
        _garnet.Set(GarnetKeyspace.Auth(keyHash), serializedSnapshot,
            ttl ?? TimeSpan.FromSeconds(60));
    }

    public void EvictAuthSnapshot(string keyHash)
    {
        _garnet.Delete(GarnetKeyspace.Auth(keyHash));
    }

    public void WriteAccountProjection(long accountId, string serializedProjection)
    {
        _garnet.Set(GarnetKeyspace.AccountProjection(accountId), serializedProjection,
            TimeSpan.FromSeconds(30));
    }

    public void WriteGroupRoutes(long groupId, string serializedRoutes)
    {
        _garnet.Set(GarnetKeyspace.GroupRoutes(groupId), serializedRoutes,
            TimeSpan.FromMinutes(5));
    }

    public void WriteGroupConfig(long groupId, string serializedConfig)
    {
        _garnet.Set(GarnetKeyspace.GroupConfig(groupId), serializedConfig,
            TimeSpan.FromMinutes(5));
    }

    public void WriteStickySession(long groupId, string sessionHash, long accountId, TimeSpan ttl)
    {
        _garnet.Set(GarnetKeyspace.StickySession(groupId, sessionHash),
            accountId.ToString(), ttl);
    }

    public long PublishContentPolicyRevision(long revision)
    {
        if (revision < 1)
            throw new ArgumentOutOfRangeException(nameof(revision));

        // This projection must survive idle periods; PostgreSQL rebuild restores
        // it after Garnet loss instead of relying on a time-based expiry.
        return _garnet.PublishMonotonicRevision(
            GarnetKeyspace.ContentPolicyRevision,
            revision,
            GarnetKeyspace.InvalidationVersion);
    }

    public long PublishConfigRevision(long revision)
    {
        if (revision < 1)
            throw new ArgumentOutOfRangeException(nameof(revision));

        return _garnet.PublishMonotonicRevision(
            GarnetKeyspace.ConfigRevision,
            revision,
            GarnetKeyspace.InvalidationVersion);
    }

    public void Evict(string key)
    {
        _garnet.Delete(key);
    }
}
