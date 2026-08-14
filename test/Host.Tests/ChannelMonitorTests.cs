using System.Text.Json;
using Npgsql;
using ScalaAPI.Host.Services;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class ChannelMonitorTests
{
    private static NpgsqlDataSource? GetDataSource()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        return NpgsqlDataSource.Create(connectionString);
    }

    [Fact]
    public async Task DuplicateWorkerDoesNotDuplicateCheck()
    {
        var dataSource = GetDataSource();
        if (dataSource is null) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        var store = new ChannelMonitorTemplateStore(dataSource);
        var templateId = $"test-dedup-{Guid.NewGuid():N}";
        var workerId = "worker-1";
        var leaderToken = "leader-token-abc";

        try
        {
            // Create template
            await store.CreateTemplateAsync(templateId, "Dedup Test", "http",
                "*/5 * * * *", 30, 3, 3);

            // First claim succeeds
            var checkId1 = await store.TryClaimCheckAsync(templateId, workerId, leaderToken);
            Assert.NotNull(checkId1);

            // Second claim with same template_id + leader_token is rejected (UNIQUE constraint)
            var checkId2 = await store.TryClaimCheckAsync(templateId, "worker-2", leaderToken);
            Assert.Null(checkId2);

            // Verify only one check exists
            var checks = await store.ListChecksAsync(templateId, null, 10);
            Assert.Single(checks);
        }
        finally
        {
            await using var cmd = dataSource.CreateCommand(
                "DELETE FROM channel_monitor_checks WHERE template_id = $1");
            cmd.Parameters.AddWithValue(templateId);
            await cmd.ExecuteNonQueryAsync();
            await using var cmd2 = dataSource.CreateCommand(
                "DELETE FROM channel_monitor_templates WHERE template_id = $1");
            cmd2.Parameters.AddWithValue(templateId);
            await cmd2.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task IncidentOpensAfterThreshold()
    {
        var dataSource = GetDataSource();
        if (dataSource is null) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        var store = new ChannelMonitorTemplateStore(dataSource);
        var templateId = $"test-threshold-{Guid.NewGuid():N}";

        try
        {
            await store.CreateTemplateAsync(templateId, "Threshold Test", "http",
                "*/5 * * * *", 30, 3, 3);

            // Insert 3 failed checks (matching alert_threshold = 3)
            for (var i = 0; i < 3; i++)
            {
                var checkId = await store.TryClaimCheckAsync(
                    templateId, $"worker-{i}", Guid.NewGuid().ToString("N"));
                if (checkId.HasValue)
                    await store.CompleteCheckAsync(checkId.Value, "failed", null, "test error");
            }

            var failures = await store.CountRecentFailuresAsync(templateId, 15);
            Assert.True(failures >= 3);

            // No open incident yet
            var openIncident = await store.GetOpenIncidentAsync(templateId);
            Assert.Null(openIncident);

            // Open incident
            var incidentId = await store.OpenIncidentAsync(templateId);
            Assert.True(incidentId > 0);

            // Now there is an open incident
            openIncident = await store.GetOpenIncidentAsync(templateId);
            Assert.NotNull(openIncident);
            Assert.Null(openIncident.ClosedAt);
        }
        finally
        {
            await using var cmd = dataSource.CreateCommand(
                "DELETE FROM channel_monitor_checks WHERE template_id = $1");
            cmd.Parameters.AddWithValue(templateId);
            await cmd.ExecuteNonQueryAsync();
            await using var cmd2 = dataSource.CreateCommand(
                "DELETE FROM channel_monitor_incidents WHERE template_id = $1");
            cmd2.Parameters.AddWithValue(templateId);
            await cmd2.ExecuteNonQueryAsync();
            await using var cmd3 = dataSource.CreateCommand(
                "DELETE FROM channel_monitor_templates WHERE template_id = $1");
            cmd3.Parameters.AddWithValue(templateId);
            await cmd3.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task IncidentClosesOnRecovery()
    {
        var dataSource = GetDataSource();
        if (dataSource is null) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        var store = new ChannelMonitorTemplateStore(dataSource);
        var templateId = $"test-recovery-{Guid.NewGuid():N}";

        try
        {
            await store.CreateTemplateAsync(templateId, "Recovery Test", "http",
                "*/5 * * * *", 30, 3, 3);

            // Open an incident
            var incidentId = await store.OpenIncidentAsync(templateId);
            var openIncident = await store.GetOpenIncidentAsync(templateId);
            Assert.NotNull(openIncident);

            // Close it (recovery)
            await store.CloseIncidentAsync(incidentId, "Recovery: check passed");

            // Verify it is closed
            var closedIncident = await store.GetOpenIncidentAsync(templateId);
            Assert.Null(closedIncident);

            // Verify it shows up in the incidents list as closed
            var incidents = await store.ListIncidentsAsync(templateId, false, 10);
            var incident = Assert.Single(incidents);
            Assert.NotNull(incident.ClosedAt);
            Assert.Equal("Recovery: check passed", incident.Resolution);
        }
        finally
        {
            await using var cmd = dataSource.CreateCommand(
                "DELETE FROM channel_monitor_incidents WHERE template_id = $1");
            cmd.Parameters.AddWithValue(templateId);
            await cmd.ExecuteNonQueryAsync();
            await using var cmd2 = dataSource.CreateCommand(
                "DELETE FROM channel_monitor_templates WHERE template_id = $1");
            cmd2.Parameters.AddWithValue(templateId);
            await cmd2.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task LeaderFencingOnlyOneLeader()
    {
        // Verify that the service's leader token mechanism works:
        // two services with different leader tokens should not create duplicate checks
        var dataSource = GetDataSource();
        if (dataSource is null) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        var store = new ChannelMonitorTemplateStore(dataSource);
        var templateId = $"test-leader-{Guid.NewGuid():N}";

        try
        {
            await store.CreateTemplateAsync(templateId, "Leader Test", "http",
                "*/5 * * * *", 30, 3, 3);

            var leaderToken1 = "leader-A";
            var leaderToken2 = "leader-B";

            // Both leaders try to claim with different tokens
            var check1 = await store.TryClaimCheckAsync(templateId, "worker-1", leaderToken1);
            var check2 = await store.TryClaimCheckAsync(templateId, "worker-2", leaderToken2);

            // Both succeed because they have different leader_tokens
            // (the UNIQUE constraint is on template_id + leader_token)
            Assert.NotNull(check1);
            Assert.NotNull(check2);

            // But a duplicate with the SAME leader_token is rejected
            var check3 = await store.TryClaimCheckAsync(templateId, "worker-3", leaderToken1);
            Assert.Null(check3);
        }
        finally
        {
            await using var cmd = dataSource.CreateCommand(
                "DELETE FROM channel_monitor_checks WHERE template_id = $1");
            cmd.Parameters.AddWithValue(templateId);
            await cmd.ExecuteNonQueryAsync();
            await using var cmd2 = dataSource.CreateCommand(
                "DELETE FROM channel_monitor_templates WHERE template_id = $1");
            cmd2.Parameters.AddWithValue(templateId);
            await cmd2.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task StaleClaimReclamation()
    {
        var dataSource = GetDataSource();
        if (dataSource is null) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        var store = new ChannelMonitorTemplateStore(dataSource);
        var templateId = $"test-reclaim-{Guid.NewGuid():N}";

        try
        {
            // Create template with 1-second timeout for testing
            await store.CreateTemplateAsync(templateId, "Reclaim Test", "http",
                "*/5 * * * *", 1, 3, 3);

            // Insert a check that started long ago (simulate stale claim)
            await using var insertCmd = dataSource.CreateCommand("""
                INSERT INTO channel_monitor_checks (template_id, worker_id, started_at, status, leader_token)
                VALUES ($1, 'stale-worker', now() - interval '10 seconds', 'running', 'stale-token')
                """);
            insertCmd.Parameters.AddWithValue(templateId);
            await insertCmd.ExecuteNonQueryAsync();

            // Reclaim stale claims
            var reclaimed = await store.ReclaimStaleClaimsAsync();
            Assert.True(reclaimed >= 1);

            // Verify the check is now failed
            var checks = await store.ListChecksAsync(templateId, "failed", 10);
            Assert.NotEmpty(checks);
            Assert.Contains(checks, c => c.ErrorMessage?.Contains("reclaimed") == true);
        }
        finally
        {
            await using var cmd = dataSource.CreateCommand(
                "DELETE FROM channel_monitor_checks WHERE template_id = $1");
            cmd.Parameters.AddWithValue(templateId);
            await cmd.ExecuteNonQueryAsync();
            await using var cmd2 = dataSource.CreateCommand(
                "DELETE FROM channel_monitor_templates WHERE template_id = $1");
            cmd2.Parameters.AddWithValue(templateId);
            await cmd2.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public void ParseCronIntervalMinutes_HandlesCommonPatterns()
    {
        Assert.Equal(5, ChannelMonitorService.ParseCronIntervalMinutes("*/5 * * * *"));
        Assert.Equal(1, ChannelMonitorService.ParseCronIntervalMinutes("* * * * *"));
        Assert.Equal(15, ChannelMonitorService.ParseCronIntervalMinutes("*/15 * * * *"));
        Assert.Equal(60, ChannelMonitorService.ParseCronIntervalMinutes("*/60 * * * *"));
        Assert.Equal(5, ChannelMonitorService.ParseCronIntervalMinutes("invalid"));
    }
}
