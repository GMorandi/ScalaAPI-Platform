using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Grains;

[GenerateSerializer]
public class UsageState
{
    [Id(0)] public long TotalInputTokens { get; set; }
    [Id(1)] public long TotalOutputTokens { get; set; }
    [Id(2)] public decimal TotalCost { get; set; }
    [Id(3)] public long WindowStart { get; set; }
    [Id(4)] public int PendingEvents { get; set; }
}

public class UsageGrain : Grain, IUsageGrain
{
    private readonly IPersistentState<UsageState> _state;
    private readonly ILogger<UsageGrain> _logger;

    public UsageGrain(
        [PersistentState("usage", "postgres")] IPersistentState<UsageState> state,
        ILogger<UsageGrain> logger)
    {
        _state = state;
        _logger = logger;
    }

    public Task Record(UsageEventData e)
    {
        _state.State.TotalInputTokens += e.InputTokens;
        _state.State.TotalOutputTokens += e.OutputTokens;
        _state.State.PendingEvents++;
        return Task.CompletedTask;
    }

    public async Task Flush()
    {
        if (_state.State.PendingEvents == 0) return;
        await _state.WriteStateAsync();
        _state.State.PendingEvents = 0;
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
    {
        await Flush();
        await base.OnDeactivateAsync(reason, ct);
    }
}
