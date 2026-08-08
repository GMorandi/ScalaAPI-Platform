using Npgsql;
using ScalaAPI.Data.Accounting;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class AccountingStoreTests
{
    [Fact]
    public async Task EffectsAreSerializedVersionedAndHoldAware()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new AccountingStore(dataSource);
        var userId = 9_000_000L + Random.Shared.Next(1, 900_000);
        var prefix = $"accounting-test:{Guid.NewGuid():N}";
        try
        {
            var writes = Enumerable.Range(1, 20)
                .Select(i => store.AppendEffectAsync(new AccountingEffect(
                    userId, $"{prefix}:{i}", "test_credit", 1m)))
                .ToArray();
            var results = await Task.WhenAll(writes);

            Assert.All(results, result =>
                Assert.Equal(AccountingEffectStatus.Created, result.Status));
            Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i),
                results.Select(result => result.Snapshot.Version).Order());
            Assert.Equal(20m, results.MaxBy(result => result.Snapshot.Version)!.Snapshot.Balance);

            var replay = await store.AppendEffectAsync(new AccountingEffect(
                userId, $"{prefix}:1", "test_credit", 1m));
            var conflict = await store.AppendEffectAsync(new AccountingEffect(
                userId, $"{prefix}:1", "test_credit", 2m));
            Assert.Equal(AccountingEffectStatus.Replay, replay.Status);
            Assert.Equal(AccountingEffectStatus.Conflict, conflict.Status);
            Assert.Equal(20, replay.Snapshot.Version);
            Assert.Equal(20m, replay.Snapshot.Balance);

            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                Assert.True(await store.TryReserveHoldAsync(connection, transaction,
                    userId, $"{prefix}:hold", $"{prefix}:lease", 15m));
                await transaction.CommitAsync();
            }

            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                Assert.False(await store.TryReserveHoldAsync(connection, transaction,
                    userId, $"{prefix}:hold-over", $"{prefix}:lease-over", 6m));
                await transaction.RollbackAsync();
            }

            var blocked = await store.AppendEffectAsync(new AccountingEffect(
                userId, $"{prefix}:blocked-debit", "test_debit", -6m,
                MinimumBalance: 15m));
            Assert.Equal(AccountingEffectStatus.InsufficientFunds, blocked.Status);
            Assert.Equal(20, blocked.Snapshot.Version);

            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await store.FinalizeHoldAsync(connection, transaction, userId,
                    $"{prefix}:hold", "released");
                await transaction.CommitAsync();
            }
            var debit = await store.AppendEffectAsync(new AccountingEffect(
                userId, $"{prefix}:debit", "test_debit", -6m,
                MinimumBalance: 0m));
            Assert.Equal(AccountingEffectStatus.Created, debit.Status);
            Assert.Equal(new AccountingSnapshot(userId, 21, 14m), debit.Snapshot);

            await using var verify = dataSource.CreateCommand("""
                SELECT account.posted_balance, account.ledger_version,
                       count(ledger.id), count(DISTINCT ledger.ledger_version),
                       min(ledger.ledger_version), max(ledger.ledger_version),
                       outbox.ledger_version, outbox.posted_balance
                FROM accounting_accounts account
                JOIN balance_ledger ledger ON ledger.user_id = account.user_id
                JOIN accounting_projection_outbox outbox ON outbox.user_id = account.user_id
                WHERE account.user_id = $1
                GROUP BY account.posted_balance, account.ledger_version,
                         outbox.ledger_version, outbox.posted_balance
                """);
            verify.Parameters.AddWithValue(userId);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(14m, reader.GetDecimal(0));
            Assert.Equal(21, reader.GetInt64(1));
            Assert.Equal(21, reader.GetInt64(2));
            Assert.Equal(21, reader.GetInt64(3));
            Assert.Equal(1, reader.GetInt64(4));
            Assert.Equal(21, reader.GetInt64(5));
            Assert.Equal(21, reader.GetInt64(6));
            Assert.Equal(14m, reader.GetDecimal(7));
        }
        finally
        {
            foreach (var table in new[]
                     {
                         "balance_holds", "accounting_projection_outbox",
                         "balance_ledger", "accounting_accounts"
                     })
            {
                await using var cleanup = dataSource.CreateCommand(
                    $"DELETE FROM {table} WHERE user_id = $1");
                cleanup.Parameters.AddWithValue(userId);
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }
}
