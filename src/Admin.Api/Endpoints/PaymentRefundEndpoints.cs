using System.Security.Claims;
using ScalaAPI.Admin.Auth;
using ScalaAPI.Admin.Data;
using ScalaAPI.Admin.Models;
using ScalaAPI.Admin.Payments;

namespace ScalaAPI.Admin.Endpoints;

public sealed record PaymentRefundCommandRequest(decimal Amount, string? Currency, string? Reason);

public static class PaymentRefundEndpoints
{
    public static void MapPaymentRefundEndpoints(this WebApplication app)
    {
        app.MapPost("/admin/payments/{id:long}/refund", HandleAsync)
            .RequireAuthorization("AdminOnly");
    }

    private static async Task<IResult> HandleAsync(
        long id,
        ClaimsPrincipal principal,
        HttpRequest request,
        PaymentRefundCommandRequest input,
        PaymentRefundStore refunds,
        PaymentProviderRouter providers,
        AccountingProjectionService projection,
        CancellationToken ct)
    {
        if (!AuthClaims.TryGetUserId(principal, out var actorId))
            return Results.Unauthorized();
        var idempotencyKey = request.Headers["Idempotency-Key"].FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
            return Results.BadRequest(new { error = "Idempotency-Key is required" });
        var reason = (input.Reason ?? "").Trim();
        if (reason.Length > 500)
            return Results.BadRequest(new { error = "Refund reason is too long" });
        var currency = (input.Currency ?? "USD").Trim().ToUpperInvariant();
        if (input.Amount <= 0m || decimal.Round(input.Amount, 2) != input.Amount
            || currency.Length != 3 || currency.Any(ch => ch is < 'A' or > 'Z'))
            return Results.BadRequest(new { error = "Invalid refund amount or currency" });

        var prepared = await refunds.PrepareAsync(id, actorId, idempotencyKey,
            input.Amount, currency, reason, ct);
        if (prepared.Status == PaymentRefundPrepareStatus.NotFound)
            return Results.NotFound(new { error = "Payment order not found" });
        if (prepared.Status == PaymentRefundPrepareStatus.Conflict)
            return Results.Conflict(new { error = "Idempotency-Key was already used with different refund data" });
        if (prepared.Status == PaymentRefundPrepareStatus.InvalidState)
            return Results.Conflict(new { error = "Refund amount exceeds the remaining paid amount or order is not refundable" });
        if (prepared.Status == PaymentRefundPrepareStatus.InProgress)
            return Results.Accepted($"/admin/payments/{id}/refund", new
            {
                id = prepared.RefundId, payment_order_id = prepared.PaymentOrderId,
                status = prepared.RefundStatus, retryable = true,
            });
        if (prepared.Status == PaymentRefundPrepareStatus.Replay)
            return Results.Ok(new
            {
                id = prepared.RefundId, payment_order_id = prepared.PaymentOrderId,
                status = prepared.RefundStatus, provider_refund_id = prepared.ProviderRefundId,
                duplicate = true,
            });

        PaymentRefundResult providerResult;
        try
        {
            providerResult = await providers.RefundAsync(prepared.Provider,
                new ScalaAPI.Admin.Payments.PaymentRefundRequest(prepared.PaymentOrderId, prepared.Amount,
                    prepared.Currency, prepared.ProviderOrderId, prepared.ProviderPaymentId,
                    prepared.Reason, prepared.IdempotencyKey), ct);
        }
        catch (PaymentProviderException ex)
        {
            var retryable = ex.Code is "payment_provider_timeout"
                or "payment_provider_unavailable";
            var failed = await refunds.FinalizeAsync(prepared.RefundId, actorId,
                "failed", null, ex.Code, retryable, ct);
            return retryable
                ? Results.Json(new { id = prepared.RefundId, status = "reconciliation_needed", retryable = true },
                    statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.BadRequest(new { id = prepared.RefundId, status = failed.Status.ToString().ToLowerInvariant(), error = ex.Code });
        }

        var finalized = await refunds.FinalizeAsync(prepared.RefundId, actorId,
            providerResult.Status, providerResult.ProviderRefundId, null, false, ct);
        if (finalized.Status == PaymentRefundFinalizeStatus.Succeeded)
        {
            try
            {
                await projection.ApplyAsync(new ScalaAPI.Data.Accounting.AccountingSnapshot(
                    finalized.UserId, finalized.LedgerVersion, finalized.BalanceAfter), ct);
            }
            catch
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            return Results.Ok(new
            {
                id = finalized.RefundId, payment_order_id = finalized.PaymentOrderId,
                status = "succeeded", provider_refund_id = finalized.ProviderRefundId,
                ledger_version = finalized.LedgerVersion,
                balance = finalized.BalanceAfter,
            });
        }
        if (finalized.Status == PaymentRefundFinalizeStatus.Pending)
            return Results.Accepted($"/admin/payments/{id}/refund", new
            {
                id = finalized.RefundId, payment_order_id = finalized.PaymentOrderId,
                status = "pending", provider_refund_id = finalized.ProviderRefundId,
            });
        return Results.BadRequest(new
        {
            id = finalized.RefundId, payment_order_id = finalized.PaymentOrderId,
            status = finalized.Status.ToString().ToLowerInvariant(),
            error = finalized.ErrorCode ?? "payment_refund_failed",
        });
    }
}
