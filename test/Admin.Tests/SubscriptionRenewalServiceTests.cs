using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ScalaAPI.Admin.Payments;
using Xunit;

namespace ScalaAPI.Admin.Tests;

public sealed class SubscriptionRenewalServiceTests
{
    [Fact]
    public async Task DueSubscriptionsExpireRenewOrWaitForHeldQuotaExactlyOnce()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var planId = await InsertPlanAsync(dataSource, suffix, 10m);
        var renewUser = Random.Shared.NextInt64(600_000_000, 700_000_000);
        var expireUser = renewUser + 1;
        var blockedUser = renewUser + 2;
        var staleUser = renewUser + 3;
        await InsertUserAsync(dataSource, renewUser, $"renew-{suffix}@example.test");
        await InsertUserAsync(dataSource, expireUser, $"expire-{suffix}@example.test");
        await InsertUserAsync(dataSource, blockedUser, $"blocked-{suffix}@example.test");
        await InsertUserAsync(dataSource, staleUser, $"stale-{suffix}@example.test");
        var renewId = await InsertSubscriptionAsync(dataSource, renewUser, planId,
            renewalAt: DateTime.UtcNow.AddMinutes(-5), quotaGranted: 5m, quotaUsed: 4m);
        var expireId = await InsertSubscriptionAsync(dataSource, expireUser, planId,
            renewalAt: null, quotaGranted: 10m, quotaUsed: 2m);
        var blockedId = await InsertSubscriptionAsync(dataSource, blockedUser, planId,
            renewalAt: DateTime.UtcNow.AddMinutes(-5), quotaGranted: 5m, quotaUsed: 4m,
            quotaReserved: 1m);
        var staleId = await InsertSubscriptionAsync(dataSource, staleUser, planId,
            renewalAt: DateTime.UtcNow.AddMinutes(-5), quotaGranted: 5m, quotaUsed: 4m,
            status: "expired");
        var service = new SubscriptionRenewalService(dataSource,
            NullLogger<SubscriptionRenewalService>.Instance);

        try
        {
            var workerRuns = await Task.WhenAll(
                service.ProcessDueOnceAsync(), service.ProcessDueOnceAsync());
            Assert.Equal(4, workerRuns.Sum());
            Assert.Equal(("active", 10m, 0m, 0m),
                await ReadSubscriptionAsync(dataSource, renewId));
            Assert.Equal(1, await ReadEventCountAsync(dataSource, renewId, "renewed"));

            Assert.Equal(("expired", 10m, 2m, 0m),
                await ReadSubscriptionAsync(dataSource, expireId));
            Assert.Equal(1, await ReadEventCountAsync(dataSource, expireId, "expired"));

            Assert.Equal(("past_due", 5m, 4m, 1m),
                await ReadSubscriptionAsync(dataSource, blockedId));
            Assert.Equal(0, await ReadEventCountAsync(dataSource, blockedId, "renewed"));

            Assert.Equal(("active", 10m, 0m, 0m),
                await ReadSubscriptionAsync(dataSource, staleId));
            Assert.Equal(1, await ReadEventCountAsync(dataSource, staleId, "renewed"));

            await using (var release = dataSource.CreateCommand(
                "UPDATE user_subscriptions SET quota_reserved_usd = 0 WHERE id = $1"))
            {
                release.Parameters.AddWithValue(blockedId);
                await release.ExecuteNonQueryAsync();
            }
            Assert.Equal(1, await service.ProcessDueOnceAsync());
            Assert.Equal(("active", 10m, 0m, 0m),
                await ReadSubscriptionAsync(dataSource, blockedId));
            Assert.Equal(1, await ReadEventCountAsync(dataSource, blockedId, "renewed"));
        }
        finally
        {
            await using (var subscriptions = dataSource.CreateCommand(
                "DELETE FROM user_subscriptions WHERE id = ANY($1)"))
            {
                subscriptions.Parameters.AddWithValue(new[] { renewId, expireId, blockedId, staleId });
                await subscriptions.ExecuteNonQueryAsync();
            }
            await using (var plans = dataSource.CreateCommand(
                "DELETE FROM subscription_plans WHERE id = $1"))
            {
                plans.Parameters.AddWithValue(planId);
                await plans.ExecuteNonQueryAsync();
            }
            await using (var users = dataSource.CreateCommand(
                "DELETE FROM user_accounts WHERE id = ANY($1)"))
            {
                users.Parameters.AddWithValue(new[] { renewUser, expireUser, blockedUser, staleUser });
                await users.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task InsertUserAsync(NpgsqlDataSource dataSource, long id, string email)
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO user_accounts(id, email) VALUES ($1, $2)");
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(email);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> InsertPlanAsync(NpgsqlDataSource dataSource,
        string suffix, decimal quota)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO subscription_plans(name, price_monthly, quota_usd, status)
            VALUES ($1, 10, $2, 'active') RETURNING id
            """);
        command.Parameters.AddWithValue($"renewal-plan-{suffix}");
        command.Parameters.AddWithValue(quota);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<long> InsertSubscriptionAsync(NpgsqlDataSource dataSource,
        long userId, long planId, DateTime? renewalAt, decimal quotaGranted,
        decimal quotaUsed, decimal quotaReserved = 0m, string status = "active")
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO user_subscriptions(
                user_id, plan_id, status, expires_at, renewal_at, provider,
                quota_granted_usd, quota_used_usd, quota_reserved_usd)
            VALUES ($1, $2, $7, now() - interval '1 minute', $3, 'internal', $4, $5, $6)
            RETURNING id
            """);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(planId);
        command.Parameters.AddWithValue((object?)renewalAt ?? DBNull.Value);
        command.Parameters.AddWithValue(quotaGranted);
        command.Parameters.AddWithValue(quotaUsed);
        command.Parameters.AddWithValue(quotaReserved);
        command.Parameters.AddWithValue(status);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<(string Status, decimal Granted, decimal Used, decimal Reserved)>
        ReadSubscriptionAsync(NpgsqlDataSource dataSource, long id)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT status, quota_granted_usd, quota_used_usd, quota_reserved_usd
            FROM user_subscriptions WHERE id = $1
            """);
        command.Parameters.AddWithValue(id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetDecimal(1), reader.GetDecimal(2),
            reader.GetDecimal(3));
    }

    private static async Task<long> ReadEventCountAsync(NpgsqlDataSource dataSource,
        long subscriptionId, string eventType)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT count(*) FROM subscription_events WHERE subscription_id = $1 AND event_type = $2");
        command.Parameters.AddWithValue(subscriptionId);
        command.Parameters.AddWithValue(eventType);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
