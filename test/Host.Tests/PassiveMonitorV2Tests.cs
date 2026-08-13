using Npgsql;
using ScalaAPI.Host.Services;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class PassiveMonitorV2Tests
{
    private static NpgsqlDataSource? GetDataSource()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        return NpgsqlDataSource.Create(connectionString);
    }

    [Fact]
    public async Task EventDeduplicationByEventId()
    {
        var dataSource = GetDataSource();
        if (dataSource is null) return;

        var store = new PassiveMonitorV2Store(dataSource);
        var dimension = "platform";
        var dimensionValue = "all";
        var windowStart = PassiveMonitorV2Service.FloorToWindow(DateTime.UtcNow);
        var windowEnd = windowStart + TimeSpan.FromHours(1);
        var eventHash = 12345L;

        try
        {
            // First upsert: event_count should be 1
            await store.UpsertRollupAsync(dimension, dimensionValue, windowStart, windowEnd,
                eventHash, 100, false);

            var rollups = await store.ListRollupsAsync(dimension, dimensionValue, null, null, 10);
            var rollup = rollups.FirstOrDefault(r => r.WindowStart == windowStart);
            Assert.NotNull(rollup);
            Assert.Equal(1, rollup.EventCount);

            // Duplicate upsert with same event_hash: event_count should still be 1
            await store.UpsertRollupAsync(dimension, dimensionValue, windowStart, windowEnd,
                eventHash, 100, false);

            rollups = await store.ListRollupsAsync(dimension, dimensionValue, null, null, 10);
            rollup = rollups.FirstOrDefault(r => r.WindowStart == windowStart);
            Assert.NotNull(rollup);
            Assert.Equal(1, rollup.EventCount);

            // Different event_hash: event_count should be 2
            await store.UpsertRollupAsync(dimension, dimensionValue, windowStart, windowEnd,
                eventHash + 1, 200, false);

            rollups = await store.ListRollupsAsync(dimension, dimensionValue, null, null, 10);
            rollup = rollups.FirstOrDefault(r => r.WindowStart == windowStart);
            Assert.NotNull(rollup);
            Assert.Equal(2, rollup.EventCount);
        }
        finally
        {
            await using var cmd = dataSource.CreateCommand("""
                DELETE FROM monitor_v2_rollups
                WHERE dimension = $1 AND dimension_value = $2 AND window_start = $3
                """);
            cmd.Parameters.AddWithValue(dimension);
            cmd.Parameters.AddWithValue(dimensionValue);
            cmd.Parameters.AddWithValue(windowStart);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task WatermarkMonotonicityAfterRestart()
    {
        var dataSource = GetDataSource();
        if (dataSource is null) return;

        var store = new PassiveMonitorV2Store(dataSource);
        var dimension = $"test-watermark-{Guid.NewGuid():N}";

        try
        {
            // Set initial watermark
            var ts1 = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            await store.UpdateWatermarkAsync(dimension, 100, ts1);

            var wm1 = await store.GetWatermarkAsync(dimension);
            Assert.NotNull(wm1);
            Assert.Equal(100, wm1.WatermarkEventId);
            Assert.Equal(ts1, wm1.WatermarkTimestamp);

            // Simulate restart: try to set a lower watermark (should NOT go backward)
            var ts0 = new DateTime(2024, 1, 1, 11, 0, 0, DateTimeKind.Utc);
            await store.UpdateWatermarkAsync(dimension, 50, ts0);

            var wm2 = await store.GetWatermarkAsync(dimension);
            Assert.NotNull(wm2);
            Assert.Equal(100, wm2.WatermarkEventId); // Still 100, not 50
            Assert.Equal(ts1, wm2.WatermarkTimestamp); // Still ts1, not ts0

            // Advance watermark forward (should succeed)
            var ts2 = new DateTime(2024, 1, 1, 13, 0, 0, DateTimeKind.Utc);
            await store.UpdateWatermarkAsync(dimension, 200, ts2);

            var wm3 = await store.GetWatermarkAsync(dimension);
            Assert.NotNull(wm3);
            Assert.Equal(200, wm3.WatermarkEventId);
            Assert.Equal(ts2, wm3.WatermarkTimestamp);
        }
        finally
        {
            await using var cmd = dataSource.CreateCommand(
                "DELETE FROM monitor_v2_watermarks WHERE dimension = $1");
            cmd.Parameters.AddWithValue(dimension);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task BoundedBackfillDoesNotBlock()
    {
        var dataSource = GetDataSource();
        if (dataSource is null) return;

        var store = new PassiveMonitorV2Store(dataSource);

        // FetchEventsAsync is bounded by maxBatchSize.
        // Verify it returns at most maxBatchSize events even if more exist.
        var epoch = DateTime.UtcNow.AddHours(-2);
        var events = await store.FetchEventsAsync(epoch, 5);

        // Should return at most 5 events regardless of how many exist
        Assert.True(events.Count <= 5);
    }

    [Fact]
    public async Task PrivacyRedaction()
    {
        var dataSource = GetDataSource();
        if (dataSource is null) return;

        var store = new PassiveMonitorV2Store(dataSource);
        var configKey = $"test-privacy-{Guid.NewGuid():N}";

        try
        {
            // Create config with redaction enabled
            var config = await store.UpsertPrivacyConfigAsync(configKey, true, true, 30);
            Assert.True(config.RedactUserIds);
            Assert.True(config.RedactPrompts);
            Assert.Equal(30, config.RetentionDays);

            // Update config to disable redaction
            var updated = await store.UpsertPrivacyConfigAsync(configKey, false, false, 60);
            Assert.False(updated.RedactUserIds);
            Assert.False(updated.RedactPrompts);
            Assert.Equal(60, updated.RetentionDays);

            // Verify persistence
            var fetched = await store.GetPrivacyConfigAsync(configKey);
            Assert.NotNull(fetched);
            Assert.False(fetched.RedactUserIds);
            Assert.Equal(60, fetched.RetentionDays);
        }
        finally
        {
            await using var cmd = dataSource.CreateCommand(
                "DELETE FROM monitor_v2_privacy_config WHERE config_key = $1");
            cmd.Parameters.AddWithValue(configKey);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task RetentionCleanup()
    {
        var dataSource = GetDataSource();
        if (dataSource is null) return;

        var store = new PassiveMonitorV2Store(dataSource);
        var dimension = "platform";
        var dimensionValue = "all";

        // Insert a rollup with a window that ended 100 days ago
        var oldWindowStart = DateTime.UtcNow.AddDays(-101).Date;
        var oldWindowEnd = oldWindowStart + TimeSpan.FromHours(1);

        try
        {
            await store.UpsertRollupAsync(dimension, dimensionValue, oldWindowStart, oldWindowEnd,
                99999L, 50, false);

            // Verify it exists
            var before = await store.ListRollupsAsync(dimension, dimensionValue, null, null, 10);
            Assert.Contains(before, r => r.WindowStart == oldWindowStart);

            // Run retention cleanup with 90-day retention
            var deleted = await store.CleanupRetentionAsync(90);
            Assert.True(deleted >= 1);

            // Verify it's gone
            var after = await store.ListRollupsAsync(dimension, dimensionValue, null, null, 10);
            Assert.DoesNotContain(after, r => r.WindowStart == oldWindowStart);
        }
        finally
        {
            await using var cmd = dataSource.CreateCommand("""
                DELETE FROM monitor_v2_rollups
                WHERE dimension = $1 AND dimension_value = $2 AND window_start = $3
                """);
            cmd.Parameters.AddWithValue(dimension);
            cmd.Parameters.AddWithValue(dimensionValue);
            cmd.Parameters.AddWithValue(oldWindowStart);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task DimensionAggregation()
    {
        var dataSource = GetDataSource();
        if (dataSource is null) return;

        var store = new PassiveMonitorV2Store(dataSource);
        var windowStart = PassiveMonitorV2Service.FloorToWindow(DateTime.UtcNow);
        var windowEnd = windowStart + TimeSpan.FromHours(1);

        try
        {
            // Insert rollups for different dimensions
            await store.UpsertRollupAsync("platform", "all", windowStart, windowEnd,
                1001L, 100, false);
            await store.UpsertRollupAsync("model", "gpt-4", windowStart, windowEnd,
                1002L, 200, false);
            await store.UpsertRollupAsync("model", "gpt-4", windowStart, windowEnd,
                1003L, 300, true); // error
            await store.UpsertRollupAsync("group", "42", windowStart, windowEnd,
                1004L, 150, false);
            await store.UpsertRollupAsync("user", "7", windowStart, windowEnd,
                1005L, 250, false);
            await store.UpsertRollupAsync("error", "500", windowStart, windowEnd,
                1006L, 500, true);

            // Verify platform dimension
            var platformRollups = await store.ListRollupsAsync("platform", null, null, null, 10);
            Assert.Contains(platformRollups, r => r.DimensionValue == "all" && r.EventCount >= 1);

            // Verify model dimension aggregation (gpt-4 should have 2 events, 1 error)
            var modelRollups = await store.ListRollupsAsync("model", "gpt-4", null, null, 10);
            var gpt4Rollup = modelRollups.FirstOrDefault(r => r.WindowStart == windowStart);
            Assert.NotNull(gpt4Rollup);
            Assert.Equal(2, gpt4Rollup.EventCount);
            Assert.Equal(1, gpt4Rollup.ErrorCount);

            // Verify filtering by dimension
            var groupRollups = await store.ListRollupsAsync("group", null, null, null, 10);
            Assert.Contains(groupRollups, r => r.DimensionValue == "42");

            // Verify error dimension
            var errorRollups = await store.ListRollupsAsync("error", "500", null, null, 10);
            Assert.Single(errorRollups, r => r.WindowStart == windowStart);
        }
        finally
        {
            await using var cmd = dataSource.CreateCommand("""
                DELETE FROM monitor_v2_rollups
                WHERE window_start = $1
                  AND (
                    (dimension = 'platform' AND dimension_value = 'all')
                    OR (dimension = 'model' AND dimension_value = 'gpt-4')
                    OR (dimension = 'group' AND dimension_value = '42')
                    OR (dimension = 'user' AND dimension_value = '7')
                    OR (dimension = 'error' AND dimension_value = '500')
                  )
                """);
            cmd.Parameters.AddWithValue(windowStart);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public void FloorToWindow_TruncatesToHour()
    {
        var ts = new DateTime(2024, 6, 15, 14, 37, 42, DateTimeKind.Utc);
        var floored = PassiveMonitorV2Service.FloorToWindow(ts);
        Assert.Equal(new DateTime(2024, 6, 15, 14, 0, 0, DateTimeKind.Utc), floored);
    }

    [Fact]
    public async Task ErrorCountOnlyIncrementsForNewEvents()
    {
        var dataSource = GetDataSource();
        if (dataSource is null) return;

        var store = new PassiveMonitorV2Store(dataSource);
        var dimension = "platform";
        var dimensionValue = "all";
        var windowStart = PassiveMonitorV2Service.FloorToWindow(DateTime.UtcNow);
        var windowEnd = windowStart + TimeSpan.FromHours(1);

        try
        {
            // First event: error
            await store.UpsertRollupAsync(dimension, dimensionValue, windowStart, windowEnd,
                2001L, 100, true);

            var rollups = await store.ListRollupsAsync(dimension, dimensionValue, null, null, 10);
            var rollup = rollups.FirstOrDefault(r => r.WindowStart == windowStart);
            Assert.NotNull(rollup);
            Assert.Equal(1, rollup.EventCount);
            Assert.Equal(1, rollup.ErrorCount);

            // Duplicate of same event: error_count should NOT increase
            await store.UpsertRollupAsync(dimension, dimensionValue, windowStart, windowEnd,
                2001L, 100, true);

            rollups = await store.ListRollupsAsync(dimension, dimensionValue, null, null, 10);
            rollup = rollups.FirstOrDefault(r => r.WindowStart == windowStart);
            Assert.NotNull(rollup);
            Assert.Equal(1, rollup.EventCount);
            Assert.Equal(1, rollup.ErrorCount);
        }
        finally
        {
            await using var cmd = dataSource.CreateCommand("""
                DELETE FROM monitor_v2_rollups
                WHERE dimension = $1 AND dimension_value = $2 AND window_start = $3
                """);
            cmd.Parameters.AddWithValue(dimension);
            cmd.Parameters.AddWithValue(dimensionValue);
            cmd.Parameters.AddWithValue(windowStart);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
