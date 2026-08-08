using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace ScalaAPI.Admin.Auth;

public sealed record PasswordResetIssue(string Token, DateTime ExpiresAt);

public sealed class PasswordResetService(
    NpgsqlDataSource dataSource, ILogger<PasswordResetService> logger)
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);

    public async Task<PasswordResetIssue?> IssueAsync(string? email,
        CancellationToken ct = default)
    {
        var normalized = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized)) return null;

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var find = connection.CreateCommand();
        find.CommandText = "SELECT id FROM user_accounts WHERE email = $1 AND status = 'active'";
        find.Parameters.AddWithValue(normalized);
        var userIdValue = await find.ExecuteScalarAsync(ct);
        if (userIdValue is not long userId) return null;

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .TrimEnd('=');
        var expiresAt = DateTime.UtcNow.Add(TokenLifetime);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        await using (var invalidate = connection.CreateCommand())
        {
            invalidate.Transaction = transaction;
            invalidate.CommandText = """
                UPDATE password_reset_tokens
                SET used_at = now()
                WHERE user_id = $1 AND used_at IS NULL
                """;
            invalidate.Parameters.AddWithValue(userId);
            await invalidate.ExecuteNonQueryAsync(ct);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO password_reset_tokens(token_hash, user_id, expires_at)
                VALUES ($1, $2, $3)
                """;
            insert.Parameters.AddWithValue(Hash(token));
            insert.Parameters.AddWithValue(userId);
            insert.Parameters.AddWithValue(expiresAt);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        logger.LogInformation("Issued password reset token for user {UserId}", userId);
        return new PasswordResetIssue(token, expiresAt);
    }

    public async Task<bool> ConsumeAsync(string? token, string? newPassword,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(newPassword)
            || newPassword.Length < 12) return false;

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = """
            SELECT user_id, expires_at, used_at
            FROM password_reset_tokens
            WHERE token_hash = $1
            FOR UPDATE
            """;
        find.Parameters.AddWithValue(Hash(token));
        await using var reader = await find.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return false;
        var userId = reader.GetInt64(0);
        var expiresAt = reader.GetDateTime(1);
        var used = !reader.IsDBNull(2);
        await reader.DisposeAsync();
        if (used || expiresAt <= DateTime.UtcNow) return false;

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE user_accounts
                SET password_hash = $2, last_login_at = NULL
                WHERE id = $1 AND status = 'active'
                """;
            update.Parameters.AddWithValue(userId);
            update.Parameters.AddWithValue(BCrypt.Net.BCrypt.HashPassword(newPassword));
            if (await update.ExecuteNonQueryAsync(ct) != 1)
            {
                await transaction.RollbackAsync(ct);
                return false;
            }
        }

        await using (var revoke = connection.CreateCommand())
        {
            revoke.Transaction = transaction;
            revoke.CommandText = """
                UPDATE auth_sessions SET revoked_at = now()
                WHERE user_id = $1 AND revoked_at IS NULL
                """;
            revoke.Parameters.AddWithValue(userId);
            await revoke.ExecuteNonQueryAsync(ct);
        }

        await using (var consume = connection.CreateCommand())
        {
            consume.Transaction = transaction;
            consume.CommandText = """
                UPDATE password_reset_tokens SET used_at = now()
                WHERE token_hash = $1 AND used_at IS NULL
                """;
            consume.Parameters.AddWithValue(Hash(token));
            await consume.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return true;
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
