using System.Security.Cryptography;
using System.Text;
using ScalaAPI.Admin.Payments;
using Xunit;

namespace ScalaAPI.Admin.Tests;

public sealed class StripePaymentWebhookTests
{
    [Fact]
    public void StripeSignatureRequiresFreshTimestampAndRawPayload()
    {
        const string secret = "whsec_test";
        var body = Encoding.UTF8.GetBytes("""{"id":"evt_1"}""");
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var signature = Sign(secret, body, now.ToUnixTimeSeconds());

        Assert.True(PaymentWebhookVerifier.VerifyStripe(secret, body, signature, now,
            TimeSpan.FromMinutes(5)));
        Assert.False(PaymentWebhookVerifier.VerifyStripe(secret, Encoding.UTF8.GetBytes(
            """{"id":"evt_tampered"}"""), signature, now, TimeSpan.FromMinutes(5)));
        Assert.False(PaymentWebhookVerifier.VerifyStripe(secret, body, signature,
            now.AddMinutes(6), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void ParsesPaidCheckoutSessionAndOrderMetadata()
    {
        var parsed = StripePaymentWebhookParser.TryParse(
            Encoding.UTF8.GetBytes("""
            {
              "id": "evt_checkout_1",
              "type": "checkout.session.completed",
              "data": { "object": {
                "id": "cs_test_1",
                "payment_intent": "pi_test_1",
                "amount_total": 1234,
                "currency": "usd",
                "payment_status": "paid",
                "metadata": { "order_id": "42" }
              }}
            }
            """), out var payload, out var error);

        Assert.True(parsed, error);
        Assert.Equal("evt_checkout_1", payload?.EventId);
        Assert.Equal("payment.succeeded", payload?.EventType);
        Assert.Equal(42, payload?.OrderId);
        Assert.Equal("cs_test_1", payload?.ProviderOrderId);
        Assert.Equal("pi_test_1", payload?.ProviderPaymentId);
        Assert.Equal(12.34m, payload?.Amount);
        Assert.Equal("USD", payload?.Currency);
    }

    [Fact]
    public void ParsesFullChargeRefundByPaymentIntent()
    {
        var parsed = StripePaymentWebhookParser.TryParse(
            Encoding.UTF8.GetBytes("""
            {
              "id": "evt_refund_1",
              "type": "charge.refunded",
              "data": { "object": {
                "id": "ch_test_1",
                "payment_intent": "pi_test_1",
                "amount_refunded": 1234,
                "currency": "usd"
              }}
            }
            """), out var payload, out var error);

        Assert.True(parsed, error);
        Assert.Equal("payment.refunded", payload?.EventType);
        Assert.Null(payload?.OrderId);
        Assert.Equal("pi_test_1", payload?.ProviderPaymentId);
        Assert.Equal(12.34m, payload?.Amount);
        Assert.True(payload?.IsCumulativeRefund);
    }

    [Fact]
    public void ParsesPartialRefundCreatedWithIndependentRefundId()
    {
        var parsed = StripePaymentWebhookParser.TryParse(
            Encoding.UTF8.GetBytes("""
            {
              "id": "evt_refund_partial_1",
              "type": "refund.created",
              "data": { "object": {
                "id": "re_partial_1",
                "payment_intent": "pi_test_1",
                "amount": 500,
                "currency": "usd"
              }}
            }
            """), out var payload, out var error);

        Assert.True(parsed, error);
        Assert.Equal("re_partial_1", payload?.ProviderRefundId);
        Assert.Equal(5m, payload?.Amount);
        Assert.False(payload?.IsCumulativeRefund);
    }

    [Fact]
    public void RejectsUnpaidCheckoutAndUnsupportedEvents()
    {
        var unpaid = StripePaymentWebhookParser.TryParse(
            Encoding.UTF8.GetBytes("""
            {"id":"evt_unpaid","type":"checkout.session.completed","data":{"object":{
              "id":"cs_unpaid","amount_total":1000,"currency":"usd","payment_status":"unpaid"
            }}}
            """), out _, out var unpaidError);
        var unsupported = StripePaymentWebhookParser.TryParse(
            Encoding.UTF8.GetBytes("""
            {"id":"evt_ping","type":"customer.created","data":{"object":{"id":"cus_1"}}}
            """), out _, out var unsupportedError);

        Assert.False(unpaid);
        Assert.Equal("stripe_webhook_not_paid", unpaidError);
        Assert.False(unsupported);
        Assert.Equal("stripe_webhook_unsupported_event", unsupportedError);
    }

    private static string Sign(string secret, byte[] body, long timestamp)
    {
        var signed = Encoding.UTF8.GetBytes(
            timestamp + "." + Encoding.UTF8.GetString(body));
        var digest = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed);
        return $"t={timestamp},v1={Convert.ToHexString(digest).ToLowerInvariant()}";
    }
}
