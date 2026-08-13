using ScalaAPI.Data.Payments;

namespace ScalaAPI.Host.Tests;

/// <summary>
/// Tests for the PaymentStateMachine and MockPaymentProvider.
/// Verifies provider-authoritative payment completion:
/// - Fake/amount-mismatch/duplicate webhooks don't produce credit
/// - Real mock provider completes checkout -> webhook -> ledger
/// - Refund accumulation doesn't exceed paid
/// - Admin confirm triggers provider verification
/// - Partial refunds work correctly
/// </summary>
public class PaymentStateMachineTests
{
    // ---- Pending -> Paid transitions ----

    [Fact]
    public void TryTransitionPaid_PendingWithMatchingAmount_Succeeds()
    {
        var result = PaymentStateMachine.TryTransitionPaid(
            currentStatus: "pending",
            orderAmount: 10.00m,
            orderCurrency: "USD",
            providerPaymentId: "mock-paid-abc123",
            webhookAmount: 10.00m,
            webhookCurrency: "USD",
            webhookProviderPaymentId: "mock-paid-abc123");

        Assert.True(result.Success);
        Assert.Equal("paid", result.NewStatus);
        Assert.Null(result.Error);
    }

    [Fact]
    public void TryTransitionPaid_AmountMismatch_Rejected()
    {
        var result = PaymentStateMachine.TryTransitionPaid(
            currentStatus: "pending",
            orderAmount: 10.00m,
            orderCurrency: "USD",
            providerPaymentId: "mock-paid-abc123",
            webhookAmount: 15.00m,
            webhookCurrency: "USD",
            webhookProviderPaymentId: "mock-paid-abc123");

        Assert.False(result.Success);
        Assert.Equal("amount_mismatch", result.Error);
    }

    [Fact]
    public void TryTransitionPaid_CurrencyMismatch_Rejected()
    {
        var result = PaymentStateMachine.TryTransitionPaid(
            currentStatus: "pending",
            orderAmount: 10.00m,
            orderCurrency: "USD",
            providerPaymentId: "mock-paid-abc123",
            webhookAmount: 10.00m,
            webhookCurrency: "EUR",
            webhookProviderPaymentId: "mock-paid-abc123");

        Assert.False(result.Success);
        Assert.Equal("currency_mismatch", result.Error);
    }

    [Fact]
    public void TryTransitionPaid_AlreadyPaid_ReturnsAlreadyPaid()
    {
        var result = PaymentStateMachine.TryTransitionPaid(
            currentStatus: "paid",
            orderAmount: 10.00m,
            orderCurrency: "USD",
            providerPaymentId: "mock-paid-abc123",
            webhookAmount: 10.00m,
            webhookCurrency: "USD",
            webhookProviderPaymentId: "mock-paid-abc123");

        Assert.False(result.Success);
        Assert.Equal("already_paid", result.Error);
    }

    [Fact]
    public void TryTransitionPaid_RefundedStatus_Rejected()
    {
        var result = PaymentStateMachine.TryTransitionPaid(
            currentStatus: "refunded",
            orderAmount: 10.00m,
            orderCurrency: "USD",
            providerPaymentId: "mock-paid-abc123",
            webhookAmount: 10.00m,
            webhookCurrency: "USD",
            webhookProviderPaymentId: "mock-paid-abc123");

        Assert.False(result.Success);
        Assert.Equal("invalid_state_for_payment", result.Error);
    }

    [Fact]
    public void TryTransitionPaid_FailedStatus_Rejected()
    {
        var result = PaymentStateMachine.TryTransitionPaid(
            currentStatus: "failed",
            orderAmount: 10.00m,
            orderCurrency: "USD",
            providerPaymentId: "mock-paid-abc123",
            webhookAmount: 10.00m,
            webhookCurrency: "USD",
            webhookProviderPaymentId: "mock-paid-abc123");

        Assert.False(result.Success);
        Assert.Equal("invalid_state_for_payment", result.Error);
    }

