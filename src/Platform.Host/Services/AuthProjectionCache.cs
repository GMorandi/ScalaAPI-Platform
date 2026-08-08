using System.Collections.Concurrent;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Host.Services;

public sealed class AuthProjectionCache
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(60);

    public bool TryGet(string keyHash, string clientIp, long version, out AuthResult auth)
    {
        var key = MakeKey(keyHash, clientIp);
        if (_entries.TryGetValue(key, out var entry)
            && entry.ExpiresAt > DateTime.UtcNow && entry.Auth.Version == version)
        {
            auth = entry.Auth;
            return true;
        }
        _entries.TryRemove(key, out _);
        auth = default!;
        return false;
    }

    public void Set(string keyHash, string clientIp, AuthResult auth) =>
        _entries[MakeKey(keyHash, clientIp)] = new(auth, DateTime.UtcNow.Add(_ttl));

    public void EvictAll() => _entries.Clear();

    private static string MakeKey(string keyHash, string clientIp) => $"{keyHash}\n{clientIp}";
    private sealed record Entry(AuthResult Auth, DateTime ExpiresAt);
}
