using Npgsql;
using ScalaAPI.Admin.Data;
using Xunit;

namespace ScalaAPI.Admin.Tests;

public sealed class BalanceAdjustmentStoreTests
{
    [Fact]
    public async Task RecordIsIdempotentAuditedAndProtectsActiveHolds()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var userId = 8_000_000L + Random.Shared.Next(1, 900_000);
        var actorId = userId + 1_000_000L;
        var initialKey = $"test-credit-{Guid.NewGuid():N}";
        var debitKey = $"test-debit-{Guid.NewGuid():N}";
        var holdId = $"test-hold-{Guid.NewGuid():N}";
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var setup = dataSource.CreateCommand("""
            INSERT INTO entity_registry(entity_type, entity_key, entity_id)
            VALUES ('user', $1, $2)
            """);
        setup.Parameters.AddWithValue(userId.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        setup.Parameters.AddWithValue(userId);
        await setup.ExecuteNonQueryAsync();

        try
        {
            var store = new BalanceAdjustmentStore(dataSource);
            var created = await store.RecordAsync(
                userId, actorId, initialKey, 10m, "Initial test funding");
            var replay = await store.RecordAsync(
                userId, actorId, initialKey, 10m, "Initial test funding");
            var conflict = await store.RecordAsync(
                userId, actorId, initialKey, 11m, "Initial test funding");

            Assert.Equal(BalanceAdjustmentStatus.Created, created.Status);
            Assert.Equal(BalanceAdjustmentStatus.Replay, replay.Status);
            Assert.Equal(BalanceAdjustmentStatus.Conflict, conflict.Status);
            Assert.Equal(created.LedgerId, replay.LedgerId);
            Assert.Equal(10m, replay.BalanceAfter);

            await using (var hold = dataSource.CreateCommand("""
                INSERT INTO balance_holds(hold_id, user_id, amount, status)
                VALUES ($1, $2, 8, 'active')
                """))
            {
                hold.Parameters.AddWithValue(holdId);
                hold.Parameters.AddWithValue(userId);
                await hold.ExecuteNonQueryAsync();
            }

            var insufficient = await store.RecordAsync(
                userId, actorId, debitKey, -3m, "Would consume held funds");
            Assert.Equal(BalanceAdjustmentStatus.InsufficientFunds, insufficient.Status);
            Assert.Equal(10m, insufficient.BalanceAfter);

            await using var verify = dataSource.CreateCommand("""
                SELECT
                    (SELECT count(*) FROM balance_ledger
                     WHERE user_id = $1 AND entry_type = 'admin_adjustment'),
                    (SELECT count(*) FROM audit_logs
                     WHERE user_id = $2 AND action = 'balance.adjust'
                       AND resource_id = $1::text)
                """);
            verify.Parameters.AddWithValue(userId);
            verify.Parameters.AddWithValue(actorId);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
        }
        finally
        {
            foreach (var statement in new[]
            {
                "DELETE FROM balance_holds WHERE user_id = $1",
                "DELETE FROM balance_ledger WHERE user_id = $1",
                "DELETE FROM audit_logs WHERE user_id = $2 AND resource_id = $1::text",
                "DELETE FROM entity_registry WHERE entity_type = 'user' AND entity_id = $1",
            })
            {
                await using var cleanup = dataSource.CreateCommand(statement);
                cleanup.Parameters.AddWithValue(userId);
                if (statement.Contains("$2", StringComparison.Ordinal))
                    cleanup.Parameters.AddWithValue(actorId);
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }
}
