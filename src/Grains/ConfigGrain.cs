using Orleans;
using Orleans.Runtime;
using Sub2Api.Grains.Interfaces;

namespace Sub2Api.Grains;

[GenerateSerializer]
public class ConfigState
{
    [Id(0)] public Dictionary<string, string> Settings { get; set; } = new();
    [Id(1)] public long Version { get; set; }
}

public class ConfigGrain : Grain, IConfigGrain
{
    private readonly IPersistentState<ConfigState> _state;

    public ConfigGrain([PersistentState("config", "postgres")] IPersistentState<ConfigState> state)
    {
        _state = state;
    }

    public Task<Dictionary<string, string>> Get() => Task.FromResult(_state.State.Settings);

    public async Task Update(string key, string value)
    {
        _state.State.Settings[key] = value;
        _state.State.Version++;
        await _state.WriteStateAsync();
    }

    public Task<long> GetVersion() => Task.FromResult(_state.State.Version);
}
