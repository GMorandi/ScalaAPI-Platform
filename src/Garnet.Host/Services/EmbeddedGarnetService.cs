using System.Collections.Concurrent;

namespace Sub2Api.Host.Services;

public interface IGarnetService
{
    void Set(string key, string value, TimeSpan? ttl = null);
    string? Get(string key);
    void Delete(string key);
}

// In production: backed by embedded Garnet engine (RESP endpoint on UDS for C++ reads).
// This implementation provides the same semantics with in-memory storage.
public class EmbeddedGarnetService : IGarnetService, Microsoft.Extensions.Hosting.IHostedService
{
    private readonly ConcurrentDictionary<string, (string Value, long? ExpiresAt)> _store = new();
    private readonly string _socketPath;

    public EmbeddedGarnetService(string socketPath)
    {
        _socketPath = socketPath;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Production: GarnetServer.Start() binds RESP on _socketPath (UDS)
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _store.Clear();
        return Task.CompletedTask;
    }

    public void Set(string key, string value, TimeSpan? ttl = null)
    {
        long? expiresAt = ttl.HasValue
            ? DateTimeOffset.UtcNow.Add(ttl.Value).ToUnixTimeMilliseconds()
            : null;
        _store[key] = (value, expiresAt);
    }

    public string? Get(string key)
    {
        if (!_store.TryGetValue(key, out var entry))
            return null;

        if (entry.ExpiresAt.HasValue &&
            entry.ExpiresAt.Value < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            _store.TryRemove(key, out _);
            return null;
        }

        return entry.Value;
    }

    public void Delete(string key)
    {
        _store.TryRemove(key, out _);
    }
}
