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
        var recovered = await store.RequeueUnprocessedDeadLettersAsync(stoppingToken);
        if (recovered > 0)
            logger.LogWarning("Requeued {OutboxCount} unprocessed settlement events after restart", recovered);

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
                    if (expired > 0)
                        logger.LogWarning(
                            "Moved {LeaseCount} timed-out request leases to reconciliation-needed",
                            expired);
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
        await user.FinalizeLease(lease.LeaseToken, lease.RequestId);
        if (item.EventType == "complete")
        {
            var cost = lease.FinalCostUsd ?? 0m;
            await cluster.GetGrain<IGroupGrain>(lease.GroupId)
                .RecordLeaseSpend(lease.LeaseToken, cost);
            await cluster.GetGrain<IApiKeyGrain>(lease.ApiKeyHash)
                .AddLeaseUsage(lease.LeaseToken, cost);
        }
    }
}
