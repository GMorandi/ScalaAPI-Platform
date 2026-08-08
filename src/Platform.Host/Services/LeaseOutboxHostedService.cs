using Orleans;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Host.Services;

public sealed class LeaseOutboxHostedService(
    RequestLeaseStore store,
    IClusterClient cluster,
    ILogger<LeaseOutboxHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, 4)
            .Select(i => WorkerLoopAsync($"outbox-{Environment.ProcessId}-{i}", stoppingToken));
        await Task.WhenAll(workers);
    }

    private async Task WorkerLoopAsync(string workerId, CancellationToken stoppingToken)
    {
        var lastExpiryScan = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow - lastExpiryScan >= TimeSpan.FromSeconds(10))
                {
                    var expired = await store.ExpireActiveAsync(stoppingToken);
                    if (expired > 0) logger.LogWarning("Expired {LeaseCount} abandoned request leases", expired);
                    lastExpiryScan = DateTime.UtcNow;
                }

                var pending = await store.ClaimOutboxBatchAsync(workerId, 50, stoppingToken);
                if (pending.Count == 0)
                {
                    await Task.Delay(250, stoppingToken);
                    continue;
                }

                foreach (var claimed in pending)
                {
                    try
                    {
                        await ApplyAsync(claimed.Item, claimed.Lease);
                        await store.MarkProcessedAsync(claimed.Item.Id, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        await store.MarkRetryAsync(claimed.Item, ex, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Lease outbox worker {WorkerId} failed", workerId);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task ApplyAsync(OutboxItem item, RequestLease lease)
    {
        var account = cluster.GetGrain<IAccountGrain>(lease.AccountId);
        await account.ReleaseSlot(lease.LeaseToken);

        var user = cluster.GetGrain<IUserGrain>(lease.UserId);
        if (item.EventType == "complete")
        {
            var cost = lease.FinalCostUsd ?? 0m;
            await user.CompleteLease(lease.LeaseToken, lease.RequestId,
                lease.HoldHandle is null ? null : new HoldHandle(lease.HoldHandle, lease.HoldAmount), cost);
            await cluster.GetGrain<IGroupGrain>(lease.GroupId)
                .RecordLeaseSpend(lease.LeaseToken, cost);
            await cluster.GetGrain<IApiKeyGrain>(lease.ApiKeyHash)
                .AddLeaseUsage(lease.LeaseToken, cost);
        }
        else
        {
            await user.AbortLease(lease.LeaseToken, lease.RequestId,
                lease.HoldHandle is null ? null : new HoldHandle(lease.HoldHandle, lease.HoldAmount));
        }
    }
}
