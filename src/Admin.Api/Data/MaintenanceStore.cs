using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace ScalaAPI.Admin.Data;

public sealed record UserExportAccount(
    long Id,
    string Email,
    string? DisplayName,
    string Status,
    string Role,
    bool EmailVerified,
    DateTime CreatedAt,
    DateTime? LastLoginAt);

public sealed record UserExportApiKey(
    long Id,
    string Prefix,
    string? Name,
    string Status,
    DateTime CreatedAt,
    DateTime? LastUsedAt);

public sealed record UserExportUsage(
    string RequestId,
    string Model,
    int InputTokens,
    int OutputTokens,
    decimal CostUsd,
    int DurationMs,
    DateTime CreatedAt);

public sealed record UserExportSession(
    DateTime CreatedAt,
    DateTime LastSeenAt,
    DateTime ExpiresAt,
    DateTime? RevokedAt,
    string? IpAddress,
    string? UserAgent);

public sealed record UserExportPasskey(
    string DisplayName,
    DateTime CreatedAt,
    DateTime? LastUsedAt);

public sealed record UserDataExport(
    UserExportAccount Account,
    IReadOnlyList<UserExportApiKey> ApiKeys,
    IReadOnlyList<UserExportUsage> Usage,
    IReadOnlyList<UserExportSession> Sessions,
    IReadOnlyList<UserExportPasskey> Passkeys,
    bool Truncated,
    DateTime ExportedAt);

public sealed record MaintenanceCleanupSummary(
    string OperationKey,
    bool DryRun,
    int RetentionDays,
    int Limit,
    int AuthSessions,
    int PasswordResetTokens,
    int EmailVerificationTokens,
    int PasskeyChallenges,
    int AbuseCounters,
    DateTime CompletedAt);

public enum MaintenanceOperationStatus
{
    Applied,
    Replayed,
    Conflict,
}

public sealed record MaintenanceOperationResult(
    MaintenanceOperationStatus Status,
    MaintenanceCleanupSummary? Summary,
    string? Error);

