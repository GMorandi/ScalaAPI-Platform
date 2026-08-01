namespace Sub2Api.Grains.Interfaces;

[GenerateSerializer]
public record SelectRequest(
    string Model, string SessionHash, string RequestId,
    string? MetadataUserId, long[] ExcludedAccountIds, string Endpoint);

[GenerateSerializer]
public record SelectionResult(
    SelectionOutcome Outcome, long? AccountId, string? LeaseToken,
    int? WaitTimeoutMs, string? RejectReason);

[GenerateSerializer]
public enum SelectionOutcome { Ok, Wait, Rejected }

public interface ISchedulerGrain : IGrainWithIntegerKey
{
    Task<SelectionResult> Select(SelectRequest req);
    Task<long?> GetStickyAccount(string sessionHash);
    Task BindSticky(string sessionHash, long accountId, TimeSpan ttl);
    Task ClearSticky(string sessionHash);
}
