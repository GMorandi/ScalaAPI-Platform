using System.Text.Json;
using Npgsql;

namespace ScalaAPI.Admin.Payments;

public sealed class SubscriptionRenewalService(
    NpgsqlDataSource dataSource,
    ILogger<SubscriptionRenewalService> logger) : BackgroundService
{
    private const int BatchSize = 20;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    public async Task<int> ProcessDueOnceAsync(CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var due = new List<DueSubscription>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT s.id, s.user_id, s.status, s.provider, s.plan_id,
                       s.expires_at, s.renewal_at, s.quota_granted_usd,
                       s.quota_used_usd, s.quota_reserved_usd, p.quota_usd
                FROM user_subscriptions s
                JOIN subscription_plans p ON p.id = s.plan_id
                WHERE (
                    (s.status = 'active' AND s.expires_at IS NOT NULL AND s.expires_at <= now())
                    OR (s.status = 'expired' AND s.renewal_at IS NOT NULL)
                    OR (s.status = 'past_due' AND s.provider = 'internal'
                        AND s.renewal_at IS NOT NULL AND s.quota_reserved_usd = 0)
                )
                ORDER BY s.id
                LIMIT $1
                FOR UPDATE OF s SKIP LOCKED
                """;
            command.Parameters.AddWithValue(BatchSize);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                due.Add(new DueSubscription(
                    reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                    reader.GetString(3), reader.GetInt64(4), reader.GetDateTime(5),
                    reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                    reader.GetDecimal(7), reader.GetDecimal(8), reader.GetDecimal(9),
                    reader.GetDecimal(10)));
            }
        }

        foreach (var subscription in due)
            await ApplyDueAsync(connection, transaction, subscription, ct);

        await transaction.CommitAsync(ct);
        return due.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Subscription renewal iteration failed");
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

    private static async Task ApplyDueAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        DueSubscription subscription, CancellationToken ct)
    {
        var periodKey = subscription.ExpiresAt.ToUniversalTime().Ticks.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        if (subscription.RenewalAt is null)
        {
            await UpdateSubscriptionAsync(connection, transaction, """
                UPDATE user_subscriptions
                SET status = 'expired', cancelled_at = NULL
                WHERE id = $1 AND status = 'active'
                """, subscription.Id, ct);
            await InsertEventAsync(connection, transaction, subscription,
                "expired", $"expiry:{subscription.Id}:{periodKey}", ct);
            return;
        }

        if (!string.Equals(subscription.Provider, "internal", StringComparison.OrdinalIgnoreCase))
        {
            await UpdateSubscriptionAsync(connection, transaction, """
                UPDATE user_subscriptions
                SET status = 'past_due'
                WHERE id = $1 AND status IN ('active', 'expired', 'past_due')
                """, subscription.Id, ct);
            return;
        }

        if (subscription.QuotaReservedUsd > 0m)
        {
            await UpdateSubscriptionAsync(connection, transaction, """
                UPDATE user_subscriptions
                SET status = 'past_due'
                WHERE id = $1 AND status IN ('active', 'expired')
                """, subscription.Id, ct);
            return;
        }

        await using (var renew = connection.CreateCommand())
        {
            renew.Transaction = transaction;
            renew.CommandText = """
                UPDATE user_subscriptions
                SET status = 'active', cancelled_at = NULL,
                    expires_at = GREATEST(COALESCE(expires_at, now()), now()) + interval '30 days',
                    renewal_at = GREATEST(COALESCE(expires_at, now()), now()) + interval '60 days',
                    quota_granted_usd = $2, quota_used_usd = 0, quota_reserved_usd = 0
                WHERE id = $1 AND status IN ('active', 'expired', 'past_due')
                """;
            renew.Parameters.AddWithValue(subscription.Id);
            renew.Parameters.AddWithValue(subscription.PlanQuotaUsd);
            await renew.ExecuteNonQueryAsync(ct);
        }
        await InsertEventAsync(connection, transaction, subscription,
            "renewed", $"renewal:{subscription.Id}:{periodKey}", ct,
            new
            {
                previous_granted_usd = subscription.QuotaGrantedUsd,
                previous_used_usd = subscription.QuotaUsedUsd,
                granted_usd = subscription.PlanQuotaUsd,
            });
    }

    private static async Task UpdateSubscriptionAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        long subscriptionId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue(subscriptionId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertEventAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        DueSubscription subscription, string eventType, string idempotencyKey,
        CancellationToken ct, object? payload = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO subscription_events(
                subscription_id, user_id, event_type, idempotency_key, payload)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (user_id, event_type, idempotency_key) DO NOTHING
            """;
        command.Parameters.AddWithValue(subscription.Id);
        command.Parameters.AddWithValue(subscription.UserId);
        command.Parameters.AddWithValue(eventType);
        command.Parameters.AddWithValue(idempotencyKey);
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = JsonSerializer.Serialize(payload ?? new { }),
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb,
        });
        await command.ExecuteNonQueryAsync(ct);
    }

    private sealed record DueSubscription(
        long Id,
        long UserId,
        string Status,
        string Provider,
        long PlanId,
        DateTime ExpiresAt,
        DateTime? RenewalAt,
        decimal QuotaGrantedUsd,
        decimal QuotaUsedUsd,
        decimal QuotaReservedUsd,
        decimal PlanQuotaUsd);
}
