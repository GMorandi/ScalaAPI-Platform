using System.Text;
using ScalaAPI.Admin.Payments;
using Xunit;

namespace ScalaAPI.Admin.Tests;

public sealed class PaymentWebhookVerifierTests
{
    [Fact]
    public void VerifyAcceptsCanonicalHexAndPrefixedSignatures()
    {
        var payload = Encoding.UTF8.GetBytes("{\"event_id\":\"evt-1\"}");
        var signature = PaymentWebhookVerifier.ComputeSignature("test-secret", payload);

        Assert.True(PaymentWebhookVerifier.Verify("test-secret", payload, signature));
        Assert.True(PaymentWebhookVerifier.Verify("test-secret", payload, $"sha256={signature}"));
    }

    [Fact]
    public void VerifyAcceptsBase64AndRejectsTampering()
    {
        var payload = Encoding.UTF8.GetBytes("payload");
        var hex = PaymentWebhookVerifier.ComputeSignature("test-secret", payload);
        var base64 = Convert.ToBase64String(Convert.FromHexString(hex));

        Assert.True(PaymentWebhookVerifier.Verify("test-secret", payload, base64));
        Assert.False(PaymentWebhookVerifier.Verify("test-secret", Encoding.UTF8.GetBytes("tampered"), hex));
        Assert.False(PaymentWebhookVerifier.Verify("wrong-secret", payload, hex));
    }

    [Fact]
    public void VerifyRejectsMalformedOrMissingSignature()
    {
        var payload = Encoding.UTF8.GetBytes("payload");

        Assert.False(PaymentWebhookVerifier.Verify("test-secret", payload, null));
        Assert.False(PaymentWebhookVerifier.Verify("test-secret", payload, "not-a-signature"));
        Assert.False(PaymentWebhookVerifier.Verify("", payload, "00"));
    }
}
