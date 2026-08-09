using Orleans;
using Orleans.Runtime;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Grains;

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

    public Task<Dictionary<string, string>> Get() =>
        Task.FromResult(new Dictionary<string, string>(_state.State.Settings));

    public Task<ConfigSnapshot> GetSnapshot() => Task.FromResult(Snapshot());

    public async Task<ConfigSnapshot> Update(string key, string value, long? expectedVersion = null)
    {
        ConfigValidation.Validate(key, value);
        if (expectedVersion.HasValue && expectedVersion.Value != _state.State.Version)
            throw new InvalidOperationException("config_version_conflict");
        _state.State.Settings[key] = value;
        _state.State.Version++;
        await _state.WriteStateAsync();
        return Snapshot();
    }

    public Task<long> GetVersion() => Task.FromResult(_state.State.Version);

    private ConfigSnapshot Snapshot() => new(
        new Dictionary<string, string>(_state.State.Settings), _state.State.Version);
}
