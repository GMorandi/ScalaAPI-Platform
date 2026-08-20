using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ScalaAPI.Data.Backups;

/// <summary>
/// Handles backup encryption, signing, offsite upload, retention enforcement,
/// and key rotation. Works alongside the existing BackupStore for core pg_dump/restore.
/// </summary>
public sealed class BackupService(
    NpgsqlDataSource dataSource,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<BackupService> logger)
{
    private readonly NpgsqlDataSource _dataSource = dataSource;
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly ILogger<BackupService> _logger = logger;
    private readonly string _directory = configuration["Backup:Directory"]?.Trim() is { Length: > 0 } dir
        ? dir : "/var/lib/scalaapi/backups";
    private readonly string _pgDump = configuration["Backup:PgDumpPath"]?.Trim() is { Length: > 0 } dump
        ? dump : "pg_dump";
    private readonly int _timeoutSeconds = Math.Clamp(
        configuration.GetValue("Backup:CommandTimeoutSeconds", 120), 5, 900);
    private readonly IConfiguration _configuration = configuration;
    private string SourceConnection => _configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required for pg_dump");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Encrypts a backup artifact in-place using AES-256-GCM with the active key.
    /// Returns the encryption metadata (key_id, nonce, tag) for verification.
    /// </summary>
    public async Task<EncryptionResult?> EncryptArtifactAsync(
        string artifactPath,
        CancellationToken ct = default)
    {
        var key = await GetActiveKeyAsync("aes-256-gcm", ct);
        if (key is null)
        {
            _logger.LogWarning("No active AES-256-GCM key found; skipping encryption");
            return null;
        }

        var plaintext = await File.ReadAllBytesAsync(artifactPath, ct);
        var nonce = RandomNumberGenerator.GetBytes(12); // 96-bit nonce for GCM
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16]; // 128-bit auth tag

        using var aes = new AesGcm(key.KeyMaterial, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Output format: [nonce(12)][tag(16)][ciphertext]
        await using (var output = File.Create(artifactPath + ".enc"))
        {
            await output.WriteAsync(nonce, ct);
            await output.WriteAsync(tag, ct);
            await output.WriteAsync(ciphertext, ct);
        }

        File.Delete(artifactPath);
        File.Move(artifactPath + ".enc", artifactPath);

        var checksum = await ComputeChecksumAsync(artifactPath, ct);
        var result = new EncryptionResult(
            key.KeyId,
            Convert.ToHexString(nonce).ToLowerInvariant(),
            Convert.ToHexString(tag).ToLowerInvariant(),
            checksum,
            "aes-256-gcm");

        _logger.LogInformation("Encrypted backup artifact {Path} with key {KeyId}",
            artifactPath, key.KeyId);
        return result;
    }

    /// <summary>
    /// Decrypts a backup artifact in-place using the specified key.
    /// </summary>
    public async Task<bool> DecryptArtifactAsync(
        string artifactPath,
        string keyId,
        string nonceHex,
        CancellationToken ct = default)
    {
        var key = await GetKeyByIdAsync(keyId, ct);
        if (key is null)
        {
            _logger.LogError("Key {KeyId} not found for decryption", keyId);
            return false;
        }

        var data = await File.ReadAllBytesAsync(artifactPath, ct);
        if (data.Length < 28) // 12 nonce + 16 tag minimum
        {
            _logger.LogError("Encrypted artifact too small: {Length} bytes", data.Length);
            return false;
        }

        var nonce = data[..12];
        var tag = data[12..28];
        var ciphertext = data[28..];
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(key.KeyMaterial, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "Decryption failed for artifact {Path} with key {KeyId}",
                artifactPath, keyId);
            return false;
        }

        await File.WriteAllBytesAsync(artifactPath, plaintext, ct);
        return true;
    }

    /// <summary>
    /// Signs a backup artifact using HMAC-SHA256 with the active signing key.
    /// Returns the signature for storage alongside the artifact.
    /// </summary>
    public async Task<SigningResult?> SignArtifactAsync(
        string artifactPath,
        CancellationToken ct = default)
    {
        var key = await GetActiveKeyAsync("hmac-sha256", ct);
        if (key is null)
        {
            _logger.LogWarning("No active HMAC-SHA256 key found; skipping signing");
            return null;
        }

        var data = await File.ReadAllBytesAsync(artifactPath, ct);
        var signature = HMACSHA256.HashData(key.KeyMaterial, data);
        var signatureHex = Convert.ToHexString(signature).ToLowerInvariant();

        _logger.LogInformation("Signed backup artifact {Path} with key {KeyId}",
            artifactPath, key.KeyId);
        return new SigningResult(key.KeyId, signatureHex, "hmac-sha256");
    }

    /// <summary>
    /// Verifies the signature of a backup artifact.
    /// </summary>
    public async Task<bool> VerifySignatureAsync(
        string artifactPath,
        string keyId,
        string expectedSignatureHex,
        CancellationToken ct = default)
    {
        var key = await GetKeyByIdAsync(keyId, ct);
        if (key is null)
        {
            _logger.LogError("Key {KeyId} not found for signature verification", keyId);
            return false;
        }

        var data = await File.ReadAllBytesAsync(artifactPath, ct);
        var actual = HMACSHA256.HashData(key.KeyMaterial, data);
        var actualHex = Convert.ToHexString(actual).ToLowerInvariant();

        var valid = string.Equals(actualHex, expectedSignatureHex, StringComparison.Ordinal);
        if (!valid)
            _logger.LogWarning("Signature mismatch for artifact {Path} with key {KeyId}",
                artifactPath, keyId);
        return valid;
    }

    /// <summary>
    /// Computes SHA-256 checksum of a file.
    /// </summary>
    public static async Task<string> ComputeChecksumAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Verifies a backup artifact's checksum against the expected value.
    /// </summary>
    public async Task<bool> VerifyChecksumAsync(
        string artifactPath,
        string expectedChecksum,
        CancellationToken ct = default)
    {
        var actual = await ComputeChecksumAsync(artifactPath, ct);
        return string.Equals(actual, expectedChecksum, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Uploads a backup artifact to an S3-compatible offsite target.
    /// Uses a simple HTTP PUT with pre-signed URL or AWS SDK-compatible approach.
    /// </summary>
    public async Task<OffsiteUploadResult> UploadOffsiteAsync(
        string backupId,
        string artifactPath,
        string offsiteUrl,
        string? bucket = null,
        CancellationToken ct = default)
    {
        var uploadId = $"upl_{Guid.NewGuid():N}";
        var checksum = await ComputeChecksumAsync(artifactPath, ct);
        var size = new FileInfo(artifactPath).Length;

        await using (var connection = await _dataSource.OpenConnectionAsync(ct))
        await using (var transaction = await connection.BeginTransactionAsync(ct))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO backup_offsite_uploads(upload_id, backup_id, provider, remote_url,
                    status, size_bytes, started_at)
                VALUES ($1, $2, 's3', $3, 'uploading', $4, now())
                """;
            insert.Parameters.AddWithValue(uploadId);
            insert.Parameters.AddWithValue(backupId);
            insert.Parameters.AddWithValue(offsiteUrl);
            insert.Parameters.AddWithValue(size);
            await insert.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
        }

        try
        {
            var remoteUrl = $"{offsiteUrl.TrimEnd('/')}/{backupId}/{Path.GetFileName(artifactPath)}";

            using var client = _httpClientFactory.CreateClient("BackupOffsite");
            await using var stream = File.OpenRead(artifactPath);
            using var content = new StreamContent(stream);
            content.Headers.Add("Content-SHA256", checksum);

            var response = await client.PutAsync(remoteUrl, content, ct);
            response.EnsureSuccessStatusCode();

            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE backup_offsite_uploads
                SET status = 'completed', remote_url = $2, remote_checksum = $3,
                    completed_at = now()
                WHERE upload_id = $1
                """;
            update.Parameters.AddWithValue(uploadId);
            update.Parameters.AddWithValue(remoteUrl);
            update.Parameters.AddWithValue(checksum);
            await update.ExecuteNonQueryAsync(ct);

            _logger.LogInformation("Offsite upload {UploadId} for backup {BackupId} completed",
                uploadId, backupId);
            return new OffsiteUploadResult(uploadId, remoteUrl, checksum, size, "completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Offsite upload {UploadId} for backup {BackupId} failed",
                uploadId, backupId);

            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE backup_offsite_uploads
                SET status = 'failed', error_message = $2
                WHERE upload_id = $1
                """;
            update.Parameters.AddWithValue(uploadId);
            update.Parameters.AddWithValue(SanitizeError(ex.Message));
            await update.ExecuteNonQueryAsync(ct);

            return new OffsiteUploadResult(uploadId, null, null, size, "failed");
        }
    }

    /// <summary>
    /// Executes pg_dump for a scheduled backup job, updates the job row on success/failure.
    /// Returns the artifact path on success, null on failure.
    /// </summary>
    public async Task<string?> RunScheduledDumpAsync(string jobId, CancellationToken ct = default)
    {
        var finalPath = Path.Combine(_directory, jobId + ".dump");
        var temporaryPath = Path.Combine(_directory, "." + jobId + ".tmp");

        try
        {
            Directory.CreateDirectory(_directory);
            await RunPgDumpAsync(temporaryPath, ct);

            var info = new FileInfo(temporaryPath);
            if (!info.Exists || info.Length <= 0)
                throw new InvalidOperationException("pg_dump produced an empty artifact");

            var sha = await Sha256FileAsync(temporaryPath, ct);
            File.Move(temporaryPath, finalPath);

            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE backup_jobs
                SET status = 'completed', artifact_path = $2, size_bytes = $3,
                    sha256 = $4, completed_at = now()
                WHERE id = $1 AND status = 'running'
                """;
            update.Parameters.AddWithValue(jobId);
            update.Parameters.AddWithValue(Path.GetFileName(finalPath));
            update.Parameters.AddWithValue(info.Length);
            update.Parameters.AddWithValue(sha);
            await update.ExecuteNonQueryAsync(ct);

            _logger.LogInformation("Scheduled dump completed for job {JobId}, size={Size}",
                jobId, info.Length);
            return finalPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled dump failed for job {JobId}", jobId);
            await FailScheduledDumpAsync(jobId, "dump_failed", SanitizeError(ex.Message));
            if (File.Exists(temporaryPath))
                TryDelete(temporaryPath);
            return null;
        }
    }

    /// <summary>
    /// Verifies an offsite upload by reading it back and comparing SHA-256.
    /// Updates the upload row with verified_at and verify_status.
    /// </summary>
    public async Task<bool> VerifyOffsiteUploadAsync(
        string uploadId,
        string remoteUrl,
        string expectedChecksum,
        CancellationToken ct = default)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient("BackupOffsite");
            await using var stream = await client.GetStreamAsync(remoteUrl, ct);
            var hash = await SHA256.HashDataAsync(stream, ct);
            var actual = Convert.ToHexString(hash).ToLowerInvariant();

            var verified = string.Equals(actual, expectedChecksum, StringComparison.OrdinalIgnoreCase);
            var status = verified ? "verified" : "mismatch";

            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE backup_offsite_uploads
                SET verified_at = now(), verify_status = $2
                WHERE upload_id = $1
                """;
            update.Parameters.AddWithValue(uploadId);
            update.Parameters.AddWithValue(status);
            await update.ExecuteNonQueryAsync(ct);

            if (!verified)
                _logger.LogWarning("Offsite verification mismatch for upload {UploadId}: expected={Expected}, actual={Actual}",
                    uploadId, expectedChecksum, actual);
            else
                _logger.LogInformation("Offsite upload {UploadId} verified", uploadId);

            return verified;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Offsite verification unreachable for upload {UploadId}", uploadId);

            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE backup_offsite_uploads
                SET verified_at = now(), verify_status = 'unreachable'
                WHERE upload_id = $1
                """;
            update.Parameters.AddWithValue(uploadId);
            await update.ExecuteNonQueryAsync(ct);

            return false;
        }
    }

    /// <summary>
    /// Reclaims zombie backup jobs stuck in 'running' state for over 1 hour.
    /// Marks them as failed with error_code 'zombie_reclaimed'.
    /// </summary>
    public async Task<int> ReclaimZombieBackupsAsync(CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE backup_jobs
            SET status = 'failed', error_code = 'zombie_reclaimed',
                error_detail = 'reclaimed by scheduler: stuck running >1h',
                completed_at = now()
            WHERE status = 'running'
              AND created_at < now() - interval '1 hour'
            """;
        var reclaimed = await cmd.ExecuteNonQueryAsync(ct);
        if (reclaimed > 0)
            _logger.LogWarning("Reclaimed {Count} zombie backup jobs", reclaimed);
        return reclaimed;
    }

    private async Task FailScheduledDumpAsync(string jobId, string errorCode, string errorDetail)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE backup_jobs
                SET status = 'failed', error_code = $2, error_detail = $3, completed_at = now()
                WHERE id = $1 AND status = 'running'
                """;
            update.Parameters.AddWithValue(jobId);
            update.Parameters.AddWithValue(errorCode);
            update.Parameters.AddWithValue(errorDetail);
            await update.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to persist failed backup {JobId}", jobId);
        }
    }

    private async Task RunPgDumpAsync(string path, CancellationToken ct)
    {
        var connection = new NpgsqlConnectionStringBuilder(SourceConnection);
        var psi = new ProcessStartInfo
        {
            FileName = _pgDump,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--host");
        psi.ArgumentList.Add(connection.Host ?? "localhost");
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--username");
        psi.ArgumentList.Add(connection.Username ?? "");
        psi.ArgumentList.Add("--dbname");
        psi.ArgumentList.Add(connection.Database ?? "postgres");
        psi.ArgumentList.Add("--format=custom");
        psi.ArgumentList.Add("--no-owner");
        psi.ArgumentList.Add("--no-privileges");
        psi.ArgumentList.Add("--file");
        psi.ArgumentList.Add(path);
        psi.Environment["PGPASSWORD"] = connection.Password;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException("Unable to start pg_dump");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        _ = await process.StandardOutput.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException("pg_dump exceeded the configured timeout");
        }

        var error = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"pg_dump failed: {SanitizeError(error)}");
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>
    /// Enforces retention policy by deleting expired backup artifacts and their records.
    /// </summary>
    public async Task<RetentionEnforcementResult> EnforceRetentionAsync(
        CancellationToken ct = default)
    {
        var policy = await GetRetentionPolicyAsync(ct);
        if (policy is null)
            return new RetentionEnforcementResult(0, 0, 0);

        var deleted = 0;
        var failed = 0;
        var freedBytes = 0L;

        await using var connection = await _dataSource.OpenConnectionAsync(ct);

        // Find expired backups.
        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT id, artifact_path, size_bytes
            FROM backup_jobs
            WHERE status = 'completed'
              AND retention_until IS NOT NULL
              AND retention_until < now()
            ORDER BY created_at ASC
            LIMIT 100
            """;
        await using var reader = await select.ExecuteReaderAsync(ct);
        var expired = new List<(string Id, string? ArtifactPath, long? SizeBytes)>();
        while (await reader.ReadAsync(ct))
            expired.Add((reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2)));
        await reader.DisposeAsync();

        foreach (var (id, artifactPath, sizeBytes) in expired)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(artifactPath) && File.Exists(artifactPath))
                {
                    File.Delete(artifactPath);
                    freedBytes += sizeBytes ?? 0;
                }

                await using var update = connection.CreateCommand();
                update.CommandText = """
                    UPDATE backup_jobs
                    SET status = 'failed', error_code = 'retention_expired',
                        error_detail = 'deleted by retention policy',
                        completed_at = now()
                    WHERE id = $1
                    """;
                update.Parameters.AddWithValue(id);
                await update.ExecuteNonQueryAsync(ct);
                deleted++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enforce retention for backup {BackupId}", id);
                failed++;
            }
        }

        _logger.LogInformation(
            "Retention enforcement: deleted={Deleted}, failed={Failed}, freed_bytes={FreedBytes}",
            deleted, failed, freedBytes);
        return new RetentionEnforcementResult(deleted, failed, freedBytes);
    }

    /// <summary>
    /// Gets or creates the default retention policy.
    /// </summary>
    public async Task<RetentionPolicyView?> GetRetentionPolicyAsync(CancellationToken ct = default)
    {
        await using var command = _dataSource.CreateCommand("""
            SELECT policy_id, keep_daily, keep_weekly, keep_monthly,
                   offsite_enabled, offsite_url, offsite_bucket, offsite_region,
                   encryption_enabled, signing_enabled, encryption_key_id, updated_at
            FROM backup_retention_policies
            WHERE policy_id = 'default'
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadPolicy(reader) : null;
    }

    /// <summary>
    /// Updates the retention policy.
    /// </summary>
    public async Task<RetentionPolicyView> UpsertRetentionPolicyAsync(
        int keepDaily,
        int keepWeekly,
        int keepMonthly,
        bool offsiteEnabled,
        string? offsiteUrl,
        string? offsiteBucket,
        string? offsiteRegion,
        bool encryptionEnabled,
        bool signingEnabled,
        string? encryptionKeyId,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO backup_retention_policies(
                policy_id, keep_daily, keep_weekly, keep_monthly,
                offsite_enabled, offsite_url, offsite_bucket, offsite_region,
                encryption_enabled, signing_enabled, encryption_key_id, updated_at)
            VALUES ('default', $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, now())
            ON CONFLICT (policy_id) DO UPDATE SET
                keep_daily = EXCLUDED.keep_daily,
                keep_weekly = EXCLUDED.keep_weekly,
                keep_monthly = EXCLUDED.keep_monthly,
                offsite_enabled = EXCLUDED.offsite_enabled,
                offsite_url = EXCLUDED.offsite_url,
                offsite_bucket = EXCLUDED.offsite_bucket,
                offsite_region = EXCLUDED.offsite_region,
                encryption_enabled = EXCLUDED.encryption_enabled,
                signing_enabled = EXCLUDED.signing_enabled,
                encryption_key_id = EXCLUDED.encryption_key_id,
                updated_at = now()
            RETURNING policy_id, keep_daily, keep_weekly, keep_monthly,
                      offsite_enabled, offsite_url, offsite_bucket, offsite_region,
                      encryption_enabled, signing_enabled, encryption_key_id, updated_at
            """;
        cmd.Parameters.AddWithValue(keepDaily);
        cmd.Parameters.AddWithValue(keepWeekly);
        cmd.Parameters.AddWithValue(keepMonthly);
        cmd.Parameters.AddWithValue(offsiteEnabled);
        cmd.Parameters.AddWithValue((object?)offsiteUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)offsiteBucket ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)offsiteRegion ?? DBNull.Value);
        cmd.Parameters.AddWithValue(encryptionEnabled);
        cmd.Parameters.AddWithValue(signingEnabled);
        cmd.Parameters.AddWithValue((object?)encryptionKeyId ?? DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return ReadPolicy(reader);
    }

    /// <summary>
    /// Creates a new signing/encryption key.
    /// </summary>
    public async Task<SigningKeyView> CreateKeyAsync(
        string algorithm,
        CancellationToken ct = default)
    {
        var keyId = $"key_{Guid.NewGuid():N}";
        byte[] keyMaterial = algorithm switch
        {
            "aes-256-gcm" => RandomNumberGenerator.GetBytes(32),
            "hmac-sha256" => RandomNumberGenerator.GetBytes(32),
            "ed25519" => RandomNumberGenerator.GetBytes(32),
            _ => throw new ArgumentException($"Unsupported algorithm: {algorithm}")
        };

        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO backup_signing_keys(key_id, algorithm, key_material, status)
            VALUES ($1, $2, $3, 'active')
            RETURNING key_id, algorithm, status, created_at
            """;
        cmd.Parameters.AddWithValue(keyId);
        cmd.Parameters.AddWithValue(algorithm);
        cmd.Parameters.AddWithValue(keyMaterial);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new SigningKeyView(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetDateTime(3));
    }

    /// <summary>
    /// Rotates the active key by retiring the current one and creating a new one.
    /// </summary>
    public async Task<SigningKeyView> RotateKeyAsync(
        string algorithm,
        CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        // Retire current active key.
        await using var retire = connection.CreateCommand();
        retire.CommandText = """
            UPDATE backup_signing_keys
            SET status = 'retired', retired_at = now()
            WHERE algorithm = $1 AND status = 'active'
            """;
        retire.Parameters.AddWithValue(algorithm);
        await retire.ExecuteNonQueryAsync(ct);

        // Create new key.
        return await CreateKeyAsync(algorithm, ct);
    }

    /// <summary>
    /// Records an RPO/RTO measurement.
    /// </summary>
    public async Task<RpoRtoRecord> RecordRpoRtoAsync(
        string? backupId,
        double rpoSeconds,
        double rtoSeconds,
        double backupDurationSeconds,
        double restoreDurationSeconds,
        bool verificationPassed,
        object? details = null,
        CancellationToken ct = default)
    {
        var recordId = $"rpo_{Guid.NewGuid():N}";
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO backup_rpo_rto_records(
                record_id, backup_id, rpo_seconds, rto_seconds,
                backup_duration_seconds, restore_duration_seconds,
                verification_passed, details)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            RETURNING record_id, measured_at
            """;
        cmd.Parameters.AddWithValue(recordId);
        cmd.Parameters.AddWithValue((object?)backupId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(rpoSeconds);
        cmd.Parameters.AddWithValue(rtoSeconds);
        cmd.Parameters.AddWithValue(backupDurationSeconds);
        cmd.Parameters.AddWithValue(restoreDurationSeconds);
        cmd.Parameters.AddWithValue(verificationPassed);
        cmd.Parameters.AddWithValue(details is not null
            ? JsonSerializer.Serialize(details, JsonOptions)
            : (object)DBNull.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new RpoRtoRecord(
            reader.GetString(0),
            backupId,
            reader.GetDateTime(1),
            rpoSeconds,
            rtoSeconds,
            backupDurationSeconds,
            restoreDurationSeconds,
            verificationPassed);
    }

    /// <summary>
    /// Gets the latest RPO/RTO measurements.
    /// </summary>
    public async Task<IReadOnlyList<RpoRtoRecord>> GetLatestRpoRtoAsync(
        int limit = 10,
        CancellationToken ct = default)
    {
        await using var command = _dataSource.CreateCommand("""
            SELECT record_id, backup_id, measured_at, rpo_seconds, rto_seconds,
                   backup_duration_seconds, restore_duration_seconds, verification_passed
            FROM backup_rpo_rto_records
            ORDER BY measured_at DESC
            LIMIT $1
            """);
        command.Parameters.AddWithValue(limit);
        var result = new List<RpoRtoRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new RpoRtoRecord(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetDateTime(2),
                reader.GetDouble(3),
                reader.GetDouble(4),
                reader.GetDouble(5),
                reader.GetDouble(6),
                reader.GetBoolean(7)));
        return result;
    }

    private async Task<StoredKey?> GetActiveKeyAsync(string algorithm, CancellationToken ct)
    {
        await using var command = _dataSource.CreateCommand("""
            SELECT key_id, algorithm, key_material, status, created_at
            FROM backup_signing_keys
            WHERE algorithm = $1 AND status = 'active'
            ORDER BY created_at DESC
            LIMIT 1
            """);
        command.Parameters.AddWithValue(algorithm);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new StoredKey(
            reader.GetString(0),
            reader.GetString(1),
            (byte[])reader.GetValue(2),
            reader.GetString(3),
            reader.GetDateTime(4));
    }

    private async Task<StoredKey?> GetKeyByIdAsync(string keyId, CancellationToken ct)
    {
        await using var command = _dataSource.CreateCommand("""
            SELECT key_id, algorithm, key_material, status, created_at
            FROM backup_signing_keys
            WHERE key_id = $1
            """);
        command.Parameters.AddWithValue(keyId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new StoredKey(
            reader.GetString(0),
            reader.GetString(1),
            (byte[])reader.GetValue(2),
            reader.GetString(3),
            reader.GetDateTime(4));
    }

    private static RetentionPolicyView ReadPolicy(NpgsqlDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetBoolean(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetBoolean(8),
            reader.GetBoolean(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetDateTime(11));

    private static string SanitizeError(string value)
    {
        var normalized = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (normalized.Length > 500) normalized = normalized[..500];
        return normalized.Contains("password", StringComparison.OrdinalIgnoreCase)
            ? "operation failed without safe diagnostic details" : normalized;
    }

    private sealed record StoredKey(
        string KeyId, string Algorithm, byte[] KeyMaterial, string Status, DateTime CreatedAt);

    public sealed record EncryptionResult(
        string KeyId, string Nonce, string Tag, string Checksum, string Algorithm);

    public sealed record SigningResult(
        string KeyId, string Signature, string Algorithm);

    public sealed record OffsiteUploadResult(
        string UploadId, string? RemoteUrl, string? RemoteChecksum, long SizeBytes, string Status);

    public sealed record RetentionEnforcementResult(
        int Deleted, int Failed, long FreedBytes);

    public sealed record RetentionPolicyView(
        string PolicyId, int KeepDaily, int KeepWeekly, int KeepMonthly,
        bool OffsiteEnabled, string? OffsiteUrl, string? OffsiteBucket, string? OffsiteRegion,
        bool EncryptionEnabled, bool SigningEnabled, string? EncryptionKeyId,
        DateTime UpdatedAt);

    public sealed record SigningKeyView(
        string KeyId, string Algorithm, string Status, DateTime CreatedAt);

    public sealed record RpoRtoRecord(
        string RecordId, string? BackupId, DateTime MeasuredAt,
        double RpoSeconds, double RtoSeconds,
        double BackupDurationSeconds, double RestoreDurationSeconds,
        bool VerificationPassed);
}
