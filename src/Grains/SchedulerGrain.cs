using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Grains;

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

    private IProviderQuotaService? QuotaService =>
        ServiceProvider.GetService<IProviderQuotaService>();

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
            var quotaOk = await CheckQuotaForAccount(sticky.Value);
            if (quotaOk)
            {
                var slotResult = await TryAcquireOnAccount(sticky.Value, req.RequestId);
                if (slotResult is not null)
                    return slotResult;
            }
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
            // Check provider quota before attempting slot acquisition
            if (!await CheckQuotaForAccount(selected.Id))
                continue;

            var result = await TryAcquireOnAccount(selected.Id, req.RequestId);
            if (result is not null)
            {
                // Reserve quota after slot acquisition
                await ReserveQuotaForAccount(selected.Id);
                await BindSticky(req.SessionHash, selected.Id, TimeSpan.FromHours(1));
                return result;
            }
        }

        return new SelectionResult(SelectionOutcome.Wait, ordered[0].Id, null, 45_000, null);
    }

    /// <summary>
    /// Checks whether the account's quota allows scheduling. Returns true
    /// if the account can proceed (or if quota service is not configured).
    /// </summary>
    private async Task<bool> CheckQuotaForAccount(long accountId)
    {
        var quotaService = QuotaService;
        if (quotaService is null) return true;

        try
        {
            var check = await quotaService.CheckAsync(accountId, estimatedCost: 0m);
            return check.Status is QuotaCheckStatus.Ok
                or QuotaCheckStatus.UnknownTier
                or QuotaCheckStatus.NoSnapshot;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Quota check failed for account {AccountId}", accountId);
            return true; // Fail-open: don't block scheduling on quota service errors
        }
    }

    /// <summary>
    /// Attempts to reserve quota for the account before dispatch.
    /// </summary>
    private async Task ReserveQuotaForAccount(long accountId)
    {
        var quotaService = QuotaService;
        if (quotaService is null) return;

        try
        {
            await quotaService.ReserveAsync(accountId, estimatedCost: 0.001m);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Quota reservation failed for account {AccountId}", accountId);
        }
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
