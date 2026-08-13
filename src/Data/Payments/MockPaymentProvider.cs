namespace ScalaAPI.Data.Payments;

/// <summary>
/// Mock payment provider for local development and testing.
/// VerifyPaymentAsync returns verified if providerPaymentId starts with "mock-paid-".
/// CreateRefundAsync always succeeds for valid inputs (no persistent state).
/// </summary>
public sealed class MockPaymentProvider : IPaymentProvider
{
    public string Name => "mock";

    public Task<PaymentVerificationResult> VerifyPaymentAsync(
        string providerPaymentId,
        decimal expectedAmount,
        string expectedCurrency,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerPaymentId))
            return Task.FromResult(new PaymentVerificationResult(
                false, "not_found", 0m, "", "payment_not_found"));

        if (!providerPaymentId.StartsWith("mock-paid-", StringComparison.Ordinal))
            return Task.FromResult(new PaymentVerificationResult(
                false, "not_paid", 0m, "", "payment_not_completed"));

        return Task.FromResult(new PaymentVerificationResult(
            true, "succeeded", expectedAmount,
            expectedCurrency.Trim().ToUpperInvariant()));
    }

    public Task<RefundResult> CreateRefundAsync(
        string providerPaymentId,
        decimal amount,
        string reason,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(providerPaymentId) || amount <= 0m)
            return Task.FromResult(new RefundResult(false, "", 0m, "invalid_request"));

        var refundId = $"mock_rf_{Guid.NewGuid():N}"[..20];
        return Task.FromResult(new RefundResult(true, refundId, amount));
    }
}
