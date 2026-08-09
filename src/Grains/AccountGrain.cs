using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Grains;

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
    [Id(8)] public decimal RateMultiplier { get; set; } = 1.0m;
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
    [Id(19)] public ProviderOAuthState? OAuth { get; set; }
}

[GenerateSerializer]
public class ProviderOAuthState
{
    [Id(0)] public string TokenEndpoint { get; set; } = "";
    [Id(1)] public string ClientId { get; set; } = "";
    [Id(2)] public string ClientSecret { get; set; } = "";
    [Id(3)] public string RefreshToken { get; set; } = "";
    [Id(4)] public string AccessToken { get; set; } = "";
    [Id(5)] public long ExpiresAtUnixSeconds { get; set; }
    [Id(6)] public string HeaderName { get; set; } = "Authorization";
    [Id(7)] public string HeaderScheme { get; set; } = "Bearer";
    [Id(8)] public string? Scope { get; set; }
    [Id(9)] public int Version { get; set; } = 1;
    [Id(10)] public string? RefreshLeaseId { get; set; }
    [Id(11)] public long RefreshLeaseUntilUnixSeconds { get; set; }
    [Id(12)] public long? LastRefreshedAtUnixSeconds { get; set; }
    [Id(13)] public string? LastRefreshError { get; set; }
}

public class AccountGrain : Grain, IAccountGrain
{
    private readonly IPersistentState<AccountState> _state;
    private readonly ILogger<AccountGrain> _logger;
    private readonly IInvalidationService _invalidation;
    private readonly ICredentialProtector _credentialProtector;

    private readonly Dictionary<string, long> _activeSlots = new();
    private int _rpmCount;
    private long _rpmWindowStart;

