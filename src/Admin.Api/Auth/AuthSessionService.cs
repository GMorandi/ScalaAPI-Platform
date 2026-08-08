using System.Security.Cryptography;
using System.Text;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace ScalaAPI.Admin.Auth;

public sealed record SessionTokens(
    string Token, string RefreshToken, string SessionId, DateTime ExpiresAt);

public sealed record SessionInfo(
    string SessionId, DateTime CreatedAt, DateTime LastSeenAt,
    DateTime ExpiresAt, string? IpAddress, string? UserAgent);

public sealed class AuthSessionService(
    NpgsqlDataSource dataSource, JwtService jwt, ILogger<AuthSessionService> logger)
{
    private static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(30);

    public static string? SessionIdFromAuthorization(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization)
            || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            var token = new JwtSecurityTokenHandler().ReadJwtToken(authorization[7..]);
            return token.Claims.FirstOrDefault(c => c.Type == "sid")?.Value;
        }
        catch (Exception ex) when (ex is ArgumentException or SecurityTokenException)
        {
            return null;
        }
    }

    public async Task<SessionTokens> IssueAsync(
        long userId, string email, string role, string? ipAddress = null, string? userAgent = null,
        CancellationToken ct = default)
    {
        var sessionId = NewToken(24);
        var refreshToken = NewToken(48);
        var now = DateTime.UtcNow;
        var expiresAt = now.Add(RefreshLifetime);

        await using var command = dataSource.CreateCommand("""
            INSERT INTO auth_sessions
                (session_id, user_id, email, role, refresh_token_hash,
                 created_at, last_seen_at, expires_at, ip_address, user_agent)
            VALUES ($1, $2, $3, $4, $5, $6, $6, $7, $8, $9)
            """);
        command.Parameters.AddWithValue(sessionId);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(email);
        command.Parameters.AddWithValue(string.IsNullOrWhiteSpace(role) ? "user" : role);
        command.Parameters.AddWithValue(Hash(refreshToken));
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(expiresAt);
        command.Parameters.AddWithValue((object?)ipAddress ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)userAgent ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);

        return new SessionTokens(
            jwt.GenerateToken(email, role, userId, sessionId), refreshToken, sessionId,
            now.Add(jwt.AccessTokenLifetime));
    }

    public async Task<SessionTokens?> RotateAsync(
        string refreshToken, string? ipAddress = null, string? userAgent = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return null;
        var now = DateTime.UtcNow;
        var oldHash = Hash(refreshToken);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = """
            SELECT session_id, user_id, email, role, expires_at, revoked_at
            FROM auth_sessions WHERE refresh_token_hash = $1 FOR UPDATE
            """;
        find.Parameters.AddWithValue(oldHash);
        await using var reader = await find.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var oldSessionId = reader.GetString(0);
        var userId = reader.GetInt64(1);
        var email = reader.GetString(2);
        var role = reader.GetString(3);
        var expiresAt = reader.GetDateTime(4);
        var revoked = !reader.IsDBNull(5);
        await reader.DisposeAsync();
        if (revoked || expiresAt <= now) return null;

        var newSessionId = NewToken(24);
        var newRefreshToken = NewToken(48);
        var newExpiresAt = now.Add(RefreshLifetime);

        await using (var revoke = connection.CreateCommand())
        {
            revoke.Transaction = transaction;
            revoke.CommandText = """
                UPDATE auth_sessions
                SET revoked_at = $2, replaced_by = $3, last_seen_at = $2
                WHERE session_id = $1 AND revoked_at IS NULL
                """;
            revoke.Parameters.AddWithValue(oldSessionId);
            revoke.Parameters.AddWithValue(now);
            revoke.Parameters.AddWithValue(newSessionId);
            await revoke.ExecuteNonQueryAsync(ct);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO auth_sessions
                    (session_id, user_id, email, role, refresh_token_hash,
                     created_at, last_seen_at, expires_at, ip_address, user_agent)
                VALUES ($1, $2, $3, $4, $5, $6, $6, $7, $8, $9)
                """;
            insert.Parameters.AddWithValue(newSessionId);
            insert.Parameters.AddWithValue(userId);
            insert.Parameters.AddWithValue(email);
            insert.Parameters.AddWithValue(role);
            insert.Parameters.AddWithValue(Hash(newRefreshToken));
            insert.Parameters.AddWithValue(now);
            insert.Parameters.AddWithValue(newExpiresAt);
            insert.Parameters.AddWithValue((object?)ipAddress ?? DBNull.Value);
            insert.Parameters.AddWithValue((object?)userAgent ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        logger.LogDebug("Rotated auth session {OldSessionId} to {NewSessionId}", oldSessionId, newSessionId);
        return new SessionTokens(
            jwt.GenerateToken(email, role, userId, newSessionId), newRefreshToken, newSessionId,
            now.Add(jwt.AccessTokenLifetime));
    }

    public async Task<bool> IsActiveAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        await using var command = dataSource.CreateCommand("""
            SELECT 1 FROM auth_sessions
            WHERE session_id = $1 AND revoked_at IS NULL AND expires_at > now()
            """);
        command.Parameters.AddWithValue(sessionId);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<bool> RevokeAsync(long userId, string sessionId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE auth_sessions SET revoked_at = now()
            WHERE user_id = $1 AND session_id = $2 AND revoked_at IS NULL
            """);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(sessionId);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> RevokeSessionAsync(string sessionId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE auth_sessions SET revoked_at = now() WHERE session_id = $1 AND revoked_at IS NULL");
        command.Parameters.AddWithValue(sessionId);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<int> RevokeOtherSessionsAsync(long userId, string currentSessionId,
        CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE auth_sessions SET revoked_at = now()
            WHERE user_id = $1 AND session_id <> $2 AND revoked_at IS NULL
            """);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(currentSessionId);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> RevokeAllAsync(long userId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE auth_sessions SET revoked_at = now()
            WHERE user_id = $1 AND revoked_at IS NULL
            """);
        command.Parameters.AddWithValue(userId);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<long?> GetUserIdAsync(string sessionId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT user_id FROM auth_sessions WHERE session_id = $1 AND revoked_at IS NULL");
        command.Parameters.AddWithValue(sessionId);
        var value = await command.ExecuteScalarAsync(ct);
        return value is long userId ? userId : null;
    }

    public async Task<IReadOnlyList<SessionInfo>> ListAsync(long userId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT session_id, created_at, last_seen_at, expires_at, ip_address, user_agent
            FROM auth_sessions
            WHERE user_id = $1 AND revoked_at IS NULL AND expires_at > now()
            ORDER BY created_at DESC
            """);
        command.Parameters.AddWithValue(userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<SessionInfo>();
        while (await reader.ReadAsync(ct))
        {
            items.Add(new SessionInfo(reader.GetString(0), reader.GetDateTime(1),
                reader.GetDateTime(2), reader.GetDateTime(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return items;
    }

    private static string NewToken(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