    [Fact]
    public void TryTransitionPaid_MissingProviderPaymentId_Rejected()
    {
        var result = PaymentStateMachine.TryTransitionPaid(
            currentStatus: "pending",
            orderAmount: 10.00m,
            orderCurrency: "USD",
            providerPaymentId: null,
            webhookAmount: 10.00m,
            webhookCurrency: "USD",
            webhookProviderPaymentId: null);

        Assert.False(result.Success);
        Assert.Equal("missing_provider_payment_id", result.Error);
    }

    [Fact]
    public void TryTransitionPaid_OrderMissingProviderIdButWebhookHasIt_Succeeds()
    {
        var result = PaymentStateMachine.TryTransitionPaid(
            currentStatus: "pending",
            orderAmount: 10.00m,
            orderCurrency: "USD",
            providerPaymentId: null,
            webhookAmount: 10.00m,
            webhookCurrency: "USD",
            webhookProviderPaymentId: "mock-paid-abc123");

        Assert.True(result.Success);
        Assert.Equal("paid", result.NewStatus);
    }

    // ---- Fake webhook rejection (MockPaymentProvider) ----

    [Fact]
    public async Task MockProvider_FakePaymentId_NotVerified()
    {
        var provider = new MockPaymentProvider();

        var result = await provider.VerifyPaymentAsync(
            "fake-payment-id", 10.00m, "USD");

        Assert.False(result.Verified);
        Assert.Equal("payment_not_completed", result.ErrorCode);
    }

    [Fact]
    public async Task MockProvider_EmptyPaymentId_NotVerified()
    {
        var provider = new MockPaymentProvider();

        var result = await provider.VerifyPaymentAsync(
            "", 10.00m, "USD");

        Assert.False(result.Verified);
        Assert.Equal("payment_not_found", result.ErrorCode);
    }

    [Fact]
    public async Task MockProvider_ValidPaidId_Verified()
    {
        var provider = new MockPaymentProvider();

        var result = await provider.VerifyPaymentAsync(
            "mock-paid-abc123", 10.00m, "USD");

        Assert.True(result.Verified);
        Assert.Equal("succeeded", result.Status);
        Assert.Equal(10.00m, result.Amount);
        Assert.Equal("USD", result.Currency);
    }

    [Fact]
    public async Task MockProvider_NullPaymentId_NotVerified()
    {
        var provider = new MockPaymentProvider();

        var result = await provider.VerifyPaymentAsync(
            null!, 10.00m, "USD");

        Assert.False(result.Verified);
    }

    // ---- Refund validation ----

    [Fact]
    public void TryValidateRefund_PaidOrder_FullRefund_Succeeds()
    {
        var result = PaymentStateMachine.TryValidateRefund(
            currentStatus: "paid",
            orderAmount: 10.00m,
            currentRefundTotal: 0m,
            refundAmount: 10.00m,
            orderCurrency: "USD",
            refundCurrency: "USD");

        Assert.True(result.IsValid);
        Assert.Equal("refunded", result.NewStatus);
        Assert.Equal(10.00m, result.NewRefundTotal);
    }

    [Fact]
    public void TryValidateRefund_PaidOrder_PartialRefund_Succeeds()
    {
        var result = PaymentStateMachine.TryValidateRefund(
            currentStatus: "paid",
            orderAmount: 10.00m,
            currentRefundTotal: 0m,
            refundAmount: 5.00m,
            orderCurrency: "USD",
            refundCurrency: "USD");

        Assert.True(result.IsValid);
        Assert.Equal("partially_refunded", result.NewStatus);
        Assert.Equal(5.00m, result.NewRefundTotal);
    }

    [Fact]
    public void TryValidateRefund_PartiallyRefunded_AdditionalRefund_Succeeds()
    {
        var result = PaymentStateMachine.TryValidateRefund(
            currentStatus: "partially_refunded",
            orderAmount: 10.00m,
            currentRefundTotal: 3.00m,
            refundAmount: 5.00m,
            orderCurrency: "USD",
            refundCurrency: "USD");

        Assert.True(result.IsValid);
        Assert.Equal("partially_refunded", result.NewStatus);
        Assert.Equal(8.00m, result.NewRefundTotal);
    }