    public AccountGrain(
        [PersistentState("account", "postgres")] IPersistentState<AccountState> state,
        ILogger<AccountGrain> logger,
        IInvalidationService invalidation,
        ICredentialProtector credentialProtector)
    {
        _state = state;
        _logger = logger;
        _invalidation = invalidation;
        _credentialProtector = credentialProtector;
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
            s.OverloadUntil, s.TempUnschedulableUntil, s.SupportedModels,
            s.OAuth?.ExpiresAtUnixSeconds,
            s.OAuth is null ? "static"
                : s.OAuth.LastRefreshError is null ? "oauth" : "refresh_error",
            s.OAuth?.Version ?? 0, s.OAuth?.LastRefreshError));
    }

    public Task<AccountCredentials> Hydrate()
    {
        var s = _state.State;
        var headers = s.Credentials.ToDictionary(kv => kv.Key,
            kv => _credentialProtector.Unprotect(kv.Value));
        if (s.OAuth is not null)
        {
            var accessToken = _credentialProtector.Unprotect(s.OAuth.AccessToken);
            headers[s.OAuth.HeaderName] = string.IsNullOrWhiteSpace(s.OAuth.HeaderScheme)
                ? accessToken : $"{s.OAuth.HeaderScheme} {accessToken}";
        }
        return Task.FromResult(new AccountCredentials(
            s.Id, s.Platform, s.Type, s.BaseUrl,
            headers,
            s.ProxyUrl, s.TlsFingerprint, s.ModelMapping));
    }

    public Task<AccountDetails> GetDetails()
    {
        var s = _state.State;
        var oauth = s.OAuth is null ? null : new ProviderOAuthCredentialProjection(
            s.OAuth.TokenEndpoint, s.OAuth.ClientId, s.OAuth.ExpiresAtUnixSeconds,
            s.OAuth.HeaderName, s.OAuth.HeaderScheme, s.OAuth.Scope,
            s.OAuth.Version, s.OAuth.LastRefreshedAtUnixSeconds,
            s.OAuth.LastRefreshError);
        return Task.FromResult(new AccountDetails(
            s.Id, s.Name, s.Platform, s.Type, s.BaseUrl, s.Priority,
            s.Concurrency, s.LoadFactor, s.RateMultiplier, s.Schedulable,
            s.Credentials.Count > 0, new(s.ModelMapping), [.. s.SupportedModels],
            s.ProxyUrl, s.TlsFingerprint, oauth));
    }

    public async Task<ProviderOAuthRefreshLease> BeginOAuthRefresh(
        long nowUnixSeconds, int refreshSkewSeconds, int leaseSeconds)
    {
        var oauth = _state.State.OAuth;
        if (oauth is null)
            return new("static", null, 0, null, null, null, null, null, null);
        if (oauth.ExpiresAtUnixSeconds > nowUnixSeconds + Math.Max(0, refreshSkewSeconds))
            return new("fresh", null, oauth.Version, null, null, null, null, null, null);
        if (!string.IsNullOrWhiteSpace(oauth.RefreshLeaseId)
            && oauth.RefreshLeaseUntilUnixSeconds > nowUnixSeconds)
            return new("in_progress", null, oauth.Version, null, null, null, null, null, null);
        if (!Uri.TryCreate(oauth.TokenEndpoint, UriKind.Absolute, out _)
            || string.IsNullOrWhiteSpace(oauth.ClientId)
            || string.IsNullOrWhiteSpace(oauth.RefreshToken))
        {
            oauth.LastRefreshError = "invalid_oauth_configuration";
            await _state.WriteStateAsync();
            return new("invalid", null, oauth.Version, null, null, null, null, null,
                oauth.LastRefreshError);
        }

        oauth.RefreshLeaseId = Guid.NewGuid().ToString("N");
        oauth.RefreshLeaseUntilUnixSeconds = nowUnixSeconds + Math.Clamp(leaseSeconds, 5, 120);
        await _state.WriteStateAsync();
        return new("acquired", oauth.RefreshLeaseId, oauth.Version, oauth.TokenEndpoint,
            oauth.ClientId, UnprotectOptional(oauth.ClientSecret),
            _credentialProtector.Unprotect(oauth.RefreshToken), oauth.Scope, null);
    }

    public async Task<bool> CompleteOAuthRefresh(string leaseId, string accessToken,
        string? refreshToken, long expiresAtUnixSeconds, string tokenType)
    {
        var oauth = _state.State.OAuth;
        if (oauth is null || string.IsNullOrWhiteSpace(accessToken)
            || accessToken.IndexOfAny(['\r', '\n']) >= 0
            || expiresAtUnixSeconds <= DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds()
            || !string.Equals(oauth.RefreshLeaseId, leaseId, StringComparison.Ordinal))
            return false;

        oauth.AccessToken = _credentialProtector.Protect(accessToken);
        if (!string.IsNullOrWhiteSpace(refreshToken))
            oauth.RefreshToken = _credentialProtector.Protect(refreshToken);
        oauth.ExpiresAtUnixSeconds = expiresAtUnixSeconds;
        oauth.HeaderScheme = string.IsNullOrWhiteSpace(tokenType) ? "Bearer" : tokenType;
        oauth.Version++;
        oauth.LastRefreshedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        oauth.LastRefreshError = null;
        oauth.RefreshLeaseId = null;
        oauth.RefreshLeaseUntilUnixSeconds = 0;
        _state.State.TempUnschedulableUntil = null;
        await _state.WriteStateAsync();
        _invalidation.NotifyChange("account", _state.State.Id.ToString());
        return true;
    }

    public async Task FailOAuthRefresh(string leaseId, string error,
        long retryAfterUnixMilliseconds)
    {
        var oauth = _state.State.OAuth;
        if (oauth is null || !string.Equals(oauth.RefreshLeaseId, leaseId, StringComparison.Ordinal))
            return;
        oauth.RefreshLeaseId = null;
        oauth.RefreshLeaseUntilUnixSeconds = 0;
        oauth.LastRefreshError = string.IsNullOrWhiteSpace(error)
            ? "credential_refresh_failed" : error[..Math.Min(error.Length, 200)];
        _state.State.TempUnschedulableUntil = retryAfterUnixMilliseconds;
        await _state.WriteStateAsync();
        _invalidation.NotifyChange("account", _state.State.Id.ToString());
    }

    public Task<SlotResult> TryAcquireSlot(string requestId, int maxConcurrency)
    {
        return TryAcquireSlot(requestId, DateTime.UtcNow.AddMinutes(10), maxConcurrency);
    }

    public Task<SlotResult> TryAcquireSlot(string requestId, DateTime expiresAt, int maxConcurrency)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var expired in _activeSlots.Where(x => x.Value <= now).Select(x => x.Key).ToArray())
            _activeSlots.Remove(expired);
        if (_activeSlots.Count >= maxConcurrency)
        {
            return Task.FromResult(new SlotResult(false, null, _activeSlots.Count, maxConcurrency));
        }

        var lease = requestId;
        _activeSlots[lease] = new DateTimeOffset(expiresAt).ToUnixTimeMilliseconds();
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
        if (input.Credentials.Count > 0)
            s.Credentials = ProtectCredentials(input.Credentials);
        s.ModelMapping = input.ModelMapping;
        s.SupportedModels = input.SupportedModels;
        s.ProxyUrl = input.ProxyUrl;
        s.TlsFingerprint = input.TlsFingerprint;
        s.OAuth = ProtectOAuth(input.OAuth);
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
        if (input.Credentials.Count > 0)
            s.Credentials = ProtectCredentials(input.Credentials);
        s.ModelMapping = input.ModelMapping;
        s.SupportedModels = input.SupportedModels;
        s.ProxyUrl = input.ProxyUrl;
        s.TlsFingerprint = input.TlsFingerprint;
        if (input.OAuth is not null)
            s.OAuth = ProtectOAuth(input.OAuth);
        else if (!string.Equals(input.Type, "oauth", StringComparison.OrdinalIgnoreCase))
            s.OAuth = null;
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

    private Dictionary<string, string> ProtectCredentials(
        IReadOnlyDictionary<string, string> credentials) =>
        credentials.ToDictionary(kv => kv.Key,
            kv => _credentialProtector.Protect(kv.Value));

    private ProviderOAuthState? ProtectOAuth(ProviderOAuthCredential? input) => input is null
        ? null
        : new ProviderOAuthState
        {
            TokenEndpoint = input.TokenEndpoint,
            ClientId = input.ClientId,
            ClientSecret = ProtectOptional(input.ClientSecret),
            RefreshToken = _credentialProtector.Protect(input.RefreshToken),
            AccessToken = _credentialProtector.Protect(input.AccessToken),
            ExpiresAtUnixSeconds = input.ExpiresAtUnixSeconds,
            HeaderName = string.IsNullOrWhiteSpace(input.HeaderName)
                ? "Authorization" : input.HeaderName,
            HeaderScheme = input.HeaderScheme,
            Scope = input.Scope,
        };

    private string ProtectOptional(string value) => string.IsNullOrEmpty(value)
        ? "" : _credentialProtector.Protect(value);

    private string UnprotectOptional(string value) => string.IsNullOrEmpty(value)
        ? "" : _credentialProtector.Unprotect(value);
}
