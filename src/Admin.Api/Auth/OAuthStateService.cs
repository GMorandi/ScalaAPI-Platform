using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace ScalaAPI.Admin.Auth;

public sealed record OAuthStateIssue(
    string Provider,
    string RedirectUri,
    string State,
    string CodeVerifier,
    string CodeChallenge,
    DateTime ExpiresAt);

public enum OAuthStateStatus
{
    Accepted,
    Invalid,
    Expired,
    Replayed,
}

public sealed record OAuthStateConsumeResult(OAuthStateStatus Status)
{
    public bool Accepted => Status == OAuthStateStatus.Accepted;
}

/// <summary>
/// Owns the one-time OAuth authorization state and PKCE binding. Only hashes are
/// persisted, and consumption is serialized by a PostgreSQL row lock.
/// </summary>
public sealed class OAuthStateService(
    NpgsqlDataSource dataSource, TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);
    private static readonly HashSet<string> Providers = ["github", "google"];
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<OAuthStateIssue?> IssueAsync(
        string? provider, string? redirectUri, CancellationToken ct = default)
    {
        var normalizedProvider = NormalizeProvider(provider);
        var normalizedRedirect = NormalizeRedirectUri(redirectUri);
        if (normalizedProvider is null || normalizedRedirect is null) return null;

        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var expiresAt = clock.GetUtcNow().UtcDateTime.Add(StateLifetime);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO auth_oauth_states
                (state_hash, provider, redirect_uri, verifier_hash, expires_at)
            VALUES ($1, $2, $3, $4, $5)
            """;
        command.Parameters.AddWithValue(Hash(state));
        command.Parameters.AddWithValue(normalizedProvider);
        command.Parameters.AddWithValue(normalizedRedirect);
        command.Parameters.AddWithValue(Hash(verifier));
        command.Parameters.AddWithValue(expiresAt);
        await command.ExecuteNonQueryAsync(ct);

        return new OAuthStateIssue(normalizedProvider, normalizedRedirect, state,
            verifier, Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), expiresAt);
    }

    public async Task<OAuthStateConsumeResult> ConsumeAsync(
        string? provider, string? state, string? redirectUri, string? codeVerifier,
        CancellationToken ct = default)
    {
        var normalizedProvider = NormalizeProvider(provider);
        var normalizedRedirect = NormalizeRedirectUri(redirectUri);
        if (normalizedProvider is null || normalizedRedirect is null
            || !IsValidVerifier(codeVerifier) || string.IsNullOrWhiteSpace(state)
            || state.Length > 256)
            return new(OAuthStateStatus.Invalid);

        var stateHash = Hash(state);
        var verifierHash = Hash(codeVerifier!);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = """
            SELECT provider, redirect_uri, verifier_hash, expires_at, consumed_at
            FROM auth_oauth_states
            WHERE state_hash = $1
            FOR UPDATE
            """;
        find.Parameters.AddWithValue(stateHash);
        await using var reader = await find.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new(OAuthStateStatus.Invalid);
        var storedProvider = reader.GetString(0);
        var storedRedirect = reader.GetString(1);
        var storedVerifierHash = reader.GetString(2);
        var expiresAt = reader.GetDateTime(3);
        var consumedAt = reader.IsDBNull(4) ? (DateTime?)null : reader.GetDateTime(4);
        await reader.DisposeAsync();

        if (consumedAt is not null)
            return new(OAuthStateStatus.Replayed);
        if (expiresAt <= clock.GetUtcNow().UtcDateTime)
            return new(OAuthStateStatus.Expired);

        var providerMatches = FixedEquals(storedProvider, normalizedProvider);
        var redirectMatches = FixedEquals(storedRedirect, normalizedRedirect);
        var verifierMatches = FixedEquals(storedVerifierHash, verifierHash);
        if (!providerMatches || !redirectMatches || !verifierMatches)
            return new(OAuthStateStatus.Invalid);

        await using var consume = connection.CreateCommand();
        consume.Transaction = transaction;
        consume.CommandText = """
            UPDATE auth_oauth_states SET consumed_at = $2
            WHERE state_hash = $1 AND consumed_at IS NULL
            """;
        consume.Parameters.AddWithValue(stateHash);
        consume.Parameters.AddWithValue(clock.GetUtcNow().UtcDateTime);
        await consume.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return new(OAuthStateStatus.Accepted);
    }

    public static string? NormalizeProvider(string? provider)
    {
        var normalized = provider?.Trim().ToLowerInvariant();
        return normalized is not null && Providers.Contains(normalized) ? normalized : null;
    }

    public static string? NormalizeRedirectUri(string? redirectUri)
    {
        if (string.IsNullOrWhiteSpace(redirectUri) || redirectUri.Length > 2048)
            return null;
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrWhiteSpace(uri.Host))
            return null;
        return uri.AbsoluteUri;
    }

    private static bool IsValidVerifier(string? verifier) =>
        verifier is not null && verifier.Length is >= 43 and <= 128
        && verifier.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~');

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));

    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal).TrimEnd('=');
}
