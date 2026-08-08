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
        _garnet.Set($"auth:{keyHash}", serializedSnapshot, ttl ?? TimeSpan.FromSeconds(60));
    }

    public void WriteAccountProjection(long accountId, string serializedProjection)
    {
        _garnet.Set($"acct:{accountId}:proj", serializedProjection, TimeSpan.FromSeconds(30));
    }

    public void WriteGroupRoutes(long groupId, string serializedRoutes)
    {
        _garnet.Set($"group:{groupId}:routes", serializedRoutes);
    }

    public void WriteGroupConfig(long groupId, string serializedConfig)
    {
        _garnet.Set($"group:{groupId}:config", serializedConfig);
    }

    public void WriteStickySession(long groupId, string sessionHash, long accountId, TimeSpan ttl)
    {
        _garnet.Set($"sticky:{groupId}:{sessionHash}", accountId.ToString(), ttl);
    }

    public void Evict(string key)
    {
        _garnet.Delete(key);
    }
}