public sealed class MaintenanceStore(NpgsqlDataSource dataSource)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<UserDataExport?> ExportUserAsync(
        long userId,
        string? clientIp,
        int limit = 1_000,
        CancellationToken ct = default)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        limit = Math.Clamp(limit, 1, 1_000);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead, ct);

        UserExportAccount? account;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id, email, display_name, status, role, email_verified,
                       created_at, last_login_at
                FROM user_accounts WHERE id = $1
                """;
            command.Parameters.AddWithValue(userId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            account = await reader.ReadAsync(ct)
                ? new UserExportAccount(reader.GetInt64(0), reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetBoolean(5), reader.GetDateTime(6),
                    reader.IsDBNull(7) ? null : reader.GetDateTime(7))
                : null;
        }
        if (account is null)
        {
            await transaction.RollbackAsync(ct);
            return null;
        }

        var apiKeys = await ReadApiKeysAsync(connection, transaction, userId, limit + 1, ct);
        var usage = await ReadUsageAsync(connection, transaction, userId, limit + 1, ct);
        var sessions = await ReadSessionsAsync(connection, transaction, userId, limit + 1, ct);
        var passkeys = await ReadPasskeysAsync(connection, transaction, userId, limit + 1, ct);
        var truncated = apiKeys.Count > limit || usage.Count > limit
            || sessions.Count > limit || passkeys.Count > limit;
        apiKeys = apiKeys.Take(limit).ToList();
        usage = usage.Take(limit).ToList();
        sessions = sessions.Take(limit).ToList();
        passkeys = passkeys.Take(limit).ToList();

        await using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = """
                INSERT INTO audit_logs(
                    user_id, action, resource_type, resource_id, details, ip_address)
                VALUES ($1, 'user.data_export', 'user', $2, $3, $4)
                """;
            audit.Parameters.AddWithValue(userId);
            audit.Parameters.AddWithValue(userId.ToString());
            audit.Parameters.AddWithValue(JsonSerializer.Serialize(new
            {
                api_keys = apiKeys.Count,
                usage = usage.Count,
                sessions = sessions.Count,
                passkeys = passkeys.Count,
                truncated,
            }, JsonOptions));
            audit.Parameters.AddWithValue((object?)clientIp ?? DBNull.Value);
            await audit.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return new(account, apiKeys, usage, sessions, passkeys, truncated, DateTime.UtcNow);
    }

    public async Task<MaintenanceOperationResult> CleanupExpiredAsync(
        long actorId,
        string operationKey,
        string requestFingerprint,
        bool dryRun,
        int retentionDays,
        int limit,
        string? clientIp,
        CancellationToken ct = default)
    {
        if (actorId <= 0) throw new ArgumentOutOfRangeException(nameof(actorId));
        operationKey = NormalizeKey(operationKey, nameof(operationKey));
        requestFingerprint = NormalizeKey(requestFingerprint, nameof(requestFingerprint));
        if (retentionDays is < 1 or > 3_650)
            throw new ArgumentOutOfRangeException(nameof(retentionDays));
        if (limit is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(limit));

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT actor_user_id, request_fingerprint, result::text
                FROM maintenance_operations
                WHERE operation_key = $1
                FOR UPDATE
                """;
            existing.Parameters.AddWithValue(operationKey);
            await using var reader = await existing.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var storedActorId = reader.GetInt64(0);
                var fingerprint = reader.GetString(1);
                var resultJson = reader.GetString(2);
                await reader.DisposeAsync();
                await transaction.CommitAsync(ct);
                if (storedActorId != actorId
                    || !string.Equals(fingerprint, requestFingerprint, StringComparison.Ordinal))
                    return new(MaintenanceOperationStatus.Conflict, null,
                        "idempotency_key_reused");
                var replay = JsonSerializer.Deserialize<MaintenanceCleanupSummary>(resultJson, JsonOptions)
                    ?? throw new InvalidOperationException("Stored maintenance result is invalid");
                return new(MaintenanceOperationStatus.Replayed, replay, null);
            }
        }

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var summary = new MaintenanceCleanupSummary(
            operationKey, dryRun, retentionDays, limit,
            await CountAsync(connection, transaction, """
                SELECT count(*) FROM auth_sessions
                WHERE expires_at < now() OR (revoked_at IS NOT NULL AND revoked_at < $1)
                """, cutoff, ct),
            await CountAsync(connection, transaction, """
                SELECT count(*) FROM password_reset_tokens
                WHERE expires_at < now() OR (used_at IS NOT NULL AND used_at < $1)
                """, cutoff, ct),
            await CountAsync(connection, transaction, """
                SELECT count(*) FROM email_verification_tokens
                WHERE expires_at < now() OR (used_at IS NOT NULL AND used_at < $1)
                """, cutoff, ct),
            await CountAsync(connection, transaction, """
                SELECT count(*) FROM passkey_challenges
                WHERE expires_at < now() OR (consumed_at IS NOT NULL AND consumed_at < $1)
                """, cutoff, ct),
            await CountAsync(connection, transaction, """
                SELECT count(*) FROM auth_abuse_counters
                WHERE updated_at < $1 AND (locked_until IS NULL OR locked_until < now())
                """, cutoff, ct),
            DateTime.UtcNow);

        if (!dryRun)
        {
            await DeleteLimitedAsync(connection, transaction, """
                DELETE FROM auth_sessions WHERE ctid IN (
                    SELECT ctid FROM auth_sessions
                    WHERE expires_at < now() OR (revoked_at IS NOT NULL AND revoked_at < $1)
                    ORDER BY expires_at LIMIT $2)
                """, cutoff, limit, ct);
            await DeleteLimitedAsync(connection, transaction, """
                DELETE FROM password_reset_tokens WHERE ctid IN (
                    SELECT ctid FROM password_reset_tokens
                    WHERE expires_at < now() OR (used_at IS NOT NULL AND used_at < $1)
                    ORDER BY expires_at LIMIT $2)
                """, cutoff, limit, ct);
            await DeleteLimitedAsync(connection, transaction, """
                DELETE FROM email_verification_tokens WHERE ctid IN (
                    SELECT ctid FROM email_verification_tokens
                    WHERE expires_at < now() OR (used_at IS NOT NULL AND used_at < $1)
                    ORDER BY expires_at LIMIT $2)
                """, cutoff, limit, ct);
            await DeleteLimitedAsync(connection, transaction, """
                DELETE FROM passkey_challenges WHERE ctid IN (
                    SELECT ctid FROM passkey_challenges
                    WHERE expires_at < now() OR (consumed_at IS NOT NULL AND consumed_at < $1)
                    ORDER BY expires_at LIMIT $2)
                """, cutoff, limit, ct);
            await DeleteLimitedAsync(connection, transaction, """
                DELETE FROM auth_abuse_counters WHERE ctid IN (
                    SELECT ctid FROM auth_abuse_counters
                    WHERE updated_at < $1 AND (locked_until IS NULL OR locked_until < now())
                    ORDER BY updated_at LIMIT $2)
                """, cutoff, limit, ct);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO maintenance_operations(
                    operation_key, actor_user_id, request_fingerprint, dry_run, result)
                VALUES ($1, $2, $3, $4, $5::jsonb)
                """;
            insert.Parameters.AddWithValue(operationKey);
            insert.Parameters.AddWithValue(actorId);
            insert.Parameters.AddWithValue(requestFingerprint);
            insert.Parameters.AddWithValue(dryRun);
            insert.Parameters.AddWithValue(JsonSerializer.Serialize(summary, JsonOptions));
            await insert.ExecuteNonQueryAsync(ct);
        }
        await using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = """
                INSERT INTO audit_logs(
                    user_id, action, resource_type, resource_id, details, ip_address)
                VALUES ($1, 'maintenance.cleanup', 'maintenance', $2, $3, $4)
                """;
            audit.Parameters.AddWithValue(actorId);
            audit.Parameters.AddWithValue(operationKey);
            audit.Parameters.AddWithValue(JsonSerializer.Serialize(summary, JsonOptions));
            audit.Parameters.AddWithValue((object?)clientIp ?? DBNull.Value);
            await audit.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return new(MaintenanceOperationStatus.Applied, summary, null);
    }

    public static string Fingerprint(bool dryRun, int retentionDays, int limit) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"dry_run={dryRun};retention_days={retentionDays};limit={limit}")))
            .ToLowerInvariant();

    private static string NormalizeKey(string value, string parameterName)
    {
        value = value.Trim();
        if (value.Length is < 1 or > 200 || value.Any(char.IsControl))
            throw new ArgumentException("Maintenance key is invalid", parameterName);
        return value;
    }

    private static async Task<int> CountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        DateTime cutoff,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue(cutoff);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    private static async Task DeleteLimitedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        DateTime cutoff,
        int limit,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue(cutoff);
        command.Parameters.AddWithValue(limit);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<UserExportApiKey>> ReadApiKeysAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long userId, int limit, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, key_prefix, name, status, created_at, last_used_at
            FROM user_api_keys WHERE user_email = (SELECT email FROM user_accounts WHERE id = $1)
            ORDER BY created_at DESC, id DESC LIMIT $2
            """;
        command.Parameters.AddWithValue(userId); command.Parameters.AddWithValue(limit);
        var items = new List<UserExportApiKey>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(new(reader.GetInt64(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
                reader.GetDateTime(4), reader.IsDBNull(5) ? null : reader.GetDateTime(5)));
        return items;
    }

    private static async Task<List<UserExportUsage>> ReadUsageAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long userId, int limit, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT request_id, model, input_tokens, output_tokens, cost_usd, duration_ms, created_at
            FROM usage_logs WHERE user_id = $1 ORDER BY created_at DESC LIMIT $2
            """;
        command.Parameters.AddWithValue(userId); command.Parameters.AddWithValue(limit);
        var items = new List<UserExportUsage>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                reader.GetInt32(3), reader.GetDecimal(4), reader.GetInt32(5), reader.GetDateTime(6)));
        return items;
    }

    private static async Task<List<UserExportSession>> ReadSessionsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long userId, int limit, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT created_at, last_seen_at, expires_at, revoked_at, ip_address, user_agent
            FROM auth_sessions WHERE user_id = $1 ORDER BY created_at DESC LIMIT $2
            """;
        command.Parameters.AddWithValue(userId); command.Parameters.AddWithValue(limit);
        var items = new List<UserExportSession>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(new(reader.GetDateTime(0), reader.GetDateTime(1), reader.GetDateTime(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        return items;
    }

    private static async Task<List<UserExportPasskey>> ReadPasskeysAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long userId, int limit, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT display_name, created_at, last_used_at
            FROM passkey_credentials WHERE user_id = $1 ORDER BY created_at DESC LIMIT $2
            """;
        command.Parameters.AddWithValue(userId); command.Parameters.AddWithValue(limit);
        var items = new List<UserExportPasskey>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(new(reader.GetString(0), reader.GetDateTime(1),
                reader.IsDBNull(2) ? null : reader.GetDateTime(2)));
        return items;
    }
}
