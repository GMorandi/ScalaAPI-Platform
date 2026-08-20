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
    ProviderCredentialRefreshService credentials,
    ProviderQuotaClientFactory quotaClientFactory,
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
    /// Refreshes quota for all active accounts by calling the provider's quota API.
    /// Uses CAS-based refresh so that two silos refreshing simultaneously produce
    /// only one valid generation.
    /// </summary>
    public async Task<int> RefreshActiveAccountsAsync(CancellationToken ct = default)
    {
        var refreshed = 0;
        var accountIds = await GetActiveAccountIdsAsync(ct);

        foreach (var accountId in accountIds)
        {
            try
            {
                var creds = await credentials.GetFreshAsync(accountId, ct);
                var client = quotaClientFactory.GetClient(creds.Platform);

                ProviderQuotaInfo? quotaInfo = null;
                if (client != null)
                {
                    quotaInfo = await client.GetQuotaAsync(creds, ct);
                }

                var result = await quotaStore.RefreshAsync(accountId, current =>
                {
                    if (quotaInfo != null)
                    {
                        return new ProviderQuotaUpdate(
                            quotaInfo.Tier,
                            quotaInfo.RemainingQuota,
                            quotaInfo.WindowStart,
                            quotaInfo.WindowEnd,
                            "refresh_worker",
                            quotaInfo.ExpiresAt);
                    }

                    // Fallback: if no quota client or call failed, preserve existing values
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
                {
                    refreshed++;
                    await UpdateStaleStateAsync(accountId, success: true, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Quota refresh failed for account {AccountId}", accountId);
                await UpdateStaleStateAsync(accountId, success: false, ct);
            }
        }

        return refreshed;
    }

    private async Task<long[]> GetActiveAccountIdsAsync(CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand();
        command.CommandText = """
            SELECT id FROM accounts
            WHERE status = 'active' AND deleted_at IS NULL
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new List<long>();
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetInt64(0));
        return ids.ToArray();
    }

    private async Task UpdateStaleStateAsync(long accountId, bool success, CancellationToken ct)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();
            if (success)
            {
                cmd.CommandText = """
                    UPDATE provider_quota_state
                    SET consecutive_failures = 0, state = 'fresh', updated_at = now()
                    WHERE account_id = $1
                    """;
            }
            else
            {
                cmd.CommandText = """
                    UPDATE provider_quota_state
                    SET consecutive_failures = consecutive_failures + 1,
                        state = CASE WHEN consecutive_failures + 1 >= 3 THEN 'stale' ELSE state END,
                        updated_at = now()
                    WHERE account_id = $1
                    """;
            }
            cmd.Parameters.AddWithValue(accountId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update stale state for account {AccountId}", accountId);
        }
    }
}
