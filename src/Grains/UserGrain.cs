using Microsoft.Extensions.Logging;
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
    [Id(4)] public decimal FrozenBalance { get; set; }
    [Id(5)] public int Concurrency { get; set; } = 1;
    [Id(6)] public int RpmLimit { get; set; }
    [Id(7)] public long[] AllowedGroups { get; set; } = [];
    [Id(8)] public HashSet<string> FinalizedLeases { get; set; } = [];
}

public class UserGrain : Grain, IUserGrain
{
    private readonly IPersistentState<UserState> _state;
    private readonly ILogger<UserGrain> _logger;
    private readonly IInvalidationService _invalidation;
    private readonly Dictionary<string, long> _activeSlots = new();
    private readonly Dictionary<string, decimal> _holds = new();
    private int _rpmCount;
    private long _rpmWindowStart;

    public UserGrain(
        [PersistentState("user", "postgres")] IPersistentState<UserState> state,
        ILogger<UserGrain> logger,
        IInvalidationService invalidation)
    {
        _state = state;
        _logger = logger;
        _invalidation = invalidation;
    }

    public Task<UserProjection> GetAuthProjection()
    {
        var s = _state.State;
        return Task.FromResult(new UserProjection(
            s.Id, s.Status, s.Role, s.Balance - s.FrozenBalance,
            s.Concurrency, s.AllowedGroups, s.RpmLimit));
    }

    public Task<SlotResult> TryAcquireSlot(string requestId)
    {
        return TryAcquireSlot(requestId, DateTime.UtcNow.AddMinutes(10));
    }

    public Task<SlotResult> TryAcquireSlot(string requestId, DateTime expiresAt)
    {
        var max = _state.State.Concurrency;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var expired in _activeSlots.Where(x => x.Value <= now).Select(x => x.Key).ToArray())
            _activeSlots.Remove(expired);
        if (_activeSlots.Count >= max)
            return Task.FromResult(new SlotResult(false, null, _activeSlots.Count, max));

        _activeSlots[requestId] = new DateTimeOffset(expiresAt).ToUnixTimeMilliseconds();
        return Task.FromResult(new SlotResult(true, requestId, _activeSlots.Count, max));
    }

    public Task ReleaseSlot(string requestId)
    {
        _activeSlots.Remove(requestId);
        return Task.CompletedTask;
    }

    public async Task<HoldHandle?> ReserveBalance(decimal amount)
    {
        amount = Math.Max(0m, amount);
        var s = _state.State;
        var available = s.Balance - s.FrozenBalance;
        if (available < amount)
            return null;

        var handleId = Guid.NewGuid().ToString("N");
        s.FrozenBalance += amount;
        _holds[handleId] = amount;
        try
        {
            await _state.WriteStateAsync();
        }
        catch
        {
            s.FrozenBalance -= amount;
            _holds.Remove(handleId);
            throw;
        }
        return new HoldHandle(handleId, amount);
    }

    public async Task CommitUsage(HoldHandle handle, decimal actual)
    {
        var s = _state.State;
        s.FrozenBalance = Math.Max(0m, s.FrozenBalance - Math.Max(0m, handle.Amount));
        var charge = Math.Min(Math.Max(0m, actual), Math.Max(0m, s.Balance));
        if (charge != actual)
            _logger.LogError("Billing anomaly for user {UserId}: requested {Actual} with balance {Balance}",
                s.Id, actual, s.Balance);
        s.Balance -= charge;
        _holds.Remove(handle.Id);
        await _state.WriteStateAsync();
    }

    public async Task ReleaseHold(HoldHandle handle)
    {
        _state.State.FrozenBalance = Math.Max(0m,
            _state.State.FrozenBalance - Math.Max(0m, handle.Amount));
        _holds.Remove(handle.Id);
        await _state.WriteStateAsync();
    }

    public async Task CompleteLease(string leaseToken, string requestId,
        HoldHandle? handle, decimal actual)
    {
        var s = _state.State;
        if (!s.FinalizedLeases.Add(leaseToken)) return;

        var hadSlot = _activeSlots.Remove(leaseToken) || _activeSlots.Remove(requestId);
        var previousFrozen = s.FrozenBalance;
        var previousBalance = s.Balance;
        if (handle is not null)
        {
            s.FrozenBalance = Math.Max(0m, s.FrozenBalance - handle.Amount);
            _holds.Remove(handle.Id);
        }
        var charge = Math.Min(Math.Max(0m, actual), Math.Max(0m, s.Balance));
        if (charge != actual)
            _logger.LogError("Billing anomaly for user {UserId}: requested {Actual} with balance {Balance}",
                s.Id, actual, s.Balance);
        s.Balance -= charge;
        try
        {
            await _state.WriteStateAsync();
        }
        catch
        {
            s.FinalizedLeases.Remove(leaseToken);
            s.FrozenBalance = previousFrozen;
            s.Balance = previousBalance;
            if (hadSlot) _activeSlots[leaseToken] = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds();
            if (handle is not null) _holds[handle.Id] = handle.Amount;
            throw;
        }
    }

    public async Task AbortLease(string leaseToken, string requestId, HoldHandle? handle)
    {
        var s = _state.State;
        if (!s.FinalizedLeases.Add(leaseToken)) return;

        var hadSlot = _activeSlots.Remove(leaseToken) || _activeSlots.Remove(requestId);
        var previousFrozen = s.FrozenBalance;
        if (handle is not null)
        {
            s.FrozenBalance = Math.Max(0m, s.FrozenBalance - handle.Amount);
            _holds.Remove(handle.Id);
        }
        try
        {
            await _state.WriteStateAsync();
        }
        catch
        {
            s.FinalizedLeases.Remove(leaseToken);
            s.FrozenBalance = previousFrozen;
            if (hadSlot) _activeSlots[leaseToken] = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds();
            if (handle is not null) _holds[handle.Id] = handle.Amount;
            throw;
        }
    }

    public Task<bool> CheckBalance(decimal required)
    {
        var available = _state.State.Balance - _state.State.FrozenBalance;
        return Task.FromResult(available >= required);
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

    public async Task Create(UserUpsert input)
    {
        var s = _state.State;
        s.Id = this.GetPrimaryKeyLong();
        s.Role = input.Role;
        s.Balance = input.Balance;
        s.Concurrency = input.Concurrency;
        s.RpmLimit = input.RpmLimit;
        s.AllowedGroups = input.AllowedGroups;
        await _state.WriteStateAsync();
        _invalidation.NotifyChange("user", s.Id.ToString());
    }

    public async Task Update(UserUpsert input)
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

    public async Task AdjustBalance(decimal delta)
    {
        _state.State.Balance = Math.Max(0m, _state.State.Balance + delta);
        await _state.WriteStateAsync();
    }

    public async Task Delete()
    {
        var id = _state.State.Id;
        await _state.ClearStateAsync();
        _invalidation.NotifyChange("user", id.ToString());
    }
}
