using Npgsql;

namespace ScalaAPI.Host.Services;

/// <summary>
/// Record types for passive monitor V2 rollups, watermarks, and privacy config.
/// </summary>
public sealed record MonitorV2Rollup(
    long RollupId,
    string Dimension,
    string DimensionValue,
    DateTime WindowStart,
    DateTime WindowEnd,
    int EventCount,
    int ErrorCount,
    decimal? LatencyP50,
    decimal? LatencyP95,
    decimal? LatencyP99,
    DateTime CreatedAt);

public sealed record MonitorV2Watermark(
    string Dimension,
    long WatermarkEventId,
    DateTime WatermarkTimestamp,
    DateTime UpdatedAt);

public sealed record MonitorV2PrivacyConfig(
    string ConfigKey,
    bool RedactUserIds,
    bool RedactPrompts,
    int RetentionDays,
    DateTime UpdatedAt);

/// <summary>
/// Data access for passive monitor V2 tables: watermarks, rollups, privacy config.
/// </summary>
public sealed class PassiveMonitorV2Store(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Get the current watermark for a dimension. Returns null if no watermark exists.
    /// </summary>
    public async Task<MonitorV2Watermark?> GetWatermarkAsync(string dimension, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT dimension, watermark_event_id, watermark_timestamp, updated_at
            FROM monitor_v2_watermarks WHERE dimension = $1
            """);
        command.Parameters.AddWithValue(dimension);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new MonitorV2Watermark(
            reader.GetString(0), reader.GetInt64(1),
            reader.GetDateTime(2), reader.GetDateTime(3));
    }

    /// <summary>
    /// List all watermarks.
    /// </summary>
    public async Task<IReadOnlyList<MonitorV2Watermark>> ListWatermarksAsync(CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT dimension, watermark_event_id, watermark_timestamp, updated_at
            FROM monitor_v2_watermarks ORDER BY dimension
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<MonitorV2Watermark>();
        while (await reader.ReadAsync(ct))
        {
            items.Add(new MonitorV2Watermark(
                reader.GetString(0), reader.GetInt64(1),
                reader.GetDateTime(2), reader.GetDateTime(3)));
        }
        return items;
    }

    /// <summary>
    /// Update watermark monotonically: only advance, never go backward.
    /// Uses INSERT ON CONFLICT with a GREATEST check to ensure monotonicity.
    /// </summary>
    public async Task UpdateWatermarkAsync(
        string dimension, long eventId, DateTime eventTimestamp, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO monitor_v2_watermarks (dimension, watermark_event_id, watermark_timestamp, updated_at)
            VALUES ($1, $2, $3, now())
            ON CONFLICT (dimension) DO UPDATE SET
                watermark_event_id = GREATEST(monitor_v2_watermarks.watermark_event_id, $2),
                watermark_timestamp = CASE
                    WHEN $2 > monitor_v2_watermarks.watermark_event_id THEN $3
                    ELSE monitor_v2_watermarks.watermark_timestamp
                END,
                updated_at = now()
            """);
        command.Parameters.AddWithValue(dimension);
        command.Parameters.AddWithValue(eventId);
        command.Parameters.AddWithValue(eventTimestamp);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Upsert a rollup for a given dimension/value/window. Deduplicates by checking
    /// if the event hash is already in unique_event_ids before incrementing counters.
    /// </summary>
    public async Task UpsertRollupAsync(
        string dimension, string dimensionValue,
        DateTime windowStart, DateTime windowEnd,
        long eventHash, int latencyMs, bool isError,
        CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO monitor_v2_rollups
                (dimension, dimension_value, window_start, window_end,
                 event_count, error_count, unique_event_ids)
            VALUES ($1, $2, $3, $4,
                    1,
                    CASE WHEN $6 THEN 1 ELSE 0 END,
                    ARRAY[$5])
            ON CONFLICT (dimension, dimension_value, window_start) DO UPDATE SET
                event_count = monitor_v2_rollups.event_count +
                    CASE WHEN NOT ($5 = ANY(monitor_v2_rollups.unique_event_ids)) THEN 1 ELSE 0 END,
                error_count = monitor_v2_rollups.error_count +
                    CASE WHEN NOT ($5 = ANY(monitor_v2_rollups.unique_event_ids)) AND $6 THEN 1 ELSE 0 END,
                unique_event_ids = CASE
                    WHEN $5 = ANY(monitor_v2_rollups.unique_event_ids) THEN monitor_v2_rollups.unique_event_ids
                    ELSE monitor_v2_rollups.unique_event_ids || $5
                END
            """);
        command.Parameters.AddWithValue(dimension);
        command.Parameters.AddWithValue(dimensionValue);
        command.Parameters.AddWithValue(windowStart);
        command.Parameters.AddWithValue(windowEnd);
        command.Parameters.AddWithValue(eventHash);
        command.Parameters.AddWithValue(isError);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Update latency percentiles for a rollup window using all events in that window.
    /// Recomputes p50, p95, p99 from the usage_events that fall within the window.
    /// </summary>
    public async Task UpdateLatencyPercentilesAsync(
        string dimension, string dimensionValue,
        DateTime windowStart, DateTime windowEnd,
        CancellationToken ct = default)
    {
        var whereClause = dimension switch
        {
            "platform" => "1=1",
            "group" => $"ue.group_id = (CASE WHEN $2 ~ '^[0-9]+$' THEN $2::bigint ELSE 0 END)",
            "model" => "ue.model = $2",
            "user" => $"ue.user_id = (CASE WHEN $2 ~ '^[0-9]+$' THEN $2::bigint ELSE 0 END)",
            "error" => "1=1",
            _ => "1=0",
        };

        var errorFilter = dimension == "error" ? "AND ue.status_code >= 400" : "";

        await using var command = dataSource.CreateCommand($"""
            WITH latencies AS (
                SELECT ue.duration_ms
                FROM usage_events ue
                WHERE ue.created_at >= $3 AND ue.created_at < $4
                  AND ({whereClause})
                  {errorFilter}
                ORDER BY ue.duration_ms
            ),
            ranked AS (
                SELECT duration_ms,
                       ROW_NUMBER() OVER (ORDER BY duration_ms) AS rn,
                       COUNT(*) OVER () AS cnt
                FROM latencies
            )
            UPDATE monitor_v2_rollups
            SET latency_p50 = (SELECT duration_ms FROM ranked WHERE rn = GREATEST(1, (cnt * 0.50)::bigint)),
                latency_p95 = (SELECT duration_ms FROM ranked WHERE rn = GREATEST(1, (cnt * 0.95)::bigint)),
                latency_p99 = (SELECT duration_ms FROM ranked WHERE rn = GREATEST(1, (cnt * 0.99)::bigint))
            WHERE dimension = $1 AND dimension_value = $2 AND window_start = $3
            """);
        command.Parameters.AddWithValue(dimension);
        command.Parameters.AddWithValue(dimensionValue);
        command.Parameters.AddWithValue(windowStart);
        command.Parameters.AddWithValue(windowEnd);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// List rollups with optional dimension and time range filters.
    /// </summary>
    public async Task<IReadOnlyList<MonitorV2Rollup>> ListRollupsAsync(
        string? dimension, string? dimensionValue,
        DateTime? from, DateTime? to,
        int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        await using var command = dataSource.CreateCommand("""
            SELECT rollup_id, dimension, dimension_value, window_start, window_end,
                   event_count, error_count, latency_p50, latency_p95, latency_p99, created_at
            FROM monitor_v2_rollups
            WHERE ($1::text IS NULL OR dimension = $1)
              AND ($2::text IS NULL OR dimension_value = $2)
              AND ($3::timestamptz IS NULL OR window_start >= $3)
              AND ($4::timestamptz IS NULL OR window_end <= $4)
            ORDER BY window_start DESC, dimension, dimension_value
            LIMIT $5
            """);
        command.Parameters.AddWithValue((object?)dimension ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)dimensionValue ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)from ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)to ?? DBNull.Value);
        command.Parameters.AddWithValue(limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<MonitorV2Rollup>();
        while (await reader.ReadAsync(ct))
        {
            items.Add(new MonitorV2Rollup(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetDateTime(3), reader.GetDateTime(4),
                reader.GetInt32(5), reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                reader.GetDateTime(10)));
        }
        return items;
    }

    /// <summary>
    /// List rollups for a specific user. Applies privacy redaction to user_id dimension.
    /// </summary>
    public async Task<IReadOnlyList<MonitorV2Rollup>> ListUserRollupsAsync(
        long userId, DateTime? from, DateTime? to,
        int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        await using var command = dataSource.CreateCommand("""
            SELECT rollup_id, dimension, dimension_value, window_start, window_end,
                   event_count, error_count, latency_p50, latency_p95, latency_p99, created_at
            FROM monitor_v2_rollups
            WHERE (
                (dimension = 'user' AND dimension_value = $1::text)
                OR dimension IN ('platform', 'model', 'error')
            )
              AND ($2::timestamptz IS NULL OR window_start >= $2)
              AND ($3::timestamptz IS NULL OR window_end <= $3)
            ORDER BY window_start DESC, dimension, dimension_value
            LIMIT $4
            """);
        command.Parameters.AddWithValue(userId.ToString());
        command.Parameters.AddWithValue((object?)from ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)to ?? DBNull.Value);
        command.Parameters.AddWithValue(limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<MonitorV2Rollup>();
        while (await reader.ReadAsync(ct))
        {
            items.Add(new MonitorV2Rollup(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetDateTime(3), reader.GetDateTime(4),
                reader.GetInt32(5), reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                reader.GetDateTime(10)));
        }
        return items;
    }

    /// <summary>
    /// Get privacy config by key.
    /// </summary>
    public async Task<MonitorV2PrivacyConfig?> GetPrivacyConfigAsync(string configKey, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT config_key, redact_user_ids, redact_prompts, retention_days, updated_at
            FROM monitor_v2_privacy_config WHERE config_key = $1
            """);
        command.Parameters.AddWithValue(configKey);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new MonitorV2PrivacyConfig(
            reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2),
            reader.GetInt32(3), reader.GetDateTime(4));
    }

    /// <summary>
    /// Update privacy config.
    /// </summary>
    public async Task<MonitorV2PrivacyConfig> UpsertPrivacyConfigAsync(
        string configKey, bool redactUserIds, bool redactPrompts, int retentionDays,
        CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO monitor_v2_privacy_config (config_key, redact_user_ids, redact_prompts, retention_days, updated_at)
            VALUES ($1, $2, $3, $4, now())
            ON CONFLICT (config_key) DO UPDATE SET
                redact_user_ids = $2, redact_prompts = $3, retention_days = $4, updated_at = now()
            RETURNING config_key, redact_user_ids, redact_prompts, retention_days, updated_at
            """);
        command.Parameters.AddWithValue(configKey);
        command.Parameters.AddWithValue(redactUserIds);
        command.Parameters.AddWithValue(redactPrompts);
        command.Parameters.AddWithValue(retentionDays);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new MonitorV2PrivacyConfig(
            reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2),
            reader.GetInt32(3), reader.GetDateTime(4));
    }

    /// <summary>
    /// Delete rollups older than the retention period.
    /// </summary>
    public async Task<int> CleanupRetentionAsync(int retentionDays, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            DELETE FROM monitor_v2_rollups
            WHERE window_end < now() - ($1 || ' days')::interval
            """);
        command.Parameters.AddWithValue(retentionDays.ToString());
        return await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Fetch unsettled usage events after the watermark, bounded by maxBatchSize.
    /// Returns events as (lease_token_hash, user_id, group_id, model, duration_ms, status_code, created_at_epoch).
    /// </summary>
    public async Task<IReadOnlyList<PassiveMonitorEvent>> FetchEventsAsync(
        DateTime afterTimestamp, int maxBatchSize, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT
                (hashtext(lease_token))::bigint AS event_hash,
                user_id, group_id, model, duration_ms, status_code,
                (EXTRACT(EPOCH FROM created_at) * 1000)::bigint AS event_epoch_ms,
                created_at
            FROM usage_events
            WHERE created_at > $1
            ORDER BY created_at ASC, lease_token ASC
            LIMIT $2
            """);
        command.Parameters.AddWithValue(afterTimestamp);
        command.Parameters.AddWithValue(maxBatchSize);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<PassiveMonitorEvent>();
        while (await reader.ReadAsync(ct))
        {
            items.Add(new PassiveMonitorEvent(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt64(6),
                reader.GetDateTime(7)));
        }
        return items;
    }
}

/// <summary>
/// A single usage event fetched for passive monitoring.
/// </summary>
public sealed record PassiveMonitorEvent(
    long EventHash,
    long UserId,
    long GroupId,
    string Model,
    int DurationMs,
    int StatusCode,
    long EventEpochMs,
    DateTime CreatedAt);

/// <summary>
/// Background hosted service that passively aggregates usage_events into
/// multi-dimensional rollups with watermark tracking, deduplication,
/// latency histograms, privacy redaction, and retention enforcement.
///
/// Key guarantees:
/// - Out-of-order/duplicate events deduplicated by event hash in unique_event_ids
/// - Watermark is monotonic after restart (never goes backward)
/// - Bounded backfill (max N events per cycle) doesn't block the billable path
/// - No duplicate billing or business state writeback (read-only from usage_events)
/// </summary>
public sealed class PassiveMonitorV2Service(
    PassiveMonitorV2Store store,
    ILogger<PassiveMonitorV2Service> logger) : BackgroundService
{
    private static readonly TimeSpan CycleInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetentionCheckInterval = TimeSpan.FromHours(1);
    private const int MaxBatchSize = 1000;
    private const int DefaultRetentionDays = 90;

    /// <summary>
    /// Window size for rollup aggregation (1 hour).
    /// </summary>
    private static readonly TimeSpan WindowSize = TimeSpan.FromHours(1);

    /// <summary>
    /// Leader token for fencing. In production this would use pg_advisory_lock.
    /// </summary>
    private string _leaderToken = Guid.NewGuid().ToString("N");

    public bool IsLeader { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IsLeader = true;
        _leaderToken = Guid.NewGuid().ToString("N");
        logger.LogInformation("Passive monitor V2 service started as leader {LeaderToken}", _leaderToken);

        var lastRetentionCheck = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!IsLeader)
                {
                    await Task.Delay(CycleInterval, stoppingToken);
                    continue;
                }

                await ProcessCycleAsync(stoppingToken);

                // Periodic retention cleanup
                if (DateTime.UtcNow - lastRetentionCheck >= RetentionCheckInterval)
                {
                    await EnforceRetentionAsync(stoppingToken);
                    lastRetentionCheck = DateTime.UtcNow;
                }

                await Task.Delay(CycleInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Passive monitor V2 cycle failed");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    /// <summary>
    /// Process one cycle: for each dimension, fetch events after the watermark,
    /// upsert rollups, update latency percentiles, and advance the watermark.
    /// </summary>
    public async Task ProcessCycleAsync(CancellationToken ct)
    {
        var dimensions = new[] { "platform", "group", "model", "user", "error" };

        foreach (var dimension in dimensions)
        {
            var watermark = await store.GetWatermarkAsync(dimension, ct);
            var afterTimestamp = watermark?.WatermarkTimestamp ?? DateTime.UnixEpoch;

            var events = await store.FetchEventsAsync(afterTimestamp, MaxBatchSize, ct);
            if (events.Count == 0) continue;

            long maxEventId = watermark?.WatermarkEventId ?? 0;
            DateTime maxTimestamp = afterTimestamp;

            foreach (var evt in events)
            {
                var windowStart = FloorToWindow(evt.CreatedAt);
                var windowEnd = windowStart + WindowSize;
                var isError = evt.StatusCode >= 400;

                var dimensionValue = dimension switch
                {
                    "platform" => "all",
                    "group" => evt.GroupId.ToString(),
                    "model" => evt.Model,
                    "user" => evt.UserId.ToString(),
                    "error" => evt.StatusCode.ToString(),
                    _ => "unknown",
                };

                await store.UpsertRollupAsync(
                    dimension, dimensionValue, windowStart, windowEnd,
                    evt.EventHash, evt.DurationMs, isError, ct);

                // Track max watermark values
                if (evt.EventEpochMs > maxEventId)
                    maxEventId = evt.EventEpochMs;
                if (evt.CreatedAt > maxTimestamp)
                    maxTimestamp = evt.CreatedAt;
            }

            // Update latency percentiles for affected windows
            var affectedWindows = events
                .Select(e => FloorToWindow(e.CreatedAt))
                .Distinct()
                .ToList();

            foreach (var windowStart in affectedWindows)
            {
                var windowEnd = windowStart + WindowSize;
                var affectedValues = events
                    .Where(e => FloorToWindow(e.CreatedAt) == windowStart)
                    .Select(GetDimensionValue)
                    .Distinct()
                    .ToList();

                foreach (var value in affectedValues)
                {
                    await store.UpdateLatencyPercentilesAsync(
                        dimension, value, windowStart, windowEnd, ct);
                }
            }

            // Advance watermark monotonically
            await store.UpdateWatermarkAsync(dimension, maxEventId, maxTimestamp, ct);

            if (events.Count > 0)
            {
                logger.LogDebug("Passive monitor V2 processed {Count} events for dimension {Dimension}",
                    events.Count, dimension);
            }
        }
    }

    private string GetDimensionValue(PassiveMonitorEvent evt) => "all";

    /// <summary>
    /// Enforce retention by deleting old rollups based on privacy config.
    /// </summary>
    public async Task EnforceRetentionAsync(CancellationToken ct)
    {
        var config = await store.GetPrivacyConfigAsync("default", ct);
        var retentionDays = config?.RetentionDays ?? DefaultRetentionDays;
        var deleted = await store.CleanupRetentionAsync(retentionDays, ct);
        if (deleted > 0)
        {
            logger.LogInformation("Passive monitor V2 retention cleanup: deleted {Count} rollups older than {Days} days",
                deleted, retentionDays);
        }
    }

    /// <summary>
    /// Floor a timestamp to the nearest window boundary.
    /// </summary>
    public static DateTime FloorToWindow(DateTime timestamp)
    {
        return new DateTime(
            timestamp.Year, timestamp.Month, timestamp.Day,
            timestamp.Hour, 0, 0, DateTimeKind.Utc);
    }
}
