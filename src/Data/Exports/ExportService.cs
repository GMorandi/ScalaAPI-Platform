using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ScalaAPI.Data.Exports;

public sealed record ExportJob(
    long JobId,
    long UserId,
    string Status,
    string RequestFingerprint,
    string? ArtifactKey,
    long? ArtifactSizeBytes,
    string? ArtifactHash,
    string? DownloadToken,
    DateTime? DownloadTokenExpiresAt,
    int DownloadCount,
    int MaxDownloads,
    DateTime ExpiresAt,
    string? Error,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ExportRequestResult(
    ExportJob Job,
    bool AlreadyExists);

public sealed record ExportDownloadResult(
    string ContentType,
    byte[] Content,
    string FileName);

public sealed record SensitiveFieldFilter(string[] FieldsToRedact);

public sealed class ExportService(
    NpgsqlDataSource dataSource,
    ILogger<ExportService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly SensitiveFieldFilter DefaultSensitiveFilter = new([
        "password_hash", "refresh_token", "key_hash", "key_secret",
        "audio_content", "audio_data", "raw_audio",
    ]);

    public const int MaxExportLimit = 1_000;
    public static readonly TimeSpan DownloadTokenLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ExportExpiry = TimeSpan.FromHours(24);
    public const int MaxDownloadsPerJob = 3;

    public async Task<ExportRequestResult> RequestExportAsync(
        long userId,
        string? clientIp,
        int limit = MaxExportLimit,
        CancellationToken ct = default)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        limit = Math.Clamp(limit, 1, MaxExportLimit);

        var fingerprint = ComputeFingerprint(userId, limit);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Idempotency: check for existing job with same fingerprint.
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT job_id, user_id, status, request_fingerprint, artifact_key,
                       artifact_size_bytes, artifact_hash, download_token,
                       download_token_expires_at, download_count, max_downloads,
                       expires_at, error, created_at, updated_at
                FROM export_jobs
                WHERE user_id = $1 AND request_fingerprint = $2
                FOR UPDATE
                """;
            existing.Parameters.AddWithValue(userId);
            existing.Parameters.AddWithValue(fingerprint);
            await using var reader = await existing.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var job = ReadJob(reader);
                await reader.DisposeAsync();
                await transaction.CommitAsync(ct);
                return new ExportRequestResult(job, AlreadyExists: true);
            }
        }

        // Create new export job.
        var expiresAt = DateTime.UtcNow.Add(ExportExpiry);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO export_jobs(user_id, status, request_fingerprint, expires_at)
                VALUES ($1, 'pending', $2, $3)
                RETURNING job_id, user_id, status, request_fingerprint, artifact_key,
                          artifact_size_bytes, artifact_hash, download_token,
                          download_token_expires_at, download_count, max_downloads,
                          expires_at, error, created_at, updated_at
                """;
            insert.Parameters.AddWithValue(userId);
            insert.Parameters.AddWithValue(fingerprint);
            insert.Parameters.AddWithValue(expiresAt);
            await using var reader = await insert.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("Export job insert returned no row");
            var job = ReadJob(reader);
            await reader.DisposeAsync();

            // Audit log.
            await using var audit = connection.CreateCommand();
            audit.Transaction = transaction;
            audit.CommandText = """
                INSERT INTO audit_logs(user_id, action, resource_type, resource_id, details, ip_address)
                VALUES ($1, 'export.requested', 'export_job', $2, $3, $4)
                """;
            audit.Parameters.AddWithValue(userId);
            audit.Parameters.AddWithValue(job.JobId.ToString());
            audit.Parameters.AddWithValue(JsonSerializer.Serialize(new { limit }, JsonOptions));
            audit.Parameters.AddWithValue((object?)clientIp ?? DBNull.Value);
            await audit.ExecuteNonQueryAsync(ct);

            await transaction.CommitAsync(ct);
            return new ExportRequestResult(job, AlreadyExists: false);
        }
    }

    public async Task<ExportJob?> GenerateExportAsync(
        long jobId, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);

        // Claim the job (transition pending -> generating).
        await using (var claim = connection.CreateCommand())
        {
            claim.CommandText = """
                UPDATE export_jobs SET status = 'generating', updated_at = now()
                WHERE job_id = $1 AND status = 'pending'
                RETURNING job_id
                """;
            claim.Parameters.AddWithValue(jobId);
            var claimed = await claim.ExecuteScalarAsync(ct);
            if (claimed is null) return null; // Already claimed or not pending.
        }

        try
        {
            // Gather user data with sensitive field filtering.
            var exportData = await GatherUserDataAsync(connection, jobId, ct);
            var json = JsonSerializer.Serialize(exportData, JsonOptions);
            var content = Encoding.UTF8.GetBytes(json);
            var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

            // Store artifact (in production this would go to object storage;
            // here we store the hash and size for verification).
            var artifactKey = $"exports/{jobId}/{hash}.json";

            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE export_jobs
                SET status = 'ready', artifact_key = $2, artifact_size_bytes = $3,
                    artifact_hash = $4, updated_at = now()
                WHERE job_id = $1
                """;
            update.Parameters.AddWithValue(jobId);
            update.Parameters.AddWithValue(artifactKey);
            update.Parameters.AddWithValue((long)content.Length);
            update.Parameters.AddWithValue(hash);
            await update.ExecuteNonQueryAsync(ct);

            return await GetJobAsync(jobId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Export generation failed for job {JobId}", jobId);
            await using var fail = connection.CreateCommand();
            fail.CommandText = """
                UPDATE export_jobs SET status = 'failed', error = $2, updated_at = now()
                WHERE job_id = $1
                """;
            fail.Parameters.AddWithValue(jobId);
            fail.Parameters.AddWithValue(ex.Message[..Math.Min(ex.Message.Length, 1000)]);
            await fail.ExecuteNonQueryAsync(ct);
            return null;
        }
    }

    public async Task<ExportJob?> GetJobAsync(long jobId, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT job_id, user_id, status, request_fingerprint, artifact_key,
                   artifact_size_bytes, artifact_hash, download_token,
                   download_token_expires_at, download_count, max_downloads,
                   expires_at, error, created_at, updated_at
            FROM export_jobs WHERE job_id = $1
            """;
        command.Parameters.AddWithValue(jobId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadJob(reader) : null;
    }

    public async Task<ExportJob?> GetJobForUserAsync(
        long jobId, long userId, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT job_id, user_id, status, request_fingerprint, artifact_key,
                   artifact_size_bytes, artifact_hash, download_token,
                   download_token_expires_at, download_count, max_downloads,
                   expires_at, error, created_at, updated_at
            FROM export_jobs WHERE job_id = $1 AND user_id = $2
            """;
        command.Parameters.AddWithValue(jobId);
        command.Parameters.AddWithValue(userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadJob(reader) : null;
    }

    public async Task<string?> IssueDownloadTokenAsync(
        long jobId, long userId, CancellationToken ct = default)
    {
        var job = await GetJobForUserAsync(jobId, userId, ct);
        if (job is null || job.Status != "ready") return null;

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var expiresAt = DateTime.UtcNow.Add(DownloadTokenLifetime);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE export_jobs
            SET download_token = $2, download_token_expires_at = $3, updated_at = now()
            WHERE job_id = $1 AND user_id = $4 AND status = 'ready'
            RETURNING download_token
            """;
        command.Parameters.AddWithValue(jobId);
        command.Parameters.AddWithValue(token);
        command.Parameters.AddWithValue(expiresAt);
        command.Parameters.AddWithValue(userId);
        var result = await command.ExecuteScalarAsync(ct);
        return result as string;
    }

    public async Task<ExportDownloadResult?> DownloadAsync(
        long jobId, string downloadToken, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Validate token and increment download count atomically.
        await using var validate = connection.CreateCommand();
        validate.Transaction = transaction;
        validate.CommandText = """
            SELECT job_id, user_id, artifact_key, artifact_size_bytes, artifact_hash,
                   download_token, download_token_expires_at, download_count, max_downloads,
                   expires_at, status
            FROM export_jobs
            WHERE job_id = $1 AND download_token = $2
            FOR UPDATE
            """;
        validate.Parameters.AddWithValue(jobId);
        validate.Parameters.AddWithValue(downloadToken);
        await using var reader = await validate.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            await reader.DisposeAsync();
            await transaction.RollbackAsync(ct);
            return null; // Invalid token or job not found.
        }

        var status = reader.GetString(10);
        var tokenExpiresAt = reader.IsDBNull(6) ? (DateTime?)null : reader.GetDateTime(6);
        var downloadCount = reader.GetInt32(7);
        var maxDownloads = reader.GetInt32(8);
        var expiresAt = reader.GetDateTime(9);
        var artifactKey = reader.IsDBNull(2) ? null : reader.GetString(2);
        var userId = reader.GetInt64(1);
        await reader.DisposeAsync();

        // Authorization checks.
        if (status != "ready") { await transaction.RollbackAsync(ct); return null; }
        if (DateTime.UtcNow > expiresAt) { await transaction.RollbackAsync(ct); return null; }
        if (tokenExpiresAt.HasValue && DateTime.UtcNow > tokenExpiresAt.Value)
        {
            await transaction.RollbackAsync(ct);
            return null; // Token expired.
        }
        if (downloadCount >= maxDownloads)
        {
            await transaction.RollbackAsync(ct);
            return null; // Download limit exceeded.
        }

        // Increment download count.
        await using var increment = connection.CreateCommand();
        increment.Transaction = transaction;
        increment.CommandText = """
            UPDATE export_jobs
            SET download_count = download_count + 1, updated_at = now()
            WHERE job_id = $1
            """;
        increment.Parameters.AddWithValue(jobId);
        await increment.ExecuteNonQueryAsync(ct);

        await transaction.CommitAsync(ct);

        // Generate the export content (in production, fetch from object storage).
        var exportData = await GatherUserDataAsync(connection, jobId, ct);
        var json = JsonSerializer.Serialize(exportData, JsonOptions);
        var content = Encoding.UTF8.GetBytes(json);

        return new ExportDownloadResult(
            "application/json",
            content,
            $"user-{userId}-export-{jobId}.json");
    }

    public async Task ExpireStaleJobsAsync(CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE export_jobs
            SET status = 'expired', updated_at = now()
            WHERE status IN ('pending', 'generating', 'ready')
              AND expires_at < now()
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task ReclaimStaleGeneratingJobsAsync(
        TimeSpan staleAfter, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE export_jobs
            SET status = 'pending', error = 'worker_crash_reclaimed', updated_at = now()
            WHERE status = 'generating' AND updated_at < $1
            """;
        command.Parameters.AddWithValue(DateTime.UtcNow.Subtract(staleAfter));
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<Dictionary<string, object>> GatherUserDataAsync(
        NpgsqlConnection connection, long jobId, CancellationToken ct)
    {
        // Get the user_id from the job.
        await using var jobCmd = connection.CreateCommand();
        jobCmd.CommandText = "SELECT user_id FROM export_jobs WHERE job_id = $1";
        jobCmd.Parameters.AddWithValue(jobId);
        var userId = (long)(await jobCmd.ExecuteScalarAsync(ct))!;

        var result = new Dictionary<string, object>();

        // Account (redact sensitive fields).
        await using var acctCmd = connection.CreateCommand();
        acctCmd.CommandText = """
            SELECT id, email, display_name, status, role, email_verified, created_at, last_login_at
            FROM user_accounts WHERE id = $1
            """;
        acctCmd.Parameters.AddWithValue(userId);
        await using (var reader = await acctCmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                result["account"] = new Dictionary<string, object?>
                {
                    ["id"] = reader.GetInt64(0),
                    ["email"] = reader.GetString(1),
                    ["display_name"] = reader.IsDBNull(2) ? null : reader.GetString(2),
                    ["status"] = reader.GetString(3),
                    ["role"] = reader.GetString(4),
                    ["email_verified"] = reader.GetBoolean(5),
                    ["created_at"] = reader.GetDateTime(6),
                    ["last_login_at"] = reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                };
            }
        }

        // API keys (only metadata, no secrets).
        result["api_keys"] = await ReadListAsync(connection, """
            SELECT id, key_prefix, name, status, created_at, last_used_at
            FROM user_api_keys WHERE user_email = (SELECT email FROM user_accounts WHERE id = $1)
            ORDER BY created_at DESC LIMIT 100
            """, [userId], ct, reader => new Dictionary<string, object?>
        {
            ["id"] = reader.GetInt64(0),
            ["prefix"] = reader.GetString(1),
            ["name"] = reader.IsDBNull(2) ? null : reader.GetString(2),
            ["status"] = reader.GetString(3),
            ["created_at"] = reader.GetDateTime(4),
            ["last_used_at"] = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
        });

        // Usage logs.
        result["usage"] = await ReadListAsync(connection, """
            SELECT request_id, model, input_tokens, output_tokens, cost_usd, duration_ms, created_at
            FROM usage_logs WHERE user_id = $1 ORDER BY created_at DESC LIMIT 100
            """, [userId], ct, reader => new Dictionary<string, object?>
        {
            ["request_id"] = reader.GetString(0),
            ["model"] = reader.GetString(1),
            ["input_tokens"] = reader.GetInt32(2),
            ["output_tokens"] = reader.GetInt32(3),
            ["cost_usd"] = reader.GetDecimal(4),
            ["duration_ms"] = reader.GetInt32(5),
            ["created_at"] = reader.GetDateTime(6),
        });

        // Sessions (no token values).
        result["sessions"] = await ReadListAsync(connection, """
            SELECT created_at, last_seen_at, expires_at, revoked_at, ip_address, user_agent
            FROM auth_sessions WHERE user_id = $1 ORDER BY created_at DESC LIMIT 100
            """, [userId], ct, reader => new Dictionary<string, object?>
        {
            ["created_at"] = reader.GetDateTime(0),
            ["last_seen_at"] = reader.GetDateTime(1),
            ["expires_at"] = reader.GetDateTime(2),
            ["revoked_at"] = reader.IsDBNull(3) ? null : reader.GetDateTime(3),
            ["ip_address"] = reader.IsDBNull(4) ? null : reader.GetString(4),
            ["user_agent"] = reader.IsDBNull(5) ? null : reader.GetString(5),
        });

        // Passkeys (metadata only).
        result["passkeys"] = await ReadListAsync(connection, """
            SELECT display_name, created_at, last_used_at
            FROM passkey_credentials WHERE user_id = $1 ORDER BY created_at DESC LIMIT 100
            """, [userId], ct, reader => new Dictionary<string, object?>
        {
            ["display_name"] = reader.GetString(0),
            ["created_at"] = reader.GetDateTime(1),
            ["last_used_at"] = reader.IsDBNull(2) ? null : reader.GetDateTime(2),
        });

        result["exported_at"] = DateTime.UtcNow;
        result["sensitive_fields_redacted"] = true;
        return result;
    }

    private static async Task<List<Dictionary<string, object?>>> ReadListAsync(
        NpgsqlConnection connection, string sql, object[] parameters,
        CancellationToken ct, Func<NpgsqlDataReader, Dictionary<string, object?>> mapper)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var p in parameters) command.Parameters.AddWithValue(p);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
            items.Add(mapper(reader));
        return items;
    }

    private static ExportJob ReadJob(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetInt64(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetDateTime(8),
        reader.GetInt32(9), reader.GetInt32(10),
        reader.GetDateTime(11),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.GetDateTime(13), reader.GetDateTime(14));

    private static string ComputeFingerprint(long userId, int limit) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"user={userId};limit={limit}")))
            .ToLowerInvariant();
}
