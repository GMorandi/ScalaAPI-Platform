using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScalaAPI.Admin.Payments;

public sealed record PaymentWebhookPayload(
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("order_id")] long? OrderId,
    [property: JsonPropertyName("provider_order_id")] string? ProviderOrderId,
    [property: JsonPropertyName("provider_payment_id")] string? ProviderPaymentId,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("currency")] string Currency);

public static class StripePaymentWebhookParser
{
    public static bool TryParse(
        ReadOnlySpan<byte> body, out PaymentWebhookPayload? payload, out string error)
    {
        payload = null;
        error = "stripe_webhook_invalid";
        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            var root = document.RootElement;
            var eventId = RequiredString(root, "id", 128);
            var eventType = RequiredString(root, "type", 128);
            if (!root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("object", out var payment)
                || payment.ValueKind != JsonValueKind.Object)
                return false;

            var normalizedType = eventType switch
            {
                "checkout.session.completed"
                    or "checkout.session.async_payment_succeeded"
                    or "payment_intent.succeeded" => "payment.succeeded",
                "charge.refunded" or "refund.created" => "payment.refunded",
                _ => null,
            };
            if (normalizedType is null)
            {
                error = "stripe_webhook_unsupported_event";
                return false;
            }

            var providerOrderId = eventType.StartsWith("checkout.session.", StringComparison.Ordinal)
                ? RequiredString(payment, "id", 128)
                : null;
            var providerPaymentId = eventType is "charge.refunded" or "refund.created"
                ? ReadString(payment, "payment_intent", 128)
                : ReadString(payment, "payment_intent", 128)
                    ?? (eventType == "payment_intent.succeeded"
                        ? ReadString(payment, "id", 128) : null);
            if (string.IsNullOrWhiteSpace(providerOrderId)
                && string.IsNullOrWhiteSpace(providerPaymentId))
            {
                error = "stripe_webhook_missing_provider_reference";
                return false;
            }

            var amountMinor = eventType switch
            {
                "checkout.session.completed"
                    or "checkout.session.async_payment_succeeded" =>
                    ReadPositiveInteger(payment, "amount_total"),
                "charge.refunded" => ReadPositiveInteger(payment, "amount_refunded"),
                "refund.created" => ReadPositiveInteger(payment, "amount"),
                "payment_intent.succeeded" =>
                    ReadPositiveInteger(payment, "amount_received")
                    ?? ReadPositiveInteger(payment, "amount"),
                _ => null,
            };
            var currency = ReadString(payment, "currency", 3)?.ToUpperInvariant();
            if (amountMinor is null || currency is null || currency.Length != 3
                || !currency.All(ch => ch is >= 'A' and <= 'Z'))
            {
                error = "stripe_webhook_amount_invalid";
                return false;
            }

            if (eventType.StartsWith("checkout.session.", StringComparison.Ordinal)
                && payment.TryGetProperty("payment_status", out var paymentStatus)
                && (paymentStatus.ValueKind != JsonValueKind.String
                    || !string.Equals(paymentStatus.GetString(), "paid", StringComparison.OrdinalIgnoreCase)))
            {
                error = "stripe_webhook_not_paid";
                return false;
            }

            var orderId = ReadOrderId(payment);
            payload = new PaymentWebhookPayload(
                eventId,
                normalizedType,
                orderId,
                providerOrderId,
                providerPaymentId,
                amountMinor.Value / 100m,
                currency);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static long? ReadOrderId(JsonElement payment)
    {
        if (!payment.TryGetProperty("metadata", out var metadata)
            || metadata.ValueKind != JsonValueKind.Object)
            return null;
        var value = ReadString(metadata, "order_id", 32);
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
            out var orderId) && orderId > 0 ? orderId : null;
    }

    private static long? ReadPositiveInteger(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt64(out var number)
            || number <= 0)
            return null;
        return number;
    }

    private static string RequiredString(JsonElement value, string name, int maxLength)
    {
        var result = ReadString(value, name, maxLength);
        if (result is null)
            throw new JsonException();
        return result;
    }

    private static string? ReadString(JsonElement value, string name, int maxLength)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String)
            return null;
        var result = property.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(result) || result.Length > maxLength ? null : result;
    }
}
