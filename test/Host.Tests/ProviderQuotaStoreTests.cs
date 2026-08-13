using Npgsql;
using ScalaAPI.Data.ProviderQuota;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class ProviderQuotaStoreTests
{
    [Fact]
    public async Task CasRefresh_TwoConcurrentRefreshes_ProduceOneValidGeneration()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new ProviderQuotaStore(dataSource);
        await store.EnsureReservationTableAsync();

        var accountId = 900_000_000L + Random.Shared.Next(1, 90_000_000);
        try
        {
            // Seed initial row
            await store.RefreshAsync(accountId, _ => new ProviderQuotaUpdate(
                "free", 100m, DateTime.UtcNow, DateTime.UtcNow.AddHours(1),
                "seed", DateTime.UtcNow.AddHours(2)));

            // Two concurrent refreshes
            var task1 = store.RefreshAsync(accountId, current => new ProviderQuotaUpdate(
                "tier-a", current!.RemainingQuota, current.WindowStart, current.WindowEnd,
                "silo-1", DateTime.UtcNow.AddHours(2)));
            var task2 = store.RefreshAsync(accountId, current => new ProviderQuotaUpdate(
                "tier-b", current!.RemainingQuota, current.WindowStart, current.WindowEnd,
                "silo-2", DateTime.UtcNow.AddHours(2)));

            var results = await Task.WhenAll(task1, task2);

            // Both should report applied (sequential under advisory lock)
            Assert.All(results, r => Assert.True(r.Applied));
            // Generations should be sequential
            var generations = results.Select(r => r.Generation).Order().ToArray();
            Assert.Equal(generations[0] + 1, generations[1]);

            // Final state should reflect the last writer
            var snapshot = await store.GetAsync(accountId);
            Assert.NotNull(snapshot);
            Assert.Equal(generations[1], snapshot!.Generation);
        }
        finally
        {
            await CleanupAsync(dataSource, accountId);
        }
    }

    [Fact]
    public async Task ExpiredSnapshot_DoesNotAllowReservation()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new ProviderQuotaStore(dataSource);
        await store.EnsureReservationTableAsync();

        var accountId = 900_000_000L + Random.Shared.Next(1, 90_000_000);
        try
        {
            // Create a snapshot that's already expired
            await store.RefreshAsync(accountId, _ => new ProviderQuotaUpdate(
                "paid", 50m, DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddHours(-1),
                "seed", DateTime.UtcNow.AddMinutes(-5))); // expired 5 minutes ago

            var reservation = await store.TryReserveAsync(accountId, 0.01m);
            Assert.Equal(QuotaReservationStatus.Expired, reservation.Status);
            Assert.Null(reservation.LeaseId);
        }
        finally
        {
            await CleanupAsync(dataSource, accountId);
        }
    }

    [Fact]
    public async Task InsufficientQuota_FailsReservation()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new ProviderQuotaStore(dataSource);
        await store.EnsureReservationTableAsync();

        var accountId = 900_000_000L + Random.Shared.Next(1, 90_000_000);
        try
        {
            await store.RefreshAsync(accountId, _ => new ProviderQuotaUpdate(
                "paid", 0.005m, DateTime.UtcNow, DateTime.UtcNow.AddHours(1),
                "seed", DateTime.UtcNow.AddHours(2)));

            var reservation = await store.TryReserveAsync(accountId, 1.0m);
            Assert.Equal(QuotaReservationStatus.InsufficientQuota, reservation.Status);
            Assert.Null(reservation.LeaseId);
        }
        finally
        {
            await CleanupAsync(dataSource, accountId);
        }
    }

    [Fact]
    public async Task SuccessfulReservationAndSettlement_AdjustQuotaCorrectly()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new ProviderQuotaStore(dataSource);
        await store.EnsureReservationTableAsync();

        var accountId = 900_000_000L + Random.Shared.Next(1, 90_000_000);
        try
        {
            await store.RefreshAsync(accountId, _ => new ProviderQuotaUpdate(
                "paid", 1.0m, DateTime.UtcNow, DateTime.UtcNow.AddHours(1),
                "seed", DateTime.UtcNow.AddHours(2)));

            // Reserve 0.1
            var reservation = await store.TryReserveAsync(accountId, 0.1m);
            Assert.Equal(QuotaReservationStatus.Reserved, reservation.Status);
            Assert.NotNull(reservation.LeaseId);

            // Verify remaining decreased
            var afterReserve = await store.GetAsync(accountId);
            Assert.NotNull(afterReserve);
            Assert.Equal(0.9m, afterReserve!.RemainingQuota);

            // Settle with actual cost 0.05 (should return 0.05 difference)
            var settlement = await store.SettleAsync(
                accountId, reservation.LeaseId!, 0.05m,
                QuotaSettlementOutcome.Success);
            Assert.True(settlement.Applied);
            Assert.Equal(0.95m, settlement.RemainingAfter);
        }
        finally
        {
            await CleanupAsync(dataSource, accountId);
        }
    }

    [Fact]
    public async Task RejectedSettlement_ReturnsFullEstimate()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new ProviderQuotaStore(dataSource);
        await store.EnsureReservationTableAsync();

        var accountId = 900_000_000L + Random.Shared.Next(1, 90_000_000);
        try
        {
            await store.RefreshAsync(accountId, _ => new ProviderQuotaUpdate(
                "paid", 1.0m, DateTime.UtcNow, DateTime.UtcNow.AddHours(1),
                "seed", DateTime.UtcNow.AddHours(2)));

            var reservation = await store.TryReserveAsync(accountId, 0.2m);
            Assert.Equal(QuotaReservationStatus.Reserved, reservation.Status);

            // Reject: full estimate returned
            var settlement = await store.SettleAsync(
                accountId, reservation.LeaseId!, 0m,
                QuotaSettlementOutcome.Rejected);
            Assert.True(settlement.Applied);
            Assert.Equal(1.0m, settlement.RemainingAfter);
        }
        finally
        {
            await CleanupAsync(dataSource, accountId);
        }
    }

    [Fact]
    public async Task Backoff_AffectsSchedulability()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new ProviderQuotaStore(dataSource);
        await store.EnsureReservationTableAsync();

        var accountId = 900_000_000L + Random.Shared.Next(1, 90_000_000);
        try
        {
            await store.RefreshAsync(accountId, _ => new ProviderQuotaUpdate(
                "paid", 10m, DateTime.UtcNow, DateTime.UtcNow.AddHours(1),
                "seed", DateTime.UtcNow.AddHours(2)));

            // Record backoff
            await store.RecordBackoffAsync(accountId, TimeSpan.FromMinutes(5));

            // Reservation should fail due to cooldown
            var reservation = await store.TryReserveAsync(accountId, 0.01m);
            Assert.Equal(QuotaReservationStatus.Cooldown, reservation.Status);
            Assert.Null(reservation.LeaseId);

            // Snapshot should reflect cooldown
            var snapshot = await store.GetAsync(accountId);
            Assert.NotNull(snapshot);
            Assert.NotNull(snapshot!.CooldownUntil);
            Assert.True(snapshot.CooldownUntil > DateTime.UtcNow);
        }
        finally
        {
            await CleanupAsync(dataSource, accountId);
        }
    }

    [Fact]
    public async Task FreeTier_AllowsReservationWithoutDeduction()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new ProviderQuotaStore(dataSource);
        await store.EnsureReservationTableAsync();

        var accountId = 900_000_000L + Random.Shared.Next(1, 90_000_000);
        try
        {
            await store.RefreshAsync(accountId, _ => new ProviderQuotaUpdate(
                "free", null, DateTime.UtcNow, DateTime.UtcNow.AddHours(1),
                "seed", DateTime.UtcNow.AddHours(2)));

            var reservation = await store.TryReserveAsync(accountId, 0.01m);
            Assert.Equal(QuotaReservationStatus.Reserved, reservation.Status);
            // Free tier doesn't deduct
        }
        finally
        {
            await CleanupAsync(dataSource, accountId);
        }
    }

    [Fact]
    public async Task UnknownTier_WithNoQuota_AllowsPassThrough()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new ProviderQuotaStore(dataSource);
        await store.EnsureReservationTableAsync();

        var accountId = 900_000_000L + Random.Shared.Next(1, 90_000_000);
        try
        {
            await store.RefreshAsync(accountId, _ => new ProviderQuotaUpdate(
                "unknown", null, null, null, null, null));

            var reservation = await store.TryReserveAsync(accountId, 0.01m);
            Assert.Equal(QuotaReservationStatus.UnknownTier, reservation.Status);
        }
        finally
        {
            await CleanupAsync(dataSource, accountId);
        }
    }

    [Fact]
    public async Task RestartRecovery_DoesNotDoubleConsumeQuota()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new ProviderQuotaStore(dataSource);
        await store.EnsureReservationTableAsync();

        var accountId = 900_000_000L + Random.Shared.Next(1, 90_000_000);
        try
        {
            await store.RefreshAsync(accountId, _ => new ProviderQuotaUpdate(
                "paid", 1.0m, DateTime.UtcNow, DateTime.UtcNow.AddHours(1),
                "seed", DateTime.UtcNow.AddHours(2)));

            // Reserve
            var reservation = await store.TryReserveAsync(accountId, 0.1m);
            Assert.Equal(QuotaReservationStatus.Reserved, reservation.Status);

            // Simulate restart: settle as Unknown (holds estimate)
            var settlement = await store.SettleAsync(
                accountId, reservation.LeaseId!, 0m,
                QuotaSettlementOutcome.Unknown);
            Assert.True(settlement.Applied);

            // Remaining should still be 0.9 (estimate held, not consumed)
            var snapshot = await store.GetAsync(accountId);
            Assert.NotNull(snapshot);
            Assert.Equal(0.9m, snapshot!.RemainingQuota);

            // Trying to reserve again with the same leaseId should fail (already settled)
            var secondSettlement = await store.SettleAsync(
                accountId, reservation.LeaseId!, 0.05m,
                QuotaSettlementOutcome.Success);
            Assert.False(secondSettlement.Applied);

            // Quota unchanged
            var afterSecond = await store.GetAsync(accountId);
            Assert.Equal(0.9m, afterSecond!.RemainingQuota);
        }
        finally
        {
            await CleanupAsync(dataSource, accountId);
        }
    }

    private static async Task CleanupAsync(NpgsqlDataSource dataSource, long accountId)
    {
        try
        {
            await using var cmd = dataSource.CreateCommand();
            cmd.CommandText = """
                DELETE FROM provider_quota_reservations WHERE account_id = $1;
                DELETE FROM provider_quota_state WHERE account_id = $1;
                """;
            cmd.Parameters.AddWithValue(accountId);
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
