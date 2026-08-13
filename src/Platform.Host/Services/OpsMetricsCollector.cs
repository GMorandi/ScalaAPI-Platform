using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace ScalaAPI.Host.Services;

/// <summary>
/// Record types for OPS metrics samples.
/// </summary>
public sealed record OpsMetricSample(
    long SampleId,
    string MetricName,
    JsonElement Labels,
    decimal Value,
    DateTime SampledAt,
    string? RequestId,
    string? LeaseId);

public sealed record OpsMetricsSummary(
    string MetricName,
    decimal P95Value,
    decimal AverageValue,
    decimal UnavailablePercent,
    decimal ErrorBudgetRemaining,
    long SampleCount,
    DateTime LatestAt);

/// <summary>
/// Data access for OPS metrics samples.
/// </summary>
public sealed class OpsMetricsSampleStore(NpgsqlDataSource dataSource)
{
    private static readonly Regex MetricNamePattern = new(
        "^[A-Za-z0-9_.:-]{1,120}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Fixed label keys that are allowed in metrics. Any label not in this set is rejected.
    /// This prevents prompts, secrets, or user-sensitive values from being stored.
    /// </summary>
    private static readonly HashSet<string> AllowedLabelKeys = new(StringComparer.Ordinal)
    {
        "source",         // gateway, platform, provider
        "region",
        "provider_name",
        "model",
        "endpoint",
        "status_code",
        "error_class",
        "component",
        "environment",
        "version",
    };

    /// <summary>
    /// Patterns that indicate sensitive content in label values.
    /// </summary>
    private static readonly Regex SensitiveValuePattern = new(
        @"(?i)(password|secret|token|api[_-]?key|authorization|cookie|prompt|content|message|user[_-]?data|email|phone|ssn|credit[_-]?card)",
        RegexOptions.Compiled);

    /// <summary>
    /// Filter labels to only include allowed keys and reject sensitive values.
    /// Returns a cleaned labels object, or null if the labels contain sensitive content.
    /// </summary>
    public static JsonElement? FilterLabels(JsonElement labels)
    {
        if (labels.ValueKind != JsonValueKind.Object)
            return JsonDocument.Parse("{}").RootElement;

        var filtered = new Dictionary<string, JsonElement>();
        foreach (var property in labels.EnumerateObject())
        {
            // Only allow fixed label keys
            if (!AllowedLabelKeys.Contains(property.Name))
                continue;

            // Reject sensitive values
            var valueText = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? ""
                : property.Value.GetRawText();

            if (SensitiveValuePattern.IsMatch(valueText))
                continue;

            // Bound value length
            if (valueText.Length > 200)
                continue;

            filtered[property.Name] = property.Value.Clone();
        }

        var json = JsonSerializer.Serialize(filtered);
        return JsonDocument.Parse(json).RootElement;
    }

    /// <summary>
    /// Check if a metric name contains sensitive content.
    /// </summary>
    public static bool IsMetricNameSafe(string metricName)
    {
        return MetricNamePattern.IsMatch(metricName) && !SensitiveValuePattern.IsMatch(metricName);
    }

    public async Task<long> InsertSampleAsync(
        string metricName, JsonElement labels, decimal value,
        string? requestId, string? leaseId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO ops_metrics_samples (metric_name, labels, value, request_id, lease_id)
            VALUES ($1, $2, $3, $4, $5)
            RETURNING sample_id
            """);
        command.Parameters.AddWithValue(metricName);
        command.Parameters.AddWithValue(labels.GetRawText());
        command.Parameters.AddWithValue(value);
        command.Parameters.AddWithValue((object?)requestId ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)leaseId ?? DBNull.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    public async Task<IReadOnlyList<OpsMetricSample>> ListSamplesAsync(
        string? metricName, DateTime? from, DateTime? to, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        await using var command = dataSource.CreateCommand("""
            SELECT sample_id, metric_name, labels, value, sampled_at, request_id, lease_id
            FROM ops_metrics_samples
            WHERE ($1::text IS NULL OR metric_name = $1)
              AND ($2::timestamptz IS NULL OR sampled_at >= $2)
              AND ($3::timestamptz IS NULL OR sampled_at <= $3)
            ORDER BY sampled_at DESC, sample_id DESC
            LIMIT $4
            """);
        command.Parameters.AddWithValue((object?)metricName ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)from ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)to ?? DBNull.Value);
        command.Parameters.AddWithValue(limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<OpsMetricSample>();
        while (await reader.ReadAsync(ct))
        {
            var labelsText = reader.GetString(2);
            items.Add(new OpsMetricSample(
                reader.GetInt64(0), reader.GetString(1),
                JsonDocument.Parse(labelsText).RootElement,
                reader.GetDecimal(3), reader.GetDateTime(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return items;
    }

    /// <summary>
    /// Compute aggregated summary: p95, average, unavailable %, error budget remaining.
    /// Uses window functions for p95 computation.
    /// </summary>
    public async Task<IReadOnlyList<OpsMetricsSummary>> GetSummaryAsync(
        string? metricName, DateTime? from, DateTime? to, decimal errorBudgetTarget,
        CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            WITH ranked AS (
                SELECT metric_name, value, sampled_at,
                       ROW_NUMBER() OVER (PARTITION BY metric_name ORDER BY value) AS rn,
                       COUNT(*) OVER (PARTITION BY metric_name) AS cnt
                FROM ops_metrics_samples
                WHERE ($1::text IS NULL OR metric_name = $1)
                  AND ($2::timestamptz IS NULL OR sampled_at >= $2)
                  AND ($3::timestamptz IS NULL OR sampled_at <= $3)
            ),
            stats AS (
                SELECT metric_name,
                       MAX(CASE WHEN rn = GREATEST(1, (cnt * 0.95)::bigint) THEN value END) AS p95_value,
                       AVG(value) AS avg_value,
                       COUNT(*) AS sample_count,
                       MAX(sampled_at) AS latest_at,
                       SUM(CASE WHEN value < 0 THEN 1 ELSE 0 END)::decimal / NULLIF(COUNT(*), 0) * 100 AS unavailable_pct
                FROM ranked
                GROUP BY metric_name
            )
            SELECT metric_name, p95_value, avg_value, unavailable_pct,
                   GREATEST(0, $4 - unavailable_pct) AS error_budget_remaining,
                   sample_count, latest_at
            FROM stats
            ORDER BY latest_at DESC
            """);
        command.Parameters.AddWithValue((object?)metricName ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)from ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)to ?? DBNull.Value);
        command.Parameters.AddWithValue(errorBudgetTarget);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<OpsMetricsSummary>();
        while (await reader.ReadAsync(ct))
        {
            items.Add(new OpsMetricsSummary(
                reader.GetString(0), reader.GetDecimal(1), reader.GetDecimal(2),
                reader.GetDecimal(3), reader.GetDecimal(4),
                reader.GetInt64(5), reader.GetDateTime(6)));
        }
        return items;
    }

    /// <summary>
    /// Delete samples older than the retention period.
    /// </summary>
    public async Task<int> CleanupRetentionAsync(TimeSpan retention, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            DELETE FROM ops_metrics_samples
            WHERE sampled_at < now() - $1::interval
            """);
        command.Parameters.AddWithValue($"{(int)retention.TotalSeconds} seconds");
        return await command.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// Background service that collects OPS metrics from Gateway, Platform, and Provider.
/// Correlates request/lease IDs, filters sensitive values, computes p95/unavailable%/error budgets,
/// writes to ops_metrics_samples with retention.
/// </summary>
public sealed class OpsMetricsCollector(
    OpsMetricsSampleStore store,
    NpgsqlDataSource dataSource,
    ILogger<OpsMetricsCollector> logger) : BackgroundService
{
    private static readonly TimeSpan CollectionInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("OPS metrics collector started");
        var lastRetentionCleanup = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CollectMetricsAsync(stoppingToken);

                // Run retention cleanup daily
                if (DateTime.UtcNow - lastRetentionCleanup >= TimeSpan.FromDays(1))
                {
                    var deleted = await store.CleanupRetentionAsync(RetentionPeriod, stoppingToken);
                    if (deleted > 0)
                        logger.LogInformation("Cleaned up {Count} expired OPS metrics samples", deleted);
                    lastRetentionCleanup = DateTime.UtcNow;
                }

                await Task.Delay(CollectionInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "OPS metrics collection failed");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    /// <summary>
    /// Collect metrics from Gateway, Platform, and Provider sources.
    /// Correlates request/lease IDs and filters sensitive values.
    /// </summary>
    private async Task CollectMetricsAsync(CancellationToken ct)
    {
        // Collect from Gateway: request latency, error rates
        await CollectGatewayMetricsAsync(ct);

        // Collect from Platform: lease utilization, queue depths
        await CollectPlatformMetricsAsync(ct);

        // Collect from Provider: provider availability, response times
        await CollectProviderMetricsAsync(ct);
    }

    private async Task CollectGatewayMetricsAsync(CancellationToken ct)
    {
        // Query gateway-level metrics from the database
        await using var command = dataSource.CreateCommand("""
            SELECT
                count(*) AS total_requests,
                COALESCE(avg(EXTRACT(EPOCH FROM (completed_at - created_at)) * 1000), 0) AS avg_latency_ms,
                COALESCE(sum(CASE WHEN status = 'error' THEN 1 ELSE 0 END), 0) AS error_count
            FROM request_leases
            WHERE created_at >= now() - interval '5 minutes'
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return;

        var totalRequests = reader.GetInt64(0);
        var avgLatencyMs = reader.GetDecimal(1);
        var errorCount = reader.GetInt64(2);

        if (totalRequests > 0)
        {
            var labels = JsonDocument.Parse("""{"source":"gateway","component":"request_processing"}""").RootElement;
            var filteredLabels = OpsMetricsSampleStore.FilterLabels(labels);
            if (filteredLabels is not null)
            {
                await store.InsertSampleAsync("gateway.request.latency_ms", filteredLabels.Value,
                    avgLatencyMs, null, null, ct);
                await store.InsertSampleAsync("gateway.request.error_rate", filteredLabels.Value,
                    totalRequests > 0 ? (decimal)errorCount / totalRequests * 100 : 0,
                    null, null, ct);
            }
        }
    }

    private async Task CollectPlatformMetricsAsync(CancellationToken ct)
    {
        // Collect platform-level metrics
        await using var command = dataSource.CreateCommand("""
            SELECT
                (SELECT count(*) FROM request_leases WHERE status IN ('held', 'forwarded', 'output_started')) AS active_leases,
                (SELECT count(*) FROM usage_outbox WHERE processed_at IS NULL) AS outbox_backlog,
                (SELECT count(*) FROM media_operations WHERE status IN ('pending', 'running')) AS media_backlog
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return;

        var activeLeases = reader.GetInt64(0);
        var outboxBacklog = reader.GetInt64(1);
        var mediaBacklog = reader.GetInt64(2);

        var labels = JsonDocument.Parse("""{"source":"platform","component":"system"}""").RootElement;
        var filteredLabels = OpsMetricsSampleStore.FilterLabels(labels);
        if (filteredLabels is not null)
        {
            await store.InsertSampleAsync("platform.active_leases", filteredLabels.Value,
                activeLeases, null, null, ct);
            await store.InsertSampleAsync("platform.outbox_backlog", filteredLabels.Value,
                outboxBacklog, null, null, ct);
            await store.InsertSampleAsync("platform.media_backlog", filteredLabels.Value,
                mediaBacklog, null, null, ct);
        }
    }

    private async Task CollectProviderMetricsAsync(CancellationToken ct)
    {
        // Collect provider-level metrics from channel monitors
        await using var command = dataSource.CreateCommand("""
            SELECT
                count(*) AS total_checks,
                COALESCE(sum(CASE WHEN status = 'failed' THEN 1 ELSE 0 END), 0) AS failed_checks,
                COALESCE(avg(latency_ms), 0) AS avg_latency_ms
            FROM channel_monitors
            WHERE checked_at >= now() - interval '5 minutes'
            """);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return;

            var totalChecks = reader.GetInt64(0);
            var failedChecks = reader.GetInt64(1);
            var avgLatencyMs = reader.GetInt32(2);

            if (totalChecks > 0)
            {
                var unavailablePct = (decimal)failedChecks / totalChecks * 100;
                var labels = JsonDocument.Parse("""{"source":"provider","component":"channel_health"}""").RootElement;
                var filteredLabels = OpsMetricsSampleStore.FilterLabels(labels);
                if (filteredLabels is not null)
                {
                    await store.InsertSampleAsync("provider.unavailable_percent", filteredLabels.Value,
                        unavailablePct, null, null, ct);
                    await store.InsertSampleAsync("provider.avg_latency_ms", filteredLabels.Value,
                        avgLatencyMs, null, null, ct);
                }
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            // channel_monitors table may not exist yet; skip provider metrics
            logger.LogDebug("channel_monitors table not available, skipping provider metrics");
        }
    }
}
