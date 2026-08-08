using ScalaAPI.Data.Accounting;

namespace ScalaAPI.Host.Services;

public sealed class AccountingReconciliationHostedService(
    AccountingReconciliationService reconciliation,
    IConfiguration configuration,
    ILogger<AccountingReconciliationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialDelay = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue("Accounting:ReconciliationInitialDelaySeconds", 5), 1, 300));
        var interval = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue("Accounting:ReconciliationIntervalSeconds", 60), 10, 86_400));
        await Task.Delay(initialDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await reconciliation.RunAsync("scheduled", stoppingToken);
                if (result.Started && result.Status == "failed")
                {
                    logger.LogWarning(
                        "Accounting reconciliation run {RunId} found {IncidentCount} open incidents",
                        result.RunId, result.OpenIncidents);
                }
                else if (result.Started && (result.RepairedHolds > 0 || result.RepairedProjections > 0))
                {
                    logger.LogInformation(
                        "Accounting reconciliation run {RunId} repaired {HoldCount} holds and {ProjectionCount} projections",
                        result.RunId, result.RepairedHolds, result.RepairedProjections);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Accounting reconciliation worker iteration failed");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }
}
