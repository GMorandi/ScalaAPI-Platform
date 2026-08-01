namespace Sub2Api.Grains.Interfaces;

public record HoldHandle(string Id, decimal Amount);

public interface IUserGrain : IGrainWithIntegerKey
{
    Task<UserProjection> GetAuthProjection();
    Task<SlotResult> TryAcquireSlot(string requestId);
    Task ReleaseSlot(string requestId);
    Task<HoldHandle?> ReserveBalance(decimal amount);
    Task CommitUsage(HoldHandle handle, decimal actual);
    Task ReleaseHold(HoldHandle handle);
    Task<bool> CheckBalance(decimal required);
}
