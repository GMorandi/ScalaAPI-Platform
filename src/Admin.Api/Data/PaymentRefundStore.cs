using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using ScalaAPI.Data.Accounting;

namespace ScalaAPI.Admin.Data;

public enum PaymentRefundPrepareStatus
{
    Created,
    Retryable,
    InProgress,
    Replay,
    Conflict,
    NotFound,
    InvalidState,
}

public sealed record PaymentRefundPreparation(
    PaymentRefundPrepareStatus Status,
    long RefundId,
    long PaymentOrderId,
    long UserId,
    string Provider,
    string? ProviderOrderId,
    string? ProviderPaymentId,
    decimal Amount,
    string Currency,
    string Reason,
    string IdempotencyKey,
    string RequestFingerprint,
    string RefundStatus,
    string? ProviderRefundId,
    long ActorUserId = 0,
    DateTime? ClaimedUntil = null);

public sealed record PaymentRefundRecoveryCommand(
    long RefundId,
    long PaymentOrderId,
    long UserId,
    long ActorUserId,
    string Provider,
    string? ProviderOrderId,
    string? ProviderPaymentId,
    decimal Amount,
    string Currency,
    string Reason,
    string IdempotencyKey,
    int Attempts);

public enum PaymentRefundFinalizeStatus
{
    Succeeded,
    Pending,
    Failed,
    ReconciliationNeeded,
    Replay,
    Conflict,
}

public sealed record PaymentRefundFinalizeResult(
    PaymentRefundFinalizeStatus Status,
    long RefundId,
    long PaymentOrderId,
    long UserId,
    string? ProviderRefundId,
    long? LedgerId,
    long LedgerVersion,
    decimal BalanceAfter,
    string? ErrorCode);

