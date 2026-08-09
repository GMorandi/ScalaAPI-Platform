using System.Text.Json;
using Orleans;
using ScalaAPI.Data.Provider;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Host.Services;

public sealed record ProviderTokenRefreshResult(
    string AccessToken, string? RefreshToken, string TokenType, long ExpiresAtUnixSeconds);

public sealed class ProviderCredentialsUnavailableException(string message) : Exception(message);

public sealed class ProviderTokenEndpointClient(HttpClient client, IConfiguration configuration)
{
    private const int MaxResponseBytes = 64 * 1024;
    private readonly bool allowInsecure = configuration.GetValue(
        "ProviderCredentials:AllowInsecureTokenEndpoints", false);

    public async Task<ProviderTokenRefreshResult> RefreshAsync(
        ProviderOAuthRefreshLease lease, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(lease.TokenEndpoint, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttps
                && !(allowInsecure && endpoint.Scheme == Uri.UriSchemeHttp)))
            throw new ProviderCredentialsUnavailableException("oauth_token_endpoint_not_allowed");
        if (string.IsNullOrWhiteSpace(lease.ClientId)
            || string.IsNullOrWhiteSpace(lease.RefreshToken))
            throw new ProviderCredentialsUnavailableException("oauth_refresh_configuration_invalid");

        var fields = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = lease.RefreshToken,
            ["client_id"] = lease.ClientId,
        };
        if (!string.IsNullOrWhiteSpace(lease.ClientSecret))
            fields["client_secret"] = lease.ClientSecret;
        if (!string.IsNullOrWhiteSpace(lease.Scope))
            fields["scope"] = lease.Scope;

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(fields),
        };
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && ex is OperationCanceledException or IOException)
        {
            throw new ProviderCredentialsUnavailableException(
                "oauth_token_endpoint_unavailable");
        }
        using (response)
        {
            return await ParseResponseAsync(response, ct);
        }
    }

    private static async Task<ProviderTokenRefreshResult> ParseResponseAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            throw new ProviderCredentialsUnavailableException("oauth_token_response_too_large");
        byte[] body;
        try
        {
            body = await ReadBoundedAsync(response.Content, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && ex is OperationCanceledException or IOException)
        {
            throw new ProviderCredentialsUnavailableException(
                "oauth_token_endpoint_unavailable");
        }
        if (!response.IsSuccessStatusCode)
            throw new ProviderCredentialsUnavailableException(
                $"oauth_token_endpoint_status_{(int)response.StatusCode}");

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("access_token", out var accessElement)
                || string.IsNullOrWhiteSpace(accessElement.GetString()))
                throw new ProviderCredentialsUnavailableException("oauth_access_token_missing");
            var accessToken = accessElement.GetString()!;
            if (accessToken.IndexOfAny(['\r', '\n']) >= 0)
                throw new ProviderCredentialsUnavailableException("oauth_access_token_invalid");
            var expiresIn = ReadExpiresIn(root);
            var refreshToken = root.TryGetProperty("refresh_token", out var refreshElement)
                ? refreshElement.GetString() : null;
            var tokenType = root.TryGetProperty("token_type", out var typeElement)
                ? typeElement.GetString() : null;
            tokenType = string.IsNullOrWhiteSpace(tokenType) ? "Bearer" : tokenType;
            if (tokenType.Length > 32 || !tokenType.All(IsTokenTypeCharacter))
                throw new ProviderCredentialsUnavailableException("oauth_token_type_invalid");
            return new ProviderTokenRefreshResult(accessToken, refreshToken, tokenType,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new ProviderCredentialsUnavailableException("oauth_token_response_invalid");
        }
    }

    private static bool IsTokenTypeCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '.' or '_' or '~' or '-';

    private static long ReadExpiresIn(JsonElement root)
    {
        if (!root.TryGetProperty("expires_in", out var element)) return 3600;
        long seconds;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number))
            seconds = number;
        else if (element.ValueKind == JsonValueKind.String
                 && long.TryParse(element.GetString(), out var parsed))
            seconds = parsed;
        else
            throw new ProviderCredentialsUnavailableException("oauth_expiry_invalid");
        return Math.Clamp(seconds, 60, 30L * 24 * 60 * 60);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        await using var source = await content.ReadAsStreamAsync(ct);
        using var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0) return destination.ToArray();
            if (destination.Length + read > MaxResponseBytes)
                throw new ProviderCredentialsUnavailableException("oauth_token_response_too_large");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
}

