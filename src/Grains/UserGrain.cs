using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Sub2Api.Grains.Interfaces;

namespace Sub2Api.Grains;

[GenerateSerializer]
public class UserState
{
    [Id(0)] public long Id { get; set; }
    [Id(1)] public string Status { get; set; } = "active";
    [Id(2)] public string Role { get; set; } = "user";
    [Id(3)] public double Balance { get; set; }
    [Id(4)] public double FrozenBalance { get; set; }
    [Id(5)] public int Concurrency { get; set; } = 1;
    [Id(6)] public int RpmLimit { get; set; }
    [Id(7)] public long[] AllowedGroups { get; set; } = [];
}

public class UserGrain : Grain, IUserGrain
{
    private readonly IPersistentState<UserState> _state;
    private readonly ILogger<UserGrain> _logger;
    private readonly Dictionary<string, long> _activeSlots = new();
    private readonly Dictionary<string, decimal> _holds = new();

    public UserGrain(
        [PersistentState("user", "postgres")] IPersistentState<UserState> state,
        ILogger<UserGrain> logger)
    {
        _state = state;
        _logger = logger;
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
        var max = _state.State.Concurrency;
        if (_activeSlots.Count >= max)
            return Task.FromResult(new SlotResult(false, null, _activeSlots.Count, max));

        _activeSlots[requestId] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return Task.FromResult(new SlotResult(true, requestId, _activeSlots.Count, max));
    }

    public Task ReleaseSlot(string requestId)
    {
        _activeSlots.Remove(requestId);
        return Task.CompletedTask;
    }

    public Task<HoldHandle?> ReserveBalance(decimal amount)
    {
        var s = _state.State;
        var available = (decimal)(s.Balance - s.FrozenBalance);
        if (available < amount)
            return Task.FromResult<HoldHandle?>(null);

        var handleId = Guid.NewGuid().ToString("N");
        s.FrozenBalance += (double)amount;
        _holds[handleId] = amount;
        return Task.FromResult<HoldHandle?>(new HoldHandle(handleId, amount));
    }

    public async Task CommitUsage(HoldHandle handle, decimal actual)
    {
        var s = _state.State;
        s.FrozenBalance -= (double)handle.Amount;
        s.Balance -= (double)actual;
        _holds.Remove(handle.Id);
        await _state.WriteStateAsync();
    }

    public async Task ReleaseHold(HoldHandle handle)
    {
        _state.State.FrozenBalance -= (double)handle.Amount;
        _holds.Remove(handle.Id);
        await _state.WriteStateAsync();
    }

    public Task<bool> CheckBalance(decimal required)
    {
        var available = (decimal)(_state.State.Balance - _state.State.FrozenBalance);
        return Task.FromResult(available >= required);
    }
}
