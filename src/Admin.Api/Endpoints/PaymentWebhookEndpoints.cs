using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Orleans;
using ScalaAPI.Admin.Payments;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Admin.Endpoints;

public sealed record PaymentWebhookPayload(
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("order_id")] long? OrderId,
    [property: JsonPropertyName("provider_order_id")] string? ProviderOrderId,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("currency")] string Currency);

public static class PaymentWebhookEndpoints
{
    public static void MapPaymentWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/payments/webhooks/{provider}", HandleAsync).AllowAnonymous();
    }

    private static async Task<IResult> HandleAsync(
        string provider,
        HttpRequest request,
        IConfiguration configuration,
        NpgsqlDataSource dataSource,
        IClusterClient cluster,
        CancellationToken ct)
    {
        provider = provider.Trim().ToLowerInvariant();
        if (provider.Length is < 1 or > 64)
            return Results.BadRequest(new { error = "Invalid provider" });

        var secret = configuration[$"Payments:WebhookSecrets:{provider}"];
        if (string.IsNullOrWhiteSpace(secret))
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        using var bodyReader = new StreamReader(request.Body, Encoding.UTF8);
        var bodyText = await bodyReader.ReadToEndAsync(ct);
        var body = Encoding.UTF8.GetBytes(bodyText);
        if (!PaymentWebhookVerifier.Verify(secret, body,
                request.Headers["X-Provider-Signature"].FirstOrDefault()))
            return Results.Unauthorized();

        PaymentWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<PaymentWebhookPayload>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Invalid webhook JSON" });
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.EventId)
            || string.IsNullOrWhiteSpace(payload.EventType)
            || payload.Amount <= 0
            || string.IsNullOrWhiteSpace(payload.Currency)
            || (!payload.OrderId.HasValue && string.IsNullOrWhiteSpace(payload.ProviderOrderId)))
            return Results.BadRequest(new { error = "Incomplete webhook payload" });

        var headerEventId = request.Headers["X-Provider-Event-Id"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(headerEventId)
            && !string.Equals(headerEventId, payload.EventId, StringComparison.Ordinal))
            return Results.BadRequest(new { error = "Event id mismatch" });

        var payloadHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var existing = await FindEventAsync(connection, transaction, provider, payload.EventId, ct);
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

        if (payment.Value.Amount != payload.Amount
            || !string.Equals(payment.Value.Currency, payload.Currency, StringComparison.OrdinalIgnoreCase))
        {
            await SetEventRejectedAsync(connection, transaction, provider, payload.EventId,
                "amount_or_currency_mismatch", ct);
            await transaction.CommitAsync(ct);
            return Results.Conflict(new { error = "Payment amount or currency mismatch" });
        }

        var isRefund = payload.EventType.Equals("payment.refunded", StringComparison.OrdinalIgnoreCase);
        if (isRefund && !payment.Value.Status.Equals("paid", StringComparison.OrdinalIgnoreCase))
        {
            await SetEventRejectedAsync(connection, transaction, provider, payload.EventId,
                "payment_not_paid", ct);
            await transaction.CommitAsync(ct);
            return Results.Conflict(new { error = "Only a paid order can be refunded" });
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
            await InsertLedgerAsync(connection, transaction, payment.Value.UserId,
                payment.Value.Id, null, payment.Value.Amount, "payment_credit", ct);
        }
        else if (isRefund)
        {
            await UpdatePaymentStatusAsync(connection, transaction, payment.Value.Id, "refunded", ct);
            await InsertLedgerAsync(connection, transaction, payment.Value.UserId,
                null, $"payment-refund:{payload.EventId}", -payment.Value.Amount, "payment_refund", ct);
        }

        await SetEventPendingAsync(connection, transaction, provider, payload.EventId,
            payment.Value.Id, ct);
        await transaction.CommitAsync(ct);

        var effectId = isRefund
            ? $"payment-refund:{payload.EventId}"
            : $"payment:{payment.Value.Id}";
        var delta = isRefund ? -payment.Value.Amount : payment.Value.Amount;
        try
        {
            await cluster.GetGrain<IUserGrain>(payment.Value.UserId)
                .ApplyBalanceEffect(effectId, delta);
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
        return Results.Ok(new { duplicate = existing is not null, status = "applied" });
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

    private static async Task<PaymentRow?> FindPaymentAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string provider, PaymentWebhookPayload payload,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = payload.OrderId.HasValue
            ? "SELECT id, user_id, amount, currency, status FROM payment_orders WHERE id = $1 AND provider = $2 FOR UPDATE"
            : "SELECT id, user_id, amount, currency, status FROM payment_orders WHERE provider_order_id = $1 AND provider = $2 FOR UPDATE";
        command.Parameters.AddWithValue(payload.OrderId.HasValue
            ? payload.OrderId.Value : payload.ProviderOrderId ?? string.Empty);
        command.Parameters.AddWithValue(provider);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new PaymentRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetDecimal(2),
            reader.GetString(3), reader.GetString(4));
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

    private static async Task InsertLedgerAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, long userId, long? paymentId, string? reference,
        decimal amount, string entryType, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO balance_ledger(user_id, payment_id, reference, amount, entry_type)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT DO NOTHING
            """;
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue((object?)paymentId ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)reference ?? DBNull.Value);
        command.Parameters.AddWithValue(amount);
        command.Parameters.AddWithValue(entryType);
        await command.ExecuteNonQueryAsync(ct);
    }

    private readonly record struct EventRow(string PayloadHash, string Status, long? PaymentId);
    private readonly record struct PaymentRow(long Id, long UserId, decimal Amount,
        string Currency, string Status);
}
