using Npgsql;
using ScalaAPI.Admin.Data;
using Xunit;

namespace ScalaAPI.Admin.Tests;

public sealed class MaintenanceStoreTests
{
    [Fact]
    public async Task CleanupIsBoundedIdempotentAndExportOmitsSecrets()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        var userId = 9_970_000L + Random.Shared.Next(1, 40_000);
        var operationKey = $"maintenance-test-{Guid.NewGuid():N}";
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new MaintenanceStore(dataSource);
        try
        {
            await ExecuteAsync(dataSource, """
                INSERT INTO user_accounts(id, email, password_hash, display_name, status, role)
                VALUES ($1, $2, 'password-secret', 'Export User', 'active', 'user')
                """, userId, $"maintenance-{userId}@example.test");
            await ExecuteAsync(dataSource, """
                INSERT INTO user_api_keys(user_email, key_hash, key_prefix, name, status)
                VALUES ($1, 'do-not-export-hash', 'sk-test', 'Export key', 'active')
                """, $"maintenance-{userId}@example.test");
            await ExecuteAsync(dataSource, """
                INSERT INTO auth_sessions(
                    session_id, user_id, email, role, refresh_token_hash, expires_at)
                VALUES ($1, $2, $3, 'user', 'refresh-hash', now() - interval '2 days')
                """, Guid.NewGuid().ToString("N"), userId, $"maintenance-{userId}@example.test");
            await ExecuteAsync(dataSource, """
                INSERT INTO password_reset_tokens(token_hash, user_id, expires_at)
                VALUES ($1, $2, now() - interval '2 days')
                """, Guid.NewGuid().ToString("N"), userId);
            await ExecuteAsync(dataSource, """
                INSERT INTO passkey_challenges(challenge_id, user_id, flow, options, expires_at)
                VALUES ($1, $2, 'registration', '{}', now() - interval '2 days')
                """, Guid.NewGuid(), userId);

            var export = await store.ExportUserAsync(userId, "127.0.0.1", 100);
            Assert.NotNull(export);
            Assert.Equal($"maintenance-{userId}@example.test", export!.Account.Email);
            Assert.Single(export.ApiKeys);
            Assert.DoesNotContain("do-not-export-hash", System.Text.Json.JsonSerializer.Serialize(export));
            Assert.DoesNotContain("password-secret", System.Text.Json.JsonSerializer.Serialize(export));

            var fingerprint = MaintenanceStore.Fingerprint(false, 30, 100);
            var applied = await store.CleanupExpiredAsync(1, operationKey, fingerprint,
                false, 30, 100, "127.0.0.1");
            Assert.Equal(MaintenanceOperationStatus.Applied, applied.Status);
            Assert.True(applied.Summary!.AuthSessions >= 1);
            Assert.True(applied.Summary.PasswordResetTokens >= 1);
            Assert.True(applied.Summary.PasskeyChallenges >= 1);
            Assert.Equal(0, await ScalarAsync(dataSource,
                "SELECT count(*) FROM auth_sessions WHERE user_id = $1 AND expires_at < now()", userId));
            Assert.Equal(0, await ScalarAsync(dataSource,
                "SELECT count(*) FROM password_reset_tokens WHERE user_id = $1", userId));
            Assert.Equal(0, await ScalarAsync(dataSource,
                "SELECT count(*) FROM passkey_challenges WHERE user_id = $1", userId));

            var replay = await store.CleanupExpiredAsync(1, operationKey, fingerprint,
                false, 30, 100, "127.0.0.1");
            Assert.Equal(MaintenanceOperationStatus.Replayed, replay.Status);
            Assert.Equal(applied.Summary, replay.Summary);

            var conflict = await store.CleanupExpiredAsync(1, operationKey,
                MaintenanceStore.Fingerprint(true, 30, 100), true, 30, 100, "127.0.0.1");
            Assert.Equal(MaintenanceOperationStatus.Conflict, conflict.Status);
        }
        finally
        {
            await ExecuteAsync(dataSource, "DELETE FROM maintenance_operations WHERE operation_key = $1", operationKey);
            await ExecuteAsync(dataSource, "DELETE FROM user_api_keys WHERE user_email = $1", $"maintenance-{userId}@example.test");
            await ExecuteAsync(dataSource, "DELETE FROM audit_logs WHERE user_id = $1 AND action IN ('user.data_export', 'maintenance.cleanup')", userId);
            await ExecuteAsync(dataSource, "DELETE FROM user_accounts WHERE id = $1", userId);
        }
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql, params object[] values)
    {
        await using var command = dataSource.CreateCommand(sql);
        for (var index = 0; index < values.Length; index++) command.Parameters.AddWithValue(values[index]);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(NpgsqlDataSource dataSource, string sql, params object[] values)
    {
        await using var command = dataSource.CreateCommand(sql);
        for (var index = 0; index < values.Length; index++) command.Parameters.AddWithValue(values[index]);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
