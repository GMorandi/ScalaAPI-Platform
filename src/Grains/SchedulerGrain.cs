using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Sub2Api.Grains.Interfaces;

namespace Sub2Api.Grains;

[GenerateSerializer]
public class SchedulerState
{
    [Id(0)] public Dictionary<string, StickyBinding> StickySessions { get; set; } = new();
}

[GenerateSerializer]
public class StickyBinding
{
    [Id(0)] public long AccountId { get; set; }
    [Id(1)] public long ExpiresAt { get; set; }
}

public class SchedulerGrain : Grain, ISchedulerGrain
{
    private readonly IPersistentState<SchedulerState> _state;
    private readonly ILogger<SchedulerGrain> _logger;

    public SchedulerGrain(
        [PersistentState("scheduler", "postgres")] IPersistentState<SchedulerState> state,
        ILogger<SchedulerGrain> logger)
    {
        _state = state;
        _logger = logger;
    }

    public async Task<SelectionResult> Select(SelectRequest req)
    {
        var groupId = this.GetPrimaryKeyLong();
        var groupGrain = GrainFactory.GetGrain<IGroupGrain>(groupId);

        // Layer 1: Model routing
        var candidateIds = await groupGrain.GetRoutingAccountIds(req.Model);
        if (candidateIds.Length == 0)
            candidateIds = await groupGrain.GetMemberAccountIds();

        candidateIds = candidateIds
            .Where(id => !req.ExcludedAccountIds.Contains(id))
            .ToArray();

        if (candidateIds.Length == 0)
            return new SelectionResult(SelectionOutcome.Rejected, null, null, null, "No available accounts");

        // Layer 1.5: Sticky session
        var sticky = await GetStickyAccount(req.SessionHash);
        if (sticky.HasValue && candidateIds.Contains(sticky.Value))
        {
            var slotResult = await TryAcquireOnAccount(sticky.Value, req.RequestId);
            if (slotResult is not null)
                return slotResult;
        }

        // Layer 2: Load-aware selection (priority -> load -> LRU)
        var projections = new List<(long Id, AccountProjection Proj)>();
        foreach (var id in candidateIds)
        {
            var acctGrain = GrainFactory.GetGrain<IAccountGrain>(id);
            var proj = await acctGrain.GetProjection();
            var platformMatches = string.IsNullOrWhiteSpace(req.ForcePlatform)
                || string.Equals(proj.Platform, req.ForcePlatform, StringComparison.OrdinalIgnoreCase);
            if (proj.Schedulable && platformMatches
                && GatewayCapabilityPolicy.Supports(proj.Platform, req.Capability))
                projections.Add((id, proj));
        }

        var ordered = projections
            .OrderBy(p => p.Proj.Priority)
            .ThenBy(p => (double)p.Proj.CurrentLoad / Math.Max(p.Proj.Concurrency, 1))
            .ToArray();

        if (ordered.Length == 0)
            return new SelectionResult(SelectionOutcome.Rejected, null, null, null, "All accounts exhausted");

        foreach (var selected in ordered)
        {
            var result = await TryAcquireOnAccount(selected.Id, req.RequestId);
            if (result is not null)
            {
                await BindSticky(req.SessionHash, selected.Id, TimeSpan.FromHours(1));
                return result;
            }
        }

        return new SelectionResult(SelectionOutcome.Wait, ordered[0].Id, null, 45_000, null);
    }

    private async Task<SelectionResult?> TryAcquireOnAccount(long accountId, string requestId)
    {
        var acctGrain = GrainFactory.GetGrain<IAccountGrain>(accountId);
        var proj = await acctGrain.GetProjection();
        var leaseToken = $"{requestId}:{Guid.NewGuid():N}";
        var slot = await acctGrain.TryAcquireSlot(leaseToken, DateTime.UtcNow.AddMinutes(10), proj.Concurrency);

        if (slot.Acquired)
            return new SelectionResult(SelectionOutcome.Ok, accountId, slot.LeaseToken, null, null);

        return null;
    }

    public Task<long?> GetStickyAccount(string sessionHash)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_state.State.StickySessions.TryGetValue(sessionHash, out var binding)
            && binding.ExpiresAt > now)
        {
            return Task.FromResult<long?>(binding.AccountId);
        }
        return Task.FromResult<long?>(null);
    }

    public Task BindSticky(string sessionHash, long accountId, TimeSpan ttl)
    {
        _state.State.StickySessions[sessionHash] = new StickyBinding
        {
            AccountId = accountId,
            ExpiresAt = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeMilliseconds()
        };
        return _state.WriteStateAsync();
    }

    public Task ClearSticky(string sessionHash)
    {
        _state.State.StickySessions.Remove(sessionHash);
        return _state.WriteStateAsync();
    }
}
