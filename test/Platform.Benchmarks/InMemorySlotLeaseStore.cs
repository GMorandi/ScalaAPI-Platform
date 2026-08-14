using System.Collections.Concurrent;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Platform.Benchmarks;

/// <summary>
/// Keeps the Orleans benchmark self-contained while satisfying the grain's
/// slot lease dependency without bringing PostgreSQL into the benchmark host.
/// </summary>
internal sealed class InMemorySlotLeaseStore : ISlotLeaseStore
{
    private readonly ConcurrentDictionary<string, AccountLease> accountLeases = new();
    private readonly ConcurrentDictionary<string, UserLease> userLeases = new();
    private readonly ConcurrentDictionary<long, AccountHealthState> health = new();

    public Task<bool> TryAcquireAccountSlot(long accountId, string leaseToken,
        string requestId, string siloId, DateTime expiresAt, int maxConcurrency,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var active = accountLeases.Values.Count(lease =>
            lease.AccountId == accountId && lease.ExpiresAt > now);
        if (active >= maxConcurrency)
            return Task.FromResult(false);

        accountLeases[leaseToken] = new AccountLease(
            accountId, leaseToken, requestId, siloId, expiresAt);
        return Task.FromResult(true);
    }

    public Task ReleaseAccountSlot(string leaseToken, string siloId,
        CancellationToken ct = default)
    {
        accountLeases.TryRemove(leaseToken, out _);
        return Task.CompletedTask;
    }

    public Task<int> ReclaimExpiredAccountSlots(long accountId, string siloId,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var reclaimed = 0;
        foreach (var lease in accountLeases)
        {
            if (lease.Value.AccountId == accountId && lease.Value.ExpiresAt <= now
                && accountLeases.TryRemove(lease.Key, out _))
                reclaimed++;
        }

        return Task.FromResult(reclaimed);
    }

    public Task<int> GetAccountActiveCount(long accountId,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return Task.FromResult(accountLeases.Values.Count(lease =>
            lease.AccountId == accountId && lease.ExpiresAt > now));
    }

    public Task<bool> TryAcquireUserSlot(long userId, string leaseToken,
        string requestId, string siloId, DateTime expiresAt, int maxConcurrency,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var active = userLeases.Values.Count(lease =>
            lease.UserId == userId && lease.ExpiresAt > now);
        if (active >= maxConcurrency)
            return Task.FromResult(false);

        userLeases[leaseToken] = new UserLease(
            userId, leaseToken, requestId, siloId, expiresAt);
        return Task.FromResult(true);
    }

    public Task ReleaseUserSlot(string leaseToken, string siloId,
        CancellationToken ct = default)
    {
        userLeases.TryRemove(leaseToken, out _);
        return Task.CompletedTask;
    }

    public Task<int> GetUserActiveCount(long userId,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return Task.FromResult(userLeases.Values.Count(lease =>
            lease.UserId == userId && lease.ExpiresAt > now));
    }

    public Task<AccountHealthState?> GetAccountHealthAsync(long accountId,
        CancellationToken ct = default)
    {
        health.TryGetValue(accountId, out var state);
        return Task.FromResult(state);
    }

    public Task UpdateAccountHealthAsync(long accountId,
        Action<AccountHealthState> mutate, CancellationToken ct = default)
    {
        var state = health.GetOrAdd(accountId,
            id => new AccountHealthState { AccountId = id });
        mutate(state);
        return Task.CompletedTask;
    }

    private sealed record AccountLease(
        long AccountId, string LeaseToken, string RequestId, string SiloId, DateTime ExpiresAt);

    private sealed record UserLease(
        long UserId, string LeaseToken, string RequestId, string SiloId, DateTime ExpiresAt);
}
