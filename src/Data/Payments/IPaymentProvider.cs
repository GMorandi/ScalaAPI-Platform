namespace ScalaAPI.Data.Payments;

/// <summary>
/// Provider-authoritative payment verification and refund interface.
/// Orders only transition to paid via this interface (signed webhook or provider query).
/// </summary>
public interface IPaymentProvider
{
    string Name { get; }

    /// <summary>
    /// Verifies a payment with the provider. Returns verified only if the provider
    /// confirms the payment exists with the expected amount and currency.
    /// </summary>
    Task<PaymentVerificationResult> VerifyPaymentAsync(
        string providerPaymentId,
        decimal expectedAmount,
        string expectedCurrency,
        CancellationToken ct = default);

    /// <summary>
    /// Creates a refund with the provider. Returns success only if the provider
    /// accepts the refund for the given amount.
    /// </summary>
    Task<RefundResult> CreateRefundAsync(
        string providerPaymentId,
        decimal amount,
        string reason,
        CancellationToken ct = default);
}

public record PaymentVerificationResult(
    bool Verified,
    string Status,
    decimal Amount,
    string Currency,
    string? ErrorCode = null);

public record RefundResult(
    bool Success,
    string RefundId,
    decimal Amount,
    string? ErrorCode = null);