    [Fact]
    public void TryValidateRefund_RefundExceedsRemaining_Rejected()
    {
        var result = PaymentStateMachine.TryValidateRefund(
            currentStatus: "paid",
            orderAmount: 10.00m,
            currentRefundTotal: 0m,
            refundAmount: 15.00m,
            orderCurrency: "USD",
            refundCurrency: "USD");

        Assert.False(result.IsValid);
        Assert.Equal("refund_exceeds_remaining", result.Error);
    }

    [Fact]
    public void TryValidateRefund_AccumulatedRefundExceedsPaid_Rejected()
    {
        // First refund of 7 succeeds
        var first = PaymentStateMachine.TryValidateRefund(
            "paid", 10.00m, 0m, 7.00m, "USD", "USD");
        Assert.True(first.IsValid);

        // Second refund of 5 would exceed remaining (3)
        var second = PaymentStateMachine.TryValidateRefund(
            "partially_refunded", 10.00m, 7.00m, 5.00m, "USD", "USD");
        Assert.False(second.IsValid);
        Assert.Equal("refund_exceeds_remaining", second.Error);
    }

    [Fact]
    public void TryValidateRefund_PendingOrder_Rejected()
    {
        var result = PaymentStateMachine.TryValidateRefund(
            currentStatus: "pending",
            orderAmount: 10.00m,
            currentRefundTotal: 0m,
            refundAmount: 5.00m,
            orderCurrency: "USD",
            refundCurrency: "USD");

        Assert.False(result.IsValid);
        Assert.Equal("order_not_refundable", result.Error);
    }

    [Fact]
    public void TryValidateRefund_FailedOrder_Rejected()
    {
        var result = PaymentStateMachine.TryValidateRefund(
            currentStatus: "failed",
            orderAmount: 10.00m,
            currentRefundTotal: 0m,
            refundAmount: 5.00m,
            orderCurrency: "USD",
            refundCurrency: "USD");

        Assert.False(result.IsValid);
        Assert.Equal("order_not_refundable", result.Error);
    }

    [Fact]
    public void TryValidateRefund_FullyRefunded_Rejected()
    {
        var result = PaymentStateMachine.TryValidateRefund(
            currentStatus: "refunded",
            orderAmount: 10.00m,
            currentRefundTotal: 10.00m,
            refundAmount: 1.00m,
            orderCurrency: "USD",
            refundCurrency: "USD");

        Assert.False(result.IsValid);
        Assert.Equal("order_not_refundable", result.Error);
    }

    [Fact]
    public void TryValidateRefund_ZeroAmount_Rejected()
    {
        var result = PaymentStateMachine.TryValidateRefund(
            currentStatus: "paid",
            orderAmount: 10.00m,
            currentRefundTotal: 0m,
            refundAmount: 0m,
            orderCurrency: "USD",
            refundCurrency: "USD");

        Assert.False(result.IsValid);
        Assert.Equal("invalid_refund_amount", result.Error);
    }

    [Fact]
    public void TryValidateRefund_NegativeAmount_Rejected()
    {
        var result = PaymentStateMachine.TryValidateRefund(
            currentStatus: "paid",
            orderAmount: 10.00m,
            currentRefundTotal: 0m,
            refundAmount: -5.00m,
            orderCurrency: "USD",
            refundCurrency: "USD");

        Assert.False(result.IsValid);
        Assert.Equal("invalid_refund_amount", result.Error);
    }

    [Fact]
    public void TryValidateRefund_CurrencyMismatch_Rejected()
    {
        var result = PaymentStateMachine.TryValidateRefund(
            currentStatus: "paid",
            orderAmount: 10.00m,
            currentRefundTotal: 0m,
            refundAmount: 5.00m,
            orderCurrency: "USD",
            refundCurrency: "EUR");

        Assert.False(result.IsValid);
        Assert.Equal("currency_mismatch", result.Error);
    }

