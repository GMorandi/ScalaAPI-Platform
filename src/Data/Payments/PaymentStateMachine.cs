namespace ScalaAPI.Data.Payments;

/// <summary>
/// Payment order states and allowed transitions.
/// States: Pending, Paid, PartiallyRefunded, Refunded, Failed, Cancelled.
/// All transitions require amount/currency/provider-payment-id validation.
/// Refund accumulation cannot exceed the paid amount.
/// </summary>
public static class PaymentStateMachine
{
    public const string Pending = "pending";
    public const string Paid = "paid";
    public const string PartiallyRefunded = "partially_refunded";
    public const string Refunded = "refunded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    /// <summary>
    /// Result of attempting to transition a payment from Pending to Paid.
    /// </summary>
    public sealed record PaymentTransitionResult(
        bool Success,
        string NewStatus,
        string? Error = null);

    /// <summary>
    /// Result of validating a refund against the current payment state.
    /// </summary>
    public sealed record RefundValidationResult(
        bool IsValid,
        string NewStatus,
        decimal NewRefundTotal,
        string? Error = null);

    /// <summary>
    /// Attempts to transition a payment from Pending to Paid.
    /// Validates: status must be pending, amount/currency must match exactly,
    /// providerPaymentId must be present.
    /// </summary>
    public static PaymentTransitionResult TryTransitionPaid(
        string currentStatus,
        decimal orderAmount,
        string orderCurrency,
        string? providerPaymentId,
        decimal webhookAmount,
        string webhookCurrency,
        string? webhookProviderPaymentId)
    {
        if (!currentStatus.Equals(Pending, StringComparison.OrdinalIgnoreCase))
            return new PaymentTransitionResult(false, currentStatus,
                currentStatus.Equals(Paid, StringComparison.OrdinalIgnoreCase)
                    ? "already_paid"
                    : "invalid_state_for_payment");

        if (orderAmount != webhookAmount)
            return new PaymentTransitionResult(false, currentStatus, "amount_mismatch");

        if (!string.Equals(orderCurrency, webhookCurrency, StringComparison.OrdinalIgnoreCase))
            return new PaymentTransitionResult(false, currentStatus, "currency_mismatch");

        if (string.IsNullOrWhiteSpace(providerPaymentId)
            && string.IsNullOrWhiteSpace(webhookProviderPaymentId))
            return new PaymentTransitionResult(false, currentStatus, "missing_provider_payment_id");

        return new PaymentTransitionResult(true, Paid);
    }

    /// <summary>
    /// Validates whether a refund of the given amount is allowed.
    /// Checks: order must be paid or partially_refunded, refund must not exceed remaining,
    /// currency must match, amount must be positive.
    /// </summary>
    public static RefundValidationResult TryValidateRefund(
        string currentStatus,
        decimal orderAmount,
        decimal currentRefundTotal,
        decimal refundAmount,
        string orderCurrency,
        string refundCurrency)
    {
        if (currentStatus is not (Paid or PartiallyRefunded))
            return new RefundValidationResult(false, currentStatus, currentRefundTotal,
                "order_not_refundable");

        if (refundAmount <= 0m)
            return new RefundValidationResult(false, currentStatus, currentRefundTotal,
                "invalid_refund_amount");

        if (!string.Equals(orderCurrency, refundCurrency, StringComparison.OrdinalIgnoreCase))
            return new RefundValidationResult(false, currentStatus, currentRefundTotal,
                "currency_mismatch");

        var remaining = orderAmount - currentRefundTotal;
        if (refundAmount > remaining)
            return new RefundValidationResult(false, currentStatus, currentRefundTotal,
                "refund_exceeds_remaining");

        var newRefundTotal = currentRefundTotal + refundAmount;
        var newStatus = newRefundTotal >= orderAmount ? Refunded : PartiallyRefunded;
        return new RefundValidationResult(true, newStatus, newRefundTotal);
    }

    /// <summary>
    /// Computes the new status and refund total after applying a validated refund.
    /// Should only be called after TryValidateRefund returns IsValid=true.
    /// </summary>
    public static (string NewStatus, decimal NewRefundTotal) ApplyRefundTransition(
        decimal orderAmount, decimal currentRefundTotal, decimal refundAmount)
    {
        var newRefundTotal = currentRefundTotal + refundAmount;
        var newStatus = newRefundTotal >= orderAmount ? Refunded : PartiallyRefunded;
        return (newStatus, newRefundTotal);
    }

    /// <summary>
    /// Returns true if the status represents a terminal state where no further
    /// payment transitions are possible.
    /// </summary>
    public static bool IsTerminal(string status) =>
        status is Failed or Cancelled;

    /// <summary>
    /// Returns true if the order can accept a payment completion webhook.
    /// </summary>
    public static bool CanAcceptPayment(string status) =>
        status.Equals(Pending, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true if the order can accept a refund.
    /// </summary>
    public static bool CanAcceptRefund(string status) =>
        status is (Paid or PartiallyRefunded);
}
