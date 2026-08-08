using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Host.Services;

public class InvalidationService : IInvalidationService
{
    private readonly IGarnetService _garnet;
    private readonly AuthProjectionCache _authCache;

    public InvalidationService(IGarnetService garnet, AuthProjectionCache authCache)
    {
        _garnet = garnet;
        _authCache = authCache;
    }

    public void NotifyChange(string entityType, string entityKey)
    {
        _garnet.Increment("invalidation:version");
        if (string.Equals(entityType, "apiKey", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(entityKey))
            _garnet.Delete($"auth:{entityKey}");
        _authCache.EvictAll();
    }
}
