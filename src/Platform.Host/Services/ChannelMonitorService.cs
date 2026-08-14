using System.Text.Json;
using Npgsql;

namespace ScalaAPI.Host.Services;

/// <summary>
/// Record types for channel monitor templates, checks, and incidents.
/// </summary>
public sealed record ChannelMonitorTemplate(
    string TemplateId,
    string Name,
    string CheckType,
    string ScheduleCron,
    int TimeoutSeconds,
    int RetryCount,
    int AlertThreshold,
    DateTime CreatedAt);

public sealed record ChannelMonitorCheck(
    long CheckId,
    string TemplateId,
    string WorkerId,
    DateTime StartedAt,
    DateTime? CompletedAt,
    string Status,
    JsonElement? Result,
    string? ErrorMessage,
    string? LeaderToken);

public sealed record ChannelMonitorIncident(
    long IncidentId,
    string TemplateId,
    DateTime OpenedAt,
    DateTime? ClosedAt,
    string? Resolution,
    int CheckCount);

/// <summary>
/// Data access for channel monitor templates, checks, and incidents.
/// </summary>
public sealed class ChannelMonitorTemplateStore(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<ChannelMonitorTemplate>> ListTemplatesAsync(CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT template_id, name, check_type, schedule_cron, timeout_seconds,
                   retry_count, alert_threshold, created_at
            FROM channel_monitor_templates
            ORDER BY template_id
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<ChannelMonitorTemplate>();
        while (await reader.ReadAsync(ct))
        {
            items.Add(new ChannelMonitorTemplate(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5),
                reader.GetInt32(6), reader.GetDateTime(7)));
        }
        return items;
    }

    public async Task<ChannelMonitorTemplate?> GetTemplateAsync(string templateId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT template_id, name, check_type, schedule_cron, timeout_seconds,
                   retry_count, alert_threshold, created_at
            FROM channel_monitor_templates WHERE template_id = $1
            """);
        command.Parameters.AddWithValue(templateId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new ChannelMonitorTemplate(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5),
            reader.GetInt32(6), reader.GetDateTime(7));
    }

    public async Task<ChannelMonitorTemplate> CreateTemplateAsync(
        string templateId, string name, string checkType, string scheduleCron,
        int timeoutSeconds, int retryCount, int alertThreshold, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO channel_monitor_templates
                (template_id, name, check_type, schedule_cron, timeout_seconds, retry_count, alert_threshold)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT (template_id) DO UPDATE SET
                name = EXCLUDED.name, check_type = EXCLUDED.check_type,
                schedule_cron = EXCLUDED.schedule_cron, timeout_seconds = EXCLUDED.timeout_seconds,
                retry_count = EXCLUDED.retry_count, alert_threshold = EXCLUDED.alert_threshold
            RETURNING template_id, name, check_type, schedule_cron, timeout_seconds,
                      retry_count, alert_threshold, created_at
            """);
        command.Parameters.AddWithValue(templateId);
        command.Parameters.AddWithValue(name);
        command.Parameters.AddWithValue(checkType);
        command.Parameters.AddWithValue(scheduleCron);
        command.Parameters.AddWithValue(timeoutSeconds);
        command.Parameters.AddWithValue(retryCount);
        command.Parameters.AddWithValue(alertThreshold);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new ChannelMonitorTemplate(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5),
            reader.GetInt32(6), reader.GetDateTime(7));
    }

    /// <summary>
    /// Try to claim a check for a template with leader fencing.
    /// Uses UNIQUE(template_id, leader_token) to prevent duplicate workers from duplicating checks.
    /// Returns null if the claim was rejected (another worker already claimed with same leader_token).
    /// </summary>
    public async Task<long?> TryClaimCheckAsync(
        string templateId, string workerId, string leaderToken, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO channel_monitor_checks (template_id, worker_id, started_at, status, leader_token)
            VALUES ($1, $2, now(), 'running', $3)
            ON CONFLICT (template_id, leader_token) DO NOTHING
            RETURNING check_id
            """);
        command.Parameters.AddWithValue(templateId);
        command.Parameters.AddWithValue(workerId);
        command.Parameters.AddWithValue(leaderToken);
        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    public async Task CompleteCheckAsync(
        long checkId, string status, JsonElement? result, string? errorMessage, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE channel_monitor_checks
            SET completed_at = now(), status = $2, result = $3, error_message = $4
            WHERE check_id = $1
            """);
        command.Parameters.AddWithValue(checkId);
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue((object?)result?.GetRawText() ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)errorMessage ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CountRecentFailuresAsync(
        string templateId, int windowMinutes, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT count(*) FROM channel_monitor_checks
            WHERE template_id = $1 AND status = 'failed'
              AND started_at >= now() - ($2 || ' minutes')::interval
            """);
        command.Parameters.AddWithValue(templateId);
        command.Parameters.AddWithValue(windowMinutes.ToString());
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    public async Task<ChannelMonitorIncident?> GetOpenIncidentAsync(
        string templateId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT incident_id, template_id, opened_at, closed_at, resolution, check_count
            FROM channel_monitor_incidents
            WHERE template_id = $1 AND closed_at IS NULL
            ORDER BY opened_at DESC LIMIT 1
            """);
        command.Parameters.AddWithValue(templateId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new ChannelMonitorIncident(
            reader.GetInt64(0), reader.GetString(1), reader.GetDateTime(2),
            reader.IsDBNull(3) ? null : reader.GetDateTime(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5));
    }

    public async Task<long> OpenIncidentAsync(string templateId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO channel_monitor_incidents (template_id, check_count)
            VALUES ($1, 0)
            RETURNING incident_id
            """);
        command.Parameters.AddWithValue(templateId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    public async Task CloseIncidentAsync(long incidentId, string resolution, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE channel_monitor_incidents
            SET closed_at = now(), resolution = $2
            WHERE incident_id = $1
            """);
        command.Parameters.AddWithValue(incidentId);
        command.Parameters.AddWithValue(resolution);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateIncidentCheckCountAsync(long incidentId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE channel_monitor_incidents
            SET check_count = (
                SELECT count(*) FROM channel_monitor_checks
                WHERE template_id = (SELECT template_id FROM channel_monitor_incidents WHERE incident_id = $1)
                  AND started_at >= (SELECT opened_at FROM channel_monitor_incidents WHERE incident_id = $1)
            )
            WHERE incident_id = $1
            """);
        command.Parameters.AddWithValue(incidentId);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Reclaim stale worker claims: checks that have been 'running' for longer than
    /// the template timeout are marked as failed so they can be retried.
    /// </summary>
    public async Task<int> ReclaimStaleClaimsAsync(CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE channel_monitor_checks
            SET status = 'failed', completed_at = now(),
                error_message = 'Claim reclaimed: worker stale'
            WHERE status = 'running'
              AND started_at < now() - (
                  SELECT (template.timeout_seconds || ' seconds')::interval
                  FROM channel_monitor_templates AS template
                  WHERE template.template_id = channel_monitor_checks.template_id
              )
            """);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ChannelMonitorCheck>> ListChecksAsync(
        string? templateId, string? status, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        await using var command = dataSource.CreateCommand("""
            SELECT check_id, template_id, worker_id, started_at, completed_at,
                   status, result, error_message, leader_token
            FROM channel_monitor_checks
            WHERE ($1::text IS NULL OR template_id = $1)
              AND ($2::text IS NULL OR status = $2)
            ORDER BY started_at DESC, check_id DESC
            LIMIT $3
            """);
        command.Parameters.AddWithValue((object?)templateId ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue(limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<ChannelMonitorCheck>();
        while (await reader.ReadAsync(ct))
        {
            var resultText = reader.IsDBNull(6) ? null : reader.GetString(6);
            items.Add(new ChannelMonitorCheck(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetDateTime(3),
                reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                reader.GetString(5),
                resultText is not null ? JsonDocument.Parse(resultText).RootElement : null,
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        return items;
    }

    public async Task<IReadOnlyList<ChannelMonitorIncident>> ListIncidentsAsync(
        string? templateId, bool? openOnly, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        await using var command = dataSource.CreateCommand("""
            SELECT incident_id, template_id, opened_at, closed_at, resolution, check_count
            FROM channel_monitor_incidents
            WHERE ($1::text IS NULL OR template_id = $1)
              AND ($2::boolean IS NULL OR ($2 = true AND closed_at IS NULL) OR ($2 = false AND closed_at IS NOT NULL))
            ORDER BY opened_at DESC, incident_id DESC
            LIMIT $3
            """);
        command.Parameters.AddWithValue((object?)templateId ?? DBNull.Value);
        command.Parameters.AddWithValue(openOnly.HasValue ? (object)openOnly.Value : DBNull.Value);
        command.Parameters.AddWithValue(limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<ChannelMonitorIncident>();
        while (await reader.ReadAsync(ct))
        {
            items.Add(new ChannelMonitorIncident(
                reader.GetInt64(0), reader.GetString(1), reader.GetDateTime(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5)));
        }
        return items;
    }
}

/// <summary>
/// Background hosted service that runs channel monitor checks with leader fencing.
/// Only one silo in the cluster runs as leader at a time. It loads templates,
/// schedules checks based on cron, runs them with bounded retry, opens/closes incidents
/// based on alert_threshold, and reclaims stale worker claims.
/// </summary>
public sealed class ChannelMonitorService(
    ChannelMonitorTemplateStore store,
    NpgsqlDataSource dataSource,
    ILogger<ChannelMonitorService> logger) : BackgroundService
{
    private string _leaderToken = Guid.NewGuid().ToString("N");
    private readonly string _workerId = $"cm-{Environment.ProcessId}-{Environment.CurrentManagedThreadId}";
    private DateTime _lastReclaimScan = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> _lastRunPerTemplate = new();
    private const int LeadershipAdvisoryLockId = 0x434D4C44; // "CMLD" in hex

    /// <summary>
    /// Leadership is determined by holding a PostgreSQL advisory lock. Only one
    /// process can hold the lock at a time, providing true distributed leadership
    /// across multiple Silos.
    /// </summary>
    public bool IsLeader { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Channel monitor service starting with worker ID {WorkerId}", _workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Try to acquire or verify leadership
                if (!IsLeader)
                {
                    IsLeader = await TryAcquireLeadershipAsync(stoppingToken);
                    if (IsLeader)
                    {
                        logger.LogInformation("Acquired leadership with token {LeaderToken}", _leaderToken);
                    }
                }
                else
                {
                    // Verify we still hold the lock
                    var stillLeader = await VerifyLeadershipAsync(stoppingToken);
                    if (!stillLeader)
                    {
                        IsLeader = false;
                        _leaderToken = Guid.NewGuid().ToString("N");
                        logger.LogWarning("Lost leadership, will retry");
                    }
                }

                // Periodically reclaim stale worker claims
                if (DateTime.UtcNow - _lastReclaimScan >= TimeSpan.FromSeconds(30))
                {
                    var reclaimed = await store.ReclaimStaleClaimsAsync(stoppingToken);
                    if (reclaimed > 0)
                        logger.LogWarning("Reclaimed {Count} stale channel monitor claims", reclaimed);
                    _lastReclaimScan = DateTime.UtcNow;
                }

                if (!IsLeader)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                var templates = await store.ListTemplatesAsync(stoppingToken);
                foreach (var template in templates)
                {
                    if (ShouldRunCheck(template))
                    {
                        await RunCheckWithRetryAsync(template, stoppingToken);
                        _lastRunPerTemplate[template.TemplateId] = DateTime.UtcNow;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Channel monitor service loop failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<bool> TryAcquireLeadershipAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT pg_try_advisory_lock(@lockId)";
            cmd.Parameters.AddWithValue("lockId", LeadershipAdvisoryLockId);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is bool acquired && acquired;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to acquire leadership lock");
            return false;
        }
    }

    private async Task<bool> VerifyLeadershipAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT pg_advisory_lock(@lockId)";
            cmd.Parameters.AddWithValue("lockId", LeadershipAdvisoryLockId);
            // If we already hold the lock, this returns immediately
            // If we don't hold it, this will block or fail
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Leadership verification failed");
            return false;
        }
    }

    /// <summary>
    /// Simple cron-based scheduling: check if enough time has passed since the last run.
    /// Parses the cron minute field to determine the interval in minutes.
    /// </summary>
    private bool ShouldRunCheck(ChannelMonitorTemplate template)
    {
        if (!_lastRunPerTemplate.TryGetValue(template.TemplateId, out var lastRun))
            return true;

        var intervalMinutes = ParseCronIntervalMinutes(template.ScheduleCron);
        return DateTime.UtcNow - lastRun >= TimeSpan.FromMinutes(intervalMinutes);
    }

    /// <summary>
    /// Parse a simple cron expression to extract the interval in minutes.
    /// Supports: " * /N * * * *" (every N minutes) and defaults to 5 minutes.
    /// </summary>
    public static int ParseCronIntervalMinutes(string cron)
    {
        var parts = cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1) return 5;
        var minuteField = parts[0];
        if (minuteField.StartsWith("*/") && int.TryParse(minuteField[2..], out var interval))
            return Math.Clamp(interval, 1, 1440);
        if (minuteField == "*") return 1;
        return 5;
    }

    /// <summary>
    /// Run a check with bounded retry logic. If the check fails after all retries,
    /// evaluate whether to open an incident based on alert_threshold.
    /// </summary>
    private async Task RunCheckWithRetryAsync(ChannelMonitorTemplate template, CancellationToken ct)
    {
        // Try to claim the check with leader fencing
        var checkId = await store.TryClaimCheckAsync(
            template.TemplateId, _workerId, _leaderToken, ct);
        if (checkId is null)
        {
            logger.LogDebug("Check for template {TemplateId} already claimed by another worker", template.TemplateId);
            return;
        }

        Exception? lastError = null;
        for (var attempt = 0; attempt <= template.RetryCount; attempt++)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(template.TimeoutSeconds));

                // Simulate the check execution
                var checkResult = await ExecuteCheckAsync(template, timeoutCts.Token);
                await store.CompleteCheckAsync(checkId.Value, "passed", checkResult, null, ct);

                // Check if we should close an open incident (recovery)
                await EvaluateRecoveryAsync(template, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                logger.LogWarning(ex, "Check for template {TemplateId} failed on attempt {Attempt}",
                    template.TemplateId, attempt + 1);
                if (attempt < template.RetryCount)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 * attempt, 10)), ct);
            }
        }

        // All retries exhausted
        await store.CompleteCheckAsync(checkId.Value, "failed", null,
            lastError?.Message ?? "Unknown error", ct);

        // Evaluate whether to open an incident
        await EvaluateIncidentAsync(template, ct);
    }

    /// <summary>
    /// Execute the actual check. This is a placeholder that simulates a health check.
    /// In production, this would probe the actual channel endpoint.
    /// </summary>
    private Task<JsonElement?> ExecuteCheckAsync(ChannelMonitorTemplate template, CancellationToken ct)
    {
        // Placeholder: in production, this would make an HTTP request to the channel endpoint
        var result = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            check_type = template.CheckType,
            template_id = template.TemplateId,
            timestamp = DateTime.UtcNow,
            status = "healthy",
        })).RootElement;
        return Task.FromResult<JsonElement?>(result);
    }

    /// <summary>
    /// Evaluate whether to open an incident based on recent failure count and alert_threshold.
    /// </summary>
    private async Task EvaluateIncidentAsync(ChannelMonitorTemplate template, CancellationToken ct)
    {
        var recentFailures = await store.CountRecentFailuresAsync(template.TemplateId, 15, ct);
        if (recentFailures >= template.AlertThreshold)
        {
            var existingIncident = await store.GetOpenIncidentAsync(template.TemplateId, ct);
            if (existingIncident is null)
            {
                var incidentId = await store.OpenIncidentAsync(template.TemplateId, ct);
                logger.LogWarning("Opened incident {IncidentId} for template {TemplateId} after {FailureCount} failures",
                    incidentId, template.TemplateId, recentFailures);
            }
        }
    }

    /// <summary>
    /// Evaluate whether to close an open incident (recovery). If the check passes
    /// and there is an open incident, close it.
    /// </summary>
    private async Task EvaluateRecoveryAsync(ChannelMonitorTemplate template, CancellationToken ct)
    {
        var openIncident = await store.GetOpenIncidentAsync(template.TemplateId, ct);
        if (openIncident is not null)
        {
            await store.UpdateIncidentCheckCountAsync(openIncident.IncidentId, ct);
            await store.CloseIncidentAsync(openIncident.IncidentId, "Recovery: check passed", ct);
            logger.LogInformation("Closed incident {IncidentId} for template {TemplateId}: recovery",
                openIncident.IncidentId, template.TemplateId);
        }
    }
}