public sealed class ProviderCredentialRefreshService(
    IClusterClient cluster,
    ProviderTokenEndpointClient tokenClient,
    IConfiguration configuration,
    ILogger<ProviderCredentialRefreshService> logger,
    ProviderCredentialRefreshAuditStore auditStore)
{
    private readonly int refreshSkewSeconds = Math.Clamp(configuration.GetValue(
        "ProviderCredentials:RefreshSkewSeconds", 120), 0, 3600);
    private readonly int refreshLeaseSeconds = Math.Clamp(configuration.GetValue(
        "ProviderCredentials:RefreshLeaseSeconds", 30), 5, 120);
    private readonly TimeSpan waitTimeout = TimeSpan.FromMilliseconds(Math.Clamp(
        configuration.GetValue("ProviderCredentials:RefreshWaitMilliseconds", 2000), 0, 10_000));

    public async Task<AccountCredentials> GetFreshAsync(long accountId,
        CancellationToken ct = default, string source = "dispatch")
    {
        source = source is "media" ? "media" : "dispatch";
        var grain = cluster.GetGrain<IAccountGrain>(accountId);
        var deadline = DateTime.UtcNow + waitTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var lease = await grain.BeginOAuthRefresh(
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), refreshSkewSeconds,
                refreshLeaseSeconds);
            if (lease.Status is "static" or "fresh") return await grain.Hydrate();
            if (lease.Status == "invalid")
                throw new ProviderCredentialsUnavailableException(
                    lease.Error ?? "oauth_refresh_configuration_invalid");
            if (lease.Status == "in_progress")
            {
                if (DateTime.UtcNow >= deadline)
                    throw new ProviderCredentialsUnavailableException("oauth_refresh_in_progress");
                await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
                continue;
            }
            if (lease.Status != "acquired" || string.IsNullOrWhiteSpace(lease.LeaseId))
                throw new ProviderCredentialsUnavailableException("oauth_refresh_state_invalid");

            var attemptId = Guid.NewGuid();
            var startedAt = DateTime.UtcNow;
            var endpointHost = EndpointHost(lease.TokenEndpoint);
            try
            {
                var refreshed = await tokenClient.RefreshAsync(lease, ct);
                if (!await grain.CompleteOAuthRefresh(lease.LeaseId,
                        refreshed.AccessToken, refreshed.RefreshToken,
                        refreshed.ExpiresAtUnixSeconds, refreshed.TokenType))
                    continue;
                await RecordAuditAsync(attemptId, accountId, source, lease.Version,
                    lease.Version + 1, "succeeded", null, endpointHost, startedAt);
                logger.LogInformation(
                    "Refreshed OAuth credential for account {AccountId} to version {Version}",
                    accountId, lease.Version + 1);
                return await grain.Hydrate();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var code = ex is ProviderCredentialsUnavailableException
                    ? ex.Message : "oauth_token_endpoint_unavailable";
                await grain.FailOAuthRefresh(lease.LeaseId, code,
                    DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds());
                await RecordAuditAsync(attemptId, accountId, source, lease.Version,
                    null, "failed", code, endpointHost, startedAt);
                logger.LogWarning("OAuth credential refresh failed for account {AccountId}: {Code}",
                    accountId, code);
                throw new ProviderCredentialsUnavailableException(code);
            }
        }
    }

    private async Task RecordAuditAsync(Guid attemptId, long accountId, string source,
        int versionBefore, int? versionAfter, string outcome, string? errorCode,
        string endpointHost, DateTime startedAt)
    {
        var completedAt = DateTime.UtcNow;
        var duration = Math.Clamp(
            (int)Math.Max(0, (completedAt - startedAt).TotalMilliseconds), 0, 900_000);
        try
        {
            await auditStore.RecordAsync(attemptId, accountId, source, versionBefore,
                versionAfter, outcome, errorCode, endpointHost, startedAt, completedAt,
                duration, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "OAuth refresh audit write failed for account {AccountId}, attempt {AttemptId}",
                accountId, attemptId);
        }
    }

    private static string EndpointHost(string? endpoint)
    {
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host[..Math.Min(uri.Host.Length, 253)]
            : "invalid";
    }
}
