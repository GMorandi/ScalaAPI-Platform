using Sub2Api.Grains.Interfaces;

namespace Sub2Api.Host.Services;

public class InvalidationService : IInvalidationService
{
    private readonly IGarnetService _garnet;
    private long _version;

    public InvalidationService(IGarnetService garnet)
    {
        _garnet = garnet;
        var existing = garnet.Get("invalidation:version");
        _version = long.TryParse(existing, out var v) ? v : 0;
    }

    public void NotifyChange(string entityType, string entityKey)
    {
        var newVersion = Interlocked.Increment(ref _version);
        _garnet.Set("invalidation:version", newVersion.ToString());
    }
}
