using Orleans;
using ScalaAPI.Data.Accounting;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Admin.Data;

public sealed class OrleansAccountingProjectionRepairer(IClusterClient cluster)
    : IAccountingProjectionRepairer
{
    public async Task<ProjectionRepairResult> RepairAsync(
        AccountingSnapshot expected,
        CancellationToken ct = default)
    {
        try
        {
            var grain = cluster.GetGrain<IUserGrain>(expected.UserId);
            var before = await grain.GetBalanceProjection().WaitAsync(ct);
            if (before.Version == expected.Version && before.Balance == expected.Balance)
                return new(ProjectionRepairState.Consistent, before.Version, before.Balance);

            await grain.ApplyBalanceSnapshot(expected.Version, expected.Balance).WaitAsync(ct);
            var after = await grain.GetBalanceProjection().WaitAsync(ct);
            return after.Version == expected.Version && after.Balance == expected.Balance
                ? new(ProjectionRepairState.Repaired, after.Version, after.Balance)
                : new(ProjectionRepairState.Failed, after.Version, after.Balance,
                    "Projection did not converge to the authoritative account snapshot");
        }
        catch (Exception ex)
        {
            return new(ProjectionRepairState.Failed, -1, 0m,
                ex.Message.Length > 500 ? ex.Message[..500] : ex.Message);
        }
    }
}
