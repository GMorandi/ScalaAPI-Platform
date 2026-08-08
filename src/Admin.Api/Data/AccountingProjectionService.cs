using Orleans;
using ScalaAPI.Data.Accounting;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Admin.Data;

public sealed class AccountingProjectionService(
    AccountingStore accounting,
    IClusterClient cluster)
{
    public async Task ApplyAsync(
        AccountingSnapshot snapshot,
        CancellationToken ct = default)
    {
        await cluster.GetGrain<IUserGrain>(snapshot.UserId)
            .ApplyBalanceSnapshot(snapshot.Version, snapshot.Balance);
        await accounting.MarkProjectionAppliedAsync(snapshot.UserId, snapshot.Version, ct);
    }
}
