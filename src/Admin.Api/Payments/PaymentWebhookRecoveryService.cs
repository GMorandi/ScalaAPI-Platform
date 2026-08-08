using Npgsql;
using Orleans;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Admin.Payments;

public sealed class PaymentWebhookRecoveryService(
    NpgsqlDataSource dataSource,
    IClusterClient cluster,
    ILogger<PaymentWebhookRecoveryService> logger) : BackgroundService
{
    private const int BatchSize = 20;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var claimed = await ClaimAsync(stoppingToken);
                foreach (var row in claimed)
                    await ApplyAsync(row, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Payment webhook recovery iteration failed");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<IReadOnlyList<PendingWebhook>> ClaimAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH claim AS (
                SELECT id
                FROM payment_webhook_events
                WHERE status = 'pending'
                  AND (next_attempt_at IS NULL OR next_attempt_at <= now())
                ORDER BY id
                FOR UPDATE SKIP LOCKED
                LIMIT $1
            ), claimed AS (
                SELECT e.id, e.provider, e.event_id, e.event_type,
                       e.payment_id, p.user_id, p.amount
                FROM payment_webhook_events e
                JOIN claim c ON c.id = e.id
                JOIN payment_orders p ON p.id = e.payment_id
            )
            UPDATE payment_webhook_events e
            SET attempts = e.attempts + 1,
                last_attempt_at = now(),
                next_attempt_at = now() + interval '30 seconds'
            FROM claimed c
            WHERE e.id = c.id
            RETURNING c.id, c.provider, c.event_id, c.event_type,
                      c.payment_id, c.user_id, c.amount, e.attempts
            """;
        command.Parameters.AddWithValue(BatchSize);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<PendingWebhook>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PendingWebhook(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetDecimal(6),
                reader.GetInt32(7)));
        }
        await reader.DisposeAsync();
        await transaction.CommitAsync(ct);
        return rows;
    }

    private async Task ApplyAsync(PendingWebhook row, CancellationToken ct)
    {
        var isRefund = row.EventType.Equals("payment.refunded", StringComparison.OrdinalIgnoreCase);
        var effectId = isRefund
            ? $"payment-refund:{row.EventId}"
            : $"payment:{row.PaymentId}";
        var delta = isRefund ? -row.Amount : row.Amount;
        try
        {
            await cluster.GetGrain<IUserGrain>(row.UserId)
                .ApplyBalanceEffect(effectId, delta);
            await using var mark = dataSource.CreateCommand("""
                UPDATE payment_webhook_events
                SET status = 'applied', applied_at = now(), next_attempt_at = NULL, error = NULL
                WHERE id = $1 AND status = 'pending'
                """);
            mark.Parameters.AddWithValue(row.Id);
            await mark.ExecuteNonQueryAsync(ct);
            logger.LogInformation("Recovered payment webhook {Provider}/{EventId} on attempt {Attempt}",
                row.Provider, row.EventId, row.Attempt);
        }
        catch (Exception ex)
        {
            await using var fail = dataSource.CreateCommand("""
                UPDATE payment_webhook_events
                SET error = $2,
                    next_attempt_at = now() + LEAST(interval '5 minutes',
                        interval '5 seconds' * power(2, LEAST(attempts, 6)))
                WHERE id = $1 AND status = 'pending'
                """);
            fail.Parameters.AddWithValue(row.Id);
            fail.Parameters.AddWithValue(ex.Message.Length > 500
                ? ex.Message[..500] : ex.Message);
            await fail.ExecuteNonQueryAsync(ct);
            logger.LogWarning(ex, "Payment webhook {Provider}/{EventId} remains pending", row.Provider, row.EventId);
        }
    }

    private sealed record PendingWebhook(long Id, string Provider, string EventId,
        string EventType, long PaymentId, long UserId, decimal Amount, int Attempt);
}
