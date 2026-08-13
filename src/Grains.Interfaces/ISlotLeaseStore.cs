namespace ScalaAPI.Grains.Interfaces;

public interface ISlotLeaseStore
{
    Task<bool> TryAcquireAccountSlot(long accountId, string leaseToken, string requestId,
        string siloId, DateTime expiresAt, int maxConcurrency, CancellationToken ct = default);

    Task ReleaseAccountSlot(string leaseToken, string siloId, CancellationToken ct = default);

    Task<int> ReclaimExpiredAccountSlots(long accountId, string siloId, CancellationToken ct = default);

    Task<int> GetAccountActiveCount(long accountId, CancellationToken ct = default);

    Task<bool> TryAcquireUserSlot(long userId, string leaseToken, string requestId,
        string siloId, DateTime expiresAt, int maxConcurrency, CancellationToken ct = default);

    Task ReleaseUserSlot(string leaseToken, string siloId, CancellationToken ct = default);

    Task<int> GetUserActiveCount(long userId, CancellationToken ct = default);

    Task<AccountHealthState?> GetAccountHealthAsync(long accountId, CancellationToken ct = default);

    Task UpdateAccountHealthAsync(long accountId, Action<AccountHealthState> mutate,
        CancellationToken ct = default);
}

[GenerateSerializer]
public sealed class AccountHealthState
{
    [Id(0)] public long AccountId { get; set; }
    [Id(1)] public int ConsecutiveErrors { get; set; }
    [Id(2)] public DateTime? LastSuccessAt { get; set; }
    [Id(3)] public DateTime? RateLimitResetAt { get; set; }
    [Id(4)] public DateTime? OverloadUntil { get; set; }
    [Id(5)] public DateTime? TempUnschedulableUntil { get; set; }
    [Id(6)] public bool DisabledPermanently { get; set; }
    [Id(7)] public string? DisableReason { get; set; }
}
