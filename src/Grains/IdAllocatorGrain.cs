using Orleans;
using Orleans.Runtime;
using Sub2Api.Grains.Interfaces;

namespace Sub2Api.Grains;

[GenerateSerializer]
public class IdAllocatorState
{
    [Id(0)] public long Counter { get; set; }
}

public class IdAllocatorGrain : Grain, IIdAllocatorGrain
{
    private readonly IPersistentState<IdAllocatorState> _state;

    public IdAllocatorGrain(
        [PersistentState("idAllocator", "postgres")] IPersistentState<IdAllocatorState> state)
    {
        _state = state;
    }

    public async Task<long> Next()
    {
        _state.State.Counter++;
        await _state.WriteStateAsync();
        return _state.State.Counter;
    }
}
