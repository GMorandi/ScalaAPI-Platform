using ScalaAPI.Data.Exports;
using ScalaAPI.Data.Retention;

namespace ScalaAPI.Host.Services;

public sealed class RetentionCleanupWorker(
    RetentionService retention,
    ExportService exports,
    ILogger<RetentionCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                // Reclaim stale export jobs that were interrupted by a worker crash.
                await exports.ReclaimStaleGeneratingJobsAsync(
                    TimeSpan.FromMinutes(10), stoppingToken);

                // Expire export jobs past their TTL.
                await exports.ExpireStaleJobsAsync(stoppingToken);

                // Run retention cleanup using the system actor (null = automated).
                var idempotencyKey = $"auto-cleanup-{DateTime.UtcNow:yyyyMMddHHmm}";
                var result = await retention.RunCleanupAsync(
                    actorUserId: null,
                    idempotencyKey: idempotencyKey,
                    dryRun: false,
                    limitPerCategory: 500,
                    ct: stoppingToken);

                if (result.TotalDeleted > 0 || result.TotalFailed > 0)
                {
                    logger.LogInformation(
                        "Retention cleanup run {RunId}: deleted={Deleted}, failed={Failed}, dry_run={DryRun}",
                        result.RunId, result.TotalDeleted, result.TotalFailed, result.DryRun);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Retention cleanup worker tick failed");
            }
        }
    }
}
