using Orleans;
using ScalaAPI.Data.Accounting;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Host.Services;

public sealed class AccountingProjectionHostedService(
    AccountingStore accounting,
    IClusterClient cluster,
    ILogger<AccountingProjectionHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerId = $"accounting-projection-{Environment.ProcessId}";
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var batch = await accounting.ClaimProjectionBatchAsync(
                    workerId, 100, stoppingToken);
                if (batch.Count == 0)
                {
                    await Task.Delay(250, stoppingToken);
                    continue;
                }

                foreach (var projection in batch)
                {
                    try
                    {
                        await cluster.GetGrain<IUserGrain>(projection.UserId)
                            .ApplyBalanceSnapshot(projection.Version, projection.Balance);
                        await accounting.MarkProjectionAppliedAsync(
                            projection.UserId, projection.Version, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        await accounting.MarkProjectionFailedAsync(
                            projection, ex, stoppingToken);
                        logger.LogWarning(ex,
                            "Accounting projection for user {UserId} version {Version} remains pending",
                            projection.UserId, projection.Version);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Accounting projection worker iteration failed");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }
}
