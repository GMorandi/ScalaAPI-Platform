using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using ScalaAPI.Admin.Data;
using ScalaAPI.Admin.Payments;
using ScalaAPI.Data.Accounting;

namespace ScalaAPI.Admin.Endpoints;

public static class PaymentWebhookEndpoints
{
    private const int MaxBodyBytes = 256 * 1024;

    public static void MapPaymentWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/payments/webhooks/{provider}", HandleAsync).AllowAnonymous();
    }

    private static async Task<IResult> HandleAsync(
        string provider,
        HttpRequest request,
        IConfiguration configuration,
        NpgsqlDataSource dataSource,
        AccountingStore accounting,
        AccountingProjectionService projection,
        CancellationToken ct)
    {
        provider = provider.Trim().ToLowerInvariant();
        if (provider.Length is < 1 or > 64)
            return Results.BadRequest(new { error = "Invalid provider" });

        var secret = configuration[$"Payments:WebhookSecrets:{provider}"];
        if (string.IsNullOrWhiteSpace(secret))
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        var body = await ReadBodyAsync(request.Body, ct);
        if (body is null)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        PaymentWebhookPayload? payload;
        if (provider == "stripe")
        {
            var tolerance = ParseStripeTolerance(configuration);
            if (!PaymentWebhookVerifier.VerifyStripe(secret, body,
                    request.Headers["Stripe-Signature"].FirstOrDefault(),
                    DateTimeOffset.UtcNow, tolerance))
                return Results.Unauthorized();
            if (!StripePaymentWebhookParser.TryParse(body, out payload, out var parseError))
                return Results.BadRequest(new { error = parseError });
        }
        else
        {
            if (!PaymentWebhookVerifier.Verify(secret, body,
                    request.Headers["X-Provider-Signature"].FirstOrDefault()))
                return Results.Unauthorized();
            try
            {
                payload = JsonSerializer.Deserialize<PaymentWebhookPayload>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Invalid webhook JSON" });
            }
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.EventId)
            || string.IsNullOrWhiteSpace(payload.EventType)
            || payload.Amount <= 0
            || string.IsNullOrWhiteSpace(payload.Currency)
            || (!payload.OrderId.HasValue
                && string.IsNullOrWhiteSpace(payload.ProviderOrderId)
                && string.IsNullOrWhiteSpace(payload.ProviderPaymentId)))
            return Results.BadRequest(new { error = "Incomplete webhook payload" });

        var headerEventId = request.Headers["X-Provider-Event-Id"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(headerEventId)
            && !string.Equals(headerEventId, payload.EventId, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Event id mismatch" });

        var payloadHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var existing = await FindEventAsync(connection, transaction, provider, payload.EventId, ct);
        var wasExisting = existing is not null;
        if (existing is not null)
        {
            if (!string.Equals(existing.Value.PayloadHash, payloadHash, StringComparison.Ordinal))
                return Results.Conflict(new { error = "Event payload changed" });
            if (existing.Value.Status == "applied")
            {
                await transaction.CommitAsync(ct);
                return Results.Ok(new { duplicate = true, status = "applied" });
            }
            if (existing.Value.Status == "rejected")
            {
                await transaction.CommitAsync(ct);
                return Results.Conflict(new { duplicate = true, status = "rejected" });
            }
        }
        else
        {
            await InsertEventAsync(connection, transaction, provider, payload, payloadHash, ct);
            existing = await FindEventAsync(connection, transaction, provider, payload.EventId, ct);
        }

        if (!IsSupportedEvent(payload.EventType))
        {
            await SetEventRejectedAsync(connection, transaction, provider, payload.EventId,
                "unsupported_event", ct);
            await transaction.CommitAsync(ct);
            return Results.BadRequest(new { error = "Unsupported payment event" });
        }

        var payment = await FindPaymentAsync(connection, transaction, provider, payload, ct);
        if (payment is null)
        {
            await SetEventRejectedAsync(connection, transaction, provider, payload.EventId,
                "payment_not_found", ct);
            await transaction.CommitAsync(ct);
            return Results.NotFound(new { error = "Payment order not found" });
        }

        var isRefund = payload.EventType.Equals("payment.refunded", StringComparison.OrdinalIgnoreCase);
        var webhookIdempotencyKey = $"webhook:{provider}:{payload.EventId}";
        var existingRefund = isRefund
            ? await FindRefundByKeyAsync(connection, transaction, payment.Value.UserId,
                webhookIdempotencyKey, ct)
            : null;
        if (!isRefund && (payment.Value.Amount != payload.Amount
            || !string.Equals(payment.Value.Currency, payload.Currency, StringComparison.OrdinalIgnoreCase)))
        {
            await SetEventRejectedAsync(connection, transaction, provider, payload.EventId,
                "amount_or_currency_mismatch", ct);
            await transaction.CommitAsync(ct);
            return Results.Conflict(new { error = "Payment amount or currency mismatch" });
        }

        var refundAmount = existingRefund is not null
            ? existingRefund.Value.Amount
            : payload.IsCumulativeRefund
                ? payload.Amount - payment.Value.RefundedAmount
                : payload.Amount;
        if (isRefund && existingRefund is null
            && (!string.Equals(payment.Value.Currency, payload.Currency, StringComparison.OrdinalIgnoreCase)
                || refundAmount <= 0m
                || refundAmount > payment.Value.Amount - payment.Value.RefundedAmount
                || payment.Value.Status is not ("paid" or "partially_refunded")))
        {
            await SetEventRejectedAsync(connection, transaction, provider, payload.EventId,
                "payment_not_paid_or_refund_exceeds_remaining", ct);
            await transaction.CommitAsync(ct);
            return Results.Conflict(new { error = "Refund exceeds the remaining paid amount" });
        }

        if (isRefund && existingRefund is not null
            && (existingRefund.Value.Amount != refundAmount
                || !string.Equals(existingRefund.Value.Currency, payload.Currency, StringComparison.OrdinalIgnoreCase)
                || existingRefund.Value.Status != "succeeded"))
        {
            await SetEventRejectedAsync(connection, transaction, provider, payload.EventId,
                "refund_event_state_conflict", ct);
            await transaction.CommitAsync(ct);
            return Results.Conflict(new { error = "Refund event state changed" });
        }

        if (!isRefund && payment.Value.Status.Equals("refunded", StringComparison.OrdinalIgnoreCase))
        {
            await SetEventRejectedAsync(connection, transaction, provider, payload.EventId,
                "payment_already_refunded", ct);
            await transaction.CommitAsync(ct);
            return Results.Conflict(new { error = "Payment was already refunded" });
        }

        if (!isRefund && payment.Value.Status.Equals("pending", StringComparison.OrdinalIgnoreCase))
        {
            await UpdatePaymentStatusAsync(connection, transaction, payment.Value.Id, "paid", ct);
        }
        long? refundId = null;
        if (isRefund && existingRefund is null)
        {
            if (!string.IsNullOrWhiteSpace(payload.ProviderRefundId)
                && await FindRefundByProviderIdAsync(connection, transaction, provider,
                    payload.ProviderRefundId, ct) is not null)
            {
                await SetEventRejectedAsync(connection, transaction, provider, payload.EventId,
                    "provider_refund_already_recorded", ct);
                await transaction.CommitAsync(ct);
                return Results.Conflict(new { error = "Provider refund was already recorded" });
            }
            refundId = await InsertWebhookRefundAsync(connection, transaction, provider,
                payment.Value, payload, webhookIdempotencyKey, payloadHash, refundAmount, ct);
        }

        var effectId = isRefund
            ? $"payment-refund:{refundId ?? existingRefund!.Value.Id}"
            : $"payment:{payment.Value.Id}";
        var effect = await accounting.AppendEffectAsync(connection, transaction,
            new AccountingEffect(
                payment.Value.UserId,
                effectId,
                isRefund ? "payment_refund" : "payment_credit",
                isRefund ? -refundAmount : payment.Value.Amount,
                IdempotencyKey: webhookIdempotencyKey,
                PaymentId: isRefund ? null : payment.Value.Id), ct);
        if (effect.Status == AccountingEffectStatus.Conflict)
        {
            await transaction.RollbackAsync(ct);
            return Results.Conflict(new { error = "Payment accounting effect changed" });
        }

        if (isRefund && existingRefund is null)
        {
            if (!await ApplyRefundOrderAsync(connection, transaction, payment.Value.Id,
                refundAmount, ct))
            {
                await transaction.RollbackAsync(ct);
                return Results.Conflict(new { error = "Refund order state changed" });
            }
        }

        await SetEventPendingAsync(connection, transaction, provider, payload.EventId,
            payment.Value.Id, ct);
        await transaction.CommitAsync(ct);

        try
        {
            await projection.ApplyAsync(effect.Snapshot, ct);
        }
        catch
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        await using var mark = dataSource.CreateCommand("""
            UPDATE payment_webhook_events
            SET status = 'applied', applied_at = now(), error = NULL
            WHERE provider = $1 AND event_id = $2
            """);
        mark.Parameters.AddWithValue(provider);
        mark.Parameters.AddWithValue(payload.EventId);
        await mark.ExecuteNonQueryAsync(ct);
        return Results.Ok(new { duplicate = wasExisting, status = "applied" });
    }

    private static bool IsSupportedEvent(string eventType) =>
        eventType.Equals("payment.succeeded", StringComparison.OrdinalIgnoreCase)
        || eventType.Equals("payment.refunded", StringComparison.OrdinalIgnoreCase);

    private static async Task<EventRow?> FindEventAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string provider, string eventId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT payload_hash, status, payment_id FROM payment_webhook_events WHERE provider = $1 AND event_id = $2 FOR UPDATE";
        command.Parameters.AddWithValue(provider);
        command.Parameters.AddWithValue(eventId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new EventRow(reader.GetString(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2));
    }

    private static async Task InsertEventAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string provider, PaymentWebhookPayload payload,
        string payloadHash, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO payment_webhook_events(provider, event_id, event_type, payload_hash) VALUES ($1, $2, $3, $4) ON CONFLICT (provider, event_id) DO NOTHING";
        command.Parameters.AddWithValue(provider);
        command.Parameters.AddWithValue(payload.EventId);
        command.Parameters.AddWithValue(payload.EventType);
        command.Parameters.AddWithValue(payloadHash);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task SetEventRejectedAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string provider, string eventId, string error,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE payment_webhook_events SET status = 'rejected', error = $3 WHERE provider = $1 AND event_id = $2";
        command.Parameters.AddWithValue(provider);
        command.Parameters.AddWithValue(eventId);
        command.Parameters.AddWithValue(error);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task SetEventPendingAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string provider, string eventId, long paymentId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE payment_webhook_events SET status = 'pending', payment_id = $3, error = NULL WHERE provider = $1 AND event_id = $2";
        command.Parameters.AddWithValue(provider);
        command.Parameters.AddWithValue(eventId);
        command.Parameters.AddWithValue(paymentId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<RefundRow?> FindRefundByKeyAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long userId,
        string idempotencyKey, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, amount, currency, status
            FROM payment_refunds
            WHERE user_id = $1 AND idempotency_key = $2
            FOR UPDATE
            """;
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new RefundRow(reader.GetInt64(0), reader.GetDecimal(1),
            reader.GetString(2), reader.GetString(3));
    }

    private static async Task<long?> FindRefundByProviderIdAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string provider,
        string providerRefundId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id FROM payment_refunds
            WHERE provider = $1 AND provider_refund_id = $2
            FOR UPDATE
            """;
        command.Parameters.AddWithValue(provider);
        command.Parameters.AddWithValue(providerRefundId);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private static async Task<long> InsertWebhookRefundAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string provider,
        PaymentRow payment,
        PaymentWebhookPayload payload, string idempotencyKey, string payloadHash,
        decimal amount, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO payment_refunds(
                payment_order_id, user_id, provider, provider_order_id,
                provider_payment_id, provider_refund_id, idempotency_key,
                request_fingerprint, amount, currency, reason, status,
                provider_status, actor_user_id, attempts, last_attempt_at,
                completed_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, 'provider webhook',
                    'succeeded', 'succeeded', 0, 1, now(), now())
            ON CONFLICT (user_id, idempotency_key) DO NOTHING
            RETURNING id
            """;
        command.Parameters.AddWithValue(payment.Id);
        command.Parameters.AddWithValue(payment.UserId);
        command.Parameters.AddWithValue(provider);
        command.Parameters.AddWithValue((object?)payload.ProviderOrderId ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)payload.ProviderPaymentId ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)payload.ProviderRefundId ?? DBNull.Value);
        command.Parameters.AddWithValue(idempotencyKey);
        command.Parameters.AddWithValue(payloadHash);
        command.Parameters.AddWithValue(amount);
        command.Parameters.AddWithValue(payload.Currency.ToUpperInvariant());
        var value = await command.ExecuteScalarAsync(ct);
        if (value is not null and not DBNull)
            return Convert.ToInt64(value);

        await using var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = "SELECT id FROM payment_refunds WHERE user_id = $1 AND idempotency_key = $2 FOR UPDATE";
        existing.Parameters.AddWithValue(payment.UserId);
        existing.Parameters.AddWithValue(idempotencyKey);
        var existingValue = await existing.ExecuteScalarAsync(ct);
        if (existingValue is null or DBNull)
            throw new InvalidOperationException("Webhook refund insert was not persisted");
        return Convert.ToInt64(existingValue);
    }

    private static async Task<bool> ApplyRefundOrderAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long paymentId,
        decimal amount, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE payment_orders
            SET refunded_amount = refunded_amount + $2,
                status = CASE WHEN refunded_amount + $2 >= amount
                    THEN 'refunded' ELSE 'partially_refunded' END
            WHERE id = $1 AND status IN ('paid', 'partially_refunded')
              AND refunded_amount + $2 <= amount
            """;
        command.Parameters.AddWithValue(paymentId);
        command.Parameters.AddWithValue(amount);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    private static async Task<PaymentRow?> FindPaymentAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string provider, PaymentWebhookPayload payload,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = payload.OrderId.HasValue
            ? "SELECT id, user_id, amount, refunded_amount, currency, status FROM payment_orders WHERE id = $1 AND provider = $2 FOR UPDATE"
            : !string.IsNullOrWhiteSpace(payload.ProviderOrderId)
                ? "SELECT id, user_id, amount, refunded_amount, currency, status FROM payment_orders WHERE provider_order_id = $1 AND provider = $2 FOR UPDATE"
                : "SELECT id, user_id, amount, refunded_amount, currency, status FROM payment_orders WHERE provider_payment_id = $1 AND provider = $2 FOR UPDATE";
        command.Parameters.AddWithValue(payload.OrderId.HasValue
            ? payload.OrderId.Value
            : !string.IsNullOrWhiteSpace(payload.ProviderOrderId)
                ? payload.ProviderOrderId!
                : payload.ProviderPaymentId ?? string.Empty);
        command.Parameters.AddWithValue(provider);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new PaymentRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetDecimal(2),
            reader.GetDecimal(3), reader.GetString(4), reader.GetString(5));
    }

    private static async Task UpdatePaymentStatusAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, long paymentId, string status, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE payment_orders SET status = $2, paid_at = CASE WHEN $2 = 'paid' THEN now() ELSE paid_at END WHERE id = $1";
        command.Parameters.AddWithValue(paymentId);
        command.Parameters.AddWithValue(status);
        await command.ExecuteNonQueryAsync(ct);
    }

    private readonly record struct EventRow(string PayloadHash, string Status, long? PaymentId);
    private readonly record struct PaymentRow(long Id, long UserId, decimal Amount,
        decimal RefundedAmount, string Currency, string Status);
    private readonly record struct RefundRow(long Id, decimal Amount, string Currency, string Status);

    private static TimeSpan ParseStripeTolerance(IConfiguration configuration)
    {
        var configured = int.TryParse(configuration["Payments:StripeWebhookToleranceSeconds"],
            out var seconds) ? seconds : 300;
        return TimeSpan.FromSeconds(Math.Clamp(configured, 30, 900));
    }

    private static async Task<byte[]?> ReadBodyAsync(Stream stream, CancellationToken ct)
    {
        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0) break;
            total += read;
            if (total > MaxBodyBytes) return null;
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        return buffer.ToArray();
    }
}
