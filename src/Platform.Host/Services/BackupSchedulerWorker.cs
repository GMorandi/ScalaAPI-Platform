using System.Globalization;
using Npgsql;
using ScalaAPI.Data.Backups;

namespace ScalaAPI.Host.Services;

/// <summary>
/// Cluster-singleton backup scheduler. Uses a database-backed leader lock
/// to ensure only one node in the cluster runs backup scheduling at a time.
/// Claims a schedule key with an expiry; other nodes skip if the claim is held.
/// </summary>
public sealed class BackupSchedulerWorker(
    NpgsqlDataSource dataSource,
    BackupService backupService,
    IConfiguration configuration,
    ILogger<BackupSchedulerWorker> logger) : BackgroundService
{
    private readonly NpgsqlDataSource _dataSource = dataSource;
    private readonly BackupService _backupService = backupService;
    private readonly string _workerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    private readonly TimeSpan _claimDuration = TimeSpan.FromMinutes(10);
    private readonly TimeSpan _tickInterval = TimeSpan.FromMinutes(
        configuration.GetValue("Backup:ScheduleIntervalMinutes", 60));
    private readonly ILogger<BackupSchedulerWorker> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for the application to fully start before scheduling.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        using var timer = new PeriodicTimer(_tickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backup scheduler tick failed for worker {WorkerId}",
                    _workerId);
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // Attempt to claim the schedule. If another worker holds it, skip.
        if (!await TryClaimScheduleAsync(ct))
        {
            _logger.LogDebug("Backup schedule already claimed by another worker");
            return;
        }

        _logger.LogInformation("Worker {WorkerId} claimed backup schedule", _workerId);

        // Reclaim any zombie running rows stuck >1h.
        await _backupService.ReclaimZombieBackupsAsync(ct);

        // Enforce retention policy.
        var retention = await _backupService.EnforceRetentionAsync(ct);
        if (retention.Deleted > 0 || retention.Failed > 0)
        {
            _logger.LogInformation(
                "Retention enforcement: deleted={Deleted}, failed={Failed}, freed_bytes={FreedBytes}",
                retention.Deleted, retention.Failed, retention.FreedBytes);
        }

        // Check if a scheduled backup is due.
        var policy = await _backupService.GetRetentionPolicyAsync(ct);
        if (policy is null) return;

        var backupDue = await IsBackupDueAsync(ct);
        if (backupDue)
        {
            var idempotencyKey = $"scheduled_{DateTime.UtcNow:yyyyMMdd_HH}";
            var jobId = $"bak_{Guid.NewGuid():N}";
            var retentionDays = policy.KeepDaily;

            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync(ct);
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO backup_jobs (id, kind, idempotency_key, request_fingerprint,
                        status, retention_until, created_by)
                    VALUES ($1, 'postgres', $2, $3, 'running', now() + ($4 || ' days')::interval, 'scheduler')
                    ON CONFLICT (idempotency_key) DO NOTHING
                    """;
                cmd.Parameters.AddWithValue(jobId);
                cmd.Parameters.AddWithValue(idempotencyKey);
                cmd.Parameters.AddWithValue($"postgres|{retentionDays}");
                cmd.Parameters.AddWithValue(retentionDays.ToString());

                var inserted = await cmd.ExecuteNonQueryAsync(ct);
                if (inserted > 0)
                {
                    _logger.LogInformation("Scheduled backup created: {JobId}", jobId);

                    // Execute pg_dump inline.
                    var artifactPath = await _backupService.RunScheduledDumpAsync(jobId, ct);
                    if (artifactPath is not null && policy.OffsiteEnabled
                        && !string.IsNullOrWhiteSpace(policy.OffsiteUrl))
                    {
                        var upload = await _backupService.UploadOffsiteAsync(
                            jobId, artifactPath, policy.OffsiteUrl,
                            policy.OffsiteBucket, ct);
                        if (upload.Status == "completed" && upload.RemoteUrl is not null
                            && upload.RemoteChecksum is not null)
                        {
                            await _backupService.VerifyOffsiteUploadAsync(
                                upload.UploadId, upload.RemoteUrl, upload.RemoteChecksum, ct);
                        }
                    }
                }
                else
                {
                    _logger.LogDebug("Scheduled backup already exists for idempotency key {Key}", idempotencyKey);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled backup creation failed");
            }
        }

        // Record last run status.
        await UpdateScheduleClaimAsync("completed", ct);
    }

    private async Task<bool> IsBackupDueAsync(CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM backup_jobs
            WHERE status = 'completed'
              AND completed_at > now() - interval '24 hours'
            UNION ALL
            SELECT COUNT(*) FROM backup_jobs
            WHERE status = 'running'
              AND created_at > now() - interval '1 hour'
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var completedRecent = reader.GetInt64(0);
        await reader.NextResultAsync(ct);
        await reader.ReadAsync(ct);
        var runningRecent = reader.GetInt64(1);
        return completedRecent == 0 && runningRecent == 0;
    }

    private async Task<bool> TryClaimScheduleAsync(CancellationToken ct)
    {
        var claimId = $"claim_{Guid.NewGuid():N}";
        var scheduleKey = "backup_daily";
        var expiresAt = DateTime.UtcNow + _claimDuration;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        // First, try to insert a new claim.
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO backup_schedule_claims(claim_id, schedule_key, worker_id, expires_at)
            VALUES ($1, $2, $3, $4)
            ON CONFLICT (schedule_key) DO UPDATE SET
                worker_id = EXCLUDED.worker_id,
                claimed_at = EXCLUDED.claimed_at,
                expires_at = EXCLUDED.expires_at
            WHERE backup_schedule_claims.expires_at < now()
                OR backup_schedule_claims.worker_id = EXCLUDED.worker_id
            """;
        insert.Parameters.AddWithValue(claimId);
        insert.Parameters.AddWithValue(scheduleKey);
        insert.Parameters.AddWithValue(_workerId);
        insert.Parameters.AddWithValue(expiresAt);

        var affected = await insert.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }

    private async Task UpdateScheduleClaimAsync(string status, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE backup_schedule_claims
            SET last_run_at = now(), last_run_status = $2
            WHERE schedule_key = 'backup_daily' AND worker_id = $1
            """;
        update.Parameters.AddWithValue(_workerId);
        update.Parameters.AddWithValue(status);
        await update.ExecuteNonQueryAsync(ct);
    }
}
