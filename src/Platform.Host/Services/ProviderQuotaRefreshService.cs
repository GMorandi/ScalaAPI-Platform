using Npgsql;
using ScalaAPI.Data.ProviderQuota;

namespace ScalaAPI.Host.Services;

/// <summary>
/// Background service that periodically refreshes provider quota snapshots
/// for active accounts. Uses CAS-based refresh so that two silos refreshing
/// simultaneously produce only one valid generation.
/// </summary>
public sealed class ProviderQuotaRefreshService(
    NpgsqlDataSource dataSource,
    IProviderQuotaStore quotaStore,
    IConfiguration configuration,
    ILogger<ProviderQuotaRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue("ProviderQuota:RefreshSeconds", 120), 30, 3600));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshActiveAccountsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Provider quota refresh iteration failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Refreshes quota for all active accounts that have a quota snapshot row.
    /// In a real deployment this would call the provider's usage/quota API;
    /// here we ensure the row exists and bump the generation to prove CAS works.
    /// </summary>
    public async Task<int> RefreshActiveAccountsAsync(CancellationToken ct = default)
    {
        var refreshed = 0;
        var accountIds = await GetActiveAccountIdsAsync(ct);

        foreach (var accountId in accountIds)
        {
            try
            {
                var result = await quotaStore.RefreshAsync(accountId, current =>
                {
                    // If there's no existing snapshot, create a default one.
                    // In production this would call the provider's quota API.
                    var tier = current?.Tier ?? "free";
                    var remaining = current?.RemainingQuota;
                    var expiresAt = current?.ExpiresAt
                        ?? DateTime.UtcNow.AddHours(1);

                    return new ProviderQuotaUpdate(
                        tier,
                        remaining,
                        current?.WindowStart ?? DateTime.UtcNow,
                        current?.WindowEnd ?? DateTime.UtcNow.AddHours(1),
                        "refresh_worker",
                        expiresAt);
                }, ct);

                if (result.Applied)
                    refreshed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Quota refresh failed for account {AccountId}", accountId);
            }
        }

        return refreshed;
    }

    private async Task<long[]> GetActiveAccountIdsAsync(CancellationToken ct)
    {
        // Read account IDs from the provider_quota_state table that have
        // been seeded. In production this would come from the account listing.
        await using var command = dataSource.CreateCommand();
        command.CommandText = "SELECT account_id FROM provider_quota_state";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new List<long>();
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetInt64(0));
        return ids.ToArray();
    }
}
