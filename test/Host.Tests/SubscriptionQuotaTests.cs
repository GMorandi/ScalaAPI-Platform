using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ScalaAPI.Data.Accounting;
using ScalaAPI.Host.Services;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class SubscriptionQuotaTests
{
    [Fact]
    public async Task ReservationsAreSerializedAndSettlementReleasesQuota()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var userId = Random.Shared.NextInt64(600_000_000, 900_000_000);
        var planId = await InsertPlanAsync(dataSource, suffix);
        var subscriptionId = await InsertSubscriptionAsync(dataSource, userId, planId);
        var accounting = new AccountingStore(dataSource);
        await accounting.AppendEffectAsync(new AccountingEffect(
            userId, $"subscription-test-funding:{suffix}", "test_credit", 10m));
        var pricing = new ModelPricingService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pricing:Models:gpt-4o:InputPerMillion"] = "1",
            }).Build());
        var store = new RequestLeaseStore(dataSource, accounting, pricing,
            NullLogger<RequestLeaseStore>.Instance);
        var leaseTokens = new List<string>();

        try
        {
            var first = NewRequest(suffix, "first", userId, 0.60m);
            var second = NewRequest(suffix, "second", userId, 0.60m);
            var results = await Task.WhenAll(store.CreateDetailedAsync(first),
                store.CreateDetailedAsync(second));
            Assert.Single(results, result => result.Created);
            Assert.Single(results, result => result.SubscriptionQuotaExceeded);
            leaseTokens.AddRange(new[] { first.LeaseToken, second.LeaseToken });

            var winning = results[0].Created ? first : second;
            var completed = await store.CompleteAsync(new LeaseCompletion(
                winning.LeaseToken, 100, 0, 0, 0, 10, 0, 200, false, false));
            Assert.True(completed.Accepted);
            Assert.Equal((0m, 0.0001m), await ReadQuotaAsync(dataSource, subscriptionId));

            var followUp = NewRequest(suffix, "follow-up", userId, 0.999m);
            Assert.True((await store.CreateDetailedAsync(followUp)).Created);
            leaseTokens.Add(followUp.LeaseToken);
            Assert.Equal(0.999m, (await ReadQuotaAsync(dataSource, subscriptionId)).Reserved);

            Assert.True((await store.AbortAsync(followUp.LeaseToken, "provider_rejected")).Accepted);
            Assert.Equal((0m, 0.0001m), await ReadQuotaAsync(dataSource, subscriptionId));
        }
        finally
        {
            foreach (var table in new[] { "usage_outbox", "usage_logs", "usage_events" })
            {
                await using var cleanup = dataSource.CreateCommand(
                    $"DELETE FROM {table} WHERE lease_token = ANY($1)");
                cleanup.Parameters.AddWithValue(leaseTokens.ToArray());
                await cleanup.ExecuteNonQueryAsync();
            }
            await using (var leases = dataSource.CreateCommand(
                "DELETE FROM request_leases WHERE lease_token = ANY($1)"))
            {
                leases.Parameters.AddWithValue(leaseTokens.ToArray());
                await leases.ExecuteNonQueryAsync();
            }
            await using (var subscriptions = dataSource.CreateCommand(
                "DELETE FROM user_subscriptions WHERE id = $1"))
            {
                subscriptions.Parameters.AddWithValue(subscriptionId);
                await subscriptions.ExecuteNonQueryAsync();
            }
            await using (var plans = dataSource.CreateCommand(
                "DELETE FROM subscription_plans WHERE id = $1"))
            {
                plans.Parameters.AddWithValue(planId);
                await plans.ExecuteNonQueryAsync();
            }
            foreach (var table in new[]
                     { "accounting_projection_outbox", "balance_ledger", "accounting_accounts" })
            {
                await using var accountingCleanup = dataSource.CreateCommand(
                    $"DELETE FROM {table} WHERE user_id = $1");
                accountingCleanup.Parameters.AddWithValue(userId);
                await accountingCleanup.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public async Task QuotaEventsAreIdempotent()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var userId = Random.Shared.NextInt64(600_000_000, 900_000_000);
        var planId = await InsertPlanAsync(dataSource, suffix);
        var subscriptionId = await InsertSubscriptionAsync(dataSource, userId, planId);
        var accounting = new AccountingStore(dataSource);
        await accounting.AppendEffectAsync(new AccountingEffect(
            userId, $"quota-event-test-funding:{suffix}", "test_credit", 10m));
        var pricing = new ModelPricingService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pricing:Models:gpt-4o:InputPerMillion"] = "1",
            }).Build());
        var store = new RequestLeaseStore(dataSource, accounting, pricing,
            NullLogger<RequestLeaseStore>.Instance);
        var leaseToken = $"quota-event-lease-{suffix}";

        try
        {
            var request = new LeaseCreateRequest(
                leaseToken, $"quota-event-request-{suffix}",
                "subscription-key", 777001, userId, 777002, 777003, "gpt-4o", "gpt-4o",
                "chat_completions", 1m, null, 0.50m, DateTime.UtcNow.AddMinutes(5));
            Assert.True((await store.CreateDetailedAsync(request)).Created);

            var eventCount = await CountEventsAsync(dataSource, leaseToken);
            Assert.Equal(1L, eventCount);

            var eventType = await ReadEventTypeAsync(dataSource, leaseToken);
            Assert.Equal("reserved", eventType);

            var completed = await store.CompleteAsync(new LeaseCompletion(
                leaseToken, 100, 0, 0, 0, 10, 0, 200, false, false));
            Assert.True(completed.Accepted);

            var committedType = await ReadEventTypeAsync(dataSource, leaseToken);
            Assert.Equal("committed", committedType);

            var usedAmount = await ReadUsedAmountAsync(dataSource, leaseToken);
            Assert.Equal(0.0001m, usedAmount);
        }
        finally
        {
            await CleanupAsync(dataSource, new[] { leaseToken }, subscriptionId, planId, userId);
        }
    }

    [Fact]
    public async Task QuotaEventTransitionIsMonotonic()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var userId = Random.Shared.NextInt64(600_000_000, 900_000_000);
        var planId = await InsertPlanAsync(dataSource, suffix);
        var subscriptionId = await InsertSubscriptionAsync(dataSource, userId, planId);
        var accounting = new AccountingStore(dataSource);
        await accounting.AppendEffectAsync(new AccountingEffect(
            userId, $"quota-monotonic-test-funding:{suffix}", "test_credit", 10m));
        var pricing = new ModelPricingService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pricing:Models:gpt-4o:InputPerMillion"] = "1",
            }).Build());
        var store = new RequestLeaseStore(dataSource, accounting, pricing,
            NullLogger<RequestLeaseStore>.Instance);
        var leaseToken = $"quota-monotonic-lease-{suffix}";

        try
        {
            var request = new LeaseCreateRequest(
                leaseToken, $"quota-monotonic-request-{suffix}",
                "subscription-key", 777001, userId, 777002, 777003, "gpt-4o", "gpt-4o",
                "chat_completions", 1m, null, 0.50m, DateTime.UtcNow.AddMinutes(5));
            Assert.True((await store.CreateDetailedAsync(request)).Created);

            var completed = await store.CompleteAsync(new LeaseCompletion(
                leaseToken, 100, 0, 0, 0, 10, 0, 200, false, false));
            Assert.True(completed.Accepted);
            Assert.Equal("committed", await ReadEventTypeAsync(dataSource, leaseToken));

            var duplicate = await store.CompleteAsync(new LeaseCompletion(
                leaseToken, 100, 0, 0, 0, 10, 0, 200, false, false));
            Assert.True(duplicate.Duplicate);
            Assert.Equal("committed", await ReadEventTypeAsync(dataSource, leaseToken));

            var eventCount = await CountEventsAsync(dataSource, leaseToken);
            Assert.Equal(1L, eventCount);
        }
        finally
        {
            await CleanupAsync(dataSource, new[] { leaseToken }, subscriptionId, planId, userId);
        }
    }

    private static async Task<long> CountEventsAsync(NpgsqlDataSource dataSource, string leaseToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT count(*) FROM subscription_quota_events WHERE lease_token = $1");
        command.Parameters.AddWithValue(leaseToken);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ReadEventTypeAsync(NpgsqlDataSource dataSource, string leaseToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT event_type FROM subscription_quota_events WHERE lease_token = $1");
        command.Parameters.AddWithValue(leaseToken);
        return (string)(await command.ExecuteScalarAsync() ?? "");
    }

    private static async Task<decimal> ReadUsedAmountAsync(NpgsqlDataSource dataSource, string leaseToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT used_amount FROM subscription_quota_events WHERE lease_token = $1");
        command.Parameters.AddWithValue(leaseToken);
        return Convert.ToDecimal(await command.ExecuteScalarAsync() ?? 0m);
    }

    private static async Task CleanupAsync(NpgsqlDataSource dataSource, string[] leaseTokens,
        long subscriptionId, long planId, long userId)
    {
        foreach (var table in new[] { "usage_outbox", "usage_logs", "usage_events" })
        {
            await using var cleanup = dataSource.CreateCommand(
                $"DELETE FROM {table} WHERE lease_token = ANY($1)");
            cleanup.Parameters.AddWithValue(leaseTokens);
            await cleanup.ExecuteNonQueryAsync();
        }
        await using (var events = dataSource.CreateCommand(
            "DELETE FROM subscription_quota_events WHERE lease_token = ANY($1)"))
        {
            events.Parameters.AddWithValue(leaseTokens);
            await events.ExecuteNonQueryAsync();
        }
        await using (var leases = dataSource.CreateCommand(
            "DELETE FROM request_leases WHERE lease_token = ANY($1)"))
        {
            leases.Parameters.AddWithValue(leaseTokens);
            await leases.ExecuteNonQueryAsync();
        }
        await using (var subscriptions = dataSource.CreateCommand(
            "DELETE FROM user_subscriptions WHERE id = $1"))
        {
            subscriptions.Parameters.AddWithValue(subscriptionId);
            await subscriptions.ExecuteNonQueryAsync();
        }
        await using (var plans = dataSource.CreateCommand(
            "DELETE FROM subscription_plans WHERE id = $1"))
        {
            plans.Parameters.AddWithValue(planId);
            await plans.ExecuteNonQueryAsync();
        }
        foreach (var table in new[] { "accounting_projection_outbox", "balance_ledger", "accounting_accounts" })
        {
            await using var accountingCleanup = dataSource.CreateCommand(
                $"DELETE FROM {table} WHERE user_id = $1");
            accountingCleanup.Parameters.AddWithValue(userId);
            await accountingCleanup.ExecuteNonQueryAsync();
        }
    }

    private static LeaseCreateRequest NewRequest(string suffix, string name, long userId,
        decimal holdAmount) => new(
        $"subscription-lease-{name}-{suffix}", $"subscription-request-{name}-{suffix}",
        "subscription-key", 777001, userId, 777002, 777003, "gpt-4o", "gpt-4o",
        "chat_completions", 1m, null, holdAmount, DateTime.UtcNow.AddMinutes(5));

    private static async Task<long> InsertPlanAsync(NpgsqlDataSource dataSource, string suffix)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO subscription_plans(name, price_monthly, quota_usd, status)
            VALUES ($1, 1, 1, 'active') RETURNING id
            """);
        command.Parameters.AddWithValue($"quota-plan-{suffix}");
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<long> InsertSubscriptionAsync(NpgsqlDataSource dataSource,
        long userId, long planId)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO user_subscriptions(
                user_id, plan_id, status, expires_at, quota_granted_usd)
            VALUES ($1, $2, 'active', now() + interval '1 day', 1)
            RETURNING id
            """);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(planId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<(decimal Reserved, decimal Used)> ReadQuotaAsync(
        NpgsqlDataSource dataSource, long subscriptionId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT quota_reserved_usd, quota_used_usd FROM user_subscriptions WHERE id = $1");
        command.Parameters.AddWithValue(subscriptionId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetDecimal(0), reader.GetDecimal(1));
    }
}
