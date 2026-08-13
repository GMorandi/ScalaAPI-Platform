namespace ScalaAPI.Grains.Interfaces;

/// <summary>
/// Grain-facing interface for provider quota operations. The implementation
/// lives in the Data layer and is registered via DI.
/// </summary>
public interface IProviderQuotaService
{
    /// <summary>
    /// Checks whether the account can accept a request of the given estimated cost.
    /// Returns a decision indicating whether to proceed, skip, or wait.
    /// </summary>
    Task<QuotaCheckResult> CheckAsync(long accountId, decimal estimatedCost,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically reserves estimated cost against the account's remaining quota.
    /// Must be called before dispatching a request.
    /// </summary>
    Task<QuotaReservationResult> ReserveAsync(long accountId, decimal estimatedCost,
        CancellationToken ct = default);

    /// <summary>
    /// Settles a previously reserved cost after the request completes, is rejected,
    /// or has an unknown outcome.
    /// </summary>
    Task<QuotaSettleResult> SettleAsync(long accountId, string leaseId,
        decimal actualCost, QuotaSettlementOutcomeKind outcome,
        CancellationToken ct = default);

    /// <summary>
    /// Records a backoff/cooldown period for the account (e.g. after a 429).
    /// </summary>
    Task RecordBackoffAsync(long accountId, TimeSpan backoff,
        CancellationToken ct = default);
}

[GenerateSerializer]
public record QuotaCheckResult(
    QuotaCheckStatus Status,
    string? RejectionReason,
    string? Tier,
    decimal? RemainingQuota,
    DateTime? CooldownUntil);

[GenerateSerializer]
public enum QuotaCheckStatus
{
    Ok,
    Expired,
    InsufficientQuota,
    Cooldown,
    UnknownTier,
    NoSnapshot,
}

[GenerateSerializer]
public record QuotaReservationResult(
    bool Reserved,
    string? LeaseId,
    string? RejectionReason,
    decimal? RemainingAfter);

[GenerateSerializer]
public record QuotaSettleResult(
    bool Applied,
    decimal? RemainingAfter);

[GenerateSerializer]
public enum QuotaSettlementOutcomeKind
{
    Success,
    Rejected,
    Unknown,
}
