using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace ScalaAPI.Admin.Data;

public sealed record BackupJobView(
    string Id,
    string Kind,
    string Status,
    string? ArtifactName,
    long? SizeBytes,
    string? Sha256,
    DateTime? RetentionUntil,
    long CreatedBy,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    string? ErrorCode,
    string? ErrorDetail);

public sealed record BackupCommandResult(
    BackupCommandStatus Status,
    BackupJobView? Job = null,
    string? ErrorCode = null,
    string? Error = null);

public enum BackupCommandStatus
{
    Created,
    Replayed,
    Busy,
    Conflict,
    Invalid,
    NotFound,
    NotConfigured,
}

public sealed record RestoreRunView(
    string Id,
    string BackupId,
    string Status,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    string? ErrorCode,
    string? ErrorDetail);

public sealed record RestoreCommandResult(
    BackupCommandStatus Status,
    RestoreRunView? Run = null,
    string? ErrorCode = null,
    string? Error = null);

/// <summary>
/// Owns the new project's explicit PostgreSQL backup boundary. Artifacts live
/// below a dedicated configured volume and restore can target only a separately
/// configured database, never the live authority connection.
/// </summary>
public sealed class BackupStore(
    NpgsqlDataSource dataSource,
    IConfiguration configuration,
    ILogger<BackupStore> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource = dataSource;
    private readonly string _sourceConnection = configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required");
    private readonly string? _restoreConnection = configuration["Backup:RestoreTargetConnection"];
    private readonly string _directory = configuration["Backup:Directory"]?.Trim() is { Length: > 0 } directory
        ? directory : "/var/lib/scalaapi/backups";
    private readonly string _pgDump = configuration["Backup:PgDumpPath"]?.Trim() is { Length: > 0 } dump
        ? dump : "pg_dump";
    private readonly string _pgRestore = configuration["Backup:PgRestorePath"]?.Trim() is { Length: > 0 } restore
        ? restore : "pg_restore";
    private readonly int _timeoutSeconds = Math.Clamp(
        configuration.GetValue("Backup:CommandTimeoutSeconds", 120), 5, 900);
    private readonly ILogger<BackupStore> _logger = logger;

    public bool RestoreConfigured => !string.IsNullOrWhiteSpace(_restoreConnection);

    public async Task<IReadOnlyList<BackupJobView>> ListAsync(
        int page = 1,
        int size = 50,
        CancellationToken ct = default)
    {
        page = Math.Clamp(page, 1, 10_000);
        size = Math.Clamp(size, 1, 100);
        await using var command = _dataSource.CreateCommand("""
            SELECT id, kind, status, artifact_path, size_bytes, sha256,
                   retention_until, created_by, created_at, completed_at,
                   error_code, error_detail
            FROM backup_jobs
            ORDER BY created_at DESC, id DESC
            OFFSET $1 LIMIT $2
            """);
        command.Parameters.AddWithValue((page - 1) * size);
        command.Parameters.AddWithValue(size);
        var result = new List<BackupJobView>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadJob(reader));
        return result;
    }

    public async Task<BackupJobView?> GetAsync(string id, CancellationToken ct = default)
    {
        if (!IsId(id)) return null;
        await using var command = _dataSource.CreateCommand("""
            SELECT id, kind, status, artifact_path, size_bytes, sha256,
                   retention_until, created_by, created_at, completed_at,
                   error_code, error_detail
            FROM backup_jobs WHERE id = $1
            """);
        command.Parameters.AddWithValue(id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadJob(reader) : null;
    }

    public async Task<BackupCommandResult> CreateAsync(
        long actorId,
        string? idempotencyKey,
        string? kind,
        int retentionDays,
        string? clientIp,
        CancellationToken ct = default)
    {
        if (actorId <= 0) return Invalid("actor_id_invalid", "actor id must be positive");
        if (!TryNormalizeKey(idempotencyKey, out var key))
            return Invalid("idempotency_key_invalid", "Idempotency-Key must be 8-200 characters");
        if (!string.Equals(kind?.Trim(), "postgres", StringComparison.OrdinalIgnoreCase))
            return Invalid("backup_kind_unsupported", "only postgres backup jobs are available");
        if (retentionDays is < 1 or > 365)
            return Invalid("retention_invalid", "retention_days must be 1-365");

        var normalizedKind = "postgres";
        var fingerprint = Fingerprint($"{normalizedKind}|{retentionDays}");
        var id = $"bak_{Guid.NewGuid():N}";
        await using (var connection = await _dataSource.OpenConnectionAsync(ct))
        await using (var transaction = await connection.BeginTransactionAsync(ct))
        {
            var existing = await ReadJobByKeyAsync(connection, transaction, key, ct);
            if (existing is not null)
            {
                if (!string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                    return new(BackupCommandStatus.Conflict, existing.View,
                        "idempotency_conflict", "the idempotency key was used with a different backup request");
                if (existing.View.Status == "completed" || existing.View.Status == "failed")
                    return new(BackupCommandStatus.Replayed, existing.View);
                return new(BackupCommandStatus.Busy, existing.View,
                    "backup_in_progress", "a backup with this idempotency key is already running");
            }

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO backup_jobs(
                    id, kind, idempotency_key, request_fingerprint, status,
                    retention_until, created_by)
                VALUES ($1, $2, $3, $4, 'running', now() + ($5 || ' days')::interval, $6)
                """;
            insert.Parameters.AddWithValue(id);
            insert.Parameters.AddWithValue(normalizedKind);
            insert.Parameters.AddWithValue(key);
            insert.Parameters.AddWithValue(fingerprint);
            insert.Parameters.AddWithValue(retentionDays.ToString(CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue(actorId);
            await insert.ExecuteNonQueryAsync(ct);
            await InsertAuditAsync(connection, transaction, actorId, "backup.requested", id,
                new { kind = normalizedKind, retention_days = retentionDays }, clientIp, ct);
            await transaction.CommitAsync(ct);
        }

        var finalPath = Path.Combine(_directory, id + ".dump");
        var temporaryPath = Path.Combine(_directory, "." + id + ".tmp");
        try
        {
            Directory.CreateDirectory(_directory);
            await RunDumpAsync(temporaryPath, ct);
            var info = new FileInfo(temporaryPath);
            if (!info.Exists || info.Length <= 0)
                throw new InvalidOperationException("pg_dump produced an empty artifact");
            var sha = await Sha256FileAsync(temporaryPath, ct);
            File.Move(temporaryPath, finalPath);
            var completed = await CompleteBackupAsync(id, Path.GetFileName(finalPath), info.Length,
                sha, actorId, clientIp, ct);
            return new(BackupCommandStatus.Created, completed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await FailBackupAsync(id, "backup_cancelled", "backup command was cancelled", actorId, clientIp);
            TryDelete(temporaryPath);
            throw;
        }
        catch (Exception ex)
        {
            var detail = SanitizeError(ex.Message);
            _logger.LogWarning(ex, "Backup job {BackupId} failed", id);
            await FailBackupAsync(id, "backup_failed", detail, actorId, clientIp);
            TryDelete(temporaryPath);
            return new(BackupCommandStatus.Invalid, await GetAsync(id, ct), "backup_failed", detail);
        }
    }

    public async Task<RestoreCommandResult> RestoreAsync(
        long actorId,
        string backupId,
        string? idempotencyKey,
        string? clientIp,
        CancellationToken ct = default)
    {
        if (actorId <= 0) return new(BackupCommandStatus.Invalid, ErrorCode: "actor_id_invalid");
        if (!TryNormalizeKey(idempotencyKey, out var key))
            return new(BackupCommandStatus.Invalid, ErrorCode: "idempotency_key_invalid",
                Error: "Idempotency-Key must be 8-200 characters");
        if (!RestoreConfigured)
            return new(BackupCommandStatus.NotConfigured, ErrorCode: "restore_not_configured",
                Error: "Backup:RestoreTargetConnection is not configured");
        if (!IsId(backupId)) return new(BackupCommandStatus.NotFound, ErrorCode: "backup_not_found");

        var backup = await GetAsync(backupId, ct);
        if (backup is null) return new(BackupCommandStatus.NotFound, ErrorCode: "backup_not_found");
        if (backup.Status != "completed" || string.IsNullOrWhiteSpace(backup.ArtifactName))
            return new(BackupCommandStatus.Invalid, ErrorCode: "backup_not_completed",
                Error: "only completed backups can be restored");
        var artifact = Path.Combine(_directory, backup.ArtifactName);
        if (!File.Exists(artifact))
            return new(BackupCommandStatus.Invalid, ErrorCode: "backup_artifact_missing",
                Error: "the backup artifact is no longer available");

        var target = new NpgsqlConnectionStringBuilder(_restoreConnection);
        var source = new NpgsqlConnectionStringBuilder(_sourceConnection);
        if (string.Equals(target.Host, source.Host, StringComparison.OrdinalIgnoreCase)
            && target.Port == source.Port
            && string.Equals(target.Database, source.Database, StringComparison.OrdinalIgnoreCase))
            return new(BackupCommandStatus.Invalid, ErrorCode: "restore_target_is_authority",
                Error: "restore target must be a separate database");
        var targetFingerprint = Fingerprint($"{target.Host}|{target.Port}|{target.Database}");
        var fingerprint = Fingerprint(backupId + "|" + targetFingerprint);
        var runId = $"rst_{Guid.NewGuid():N}";

        await using (var connection = await _dataSource.OpenConnectionAsync(ct))
        await using (var transaction = await connection.BeginTransactionAsync(ct))
        {
            var existing = await ReadRestoreByKeyAsync(connection, transaction, key, ct);
            if (existing is not null)
            {
                if (!string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                    return new(BackupCommandStatus.Conflict, existing.View,
                        "idempotency_conflict", "the idempotency key was used with a different restore request");
                if (existing.View.Status is "completed" or "failed")
                    return new(BackupCommandStatus.Replayed, existing.View);
                return new(BackupCommandStatus.Busy, existing.View,
                    "restore_in_progress", "a restore with this idempotency key is already running");
            }
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO backup_restore_runs(
                    id, backup_id, idempotency_key, request_fingerprint, status,
                    target_fingerprint, created_by)
                VALUES ($1, $2, $3, $4, 'running', $5, $6)
                """;
            insert.Parameters.AddWithValue(runId);
            insert.Parameters.AddWithValue(backupId);
            insert.Parameters.AddWithValue(key);
            insert.Parameters.AddWithValue(fingerprint);
            insert.Parameters.AddWithValue(targetFingerprint);
            insert.Parameters.AddWithValue(actorId);
            await insert.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
        }

        try
        {
            await RunRestoreAsync(artifact, target, ct);
            var completed = await CompleteRestoreAsync(runId, actorId, backupId, clientIp, ct);
            return new(BackupCommandStatus.Created, completed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await FailRestoreAsync(runId, "restore_cancelled", "restore command was cancelled",
                actorId, backupId, clientIp);
            throw;
        }
        catch (Exception ex)
        {
            var detail = SanitizeError(ex.Message);
            _logger.LogWarning(ex, "Restore run {RunId} failed", runId);
            await FailRestoreAsync(runId, "restore_failed", detail, actorId, backupId, clientIp);
            return new(BackupCommandStatus.Invalid, await GetRestoreAsync(runId, ct),
                "restore_failed", detail);
        }
    }

    private async Task RunDumpAsync(string path, CancellationToken ct)
    {
        var connection = new NpgsqlConnectionStringBuilder(_sourceConnection);
        var start = StartProcess(_pgDump, connection, psi =>
        {
            psi.ArgumentList.Add("--dbname");
            psi.ArgumentList.Add(connection.Database ?? "postgres");
            psi.ArgumentList.Add("--format=custom");
            psi.ArgumentList.Add("--no-owner");
            psi.ArgumentList.Add("--no-privileges");
            psi.ArgumentList.Add("--file");
            psi.ArgumentList.Add(path);
        });
        await WaitProcessAsync(start, "pg_dump", ct);
    }

    private async Task RunRestoreAsync(string path, NpgsqlConnectionStringBuilder target,
        CancellationToken ct)
    {
        var start = StartProcess(_pgRestore, target, psi =>
        {
            psi.ArgumentList.Add("--clean");
            psi.ArgumentList.Add("--if-exists");
            psi.ArgumentList.Add("--no-owner");
            psi.ArgumentList.Add("--no-privileges");
            psi.ArgumentList.Add("--exit-on-error");
            psi.ArgumentList.Add("--dbname");
            psi.ArgumentList.Add(target.Database ?? "postgres");
            psi.ArgumentList.Add(path);
        });
        await WaitProcessAsync(start, "pg_restore", ct);
    }

    private Process StartProcess(string executable, NpgsqlConnectionStringBuilder connection,
        Action<ProcessStartInfo> configure)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--host");
        psi.ArgumentList.Add(connection.Host ?? "localhost");
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(connection.Port.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--username");
        psi.ArgumentList.Add(connection.Username ?? "");
        configure(psi);
        psi.Environment["PGPASSWORD"] = connection.Password;
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException($"Unable to start {executable}");
        return process;
    }

    private async Task WaitProcessAsync(Process process, string name, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            TryKill(process);
            throw new TimeoutException($"{name} exceeded the configured timeout");
        }
        var error = await stderr;
        _ = await stdout;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{name} failed: {SanitizeError(error)}");
        process.Dispose();
    }

    private async Task<BackupJobView> CompleteBackupAsync(string id, string artifactName,
        long size, string sha, long actorId, string? clientIp, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE backup_jobs
                SET status = 'completed', artifact_path = $2, size_bytes = $3,
                    sha256 = $4, completed_at = now()
                WHERE id = $1 AND status = 'running'
                """;
            update.Parameters.AddWithValue(id);
            update.Parameters.AddWithValue(artifactName);
            update.Parameters.AddWithValue(size);
            update.Parameters.AddWithValue(sha);
            if (await update.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException("backup job changed while running");
        }
        await InsertAuditAsync(connection, transaction, actorId, "backup.completed", id,
            new { artifact = artifactName, size_bytes = size, sha256 = sha }, clientIp, ct);
        await transaction.CommitAsync(ct);
        return await GetAsync(id, ct) ?? throw new InvalidOperationException("backup row disappeared");
    }

    private async Task FailBackupAsync(string id, string code, string detail,
        long actorId, string? clientIp)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE backup_jobs SET status = 'failed', error_code = $2,
                    error_detail = $3, completed_at = now()
                WHERE id = $1 AND status = 'running'
                """;
            update.Parameters.AddWithValue(id);
            update.Parameters.AddWithValue(code);
            update.Parameters.AddWithValue(detail);
            await update.ExecuteNonQueryAsync();
            await InsertAuditAsync(connection, transaction, actorId, "backup.failed", id,
                new { error_code = code }, clientIp, CancellationToken.None);
            await transaction.CommitAsync();
        }
        catch (Exception failure)
        {
            _logger.LogError(failure, "Unable to persist failed backup {BackupId}", id);
        }
    }

    private async Task<RestoreRunView> CompleteRestoreAsync(string id, long actorId,
        string backupId, string? clientIp, CancellationToken ct)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE backup_restore_runs SET status = 'completed', completed_at = now()
            WHERE id = $1 AND status = 'running'
            """;
        update.Parameters.AddWithValue(id);
        if (await update.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("restore run changed while running");
        await InsertAuditAsync(connection, transaction, actorId, "backup.restored", backupId,
            new { restore_run_id = id }, clientIp, ct);
        await transaction.CommitAsync(ct);
        return await GetRestoreAsync(id, ct) ?? throw new InvalidOperationException("restore row disappeared");
    }

    private async Task FailRestoreAsync(string id, string code, string detail, long actorId,
        string backupId, string? clientIp)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE backup_restore_runs SET status = 'failed', error_code = $2,
                    error_detail = $3, completed_at = now()
                WHERE id = $1 AND status = 'running'
                """;
            update.Parameters.AddWithValue(id);
            update.Parameters.AddWithValue(code);
            update.Parameters.AddWithValue(detail);
            await update.ExecuteNonQueryAsync();
            await InsertAuditAsync(connection, transaction, actorId, "backup.restore_failed", backupId,
                new { restore_run_id = id, error_code = code }, clientIp, CancellationToken.None);
            await transaction.CommitAsync();
        }
        catch (Exception failure)
        {
            _logger.LogError(failure, "Unable to persist failed restore {RestoreId}", id);
        }
    }

    private async Task<RestoreRunView?> GetRestoreAsync(string id, CancellationToken ct)
    {
        await using var command = _dataSource.CreateCommand("""
            SELECT id, backup_id, status, created_at, completed_at, error_code, error_detail
            FROM backup_restore_runs WHERE id = $1
            """);
        command.Parameters.AddWithValue(id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new RestoreRunView(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetDateTime(3), reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6))
            : null;
    }

    private async Task<StoredJob?> ReadJobByKeyAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string key, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, kind, status, artifact_path, size_bytes, sha256, retention_until,
                   created_by, created_at, completed_at, error_code, error_detail,
                   request_fingerprint
            FROM backup_jobs WHERE idempotency_key = $1 FOR UPDATE
            """;
        command.Parameters.AddWithValue(key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadStoredJob(reader);
    }

    private async Task<StoredRestore?> ReadRestoreByKeyAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string key, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, backup_id, status, created_at, completed_at, error_code, error_detail,
                   request_fingerprint
            FROM backup_restore_runs WHERE idempotency_key = $1 FOR UPDATE
            """;
        command.Parameters.AddWithValue(key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var view = new RestoreRunView(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetDateTime(3), reader.IsDBNull(4) ? null : reader.GetDateTime(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
        return new(view, reader.GetString(7));
    }

    private static StoredJob ReadStoredJob(NpgsqlDataReader reader)
    {
        var view = new BackupJobView(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetDateTime(6), reader.GetInt64(7),
            reader.GetDateTime(8), reader.IsDBNull(9) ? null : reader.GetDateTime(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11));
        return new(view, reader.GetString(12));
    }

    private static BackupJobView ReadJob(NpgsqlDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetDateTime(6), reader.GetInt64(7),
            reader.GetDateTime(8), reader.IsDBNull(9) ? null : reader.GetDateTime(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11));

    private async Task InsertAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long actorId, string action, string resourceId, object details, string? clientIp,
        CancellationToken ct)
    {
        await using var audit = connection.CreateCommand();
        audit.Transaction = transaction;
        audit.CommandText = """
            INSERT INTO audit_logs(user_id, action, resource_type, resource_id, details, ip_address)
            VALUES ($1, $2, 'backup', $3, $4, $5)
            """;
        audit.Parameters.AddWithValue(actorId);
        audit.Parameters.AddWithValue(action);
        audit.Parameters.AddWithValue(resourceId);
        audit.Parameters.AddWithValue(JsonSerializer.Serialize(details, JsonOptions));
        audit.Parameters.AddWithValue((object?)clientIp ?? DBNull.Value);
        await audit.ExecuteNonQueryAsync(ct);
    }

    private static BackupCommandResult Invalid(string code, string error) =>
        new(BackupCommandStatus.Invalid, ErrorCode: code, Error: error);

    private static bool TryNormalizeKey(string? value, out string key)
    {
        key = value?.Trim() ?? "";
        return key.Length is >= 8 and <= 200 && key.All(ch => !char.IsControl(ch));
    }

    private static bool IsId(string value) => value.StartsWith("bak_", StringComparison.Ordinal)
        && value.Length == 36 && value.Skip(4).All(Uri.IsHexDigit);

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async Task<string> Sha256FileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    private static string SanitizeError(string value)
    {
        var normalized = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (normalized.Length > 500) normalized = normalized[..500];
        return normalized.Contains("password", StringComparison.OrdinalIgnoreCase)
            ? "backup command failed without safe diagnostic details" : normalized;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        process.Dispose();
    }

    private sealed record StoredJob(BackupJobView View, string RequestFingerprint);
    private sealed record StoredRestore(RestoreRunView View, string RequestFingerprint);
}
