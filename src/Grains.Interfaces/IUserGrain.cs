namespace Sub2Api.Grains.Interfaces;

public record HoldHandle(string Id, decimal Amount);

public record UserUpsert(
    string Role, double Balance, int Concurrency,
    int RpmLimit, long[] AllowedGroups);

public interface IUserGrain : IGrainWithIntegerKey
{
    Task<UserProjection> GetAuthProjection();
    Task<SlotResult> TryAcquireSlot(string requestId);
    Task ReleaseSlot(string requestId);
    Task<HoldHandle?> ReserveBalance(decimal amount);
    Task CommitUsage(HoldHandle handle, decimal actual);
    Task ReleaseHold(HoldHandle handle);
    Task<bool> CheckBalance(decimal required);

    Task Create(UserUpsert input);
    Task Update(UserUpsert input);
    Task SetStatus(string status);
    Task AdjustBalance(double delta);
    Task Delete();
}
