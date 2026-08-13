using Orleans;
using Orleans.Runtime;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Grains;

[GenerateSerializer]
public class UserState
{
    [Id(0)] public long Id { get; set; }
    [Id(1)] public string Status { get; set; } = "active";
    [Id(2)] public string Role { get; set; } = "user";
    [Id(3)] public decimal Balance { get; set; }
    [Id(5)] public int Concurrency { get; set; } = 1;
    [Id(6)] public int RpmLimit { get; set; }
    [Id(7)] public long[] AllowedGroups { get; set; } = [];
    [Id(8)] public HashSet<string> FinalizedLeases { get; set; } = [];
    [Id(10)] public long BalanceVersion { get; set; }
}

public class UserGrain : Grain, IUserGrain
{
    private readonly IPersistentState<UserState> _state;
    private readonly IInvalidationService _invalidation;
    private readonly ISlotLeaseStore _slotLeaseStore;
    private int _rpmCount;
    private long _rpmWindowStart;

    public UserGrain(
        [PersistentState("user", "postgres")] IPersistentState<UserState> state,
        IInvalidationService invalidation,
        ISlotLeaseStore slotLeaseStore)
    {
        _state = state;
        _invalidation = invalidation;
        _slotLeaseStore = slotLeaseStore;
    }

    public Task<UserProjection> GetAuthProjection()
    {
        var s = _state.State;
        return Task.FromResult(new UserProjection(
            s.Id, s.Status, s.Role, s.Balance,
            s.Concurrency, s.AllowedGroups, s.RpmLimit));
    }

    public async Task<SlotResult> TryAcquireSlot(string requestId)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(10);
        return await TryAcquireSlot(requestId, expiresAt);
    }

    public async Task<SlotResult> TryAcquireSlot(string requestId, DateTime expiresAt)
    {
        var userId = this.GetPrimaryKeyLong();
        var max = _state.State.Concurrency;
        var leaseToken = Guid.NewGuid().ToString("N");
        var siloId = this.RuntimeIdentity;
        var acquired = await _slotLeaseStore.TryAcquireUserSlot(
            userId, leaseToken, requestId, siloId, expiresAt, max);
        var currentLoad = await _slotLeaseStore.GetUserActiveCount(userId);
        return new SlotResult(acquired, acquired ? leaseToken : null, currentLoad, max);
    }

    public async Task ReleaseSlot(string requestId)
    {
        var siloId = this.RuntimeIdentity;
        await _slotLeaseStore.ReleaseUserSlot(requestId, siloId);
    }

    public async Task FinalizeLease(string leaseToken, string requestId)
    {
        var s = _state.State;
        if (!s.FinalizedLeases.Add(leaseToken)) return;

        try
        {
            await _state.WriteStateAsync();
        }
        catch
        {
            s.FinalizedLeases.Remove(leaseToken);
            throw;
        }

        // Release the persistent slot now that the lease is finalized
        var siloId = this.RuntimeIdentity;
        await _slotLeaseStore.ReleaseUserSlot(leaseToken, siloId);
    }

    public Task<bool> CheckAndRecordRpm(int limit)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - _rpmWindowStart > 60_000)
        {
            _rpmCount = 0;
            _rpmWindowStart = now;
        }
        if (_rpmCount >= limit)
            return Task.FromResult(false);
        _rpmCount++;
        return Task.FromResult(true);
    }

    public async Task Create(UserCreate input)
    {
        var s = _state.State;
        s.Id = this.GetPrimaryKeyLong();
        s.Role = input.Role;
        s.Balance = 0m;
        s.BalanceVersion = 0;
        s.Concurrency = input.Concurrency;
        s.RpmLimit = input.RpmLimit;
        s.AllowedGroups = input.AllowedGroups;
        await _state.WriteStateAsync();
        _invalidation.NotifyChange("user", s.Id.ToString());
    }

    public async Task Update(UserConfiguration input)
    {
        var s = _state.State;
        s.Role = input.Role;
        s.Concurrency = input.Concurrency;
        s.RpmLimit = input.RpmLimit;
        s.AllowedGroups = input.AllowedGroups;
        await _state.WriteStateAsync();
        _invalidation.NotifyChange("user", s.Id.ToString());
    }

    public async Task SetStatus(string status)
    {
        _state.State.Status = status;
        await _state.WriteStateAsync();
        _invalidation.NotifyChange("user", _state.State.Id.ToString());
    }

    public Task<BalanceProjection> GetBalanceProjection()
    {
        var s = _state.State;
        return Task.FromResult(new BalanceProjection(s.BalanceVersion, s.Balance));
    }

    public async Task ApplyBalanceSnapshot(long version, decimal balance)
    {
        var s = _state.State;
        if (version < s.BalanceVersion)
            return;
        if (version == s.BalanceVersion && balance == s.Balance)
            return;

        var previousBalance = s.Balance;
        var previousVersion = s.BalanceVersion;
        s.Balance = balance;
        s.BalanceVersion = version;
        try
        {
            await _state.WriteStateAsync();
        }
        catch
        {
            s.Balance = previousBalance;
            s.BalanceVersion = previousVersion;
            throw;
        }
        _invalidation.NotifyChange("user", s.Id.ToString());
    }

    public async Task Delete()
    {
        var id = _state.State.Id;
        await _state.ClearStateAsync();
        _invalidation.NotifyChange("user", id.ToString());
    }
}
