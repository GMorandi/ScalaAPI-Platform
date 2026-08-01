namespace Sub2Api.Grains.Interfaces;

public record SelectRequest(
    string Model, string SessionHash, string RequestId,
    string? MetadataUserId, long[] ExcludedAccountIds, string Endpoint);

public record SelectionResult(
    SelectionOutcome Outcome, long? AccountId, string? LeaseToken,
    int? WaitTimeoutMs, string? RejectReason);

public enum SelectionOutcome { Ok, Wait, Rejected }

public interface ISchedulerGrain : IGrainWithIntegerKey
{
    Task<SelectionResult> Select(SelectRequest req);
    Task<long?> GetStickyAccount(string sessionHash);
    Task BindSticky(string sessionHash, long accountId, TimeSpan ttl);
    Task ClearSticky(string sessionHash);
}
