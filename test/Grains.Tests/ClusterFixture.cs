using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans.TestingHost;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Grains.Tests;

public class ClusterFixture : IAsyncLifetime
{
    public TestCluster Cluster { get; private set; } = null!;
    public static IInvalidationService InvalidationService { get; } = Substitute.For<IInvalidationService>();
    public static ICredentialProtector CredentialProtector { get; } =
        Substitute.For<ICredentialProtector>();
    public static InMemorySlotLeaseStore SlotLeaseStore { get; } = new();

    public async Task InitializeAsync()
    {
        CredentialProtector.Protect(Arg.Any<string>())
            .Returns(call => $"protected:{call.Arg<string>()}");
        CredentialProtector.Unprotect(Arg.Any<string>())
            .Returns(call =>
            {
                var value = call.Arg<string>();
                if (!value.StartsWith("protected:", StringComparison.Ordinal))
                    throw new InvalidOperationException("Credential was not protected");
                return value[10..];
            });
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public async Task DisposeAsync() => await Cluster.StopAllSilosAsync();

    private class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage("postgres");
            siloBuilder.ConfigureServices(services =>
            {
                services.AddSingleton(InvalidationService);
                services.AddSingleton(CredentialProtector);
                services.AddSingleton<ISlotLeaseStore>(SlotLeaseStore);
            });
        }
    }
}

public sealed class InMemorySlotLeaseStore : ISlotLeaseStore
{
    private readonly ConcurrentDictionary<string, AccountLease> _accountLeases = new();
    private readonly ConcurrentDictionary<string, UserLease> _userLeases = new();
    private readonly ConcurrentDictionary<long, AccountHealthState> _health = new();

    public Task<bool> TryAcquireAccountSlot(long accountId, string leaseToken, string requestId,
        string siloId, DateTime expiresAt, int maxConcurrency, CancellationToken ct = default)
    {
        var active = _accountLeases.Values.Count(l => l.AccountId == accountId && l.ExpiresAt > DateTime.UtcNow);
        if (active >= maxConcurrency)
            return Task.FromResult(false);
        _accountLeases[leaseToken] = new AccountLease(accountId, leaseToken, requestId, siloId, expiresAt);
        return Task.FromResult(true);
    }

    public Task ReleaseAccountSlot(string leaseToken, string siloId, CancellationToken ct = default)
    {
        _accountLeases.TryRemove(leaseToken, out _);
        return Task.CompletedTask;
    }

    public Task<int> ReclaimExpiredAccountSlots(long accountId, string siloId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var reclaimed = 0;
        foreach (var kv in _accountLeases)
        {
            if (kv.Value.AccountId == accountId && kv.Value.ExpiresAt <= now)
            {
                if (_accountLeases.TryRemove(kv.Key, out _))
                    reclaimed++;
            }
        }
        return Task.FromResult(reclaimed);
    }

    public Task<int> GetAccountActiveCount(long accountId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return Task.FromResult(_accountLeases.Values.Count(l => l.AccountId == accountId && l.ExpiresAt > now));
    }

    public Task<bool> TryAcquireUserSlot(long userId, string leaseToken, string requestId,
        string siloId, DateTime expiresAt, int maxConcurrency, CancellationToken ct = default)
    {
        var active = _userLeases.Values.Count(l => l.UserId == userId && l.ExpiresAt > DateTime.UtcNow);
        if (active >= maxConcurrency)
            return Task.FromResult(false);
        _userLeases[leaseToken] = new UserLease(userId, leaseToken, requestId, siloId, expiresAt);
        return Task.FromResult(true);
    }

    public Task ReleaseUserSlot(string leaseToken, string siloId, CancellationToken ct = default)
    {
        _userLeases.TryRemove(leaseToken, out _);
        return Task.CompletedTask;
    }

    public Task<int> GetUserActiveCount(long userId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return Task.FromResult(_userLeases.Values.Count(l => l.UserId == userId && l.ExpiresAt > now));
    }

    public Task<AccountHealthState?> GetAccountHealthAsync(long accountId, CancellationToken ct = default)
    {
        _health.TryGetValue(accountId, out var state);
        return Task.FromResult(state);
    }

    public Task UpdateAccountHealthAsync(long accountId, Action<AccountHealthState> mutate,
        CancellationToken ct = default)
    {
        var state = _health.GetOrAdd(accountId, id => new AccountHealthState { AccountId = id });
        mutate(state);
        return Task.CompletedTask;
    }

    private sealed record AccountLease(long AccountId, string LeaseToken, string RequestId, string SiloId, DateTime ExpiresAt);
    private sealed record UserLease(long UserId, string LeaseToken, string RequestId, string SiloId, DateTime ExpiresAt);
}

[CollectionDefinition("Cluster")]
public class ClusterCollection : ICollectionFixture<ClusterFixture>;
