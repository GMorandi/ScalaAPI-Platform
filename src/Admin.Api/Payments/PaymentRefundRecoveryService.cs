using ScalaAPI.Admin.Data;

namespace ScalaAPI.Admin.Payments;

public sealed class PaymentRefundRecoveryService(
    PaymentRefundStore refunds,
    PaymentProviderRouter providers,
    AccountingProjectionService projection,
    ILogger<PaymentRefundRecoveryService> logger) : BackgroundService
{
    private const int BatchSize = 20;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private readonly string workerId = $"refund-recovery:{Environment.MachineName}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var claimed = await refunds.ClaimRecoverableAsync(workerId, BatchSize, stoppingToken);
                foreach (var row in claimed)
                    await RecoverAsync(row, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Payment refund recovery iteration failed");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RecoverAsync(
        PaymentRefundRecoveryCommand row, CancellationToken ct)
    {
        PaymentRefundResult providerResult;
        try
        {
            providerResult = await providers.RefundAsync(row.Provider,
                new PaymentRefundRequest(row.PaymentOrderId, row.Amount, row.Currency,
                    row.ProviderOrderId, row.ProviderPaymentId, row.Reason,
                    row.IdempotencyKey), ct);
        }
        catch (PaymentProviderException ex)
        {
            var retryable = ex.Code is "payment_provider_timeout"
                or "payment_provider_unavailable";
            await refunds.FinalizeAsync(row.RefundId, row.ActorUserId, "failed", null,
                ex.Code, retryable, ct);
            logger.LogWarning("Refund {RefundId} recovery attempt {Attempt} returned {Code}",
                row.RefundId, row.Attempts, ex.Code);
            return;
        }
        catch (Exception ex)
        {
            await refunds.FinalizeAsync(row.RefundId, row.ActorUserId, "failed", null,
                "payment_refund_recovery_failed", true, ct);
            logger.LogWarning(ex, "Refund {RefundId} recovery attempt {Attempt} failed", row.RefundId, row.Attempts);
            return;
        }

        var finalized = await refunds.FinalizeAsync(row.RefundId, row.ActorUserId,
            providerResult.Status, providerResult.ProviderRefundId, null, false, ct);
        if (finalized.Status == PaymentRefundFinalizeStatus.Succeeded)
        {
            try
            {
                await projection.ApplyAsync(new ScalaAPI.Data.Accounting.AccountingSnapshot(
                    finalized.UserId, finalized.LedgerVersion, finalized.BalanceAfter), ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Refund {RefundId} projection remains retryable", row.RefundId);
            }
        }
        logger.LogInformation("Refund {RefundId} recovery attempt {Attempt} completed as {Status}",
            row.RefundId, row.Attempts, finalized.Status);
    }
}
