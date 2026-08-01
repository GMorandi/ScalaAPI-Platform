using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Sub2Api.Grains.Interfaces;

namespace Sub2Api.Grains;

[GenerateSerializer]
public class AccountState
{
    [Id(0)] public long Id { get; set; }
    [Id(1)] public string Name { get; set; } = "";
    [Id(2)] public string Platform { get; set; } = "";
    [Id(3)] public string Type { get; set; } = "";
    [Id(4)] public string Status { get; set; } = "active";
    [Id(5)] public int Priority { get; set; }
    [Id(6)] public int Concurrency { get; set; } = 1;
    [Id(7)] public int LoadFactor { get; set; } = 1;
    [Id(8)] public double RateMultiplier { get; set; } = 1.0;
    [Id(9)] public bool Schedulable { get; set; } = true;
    [Id(10)] public long? RateLimitResetAt { get; set; }
    [Id(11)] public long? OverloadUntil { get; set; }
    [Id(12)] public long? TempUnschedulableUntil { get; set; }
    [Id(13)] public string BaseUrl { get; set; } = "";
    [Id(14)] public Dictionary<string, string> Credentials { get; set; } = new();
    [Id(15)] public Dictionary<string, string> ModelMapping { get; set; } = new();
    [Id(16)] public string[] SupportedModels { get; set; } = [];
    [Id(17)] public string? ProxyUrl { get; set; }
    [Id(18)] public bool TlsFingerprint { get; set; }
}

public class AccountGrain : Grain, IAccountGrain
{
    private readonly IPersistentState<AccountState> _state;
    private readonly ILogger<AccountGrain> _logger;
    private readonly IInvalidationService _invalidation;

    private readonly Dictionary<string, long> _activeSlots = new();
    private int _rpmCount;
    private long _rpmWindowStart;

    public AccountGrain(
        [PersistentState("account", "postgres")] IPersistentState<AccountState> state,
        ILogger<AccountGrain> logger,
        IInvalidationService invalidation)
    {
        _state = state;
        _logger = logger;
        _invalidation = invalidation;
    }

    public Task<AccountProjection> GetProjection()
    {
        var s = _state.State;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var schedulable = s.Schedulable && s.Status == "active"
            && (s.RateLimitResetAt is null || s.RateLimitResetAt < now)
            && (s.OverloadUntil is null || s.OverloadUntil < now)
            && (s.TempUnschedulableUntil is null || s.TempUnschedulableUntil < now);

        return Task.FromResult(new AccountProjection(
            s.Id, s.Name, s.Platform, s.Priority, s.Concurrency,
            _activeSlots.Count, schedulable, s.RateMultiplier,
            s.LoadFactor, s.Status, s.RateLimitResetAt,
            s.OverloadUntil, s.TempUnschedulableUntil, s.SupportedModels));
    }

    public Task<AccountCredentials> Hydrate()
    {
        var s = _state.State;
        return Task.FromResult(new AccountCredentials(
            s.Id, s.Platform, s.Type, s.BaseUrl,
            s.Credentials, s.ProxyUrl, s.TlsFingerprint, s.ModelMapping));
    }

    public Task<SlotResult> TryAcquireSlot(string requestId, int maxConcurrency)
    {
        if (_activeSlots.Count >= maxConcurrency)
        {
            return Task.FromResult(new SlotResult(false, null, _activeSlots.Count, maxConcurrency));
        }

        var lease = $"{requestId}:{Guid.NewGuid():N}";
        _activeSlots[requestId] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return Task.FromResult(new SlotResult(true, lease, _activeSlots.Count, maxConcurrency));
    }

    public Task ReleaseSlot(string requestId)
    {
        _activeSlots.Remove(requestId);
        return Task.CompletedTask;
    }

    public Task<int> GetLoad() => Task.FromResult(_activeSlots.Count);

    public async Task ReportUpstreamError(ErrorInfo error)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var s = _state.State;

        switch (error.StatusCode)
        {
            case 429:
                s.RateLimitResetAt = now + (error.RetryAfterMs ?? 60_000);
                break;
            case 401 or 403:
                s.TempUnschedulableUntil = now + 300_000;
                break;
            case >= 500:
                s.OverloadUntil = now + 30_000;
                break;
        }

        await _state.WriteStateAsync();
        _logger.LogWarning("Account {Id} error {Code}, unschedulable until reset",
            s.Id, error.StatusCode);
    }

    public Task ReportSuccess() => Task.CompletedTask;

    public Task RecordRpm()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - _rpmWindowStart > 60_000)
        {
            _rpmCount = 0;
            _rpmWindowStart = now;
        }
        _rpmCount++;
        return Task.CompletedTask;
    }

    public async Task Create(AccountUpsert input)
    {
        var s = _state.State;
        s.Id = this.GetPrimaryKeyLong();
        s.Name = input.Name;
        s.Platform = input.Platform;
        s.Type = input.Type;
        s.BaseUrl = input.BaseUrl;
        s.Priority = input.Priority;
        s.Concurrency = input.Concurrency;
        s.LoadFactor = input.LoadFactor;
        s.RateMultiplier = input.RateMultiplier;
        s.Schedulable = input.Schedulable;
        s.Credentials = input.Credentials;
        s.ModelMapping = input.ModelMapping;
        s.SupportedModels = input.SupportedModels;
        s.ProxyUrl = input.ProxyUrl;
        s.TlsFingerprint = input.TlsFingerprint;
        await _state.WriteStateAsync();
        _invalidation.NotifyChange("account", s.Id.ToString());
    }

    public async Task Update(AccountUpsert input)
    {
        var s = _state.State;
        s.Name = input.Name;
        s.Platform = input.Platform;
        s.Type = input.Type;
        s.BaseUrl = input.BaseUrl;
        s.Priority = input.Priority;
        s.Concurrency = input.Concurrency;
        s.LoadFactor = input.LoadFactor;
        s.RateMultiplier = input.RateMultiplier;
        s.Schedulable = input.Schedulable;
        s.Credentials = input.Credentials;
        s.ModelMapping = input.ModelMapping;
        s.SupportedModels = input.SupportedModels;
        s.ProxyUrl = input.ProxyUrl;
        s.TlsFingerprint = input.TlsFingerprint;
        await _state.WriteStateAsync();
        _invalidation.NotifyChange("account", s.Id.ToString());
    }

    public async Task SetStatus(string status)
    {
        _state.State.Status = status;
        await _state.WriteStateAsync();
        _invalidation.NotifyChange("account", _state.State.Id.ToString());
    }

    public async Task Delete()
    {
        var id = _state.State.Id;
        await _state.ClearStateAsync();
        _invalidation.NotifyChange("account", id.ToString());
    }
}
