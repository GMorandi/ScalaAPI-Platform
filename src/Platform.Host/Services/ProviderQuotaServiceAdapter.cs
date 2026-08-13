using ScalaAPI.Data.ProviderQuota;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Host.Services;

/// <summary>
/// Adapts <see cref="IProviderQuotaStore"/> to the grain-facing
/// <see cref="IProviderQuotaService"/> interface.
/// </summary>
public sealed class ProviderQuotaServiceAdapter(IProviderQuotaStore store)
    : IProviderQuotaService
{
    public async Task<QuotaCheckResult> CheckAsync(long accountId, decimal estimatedCost,
        CancellationToken ct = default)
    {
        var snapshot = await store.GetAsync(accountId, ct);
        if (snapshot is null)
            return new QuotaCheckResult(QuotaCheckStatus.NoSnapshot, null, null, null, null);

        if (snapshot.CooldownUntil.HasValue && snapshot.CooldownUntil.Value > DateTime.UtcNow)
            return new QuotaCheckResult(QuotaCheckStatus.Cooldown, "Account in cooldown",
                snapshot.Tier, snapshot.RemainingQuota, snapshot.CooldownUntil);

        if (snapshot.ExpiresAt.HasValue && snapshot.ExpiresAt.Value <= DateTime.UtcNow)
            return new QuotaCheckResult(QuotaCheckStatus.Expired, "Quota snapshot expired",
                snapshot.Tier, snapshot.RemainingQuota, null);

        if (string.Equals(snapshot.Tier, "unknown", StringComparison.OrdinalIgnoreCase)
            && !snapshot.RemainingQuota.HasValue)
            return new QuotaCheckResult(QuotaCheckStatus.UnknownTier, null,
                snapshot.Tier, null, null);

        if (string.Equals(snapshot.Tier, "free", StringComparison.OrdinalIgnoreCase))
            return new QuotaCheckResult(QuotaCheckStatus.Ok, null,
                snapshot.Tier, snapshot.RemainingQuota, null);

        if (!snapshot.RemainingQuota.HasValue || snapshot.RemainingQuota.Value < estimatedCost)
            return new QuotaCheckResult(QuotaCheckStatus.InsufficientQuota,
                "Insufficient provider quota", snapshot.Tier,
                snapshot.RemainingQuota, null);

        return new QuotaCheckResult(QuotaCheckStatus.Ok, null,
            snapshot.Tier, snapshot.RemainingQuota, null);
    }

    public async Task<QuotaReservationResult> ReserveAsync(long accountId, decimal estimatedCost,
        CancellationToken ct = default)
    {
        var reservation = await store.TryReserveAsync(accountId, estimatedCost, ct);
        return reservation.Status switch
        {
            QuotaReservationStatus.Reserved => new QuotaReservationResult(
                true, reservation.LeaseId, null, reservation.RemainingAfter),
            QuotaReservationStatus.InsufficientQuota => new QuotaReservationResult(
                false, null, "Insufficient provider quota", reservation.RemainingAfter),
            QuotaReservationStatus.Expired => new QuotaReservationResult(
                false, null, "Quota snapshot expired", null),
            QuotaReservationStatus.Cooldown => new QuotaReservationResult(
                false, null, "Account in cooldown", null),
            QuotaReservationStatus.UnknownTier => new QuotaReservationResult(
                true, null, null, null),
            QuotaReservationStatus.NoSnapshot => new QuotaReservationResult(
                true, null, null, null),
            _ => new QuotaReservationResult(false, null, "Unknown quota state", null),
        };
    }

    public async Task<QuotaSettleResult> SettleAsync(long accountId, string leaseId,
        decimal actualCost, QuotaSettlementOutcomeKind outcome, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(leaseId))
            return new QuotaSettleResult(true, null);

        var dataOutcome = outcome switch
        {
            QuotaSettlementOutcomeKind.Success => Data.ProviderQuota.QuotaSettlementOutcome.Success,
            QuotaSettlementOutcomeKind.Rejected => Data.ProviderQuota.QuotaSettlementOutcome.Rejected,
            QuotaSettlementOutcomeKind.Unknown => Data.ProviderQuota.QuotaSettlementOutcome.Unknown,
            _ => Data.ProviderQuota.QuotaSettlementOutcome.Unknown,
        };

        var result = await store.SettleAsync(accountId, leaseId, actualCost, dataOutcome, ct);
        return new QuotaSettleResult(result.Applied, result.RemainingAfter);
    }

    public async Task RecordBackoffAsync(long accountId, TimeSpan backoff,
        CancellationToken ct = default)
    {
        await store.RecordBackoffAsync(accountId, backoff, ct);
    }
}
