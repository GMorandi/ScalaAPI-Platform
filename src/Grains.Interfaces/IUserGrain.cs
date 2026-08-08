namespace ScalaAPI.Grains.Interfaces;

[GenerateSerializer]
public record HoldHandle(string Id, decimal Amount);

[GenerateSerializer]
public record UserCreate(
    string Role, decimal InitialBalance, int Concurrency,
    int RpmLimit, long[] AllowedGroups);

[GenerateSerializer]
public record UserConfiguration(
    string Role, int Concurrency,
    int RpmLimit, long[] AllowedGroups);

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

    Task Create(UserCreate input);
    Task Update(UserConfiguration input);
    Task SetStatus(string status);
    Task ApplyBalanceEffect(string effectId, decimal delta);
    Task ApplyBalanceSnapshot(string effectId, decimal balance);
    Task Delete();
}
