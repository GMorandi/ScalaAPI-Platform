using System.Data;
using Npgsql;

namespace ScalaAPI.Data.Subscriptions;

/// <summary>
/// Manages subscription purchase lifecycle driven by payment confirmation.
/// A subscription is only created/activated when a payment order is confirmed paid.
/// Handles expiry checking, renewal, and reconciliation with payment orders.
/// </summary>
public sealed class SubscriptionService(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Creates a subscription purchase driven by payment confirmation.
    /// The purchase is only committed if the payment order status is 'paid'.
    /// Uses idempotency to prevent duplicate entitlements.
    /// </summary>
    public async Task<PurchaseResult> CreateFromPaymentAsync(
        long userId,
        string planId,
        long paymentOrderId,
        bool autoRenew,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        if (userId <= 0 || string.IsNullOrWhiteSpace(planId)
            || paymentOrderId <= 0 || string.IsNullOrWhiteSpace(idempotencyKey))
            return new PurchaseResult(PurchaseStatus.Invalid);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);

        // Verify payment is confirmed
        await using (var paymentCheck = connection.CreateCommand())
        {
            paymentCheck.Transaction = transaction;
            paymentCheck.CommandText = """
                SELECT status FROM payment_orders
                WHERE id = $1 AND user_id = $2 FOR UPDATE
                """;
            paymentCheck.Parameters.AddWithValue(paymentOrderId);
            paymentCheck.Parameters.AddWithValue(userId);
            var status = await paymentCheck.ExecuteScalarAsync(ct);
            if (status is null or DBNull
                || !string.Equals(status.ToString(), "paid", StringComparison.OrdinalIgnoreCase))
            {
                await transaction.RollbackAsync(ct);
                return new PurchaseResult(PurchaseStatus.PaymentNotConfirmed);
            }
        }

        // Check for duplicate (idempotency)
        await using (var dupCheck = connection.CreateCommand())
        {
            dupCheck.Transaction = transaction;
            dupCheck.CommandText = """
                SELECT purchase_id FROM subscription_purchases
                WHERE user_id = $1 AND plan_id = $2
                  AND payment_order_id = $3
                FOR UPDATE
                """;
            dupCheck.Parameters.AddWithValue(userId);
            dupCheck.Parameters.AddWithValue(planId);
            dupCheck.Parameters.AddWithValue(paymentOrderId);
            var existing = await dupCheck.ExecuteScalarAsync(ct);
            if (existing is not null and not DBNull)
            {
                await transaction.CommitAsync(ct);
                return new PurchaseResult(PurchaseStatus.Duplicate,
                    Convert.ToInt64(existing));
            }
        }

        var now = DateTime.UtcNow;
        var expiresAt = now.AddDays(30);

        long purchaseId;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO subscription_purchases
                    (user_id, plan_id, payment_order_id, started_at, expires_at, auto_renew)
                VALUES ($1, $2, $3, $4, $5, $6)
                ON CONFLICT (user_id, plan_id, started_at) DO NOTHING
                RETURNING purchase_id
                """;
            insert.Parameters.AddWithValue(userId);
            insert.Parameters.AddWithValue(planId);
            insert.Parameters.AddWithValue(paymentOrderId);
            insert.Parameters.AddWithValue(now);
            insert.Parameters.AddWithValue(expiresAt);
            insert.Parameters.AddWithValue(autoRenew);
            var result = await insert.ExecuteScalarAsync(ct);
            if (result is null or DBNull)
            {
                await transaction.CommitAsync(ct);
                return new PurchaseResult(PurchaseStatus.Duplicate);
            }
            purchaseId = Convert.ToInt64(result);
        }

        await transaction.CommitAsync(ct);
        return new PurchaseResult(PurchaseStatus.Created, purchaseId);
    }

    /// <summary>
    /// Checks and expires subscriptions that have passed their expiry date.
    /// Returns the number of subscriptions expired.
    /// </summary>
    public async Task<int> ExpireDueAsync(CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE subscription_purchases
            SET status = 'expired'
            WHERE status = 'active' AND expires_at <= now()
            """);
        return await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Checks whether a specific subscription is still active (not expired).
    /// </summary>
    public async Task<bool> IsActiveAsync(long purchaseId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT status = 'active' AND expires_at > now()
            FROM subscription_purchases WHERE purchase_id = $1
            """);
        command.Parameters.AddWithValue(purchaseId);
        var result = await command.ExecuteScalarAsync(ct);
        return result is bool b && b;
    }

    /// <summary>
    /// Lists subscriptions for a given user, filtered by status.
    /// Ensures users can only see their own subscriptions.
    /// </summary>
    public async Task<IReadOnlyList<SubscriptionView>> ListForUserAsync(
        long userId, string? status = null, CancellationToken ct = default)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        await using var command = dataSource.CreateCommand("""
            SELECT purchase_id, user_id, plan_id, payment_order_id,
                   started_at, expires_at, status, auto_renew, created_at
            FROM subscription_purchases
            WHERE user_id = $1
              AND ($2::text IS NULL OR status = $2)
            ORDER BY started_at DESC
            """);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue((object?)status ?? DBNull.Value);
        var items = new List<SubscriptionView>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new SubscriptionView(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.GetDateTime(4), reader.GetDateTime(5),
                reader.GetString(6), reader.GetBoolean(7), reader.GetDateTime(8)));
        }
        return items;
    }

    /// <summary>
    /// Lists all subscriptions (admin view).
    /// </summary>
    public async Task<IReadOnlyList<SubscriptionView>> ListAllAsync(
        int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(0, offset);
        await using var command = dataSource.CreateCommand("""
            SELECT purchase_id, user_id, plan_id, payment_order_id,
                   started_at, expires_at, status, auto_renew, created_at
            FROM subscription_purchases
            ORDER BY created_at DESC
            LIMIT $1 OFFSET $2
            """);
        command.Parameters.AddWithValue(limit);
        command.Parameters.AddWithValue(offset);
        var items = new List<SubscriptionView>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new SubscriptionView(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.GetDateTime(4), reader.GetDateTime(5),
                reader.GetString(6), reader.GetBoolean(7), reader.GetDateTime(8)));
        }
        return items;
    }

    /// <summary>
    /// Reconciles subscription purchases with payment orders.
    /// Finds purchases where the payment is not 'paid' but the subscription is 'active'.
    /// </summary>
    public async Task<IReadOnlyList<ReconciliationMismatch>> ReconcileAsync(
        CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT sp.purchase_id, sp.user_id, sp.payment_order_id, sp.status,
                   po.status
            FROM subscription_purchases sp
            LEFT JOIN payment_orders po ON po.id = sp.payment_order_id
            WHERE sp.status = 'active'
              AND (po.status IS NULL OR po.status != 'paid')
            """);
        var items = new List<ReconciliationMismatch>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new ReconciliationMismatch(
                reader.GetInt64(0), reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return items;
    }
}

public sealed record SubscriptionView(
    long PurchaseId, long UserId, string PlanId, long? PaymentOrderId,
    DateTime StartedAt, DateTime ExpiresAt, string Status, bool AutoRenew,
    DateTime CreatedAt);

public sealed record ReconciliationMismatch(
    long PurchaseId, long UserId, long? PaymentOrderId,
    string SubscriptionStatus, string? PaymentStatus);

public enum PurchaseStatus
{
    Created,
    Duplicate,
    PaymentNotConfirmed,
    Invalid,
}

public sealed record PurchaseResult(PurchaseStatus Status, long? PurchaseId = null);
