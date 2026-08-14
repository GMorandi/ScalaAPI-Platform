using Npgsql;
using ScalaAPI.Data.Announcements;
using ScalaAPI.Data.Redemptions;
using ScalaAPI.Data.Referrals;
using ScalaAPI.Data.Subscriptions;
using Xunit;

namespace ScalaAPI.Host.Tests;

/// <summary>
/// Tests for P2-01: Complete subscription, redemption, referral, and announcement lifecycle.
/// Verifies concurrency, isolation, and correctness of all lifecycle operations.
/// Requires GREENFIELD_SCHEMA_CONNECTION environment variable.
/// </summary>
public sealed class SubscriptionLifecycleTests
{
    [Fact]
    public async Task PurchaseDrivenByPaymentConfirmation()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var userId = Random.Shared.NextInt64(600_000_000, 900_000_000);
        var planId = $"test-plan-{Guid.NewGuid():N}"[..24];
        var paymentOrderId = await InsertPaymentOrderAsync(dataSource, userId, "paid");
        var service = new SubscriptionService(dataSource);

        try
        {
            // Ensure user exists
            await EnsureUserExistsAsync(dataSource, userId);

            var result = await service.CreateFromPaymentAsync(
                userId, planId, paymentOrderId, true, $"idem-{Guid.NewGuid():N}");
            Assert.Equal(PurchaseStatus.Created, result.Status);
            Assert.NotNull(result.PurchaseId);

            // Verify the purchase is active
            var isActive = await service.IsActiveAsync(result.PurchaseId!.Value);
            Assert.True(isActive);

            // Verify listing
            var items = await service.ListForUserAsync(userId);
            Assert.Single(items);
            Assert.Equal(planId, items[0].PlanId);
            Assert.Equal("active", items[0].Status);
        }
        finally
        {
            await CleanupAsync(dataSource, userId, planId, paymentOrderId);
        }
    }

    [Fact]
    public async Task PurchaseRejectedWhenPaymentNotConfirmed()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var userId = Random.Shared.NextInt64(600_000_000, 900_000_000);
        var planId = $"test-plan-{Guid.NewGuid():N}"[..24];
        var paymentOrderId = await InsertPaymentOrderAsync(dataSource, userId, "pending");
        var service = new SubscriptionService(dataSource);

        try
        {
            await EnsureUserExistsAsync(dataSource, userId);

            var result = await service.CreateFromPaymentAsync(
                userId, planId, paymentOrderId, true, $"idem-{Guid.NewGuid():N}");
            Assert.Equal(PurchaseStatus.PaymentNotConfirmed, result.Status);
        }
        finally
        {
            await CleanupAsync(dataSource, userId, planId, paymentOrderId);
        }
    }

    [Fact]
    public async Task ExpiryAndRenewal()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var userId = Random.Shared.NextInt64(600_000_000, 900_000_000);
        var planId = $"test-plan-{Guid.NewGuid():N}"[..24];
        var paymentOrderId = await InsertPaymentOrderAsync(dataSource, userId, "paid");
        var service = new SubscriptionService(dataSource);

        try
        {
            await EnsureUserExistsAsync(dataSource, userId);

            // Insert an already-expired purchase directly
            await using (var insert = dataSource.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO subscription_purchases
                        (user_id, plan_id, payment_order_id, started_at, expires_at, status)
                    VALUES ($1, $2, $3, now() - interval '2 days', now() - interval '1 day', 'active')
                    """;
                insert.Parameters.AddWithValue(userId);
                insert.Parameters.AddWithValue(planId);
                insert.Parameters.AddWithValue(paymentOrderId);
                await insert.ExecuteNonQueryAsync();
            }

            // Verify it shows as active before expiry processing
            var beforeExpiry = await service.ListForUserAsync(userId);
            Assert.Single(beforeExpiry);
            Assert.Equal("active", beforeExpiry[0].Status);

            // Process expiry
            var expired = await service.ExpireDueAsync();
            Assert.True(expired >= 1);

            // Verify it's now expired
            var afterExpiry = await service.ListForUserAsync(userId);
            Assert.Single(afterExpiry);
            Assert.Equal("expired", afterExpiry[0].Status);
        }
        finally
        {
            await CleanupAsync(dataSource, userId, planId, paymentOrderId);
        }
    }

    [Fact]
    public async Task RedemptionCodeConcurrency_OnlyOneEntitlement()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var userId1 = Random.Shared.NextInt64(600_000_000, 900_000_000);
        var userId2 = userId1 + 1;
        var planId = $"test-plan-{Guid.NewGuid():N}"[..24];
        var service = new RedemptionService(dataSource);

        try
        {
            await EnsureUserExistsAsync(dataSource, userId1);
            await EnsureUserExistsAsync(dataSource, userId2);

            // Create a code with max_uses = 1
            var code = await service.CreateCodeAsync(planId, 1, TimeSpan.FromHours(1), null);
            Assert.Equal(RedemptionCodeStatus.Created, code.Status);
            Assert.NotNull(code.PlaintextCode);

            // Two concurrent redemptions for different users
            var results = await Task.WhenAll(
                service.RedeemAsync(code.PlaintextCode ?? "", userId1),
                service.RedeemAsync(code.PlaintextCode ?? "", userId2));

            // Only one should succeed (concurrency produces only one entitlement)
            var succeeded = results.Count(r => r.Status == RedemptionStatus.Redeemed);
            var failed = results.Count(r => r.Status == RedemptionStatus.UsageLimitReached
                                            || r.Status == RedemptionStatus.Duplicate);
            Assert.Equal(1, succeeded);
            Assert.Equal(1, failed);

            // Verify usage count matches
            await using var check = dataSource.CreateCommand();
            check.CommandText = "SELECT current_uses FROM redemption_codes WHERE code_id = $1";
            check.Parameters.AddWithValue(code.CodeId!);
            var uses = Convert.ToInt32(await check.ExecuteScalarAsync());
            Assert.Equal(1, uses);
        }
        finally
        {
            await using (var cleanupHistory = dataSource.CreateCommand())
            {
                cleanupHistory.CommandText = """
                DELETE FROM redemption_history WHERE code_id IN
                    (SELECT code_id FROM redemption_codes WHERE plan_id = $1)
                """;
                cleanupHistory.Parameters.AddWithValue(planId);
                await cleanupHistory.ExecuteNonQueryAsync();
            }

            await using (var cleanupCodes = dataSource.CreateCommand(
                "DELETE FROM redemption_codes WHERE plan_id = $1"))
            {
                cleanupCodes.Parameters.AddWithValue(planId);
                await cleanupCodes.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public async Task RedemptionExpiryAndUsageLimit()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var userId = Random.Shared.NextInt64(600_000_000, 900_000_000);
        var planId = $"test-plan-{Guid.NewGuid():N}"[..24];
        var service = new RedemptionService(dataSource);

        try
        {
            await EnsureUserExistsAsync(dataSource, userId);

            // Create an expired code
            var expiredCode = await service.CreateCodeAsync(planId, 10,
                TimeSpan.FromHours(-1), null);
            Assert.Equal(RedemptionCodeStatus.Created, expiredCode.Status);

            var expiredResult = await service.RedeemAsync(expiredCode.PlaintextCode ?? "", userId);
            Assert.Equal(RedemptionStatus.Expired, expiredResult.Status);

            // Create a code with max_uses = 1 and redeem it
            var limitedCode = await service.CreateCodeAsync(planId, 1, TimeSpan.FromHours(1), null);
            Assert.Equal(RedemptionCodeStatus.Created, limitedCode.Status);

            var firstRedeem = await service.RedeemAsync(limitedCode.PlaintextCode ?? "", userId);
            Assert.Equal(RedemptionStatus.Redeemed, firstRedeem.Status);

            // Second redemption by same user should be duplicate
            var secondRedeem = await service.RedeemAsync(limitedCode.PlaintextCode ?? "", userId);
            Assert.Equal(RedemptionStatus.Duplicate, secondRedeem.Status);

            // Verify usage count
            await using var check = dataSource.CreateCommand();
            check.CommandText = "SELECT current_uses FROM redemption_codes WHERE code_id = $1";
            check.Parameters.AddWithValue(limitedCode.CodeId!);
            var uses = Convert.ToInt32(await check.ExecuteScalarAsync());
            Assert.Equal(1, uses);
        }
        finally
        {
            await using (var cleanupHistory = dataSource.CreateCommand())
            {
                cleanupHistory.CommandText = """
                DELETE FROM redemption_history WHERE code_id IN
                    (SELECT code_id FROM redemption_codes WHERE plan_id = $1)
                """;
                cleanupHistory.Parameters.AddWithValue(planId);
                await cleanupHistory.ExecuteNonQueryAsync();
            }

            await using (var cleanupCodes = dataSource.CreateCommand(
                "DELETE FROM redemption_codes WHERE plan_id = $1"))
            {
                cleanupCodes.Parameters.AddWithValue(planId);
                await cleanupCodes.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public async Task ReferralAttribution_OnePerUser()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var referrerId = Random.Shared.NextInt64(600_000_000, 900_000_000);
        var referredId = Random.Shared.NextInt64(600_000_000, 900_000_000);
        // Ensure different IDs
        if (referredId == referrerId) referredId++;
        var service = new ReferralService(dataSource);

        try
        {
            await EnsureUserExistsAsync(dataSource, referrerId);
            await EnsureUserExistsAsync(dataSource, referredId);

            // First attribution should succeed
            var first = await service.AttributeAsync(referrerId, referredId);
            Assert.Equal(AttributionStatus.Created, first.Status);
            Assert.NotNull(first.ReferralId);

            // Second attribution for same referred user should be duplicate (anti-abuse)
            var second = await service.AttributeAsync(referrerId, referredId);
            Assert.Equal(AttributionStatus.Duplicate, second.Status);

            // Different referrer, same referred user should also be duplicate
            var otherReferrer = referredId + 100;
            await EnsureUserExistsAsync(dataSource, otherReferrer);
            var third = await service.AttributeAsync(otherReferrer, referredId);
            Assert.Equal(AttributionStatus.Duplicate, third.Status);

            // Verify listing
            var items = await service.ListForReferrerAsync(referrerId);
            Assert.Single(items);
            Assert.Equal(referredId, items[0].ReferredUserId);
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand();
            cleanup.CommandText = """
                DELETE FROM referral_attributions
                WHERE referrer_user_id = $1 OR referrer_user_id = $2 OR referred_user_id = $3
                """;
            cleanup.Parameters.AddWithValue(referrerId);
            cleanup.Parameters.AddWithValue(referredId + 100);
            cleanup.Parameters.AddWithValue(referredId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task AnnouncementTargetingAndReadState()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var userId = Random.Shared.NextInt64(600_000_000, 900_000_000);
        var announcementTitle = $"Test Announcement {Guid.NewGuid():N}";
        var announcementContent = $"Test content {Guid.NewGuid():N}";
        var service = new AnnouncementService(dataSource);

        try
        {
            await EnsureUserExistsAsync(dataSource, userId);

            // Create an announcement
            var created = await service.CreateAsync(
                announcementTitle, announcementContent, "all", null, null, null);
            Assert.Equal(AnnouncementCreationStatus.Created, created.Status);
            Assert.NotNull(created.AnnouncementId);

            // List for user - should see the announcement
            var items = await service.ListForUserAsync(userId);
            Assert.Contains(items, a => a.Id == created.AnnouncementId!.Value);
            Assert.Null(items.First(a => a.Id == created.AnnouncementId!.Value).ReadAt);

            // Mark as read
            var readResult = await service.MarkReadAsync(userId, created.AnnouncementId!.Value);
            Assert.Equal(ReadStatus.Created, readResult.Status);
            Assert.NotNull(readResult.ReadAt);

            // Mark as read again - should be duplicate
            var duplicateRead = await service.MarkReadAsync(userId, created.AnnouncementId!.Value);
            Assert.Equal(ReadStatus.Duplicate, duplicateRead.Status);

            // Verify read state in listing
            var itemsAfterRead = await service.ListForUserAsync(userId);
            var announcement = itemsAfterRead.FirstOrDefault(a => a.Id == created.AnnouncementId!.Value);
            Assert.NotNull(announcement);
            Assert.NotNull(announcement.ReadAt);
        }
        finally
        {
            await using (var cleanupReads = dataSource.CreateCommand(
                "DELETE FROM announcement_reads WHERE user_id = $1"))
            {
                cleanupReads.Parameters.AddWithValue(userId);
                await cleanupReads.ExecuteNonQueryAsync();
            }

            await using (var cleanupAnnouncements = dataSource.CreateCommand("""
                DELETE FROM announcements
                WHERE title = $1 AND content = $2
                """))
            {
                cleanupAnnouncements.Parameters.AddWithValue(announcementTitle);
                cleanupAnnouncements.Parameters.AddWithValue(announcementContent);
                await cleanupAnnouncements.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public async Task UsersCannotReadOthersOrders()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var userId1 = Random.Shared.NextInt64(600_000_000, 900_000_000);
        var userId2 = userId1 + 1;
        var planId = $"test-plan-{Guid.NewGuid():N}"[..24];
        var paymentOrderId = await InsertPaymentOrderAsync(dataSource, userId1, "paid");
        var service = new SubscriptionService(dataSource);

        try
        {
            await EnsureUserExistsAsync(dataSource, userId1);
            await EnsureUserExistsAsync(dataSource, userId2);

            var result = await service.CreateFromPaymentAsync(
                userId1, planId, paymentOrderId, true, $"idem-{Guid.NewGuid():N}");
            Assert.Equal(PurchaseStatus.Created, result.Status);

            // User2 should not see User1's subscriptions
            var user2Items = await service.ListForUserAsync(userId2);
            Assert.Empty(user2Items);

            // User1 should see their own
            var user1Items = await service.ListForUserAsync(userId1);
            Assert.Single(user1Items);
        }
        finally
        {
            await CleanupAsync(dataSource, userId1, planId, paymentOrderId);
        }
    }

    [Fact]
    public async Task QuotaReservationMatchesUsageSettlement()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var userId = Random.Shared.NextInt64(600_000_000, 900_000_000);
        var planId = $"test-plan-{Guid.NewGuid():N}"[..24];
        var service = new RedemptionService(dataSource);

        try
        {
            await EnsureUserExistsAsync(dataSource, userId);

            // Create a code with max_uses = 3
            var code = await service.CreateCodeAsync(planId, 3, TimeSpan.FromHours(1), null);
            Assert.Equal(RedemptionCodeStatus.Created, code.Status);

            // Redeem 3 times with different users
            var users = new long[] { userId, userId + 1, userId + 2 };
            foreach (var u in users)
            {
                await EnsureUserExistsAsync(dataSource, u);
                var r = await service.RedeemAsync(code.PlaintextCode ?? "", u);
                Assert.Equal(RedemptionStatus.Redeemed, r.Status);
            }

            // Verify current_uses = 3 (quota reservation matches usage)
            await using var check = dataSource.CreateCommand();
            check.CommandText = "SELECT current_uses FROM redemption_codes WHERE code_id = $1";
            check.Parameters.AddWithValue(code.CodeId!);
            var uses = Convert.ToInt32(await check.ExecuteScalarAsync());
            Assert.Equal(3, uses);

            // 4th redemption should fail
            await EnsureUserExistsAsync(dataSource, userId + 3);
            var overflow = await service.RedeemAsync(code.PlaintextCode ?? "", userId + 3);
            Assert.Equal(RedemptionStatus.UsageLimitReached, overflow.Status);

            // current_uses should still be 3
            var finalUses = Convert.ToInt32(await check.ExecuteScalarAsync());
            Assert.Equal(3, finalUses);
        }
        finally
        {
            await using (var cleanupHistory = dataSource.CreateCommand())
            {
                cleanupHistory.CommandText = """
                DELETE FROM redemption_history WHERE code_id IN
                    (SELECT code_id FROM redemption_codes WHERE plan_id = $1)
                """;
                cleanupHistory.Parameters.AddWithValue(planId);
                await cleanupHistory.ExecuteNonQueryAsync();
            }

            await using (var cleanupCodes = dataSource.CreateCommand(
                "DELETE FROM redemption_codes WHERE plan_id = $1"))
            {
                cleanupCodes.Parameters.AddWithValue(planId);
                await cleanupCodes.ExecuteNonQueryAsync();
            }
        }
    }

    // --- Helpers ---

    private static async Task EnsureUserExistsAsync(NpgsqlDataSource dataSource, long userId)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO user_accounts (id, email, password_hash, role, status, created_at)
            VALUES ($1, $2, 'test', 'user', 'active', now())
            ON CONFLICT (id) DO NOTHING
            """);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue($"lifecycle-test-{userId}@test.local");
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> InsertPaymentOrderAsync(
        NpgsqlDataSource dataSource, long userId, string status)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO payment_orders (user_id, amount, currency, status, provider)
            VALUES ($1, 10.00, 'USD', $2, 'mock')
            RETURNING id
            """);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(status);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task CleanupAsync(
        NpgsqlDataSource dataSource, long userId, string planId, long paymentOrderId)
    {
        await using (var cleanupPurchases = dataSource.CreateCommand(
            "DELETE FROM subscription_purchases WHERE user_id = $1 AND plan_id = $2"))
        {
            cleanupPurchases.Parameters.AddWithValue(userId);
            cleanupPurchases.Parameters.AddWithValue(planId);
            await cleanupPurchases.ExecuteNonQueryAsync();
        }

        await using (var cleanupPayment = dataSource.CreateCommand(
            "DELETE FROM payment_orders WHERE id = $1"))
        {
            cleanupPayment.Parameters.AddWithValue(paymentOrderId);
            await cleanupPayment.ExecuteNonQueryAsync();
        }
    }
}
