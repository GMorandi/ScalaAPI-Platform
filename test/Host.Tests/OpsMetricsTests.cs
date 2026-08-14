using System.Text.Json;
using Npgsql;
using ScalaAPI.Host.Services;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class OpsMetricsTests
{
    private static NpgsqlDataSource? GetDataSource()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        return NpgsqlDataSource.Create(connectionString);
    }

    [Fact]
    public void SensitiveValueFiltering_RemovesPromptsAndSecrets()
    {
        // Labels with sensitive values should be filtered out
        var labels = JsonDocument.Parse("""
        {
            "source": "gateway",
            "model": "gpt-4",
            "prompt": "user secret data",
            "api_key": "sk-12345",
            "region": "us-east-1",
            "user_data": "personal info"
        }
        """).RootElement;

        var filtered = OpsMetricsSampleStore.FilterLabels(labels);
        Assert.NotNull(filtered);
        Assert.Equal(JsonValueKind.Object, filtered.Value.ValueKind);

        // Allowed keys should be present
        Assert.True(filtered.Value.TryGetProperty("source", out _));
        Assert.True(filtered.Value.TryGetProperty("model", out _));
        Assert.True(filtered.Value.TryGetProperty("region", out _));

        // Sensitive keys should be removed (not in allowed set)
        Assert.False(filtered.Value.TryGetProperty("prompt", out _));
        Assert.False(filtered.Value.TryGetProperty("api_key", out _));
        Assert.False(filtered.Value.TryGetProperty("user_data", out _));
    }

    [Fact]
    public void SensitiveValueFiltering_RejectsSensitiveValuesInAllowedKeys()
    {
        // Even allowed keys should be rejected if their values contain sensitive patterns
        var labels = JsonDocument.Parse("""
        {
            "source": "gateway",
            "model": "password123",
            "region": "us-east-1"
        }
        """).RootElement;

        var filtered = OpsMetricsSampleStore.FilterLabels(labels);
        Assert.NotNull(filtered);

        // "model" value contains "password" which matches the sensitive pattern
        Assert.False(filtered.Value.TryGetProperty("model", out _));

        // Clean values should remain
        Assert.True(filtered.Value.TryGetProperty("source", out _));
        Assert.True(filtered.Value.TryGetProperty("region", out _));
    }

    [Fact]
    public void SensitiveValueFiltering_AllowsCleanLabels()
    {
        var labels = JsonDocument.Parse("""
        {
            "source": "provider",
            "region": "eu-west-1",
            "provider_name": "openai",
            "component": "dispatch"
        }
        """).RootElement;

        var filtered = OpsMetricsSampleStore.FilterLabels(labels);
        Assert.NotNull(filtered);
        Assert.True(filtered.Value.TryGetProperty("source", out _));
        Assert.True(filtered.Value.TryGetProperty("region", out _));
        Assert.True(filtered.Value.TryGetProperty("provider_name", out _));
        Assert.True(filtered.Value.TryGetProperty("component", out _));
    }

    [Fact]
    public void IsMetricNameSafe_RejectsSensitiveNames()
    {
        Assert.True(OpsMetricsSampleStore.IsMetricNameSafe("gateway.request.latency_ms"));
        Assert.True(OpsMetricsSampleStore.IsMetricNameSafe("platform.active_leases"));
        Assert.True(OpsMetricsSampleStore.IsMetricNameSafe("provider.unavailable_percent"));

        // Names with sensitive patterns should be rejected
        Assert.False(OpsMetricsSampleStore.IsMetricNameSafe("user.password_hash"));
        Assert.False(OpsMetricsSampleStore.IsMetricNameSafe("api.secret_key"));

        // Names with invalid characters should be rejected
        Assert.False(OpsMetricsSampleStore.IsMetricNameSafe("metric with spaces"));
        Assert.False(OpsMetricsSampleStore.IsMetricNameSafe("metric;drop table"));
    }

    [Fact]
    public async Task P95Computation()
    {
        var dataSource = GetDataSource();
        if (dataSource is null) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        var store = new OpsMetricsSampleStore(dataSource);
        var metricName = $"test.p95.{Guid.NewGuid():N}";
        var labels = JsonDocument.Parse("""{"source":"test"}""").RootElement;

        try
        {
            // Insert 20 samples with values 1-20
            for (var i = 1; i <= 20; i++)
            {
                await store.InsertSampleAsync(metricName, labels, i, null, null);
            }

            var summary = await store.GetSummaryAsync(metricName, null, null, 5.0m);
            var item = Assert.Single(summary);
            Assert.Equal(metricName, item.MetricName);
            Assert.Equal(20, item.SampleCount);

            // P95 should be approximately 19 (the 95th percentile of 1-20)
            Assert.True(item.P95Value >= 18 && item.P95Value <= 20,
                $"P95 should be ~19, got {item.P95Value}");
        }
        finally
        {
            await using var cmd = dataSource.CreateCommand(
                "DELETE FROM ops_metrics_samples WHERE metric_name = $1");
            cmd.Parameters.AddWithValue(metricName);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task RetentionCleanup()
    {
        var dataSource = GetDataSource();
        if (dataSource is null) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        var store = new OpsMetricsSampleStore(dataSource);
        var metricName = $"test.retention.{Guid.NewGuid():N}";
        var labels = JsonDocument.Parse("""{"source":"test"}""").RootElement;

        try
        {
            // Insert a sample
            await store.InsertSampleAsync(metricName, labels, 42, null, null);

            // Verify it exists
            var samples = await store.ListSamplesAsync(metricName, null, null, 10);
            Assert.Single(samples);

            // Cleanup with 0-second retention (delete everything)
            var deleted = await store.CleanupRetentionAsync(TimeSpan.FromSeconds(0));
            // At least our sample should be deleted
            Assert.True(deleted >= 1);

            // Verify it's gone
            samples = await store.ListSamplesAsync(metricName, null, null, 10);
            Assert.Empty(samples);
        }
        finally
        {
            await using var cmd = dataSource.CreateCommand(
                "DELETE FROM ops_metrics_samples WHERE metric_name = $1");
            cmd.Parameters.AddWithValue(metricName);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task RequestLeaseIdCorrelation()
    {
        var dataSource = GetDataSource();
        if (dataSource is null) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        var store = new OpsMetricsSampleStore(dataSource);
        var metricName = $"test.correlation.{Guid.NewGuid():N}";
        var labels = JsonDocument.Parse("""{"source":"test"}""").RootElement;
        var requestId = $"req-{Guid.NewGuid():N}";
        var leaseId = $"lease-{Guid.NewGuid():N}";

        try
        {
            // Insert sample with request/lease IDs
            await store.InsertSampleAsync(metricName, labels, 100, requestId, leaseId);

            // Verify correlation fields are stored
            var samples = await store.ListSamplesAsync(metricName, null, null, 10);
            var sample = Assert.Single(samples);
            Assert.Equal(requestId, sample.RequestId);
            Assert.Equal(leaseId, sample.LeaseId);
        }
        finally
        {
            await using var cmd = dataSource.CreateCommand(
                "DELETE FROM ops_metrics_samples WHERE metric_name = $1");
            cmd.Parameters.AddWithValue(metricName);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public void MetricsDoNotContainSensitiveValues()
    {
        // Verify that the filtering logic ensures no prompts, secrets, or user data
        // can end up in metric labels
        var sensitiveLabels = new[]
        {
            """{"source":"gateway","prompt":"tell me a secret"}""",
            """{"source":"gateway","model":"contains-token-value"}""",
            """{"source":"gateway","endpoint":"https://api.example.com?authorization=bearer"}""",
        };

        foreach (var labelText in sensitiveLabels)
        {
            var labels = JsonDocument.Parse(labelText).RootElement;
            var filtered = OpsMetricsSampleStore.FilterLabels(labels);
            Assert.NotNull(filtered);

            // Verify no sensitive content in the filtered output
            var filteredText = filtered.Value.GetRawText();
            Assert.DoesNotContain("prompt", filteredText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", filteredText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("authorization", filteredText, StringComparison.OrdinalIgnoreCase);
        }
    }
}
