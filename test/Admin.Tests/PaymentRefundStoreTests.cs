using Npgsql;
using ScalaAPI.Admin.Data;
using ScalaAPI.Data.Accounting;
using Xunit;

namespace ScalaAPI.Admin.Tests;

public sealed class PaymentRefundStoreTests
{
    [Fact]
    public async Task FullRefundSettlesOnceAndReplaysByCommandKey()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var userId = 9_000_000L + Random.Shared.Next(1, 800_000);
        var actorId = userId + 2_000_000L;
        var key = $"refund-{Guid.NewGuid():N}";
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var accounting = new AccountingStore(dataSource);
        var orderId = 0L;
        try
        {
            var credit = await accounting.AppendEffectAsync(new AccountingEffect(
                userId, $"test-credit:{Guid.NewGuid():N}", "test_credit", 25m));
            await using (var insert = dataSource.CreateCommand("""
                INSERT INTO payment_orders(user_id, amount, currency, provider,
                    provider_order_id, status, description, paid_at)
                VALUES ($1, 10, 'USD', 'mock', 'mock_po_test', 'paid', 'test', now())
                RETURNING id
                """))
            {
                insert.Parameters.AddWithValue(userId);
                orderId = Convert.ToInt64(await insert.ExecuteScalarAsync());
            }

            var store = new PaymentRefundStore(dataSource, accounting);
            var prepared = await store.PrepareAsync(orderId, actorId, key,
                10m, "USD", "customer request");
            Assert.Equal(PaymentRefundPrepareStatus.Created, prepared.Status);
            var competing = await store.PrepareAsync(orderId, actorId,
                $"refund-competing-{Guid.NewGuid():N}", 10m, "USD", "customer request");
            Assert.Equal(PaymentRefundPrepareStatus.InvalidState, competing.Status);

            var claimed = await store.ClaimRecoverableAsync("refund-test-worker-a");
            Assert.Single(claimed);
            Assert.Equal(prepared.RefundId, claimed[0].RefundId);
            Assert.Equal(actorId, claimed[0].ActorUserId);
            var inProgress = await store.PrepareAsync(orderId, actorId, key,
                10m, "USD", "customer request");
            Assert.Equal(PaymentRefundPrepareStatus.InProgress, inProgress.Status);

            await using (var expireClaim = dataSource.CreateCommand("""
                UPDATE payment_refunds
                SET claimed_until = now() - interval '1 second', next_attempt_at = now()
                WHERE id = $1
                """))
            {
                expireClaim.Parameters.AddWithValue(prepared.RefundId);
                await expireClaim.ExecuteNonQueryAsync();
            }
            var reclaimed = await store.ClaimRecoverableAsync("refund-test-worker-b");
            Assert.Single(reclaimed);
            Assert.Equal(2, reclaimed[0].Attempts);

            var settled = await store.FinalizeAsync(prepared.RefundId, actorId,
                "succeeded", "mock_rf_test", null, false);
            var replay = await store.PrepareAsync(orderId, actorId, key,
                10m, "USD", "customer request");
            var conflict = await store.PrepareAsync(orderId, actorId, key,
                9m, "USD", "customer request");

            Assert.Equal(PaymentRefundFinalizeStatus.Succeeded, settled.Status);
            Assert.Equal(PaymentRefundPrepareStatus.Replay, replay.Status);
            Assert.Equal(PaymentRefundPrepareStatus.Conflict, conflict.Status);
            Assert.Equal(15m, settled.BalanceAfter);

            await using var verify = dataSource.CreateCommand("""
                SELECT
                    (SELECT count(*) FROM balance_ledger WHERE user_id = $1 AND entry_type = 'payment_refund'),
                    (SELECT status FROM payment_orders WHERE id = $2),
                    (SELECT status FROM payment_refunds WHERE id = $3)
                """);
            verify.Parameters.AddWithValue(userId);
            verify.Parameters.AddWithValue(orderId);
            verify.Parameters.AddWithValue(prepared.RefundId);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal("refunded", reader.GetString(1));
            Assert.Equal("succeeded", reader.GetString(2));
        }
        finally
        {
            foreach (var statement in new[]
            {
                "DELETE FROM audit_logs WHERE user_id = $1",
                "DELETE FROM payment_refunds WHERE user_id = $1",
                "DELETE FROM payment_orders WHERE user_id = $1",
                "DELETE FROM accounting_projection_outbox WHERE user_id = $1",
                "DELETE FROM balance_ledger WHERE user_id = $1",
                "DELETE FROM accounting_accounts WHERE user_id = $1",
            })
            {
                await using var cleanup = dataSource.CreateCommand(statement);
                cleanup.Parameters.AddWithValue(userId);
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public async Task PartialRefundsAccumulateAndOnlyFinalRefundClosesOrder()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var userId = 9_000_000L + Random.Shared.Next(1, 800_000);
        var actorId = userId + 2_000_000L;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var accounting = new AccountingStore(dataSource);
        var orderId = 0L;
        try
        {
            await accounting.AppendEffectAsync(new AccountingEffect(
                userId, $"test-credit:{Guid.NewGuid():N}", "test_credit", 25m));
            await using (var insert = dataSource.CreateCommand("""
                INSERT INTO payment_orders(user_id, amount, currency, provider,
                    provider_order_id, status, description, paid_at)
                VALUES ($1, 10, 'USD', 'mock', $2, 'paid', 'test', now())
                RETURNING id
                """))
            {
                insert.Parameters.AddWithValue(userId);
                insert.Parameters.AddWithValue($"mock_po_partial_{Guid.NewGuid():N}");
                orderId = Convert.ToInt64(await insert.ExecuteScalarAsync());
            }

            var store = new PaymentRefundStore(dataSource, accounting);
            var first = await store.PrepareAsync(orderId, actorId,
                $"refund-partial-a-{Guid.NewGuid():N}", 6m, "USD", "first partial");
            Assert.Equal(PaymentRefundPrepareStatus.Created, first.Status);
            var firstResult = await store.FinalizeAsync(first.RefundId, actorId,
                "succeeded", "mock_rf_partial_a", null, false);
            Assert.Equal(PaymentRefundFinalizeStatus.Succeeded, firstResult.Status);

            await using (var afterFirst = dataSource.CreateCommand(
                "SELECT refunded_amount, status FROM payment_orders WHERE id = $1"))
            {
                afterFirst.Parameters.AddWithValue(orderId);
                await using var reader = await afterFirst.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(6m, reader.GetDecimal(0));
                Assert.Equal("partially_refunded", reader.GetString(1));
            }

            var second = await store.PrepareAsync(orderId, actorId,
                $"refund-partial-b-{Guid.NewGuid():N}", 4m, "USD", "final partial");
            Assert.Equal(PaymentRefundPrepareStatus.Created, second.Status);
            var secondResult = await store.FinalizeAsync(second.RefundId, actorId,
                "succeeded", "mock_rf_partial_b", null, false);
            Assert.Equal(PaymentRefundFinalizeStatus.Succeeded, secondResult.Status);

            await using var verify = dataSource.CreateCommand("""
                SELECT refunded_amount, status,
                    (SELECT count(*) FROM balance_ledger
                     WHERE user_id = $1 AND entry_type = 'payment_refund')
                FROM payment_orders WHERE id = $2
                """);
            verify.Parameters.AddWithValue(userId);
            verify.Parameters.AddWithValue(orderId);
            await using var finalReader = await verify.ExecuteReaderAsync();
            Assert.True(await finalReader.ReadAsync());
            Assert.Equal(10m, finalReader.GetDecimal(0));
            Assert.Equal("refunded", finalReader.GetString(1));
            Assert.Equal(2L, finalReader.GetInt64(2));
        }
        finally
        {
            foreach (var statement in new[]
            {
                "DELETE FROM audit_logs WHERE user_id = $1",
                "DELETE FROM payment_refunds WHERE user_id = $1",
                "DELETE FROM payment_orders WHERE user_id = $1",
                "DELETE FROM accounting_projection_outbox WHERE user_id = $1",
                "DELETE FROM balance_ledger WHERE user_id = $1",
                "DELETE FROM accounting_accounts WHERE user_id = $1",
            })
            {
                await using var cleanup = dataSource.CreateCommand(statement);
                cleanup.Parameters.AddWithValue(userId);
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }
}
