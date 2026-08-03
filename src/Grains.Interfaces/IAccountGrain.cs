namespace Sub2Api.Grains.Interfaces;

public interface ICredentialProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}

[GenerateSerializer]
public record AccountProjection(
    long Id, string Name, string Platform, int Priority,
    int Concurrency, int CurrentLoad, bool Schedulable,
    double RateMultiplier, int LoadFactor, string Status,
    long? RateLimitResetAt, long? OverloadUntil,
    long? TempUnschedulableUntil, string[] SupportedModels);

[GenerateSerializer]
public record AccountCredentials(
    long Id, string Platform, string Type, string BaseUrl,
    Dictionary<string, string> AuthHeaders, string? ProxyUrl,
    bool TlsFingerprint, Dictionary<string, string> ModelMapping);

[GenerateSerializer]
public record SlotResult(bool Acquired, string? LeaseToken, int CurrentLoad, int MaxConcurrency);

[GenerateSerializer]
public record ErrorInfo(int StatusCode, int? RetryAfterMs, string? Message);

[GenerateSerializer]
public record AccountUpsert(
    string Name, string Platform, string Type, string BaseUrl,
    int Priority, int Concurrency, int LoadFactor, double RateMultiplier,
    bool Schedulable, Dictionary<string, string> Credentials,
    Dictionary<string, string> ModelMapping, string[] SupportedModels,
    string? ProxyUrl, bool TlsFingerprint);

[GenerateSerializer]
public record AccountMetadataUpsert(
    string Name, string Platform, string Type, string BaseUrl,
    int Priority, int Concurrency, int LoadFactor, double RateMultiplier,
    bool Schedulable, Dictionary<string, string> ModelMapping,
    string[] SupportedModels, string? ProxyUrl, bool TlsFingerprint);

public interface IAccountGrain : IGrainWithIntegerKey
{
    Task<AccountProjection> GetProjection();
    Task<AccountCredentials> Hydrate();
    Task<SlotResult> TryAcquireSlot(string requestId, int maxConcurrency);
    Task<SlotResult> TryAcquireSlot(string leaseToken, DateTime expiresAt, int maxConcurrency);
    Task ReleaseSlot(string requestId);
    Task<int> GetLoad();
    Task ReportUpstreamError(ErrorInfo error);
    Task ReportSuccess();
    Task RecordRpm();

    Task Create(AccountUpsert input);
    Task Update(AccountUpsert input);
    Task UpsertMetadata(AccountMetadataUpsert input);
    Task SetStatus(string status);
    Task Delete();
}