public sealed class PaymentRefundStore(
    NpgsqlDataSource dataSource,
    AccountingStore accounting)
{
    public async Task<PaymentRefundPreparation> PrepareAsync(
        long paymentOrderId,
        long actorId,
        string idempotencyKey,
        decimal amount,
        string currency,
        string reason,
        CancellationToken ct = default)
    {
        var fingerprint = Fingerprint(paymentOrderId, amount, currency, reason);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        await using var order = connection.CreateCommand();
        order.Transaction = transaction;
        order.CommandText = """
            SELECT user_id, amount, currency, provider, provider_order_id,
                   provider_payment_id, status, refunded_amount
            FROM payment_orders WHERE id = $1 FOR UPDATE
            """;
        order.Parameters.AddWithValue(paymentOrderId);
        await using var reader = await order.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            await transaction.RollbackAsync(ct);
            return new(PaymentRefundPrepareStatus.NotFound, 0, paymentOrderId, 0,
                "", null, null, 0m, "", "", idempotencyKey, fingerprint, "", null);
        }
        var userId = reader.GetInt64(0);
        var orderAmount = reader.GetDecimal(1);
        var orderCurrency = reader.GetString(2);
        var provider = reader.GetString(3);
        var providerOrderId = reader.IsDBNull(4) ? null : reader.GetString(4);
        var providerPaymentId = reader.IsDBNull(5) ? null : reader.GetString(5);
        var orderStatus = reader.GetString(6);
        var refundedAmount = reader.GetDecimal(7);
        await reader.DisposeAsync();

        await using var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = """
            SELECT id, amount, currency, reason, request_fingerprint, status,
                   provider_refund_id, actor_user_id, claimed_until
            FROM payment_refunds
            WHERE user_id = $1 AND idempotency_key = $2
            FOR UPDATE
            """;
        existing.Parameters.AddWithValue(userId);
        existing.Parameters.AddWithValue(idempotencyKey);
        await using var existingReader = await existing.ExecuteReaderAsync(ct);
        if (await existingReader.ReadAsync(ct))
        {
            var existingFingerprintMatches = string.Equals(
                existingReader.GetString(4), fingerprint, StringComparison.Ordinal);
            var existingStatus = existingReader.GetString(5);
            var claimedUntil = existingReader.IsDBNull(8)
                ? (DateTime?)null : existingReader.GetDateTime(8);
            var prepareStatus = !existingFingerprintMatches
                ? PaymentRefundPrepareStatus.Conflict
                : existingStatus is "pending" or "reconciliation_needed"
                    ? claimedUntil > DateTime.UtcNow
                        ? PaymentRefundPrepareStatus.InProgress
                        : PaymentRefundPrepareStatus.Retryable
                    : PaymentRefundPrepareStatus.Replay;
            var row = new PaymentRefundPreparation(
                prepareStatus,
                existingReader.GetInt64(0), paymentOrderId, userId, provider,
                providerOrderId, providerPaymentId, existingReader.GetDecimal(1),
                existingReader.GetString(2), existingReader.GetString(3), idempotencyKey,
                existingReader.GetString(4), existingStatus,
                existingReader.IsDBNull(6) ? null : existingReader.GetString(6),
                existingReader.GetInt64(7), claimedUntil);
            await existingReader.DisposeAsync();
            await transaction.CommitAsync(ct);
            return row;
        }
        await existingReader.DisposeAsync();

        await using var active = connection.CreateCommand();
        active.Transaction = transaction;
        active.CommandText = """
            SELECT id
            FROM payment_refunds
            WHERE payment_order_id = $1
              AND status IN ('pending', 'reconciliation_needed')
            LIMIT 1
            FOR UPDATE
            """;
        active.Parameters.AddWithValue(paymentOrderId);
        await using var activeReader = await active.ExecuteReaderAsync(ct);
        if (await activeReader.ReadAsync(ct))
        {
            await activeReader.DisposeAsync();
            await transaction.RollbackAsync(ct);
            return new(PaymentRefundPrepareStatus.InvalidState, 0, paymentOrderId, userId,
                provider, providerOrderId, providerPaymentId, orderAmount, orderCurrency,
                reason, idempotencyKey, fingerprint, orderStatus, null);
        }
        await activeReader.DisposeAsync();

        var remainingAmount = orderAmount - refundedAmount;
        if (orderStatus is not ("paid" or "partially_refunded")
            || amount <= 0m
            || decimal.Round(amount, 2) != amount
            || amount > remainingAmount
            || !string.Equals(currency, orderCurrency, StringComparison.OrdinalIgnoreCase)
            || provider is not ("mock" or "stripe")
            || string.IsNullOrWhiteSpace(providerOrderId) && string.IsNullOrWhiteSpace(providerPaymentId))
        {
            await transaction.RollbackAsync(ct);
            return new(PaymentRefundPrepareStatus.InvalidState, 0, paymentOrderId, userId,
                provider, providerOrderId, providerPaymentId, orderAmount, orderCurrency,
                reason, idempotencyKey, fingerprint, orderStatus, null);
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO payment_refunds(
                payment_order_id, user_id, provider, provider_order_id,
                provider_payment_id, idempotency_key, request_fingerprint,
                amount, currency, reason, actor_user_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            RETURNING id
            """;
        insert.Parameters.AddWithValue(paymentOrderId);
        insert.Parameters.AddWithValue(userId);
        insert.Parameters.AddWithValue(provider);
        insert.Parameters.AddWithValue((object?)providerOrderId ?? DBNull.Value);
        insert.Parameters.AddWithValue((object?)providerPaymentId ?? DBNull.Value);
        insert.Parameters.AddWithValue(idempotencyKey);
        insert.Parameters.AddWithValue(fingerprint);
        insert.Parameters.AddWithValue(amount);
        insert.Parameters.AddWithValue(currency.ToUpperInvariant());
        insert.Parameters.AddWithValue(reason);
        insert.Parameters.AddWithValue(actorId);
        var refundId = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
        await transaction.CommitAsync(ct);
        return new(PaymentRefundPrepareStatus.Created, refundId, paymentOrderId, userId,
            provider, providerOrderId, providerPaymentId, amount, currency, reason,
            idempotencyKey, fingerprint, "pending", null);
    }

    public async Task<IReadOnlyList<PaymentRefundRecoveryCommand>> ClaimRecoverableAsync(
        string workerId, int limit = 20, CancellationToken ct = default)
    {
        workerId = workerId.Trim();
        if (workerId.Length is < 1 or > 128)
            throw new ArgumentException("Refund recovery worker ID is invalid", nameof(workerId));
        limit = Math.Clamp(limit, 1, 100);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH claim AS (
                SELECT id
                FROM payment_refunds
                WHERE status IN ('pending', 'reconciliation_needed')
                  AND next_attempt_at <= now()
                  AND (claimed_until IS NULL OR claimed_until <= now())
                ORDER BY next_attempt_at, id
                FOR UPDATE SKIP LOCKED
                LIMIT $1
            )
            UPDATE payment_refunds r
            SET attempts = r.attempts + 1,
                last_attempt_at = now(),
                next_attempt_at = now() + interval '30 seconds',
                claimed_by = $2,
                claimed_until = now() + interval '60 seconds',
                updated_at = now()
            FROM claim c
            WHERE r.id = c.id
            RETURNING r.id, r.payment_order_id, r.user_id, r.actor_user_id,
                      r.provider, r.provider_order_id, r.provider_payment_id,
                      r.amount, r.currency, r.reason, r.idempotency_key, r.attempts
            """;
        command.Parameters.AddWithValue(limit);
        command.Parameters.AddWithValue(workerId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<PaymentRefundRecoveryCommand>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
                reader.GetInt64(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetDecimal(7),
                reader.GetString(8), reader.GetString(9), reader.GetString(10),
                reader.GetInt32(11)));
        }
        await reader.DisposeAsync();
        await transaction.CommitAsync(ct);
        return rows;
    }

    public async Task<PaymentRefundFinalizeResult> FinalizeAsync(
        long refundId,
        long actorId,
        string providerStatus,
        string? providerRefundId,
        string? errorCode,
        bool retryable,
        CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT payment_order_id, user_id, amount, currency, reason,
                   idempotency_key, status, provider_refund_id
            FROM payment_refunds WHERE id = $1 FOR UPDATE
            """;
        command.Parameters.AddWithValue(refundId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            await transaction.RollbackAsync(ct);
            return new(PaymentRefundFinalizeStatus.Conflict, refundId, 0, 0,
                null, null, 0, 0m, "payment_refund_not_found");
        }
        var orderId = reader.GetInt64(0);
        var userId = reader.GetInt64(1);
        var amount = reader.GetDecimal(2);
        var currency = reader.GetString(3);
        var reason = reader.GetString(4);
        var idempotencyKey = reader.GetString(5);
        var currentStatus = reader.GetString(6);
        var currentProviderRefundId = reader.IsDBNull(7) ? null : reader.GetString(7);
        await reader.DisposeAsync();
        if (currentStatus == "succeeded")
        {
            var snapshot = await ReadSnapshotAsync(connection, transaction, userId, ct);
            await transaction.CommitAsync(ct);
            return new(PaymentRefundFinalizeStatus.Replay, refundId, orderId, userId,
                currentProviderRefundId, null, snapshot.Version, snapshot.Balance, null);
        }

        if (actorId <= 0)
            actorId = await ReadActorAsync(connection, transaction, refundId, ct);

        var finalStatus = retryable ? "reconciliation_needed"
            : providerStatus is "succeeded" or "refunded" ? "succeeded"
            : providerStatus == "pending" ? "pending" : "failed";
        if (finalStatus == "succeeded")
        {
            await using var order = connection.CreateCommand();
            order.Transaction = transaction;
            order.CommandText = "SELECT amount, refunded_amount, status FROM payment_orders WHERE id = $1 FOR UPDATE";
            order.Parameters.AddWithValue(orderId);
            await using var orderReader = await order.ExecuteReaderAsync(ct);
            if (!await orderReader.ReadAsync(ct))
            {
                await orderReader.DisposeAsync();
                await transaction.RollbackAsync(ct);
                return new(PaymentRefundFinalizeStatus.Conflict, refundId, orderId, userId,
                    providerRefundId, null, 0, 0m, "payment_order_not_found");
            }
            var orderAmount = orderReader.GetDecimal(0);
            var refundedAmount = orderReader.GetDecimal(1);
            var orderStatus = orderReader.GetString(2);
            await orderReader.DisposeAsync();
            if (orderStatus is not ("paid" or "partially_refunded")
                || amount > orderAmount - refundedAmount)
            {
                await transaction.RollbackAsync(ct);
                return new(PaymentRefundFinalizeStatus.Conflict, refundId, orderId, userId,
                    providerRefundId, null, 0, 0m, "payment_refund_exceeds_remaining_amount");
            }

            var effect = await accounting.AppendEffectAsync(connection, transaction,
                new AccountingEffect(userId, $"payment-refund:{refundId}", "payment_refund",
                    -amount, IdempotencyKey: idempotencyKey,
                    Description: reason, CreatedBy: actorId), ct);
            if (effect.Status == AccountingEffectStatus.Conflict)
            {
                await transaction.RollbackAsync(ct);
                return new(PaymentRefundFinalizeStatus.Conflict, refundId, orderId, userId,
                    providerRefundId, null, 0, 0m, "payment_refund_effect_conflict");
            }
            await using var orderUpdate = connection.CreateCommand();
            orderUpdate.Transaction = transaction;
            orderUpdate.CommandText = """
                UPDATE payment_orders
                SET refunded_amount = refunded_amount + $2,
                    status = CASE WHEN refunded_amount + $2 >= amount
                        THEN 'refunded' ELSE 'partially_refunded' END
                WHERE id = $1 AND status IN ('paid', 'partially_refunded')
                  AND refunded_amount + $2 <= amount
                """;
            orderUpdate.Parameters.AddWithValue(orderId);
            orderUpdate.Parameters.AddWithValue(amount);
            if (await orderUpdate.ExecuteNonQueryAsync(ct) != 1)
            {
                await transaction.RollbackAsync(ct);
                return new(PaymentRefundFinalizeStatus.Conflict, refundId, orderId, userId,
                    providerRefundId, null, 0, 0m, "payment_refund_order_update_conflict");
            }
            await UpdateRefundAsync(connection, transaction, refundId, "succeeded",
                providerStatus, providerRefundId, null, ct);
            await InsertAuditAsync(connection, transaction, actorId, orderId,
                refundId, amount, currency, reason, effect.Status, ct);
            await transaction.CommitAsync(ct);
            return new(PaymentRefundFinalizeStatus.Succeeded, refundId, orderId, userId,
                providerRefundId ?? currentProviderRefundId, effect.LedgerId,
                effect.Snapshot.Version, effect.Snapshot.Balance, null);
        }

        await UpdateRefundAsync(connection, transaction, refundId, finalStatus,
            providerStatus, providerRefundId, errorCode, ct);
        await transaction.CommitAsync(ct);
        return new(
            finalStatus == "pending" ? PaymentRefundFinalizeStatus.Pending
                : finalStatus == "reconciliation_needed"
                    ? PaymentRefundFinalizeStatus.ReconciliationNeeded
                    : PaymentRefundFinalizeStatus.Failed,
            refundId, orderId, userId, providerRefundId ?? currentProviderRefundId,
            null, 0, 0m, errorCode);
    }

    private static async Task UpdateRefundAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long refundId,
        string status, string providerStatus, string? providerRefundId,
        string? errorCode, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE payment_refunds
            SET status = $2, provider_status = $3, provider_refund_id = COALESCE($4, provider_refund_id),
                error_code = $5, updated_at = now(), claimed_by = NULL, claimed_until = NULL,
                next_attempt_at = now() + CASE WHEN $2 IN ('pending', 'reconciliation_needed')
                    THEN interval '30 seconds' ELSE interval '0 seconds' END,
                completed_at = CASE WHEN $2 = 'succeeded' THEN now() ELSE completed_at END
            WHERE id = $1
            """;
        command.Parameters.AddWithValue(refundId);
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue(providerStatus);
        command.Parameters.AddWithValue((object?)providerRefundId ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)errorCode ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertAuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long actorId,
        long orderId, long refundId, decimal amount, string currency, string reason,
        AccountingEffectStatus effectStatus, CancellationToken ct)
    {
        await using var audit = connection.CreateCommand();
        audit.Transaction = transaction;
        audit.CommandText = """
            INSERT INTO audit_logs(user_id, action, resource_type, resource_id, details)
            VALUES ($1, 'payment.refund', 'payment_order', $2, $3)
            """;
        audit.Parameters.AddWithValue(actorId);
        audit.Parameters.AddWithValue(orderId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        audit.Parameters.AddWithValue(JsonSerializer.Serialize(new
        {
            refund_id = refundId, amount, currency, reason,
            accounting = effectStatus.ToString().ToLowerInvariant(),
        }));
        await audit.ExecuteNonQueryAsync(ct);
    }

    private static string Fingerprint(long orderId, decimal amount, string currency, string reason)
    {
        var canonical = $"{orderId}|{amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}|{currency.Trim().ToUpperInvariant()}|{reason.Trim()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static async Task<AccountingSnapshot> ReadSnapshotAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long userId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT ledger_version, posted_balance FROM accounting_accounts WHERE user_id = $1";
        command.Parameters.AddWithValue(userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new(userId, 0, 0m);
        return new(userId, reader.GetInt64(0), reader.GetDecimal(1));
    }

    private static async Task<long> ReadActorAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long refundId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT actor_user_id FROM payment_refunds WHERE id = $1";
        command.Parameters.AddWithValue(refundId);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }
}
