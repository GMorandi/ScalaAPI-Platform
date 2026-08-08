namespace ScalaAPI.Grains.Interfaces;

[GenerateSerializer]
public record UserCreate(
    string Role, int Concurrency,
    int RpmLimit, long[] AllowedGroups);

[GenerateSerializer]
public record UserConfiguration(
    string Role, int Concurrency,
    int RpmLimit, long[] AllowedGroups);

[GenerateSerializer]
public record BalanceProjection(long Version, decimal Balance);

public interface IUserGrain : IGrainWithIntegerKey
{
    Task<UserProjection> GetAuthProjection();
    Task<SlotResult> TryAcquireSlot(string requestId);
    Task<SlotResult> TryAcquireSlot(string leaseToken, DateTime expiresAt);
    Task ReleaseSlot(string requestId);
    Task FinalizeLease(string leaseToken, string requestId);
    Task<bool> CheckAndRecordRpm(int limit);

    Task<BalanceProjection> GetBalanceProjection();
    Task Create(UserCreate input);
    Task Update(UserConfiguration input);
    Task SetStatus(string status);
    Task ApplyBalanceSnapshot(long version, decimal balance);
    Task Delete();
}