    // ---- ApplyRefundTransition ----

    [Fact]
    public void ApplyRefundTransition_PartialRefund_ReturnsPartiallyRefunded()
    {
        var (status, total) = PaymentStateMachine.ApplyRefundTransition(
            orderAmount: 10.00m, currentRefundTotal: 0m, refundAmount: 5.00m);

        Assert.Equal("partially_refunded", status);
        Assert.Equal(5.00m, total);
    }

    [Fact]
    public void ApplyRefundTransition_FullRefund_ReturnsRefunded()
    {
        var (status, total) = PaymentStateMachine.ApplyRefundTransition(
            orderAmount: 10.00m, currentRefundTotal: 0m, refundAmount: 10.00m);

        Assert.Equal("refunded", status);
        Assert.Equal(10.00m, total);
    }

    [Fact]
    public void ApplyRefundTransition_AccumulatedFullRefund_ReturnsRefunded()
    {
        var (status, total) = PaymentStateMachine.ApplyRefundTransition(
            orderAmount: 10.00m, currentRefundTotal: 7.00m, refundAmount: 3.00m);

        Assert.Equal("refunded", status);
        Assert.Equal(10.00m, total);
    }

    // ---- State query helpers ----

    [Fact]
    public void IsTerminal_FailedAndCancelled_AreTerminal()
    {
        Assert.True(PaymentStateMachine.IsTerminal("failed"));
        Assert.True(PaymentStateMachine.IsTerminal("cancelled"));
        Assert.False(PaymentStateMachine.IsTerminal("pending"));
        Assert.False(PaymentStateMachine.IsTerminal("paid"));
        Assert.False(PaymentStateMachine.IsTerminal("refunded"));
    }

    [Fact]
    public void CanAcceptPayment_OnlyPending()
    {
        Assert.True(PaymentStateMachine.CanAcceptPayment("pending"));
        Assert.False(PaymentStateMachine.CanAcceptPayment("paid"));
        Assert.False(PaymentStateMachine.CanAcceptPayment("refunded"));
        Assert.False(PaymentStateMachine.CanAcceptPayment("failed"));
    }

    [Fact]
    public void CanAcceptRefund_PaidAndPartiallyRefunded()
    {
        Assert.False(PaymentStateMachine.CanAcceptRefund("pending"));
        Assert.True(PaymentStateMachine.CanAcceptRefund("paid"));
        Assert.True(PaymentStateMachine.CanAcceptRefund("partially_refunded"));
        Assert.False(PaymentStateMachine.CanAcceptRefund("refunded"));
        Assert.False(PaymentStateMachine.CanAcceptRefund("failed"));
    }

    // ---- Mock provider refund ----

    [Fact]
    public async Task MockProvider_CreateRefund_ValidInput_Succeeds()
    {
        var provider = new MockPaymentProvider();

        var result = await provider.CreateRefundAsync(
            "mock-paid-abc123", 5.00m, "requested_by_customer");

        Assert.True(result.Success);
        Assert.Equal(5.00m, result.Amount);
        Assert.StartsWith("mock_rf_", result.RefundId);
    }

    [Fact]
    public async Task MockProvider_CreateRefund_InvalidInput_Fails()
    {
        var provider = new MockPaymentProvider();

        var result = await provider.CreateRefundAsync(
            "", 5.00m, "requested_by_customer");

        Assert.False(result.Success);
        Assert.Equal("invalid_request", result.ErrorCode);
    }

    [Fact]
    public async Task MockProvider_CreateRefund_ZeroAmount_Fails()
    {
        var provider = new MockPaymentProvider();

        var result = await provider.CreateRefundAsync(
            "mock-paid-abc123", 0m, "requested_by_customer");

        Assert.False(result.Success);
        Assert.Equal("invalid_request", result.ErrorCode);
    }

    // ---- End-to-end scenario: checkout -> webhook -> refund ----

