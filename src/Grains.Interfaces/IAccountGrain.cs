namespace ScalaAPI.Grains.Interfaces;

public interface ICredentialProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}

[GenerateSerializer]
public record AccountProjection(
    long Id, string Name, string Platform, int Priority,
    int Concurrency, int CurrentLoad, bool Schedulable,
    decimal RateMultiplier, int LoadFactor, string Status,
    long? RateLimitResetAt, long? OverloadUntil,
    long? TempUnschedulableUntil, string[] SupportedModels,
    long? CredentialExpiresAt = null, string CredentialStatus = "static",
    int CredentialVersion = 0, string? CredentialRefreshError = null,
    long? QuotaExpiresAtUnixSeconds = null, string QuotaTier = "",
    decimal? QuotaRemaining = null);

[GenerateSerializer]
public record AccountCredentials(
    long Id, string Platform, string Type, string BaseUrl,
    Dictionary<string, string> AuthHeaders, string? ProxyUrl,
    string? ProxyUsername, string? ProxyPassword,
    bool TlsFingerprint, string? TlsFingerprintProfileId,
    Dictionary<string, string> ModelMapping);

[GenerateSerializer]
public record ProviderOAuthCredentialProjection(
    string TokenEndpoint, string ClientId, long ExpiresAtUnixSeconds,
    string HeaderName, string HeaderScheme, string? Scope,
    int Version, long? LastRefreshedAtUnixSeconds, string? LastRefreshError,
    long? RevokedAtUnixSeconds = null, string? RevocationReason = null);

[GenerateSerializer]
public record AccountDetails(
    long Id, string Name, string Platform, string Type, string BaseUrl,
    int Priority, int Concurrency, int LoadFactor, decimal RateMultiplier,
    bool Schedulable, bool HasStaticCredentials,
    Dictionary<string, string> ModelMapping, string[] SupportedModels,
    string? ProxyUrl, bool HasProxyCredentials,
    bool TlsFingerprint, string? TlsFingerprintProfileId,
    ProviderOAuthCredentialProjection? OAuth);

[GenerateSerializer]
public record ProviderOAuthCredential(
    string TokenEndpoint, string ClientId, string ClientSecret,
    string RefreshToken, string AccessToken, long ExpiresAtUnixSeconds,
    string HeaderName = "Authorization", string HeaderScheme = "Bearer",
    string? Scope = null);

[GenerateSerializer]
public record ProviderOAuthRefreshLease(
    string Status, string? LeaseId, int Version, string? TokenEndpoint,
    string? ClientId, string? ClientSecret, string? RefreshToken,
    string? Scope, string? Error);

[GenerateSerializer]
public record SlotResult(bool Acquired, string? LeaseToken, int CurrentLoad, int MaxConcurrency);

[GenerateSerializer]
public record ErrorInfo(int StatusCode, int? RetryAfterMs, string? Message);

[GenerateSerializer]
public record HealthReport(
    bool Schedulable, int ConsecutiveErrors,
    DateTime? RateLimitResetAt, DateTime? OverloadUntil,
    DateTime? TempUnschedulableUntil, bool DisabledPermanently,
    string? DisableReason, DateTime? LastSuccessAt);

[GenerateSerializer]
public record AccountUpsert(
    string Name, string Platform, string Type, string BaseUrl,
    int Priority, int Concurrency, int LoadFactor, decimal RateMultiplier,
    bool Schedulable, Dictionary<string, string> Credentials,
    Dictionary<string, string> ModelMapping, string[] SupportedModels,
    string? ProxyUrl, bool TlsFingerprint,
    string? ProxyUsername = null, string? ProxyPassword = null,
    string? TlsFingerprintProfileId = null,
    ProviderOAuthCredential? OAuth = null);

public interface IAccountGrain : IGrainWithIntegerKey
{
    Task<AccountProjection> GetProjection();
    Task<AccountDetails> GetDetails();
    Task<AccountCredentials> Hydrate();
    Task<ProviderOAuthRefreshLease> BeginOAuthRefresh(
        long nowUnixSeconds, int refreshSkewSeconds, int leaseSeconds);
    Task<bool> CompleteOAuthRefresh(string leaseId, string accessToken,
        string? refreshToken, long expiresAtUnixSeconds, string tokenType);
    Task<bool> RevokeOAuthCredential(string leaseId, string reason);
    Task FailOAuthRefresh(string leaseId, string error, long retryAfterUnixMilliseconds);
    Task<SlotResult> TryAcquireSlot(string requestId, int maxConcurrency);
    Task<SlotResult> TryAcquireSlot(string leaseToken, DateTime expiresAt, int maxConcurrency);
    Task ReleaseSlot(string requestId);
    Task<int> GetLoad();
    Task<HealthReport> GetHealthReport();
    Task ReportUpstreamError(ErrorInfo error);
    Task ReportSuccess();
    Task RecordRpm();

    Task Create(AccountUpsert input);
    Task Update(AccountUpsert input);
    Task SetStatus(string status);
    Task Delete();
}
