namespace Sub2Api.Grains.Interfaces;

[GenerateSerializer]
public record HoldHandle(string Id, decimal Amount);

[GenerateSerializer]
public record UserUpsert(
    string Role, double Balance, int Concurrency,
    int RpmLimit, long[] AllowedGroups);

[GenerateSerializer]
public record UserMetadataUpsert(
    string Role, double Balance, int Concurrency, int RpmLimit);

public interface IUserGrain : IGrainWithIntegerKey
{
    Task<UserProjection> GetAuthProjection();
    Task<SlotResult> TryAcquireSlot(string requestId);
    Task<SlotResult> TryAcquireSlot(string leaseToken, DateTime expiresAt);
    Task ReleaseSlot(string requestId);
    Task<HoldHandle?> ReserveBalance(decimal amount);
    Task CommitUsage(HoldHandle handle, decimal actual);
    Task ReleaseHold(HoldHandle handle);
    Task CompleteLease(string leaseToken, string requestId, HoldHandle? handle, decimal actual);
    Task AbortLease(string leaseToken, string requestId, HoldHandle? handle);
    Task<bool> CheckBalance(decimal required);
    Task<bool> CheckAndRecordRpm(int limit);

    Task Create(UserUpsert input);
    Task Update(UserUpsert input);
    Task UpsertMetadata(UserMetadataUpsert input);
    Task AddAllowedGroup(long groupId);
    Task RemoveAllowedGroup(long groupId);
    Task SetStatus(string status);
    Task AdjustBalance(double delta);
    Task Delete();
}