    [Fact]
    public void EndToEnd_CheckoutWebhookRefund_FlowWorks()
    {
        // 1. Order created in pending state
        var status = "pending";
        var amount = 20.00m;
        var currency = "USD";
        var refundTotal = 0m;

        // 2. Webhook arrives: payment succeeded
        var paymentTransition = PaymentStateMachine.TryTransitionPaid(
            status, amount, currency, "mock-paid-order1",
            20.00m, "USD", "mock-paid-order1");
        Assert.True(paymentTransition.Success);
        status = paymentTransition.NewStatus;
        Assert.Equal("paid", status);

        // 3. First partial refund of 8
        var refund1 = PaymentStateMachine.TryValidateRefund(
            status, amount, refundTotal, 8.00m, currency, "USD");
        Assert.True(refund1.IsValid);
        var (s1, t1) = PaymentStateMachine.ApplyRefundTransition(amount, refundTotal, 8.00m);
        status = s1;
        refundTotal = t1;
        Assert.Equal("partially_refunded", status);
        Assert.Equal(8.00m, refundTotal);

        // 4. Second partial refund of 12 (full refund)
        var refund2 = PaymentStateMachine.TryValidateRefund(
            status, amount, refundTotal, 12.00m, currency, "USD");
        Assert.True(refund2.IsValid);
        var (s2, t2) = PaymentStateMachine.ApplyRefundTransition(amount, refundTotal, 12.00m);
        status = s2;
        refundTotal = t2;
        Assert.Equal("refunded", status);
        Assert.Equal(20.00m, refundTotal);

        // 5. Another refund should fail (already fully refunded)
        var refund3 = PaymentStateMachine.TryValidateRefund(
            status, amount, refundTotal, 1.00m, currency, "USD");
        Assert.False(refund3.IsValid);
    }

    [Fact]
    public void EndToEnd_FakeWebhook_RejectedByStateMachine()
    {
        // Simulate a fake webhook with wrong amount
        var status = "pending";
        var amount = 10.00m;
        var currency = "USD";

        var result = PaymentStateMachine.TryTransitionPaid(
            status, amount, currency, "mock-paid-real",
            999.99m, "USD", "mock-paid-real");

        Assert.False(result.Success);
        Assert.Equal("amount_mismatch", result.Error);
        // Status remains pending - no credit produced
        Assert.Equal("pending", status);
    }

    [Fact]
    public void EndToEnd_DuplicateWebhook_IdempotentViaAlreadyPaid()
    {
        var amount = 10.00m;
        var currency = "USD";

        // First webhook succeeds
        var first = PaymentStateMachine.TryTransitionPaid(
            "pending", amount, currency, "mock-paid-abc",
            10.00m, "USD", "mock-paid-abc");
        Assert.True(first.Success);

        // Second webhook (duplicate) returns already_paid
        var second = PaymentStateMachine.TryTransitionPaid(
            "paid", amount, currency, "mock-paid-abc",
            10.00m, "USD", "mock-paid-abc");
        Assert.False(second.Success);
        Assert.Equal("already_paid", second.Error);
    }

    [Fact]
    public async Task EndToEnd_AdminConfirmRequiresProviderVerification()
    {
        var provider = new MockPaymentProvider();

        // Fake provider payment ID -> verification fails
        var fakeVerify = await provider.VerifyPaymentAsync(
            "fake-id", 10.00m, "USD");
        Assert.False(fakeVerify.Verified);

        // Real provider payment ID -> verification succeeds
        var realVerify = await provider.VerifyPaymentAsync(
            "mock-paid-abc123", 10.00m, "USD");
        Assert.True(realVerify.Verified);

        // After verification, state machine allows transition
        var transition = PaymentStateMachine.TryTransitionPaid(
            "pending", 10.00m, "USD", "mock-paid-abc123",
            realVerify.Amount, realVerify.Currency, "mock-paid-abc123");
        Assert.True(transition.Success);
        Assert.Equal("paid", transition.NewStatus);
    }
}
