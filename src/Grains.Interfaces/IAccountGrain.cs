namespace Sub2Api.Grains.Interfaces;

public record AccountProjection(
    long Id, string Name, string Platform, int Priority,
    int Concurrency, int CurrentLoad, bool Schedulable,
    double RateMultiplier, int LoadFactor, string Status,
    long? RateLimitResetAt, long? OverloadUntil,
    long? TempUnschedulableUntil, string[] SupportedModels);

public record AccountCredentials(
    long Id, string Platform, string Type, string BaseUrl,
    Dictionary<string, string> AuthHeaders, string? ProxyUrl,
    bool TlsFingerprint, Dictionary<string, string> ModelMapping);

public record SlotResult(bool Acquired, string? LeaseToken, int CurrentLoad, int MaxConcurrency);

public record ErrorInfo(int StatusCode, int? RetryAfterMs, string? Message);

public record AccountUpsert(
    string Name, string Platform, string Type, string BaseUrl,
    int Priority, int Concurrency, int LoadFactor, double RateMultiplier,
    bool Schedulable, Dictionary<string, string> Credentials,
    Dictionary<string, string> ModelMapping, string[] SupportedModels,
    string? ProxyUrl, bool TlsFingerprint);

public interface IAccountGrain : IGrainWithIntegerKey
{
    Task<AccountProjection> GetProjection();
    Task<AccountCredentials> Hydrate();
    Task<SlotResult> TryAcquireSlot(string requestId, int maxConcurrency);
    Task ReleaseSlot(string requestId);
    Task<int> GetLoad();
    Task ReportUpstreamError(ErrorInfo error);
    Task ReportSuccess();
    Task RecordRpm();

    Task Create(AccountUpsert input);
    Task Update(AccountUpsert input);
    Task SetStatus(string status);
    Task Delete();
}
