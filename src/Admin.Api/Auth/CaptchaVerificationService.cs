using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace ScalaAPI.Admin.Auth;

public interface ICaptchaProvider
{
    string Name { get; }
    Task<CaptchaProviderResponse> VerifyAsync(string token, string? remoteIp, CancellationToken ct = default);
}

public sealed record CaptchaProviderResponse(CaptchaProviderStatus Status, double Score = 1.0, string? ErrorCode = null);
public enum CaptchaProviderStatus { Success, InvalidToken, Expired, ProviderError, Timeout }
public enum CaptchaDecision { Accepted, Rejected, ReplayDetected, Expired, ProviderFailure, Disabled }
public sealed record CaptchaVerificationResult(CaptchaDecision Decision, double Score = 0.0, string? ErrorCode = null)
{
    public bool Accepted => Decision == CaptchaDecision.Accepted;
}

public sealed class CaptchaVerificationService(NpgsqlDataSource dataSource, ICaptchaProvider captchaProvider, TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan ChallengeTtl = TimeSpan.FromMinutes(5);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<CaptchaVerificationResult> VerifyAsync(string? nonce, string? token, string? action, string? remoteIp, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nonce) || string.IsNullOrWhiteSpace(token))
            return new(CaptchaDecision.Rejected, ErrorCode: "missing_input");
        var normalizedAction = string.IsNullOrWhiteSpace(action) ? "register" : action.Trim().ToLowerInvariant();
        var now = clock.GetUtcNow().UtcDateTime;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await using var find = connection.CreateCommand();
            find.Transaction = transaction;
            find.CommandText = "SELECT challenge_nonce, action, consumed_at, expires_at FROM captcha_challenges WHERE challenge_nonce = $1 FOR UPDATE";
            find.Parameters.AddWithValue(nonce);
            await using var reader = await find.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) { await reader.DisposeAsync(); await transaction.CommitAsync(ct); return new(CaptchaDecision.Rejected, ErrorCode: "invalid_nonce"); }
            var storedAction = reader.GetString(1);
            var consumedAt = reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2);
            var expiresAt = reader.GetDateTime(3);
            await reader.DisposeAsync();
            if (consumedAt is not null) { await transaction.CommitAsync(ct); return new(CaptchaDecision.ReplayDetected, ErrorCode: "already_consumed"); }
            if (expiresAt <= now) { await transaction.CommitAsync(ct); return new(CaptchaDecision.Expired, ErrorCode: "challenge_expired"); }
            if (!string.Equals(storedAction, normalizedAction, StringComparison.Ordinal)) { await transaction.CommitAsync(ct); return new(CaptchaDecision.Rejected, ErrorCode: "action_mismatch"); }
            var providerResponse = await captchaProvider.VerifyAsync(token, remoteIp, ct);
            var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
            DateTime? consumedTimestamp = providerResponse.Status == CaptchaProviderStatus.Success ? now : null;
            await using var markConsumed = connection.CreateCommand();
            markConsumed.Transaction = transaction;
            markConsumed.CommandText = "UPDATE captcha_challenges SET consumed_at = $2, token_hash = $3, score = $4 WHERE challenge_nonce = $1";
            markConsumed.Parameters.AddWithValue(nonce);
            markConsumed.Parameters.AddWithValue((object?)consumedTimestamp ?? DBNull.Value);
            markConsumed.Parameters.AddWithValue(tokenHash);
            markConsumed.Parameters.AddWithValue(providerResponse.Score);
            await markConsumed.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return providerResponse.Status switch
            {
                CaptchaProviderStatus.Success => new(CaptchaDecision.Accepted, providerResponse.Score),
                CaptchaProviderStatus.InvalidToken => new(CaptchaDecision.Rejected, providerResponse.Score, providerResponse.ErrorCode ?? "invalid_token"),
                CaptchaProviderStatus.Expired => new(CaptchaDecision.Expired, providerResponse.Score, providerResponse.ErrorCode ?? "token_expired"),
                CaptchaProviderStatus.Timeout or CaptchaProviderStatus.ProviderError => new(CaptchaDecision.ProviderFailure, providerResponse.Score, providerResponse.ErrorCode ?? "provider_error"),
                _ => new(CaptchaDecision.Rejected, providerResponse.Score, "unknown_status"),
            };
        }
        catch { await transaction.RollbackAsync(CancellationToken.None); throw; }
    }

    public async Task<string> IssueChallengeAsync(string? action, CancellationToken ct = default)
    {
        var normalizedAction = string.IsNullOrWhiteSpace(action) ? "register" : action.Trim().ToLowerInvariant();
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        var expiresAt = clock.GetUtcNow().UtcDateTime.Add(ChallengeTtl);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO captcha_challenges (challenge_nonce, action, expires_at) VALUES ($1, $2, $3)";
        command.Parameters.AddWithValue(nonce);
        command.Parameters.AddWithValue(normalizedAction);
        command.Parameters.AddWithValue(expiresAt);
        await command.ExecuteNonQueryAsync(ct);
        return nonce;
    }
}
