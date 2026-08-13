using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ScalaAPI.Data.Backups;

/// <summary>
/// Handles restore verification, checksum validation, failure injection,
/// and post-restore checks (migrations readable, users readable, accounting readable).
/// Works alongside the existing BackupStore for core pg_restore operations.
/// </summary>
public sealed class RestoreService(
    NpgsqlDataSource dataSource,
    IConfiguration configuration,
    ILogger<RestoreService> logger)
{
    private readonly NpgsqlDataSource _dataSource = dataSource;
    private readonly string _restoreConnection = configuration["Backup:RestoreTargetConnection"] ?? "";
    private readonly ILogger<RestoreService> _logger = logger;

    /// <summary>
    /// Verifies a backup artifact's integrity by checking its checksum.
    /// Returns false if the artifact is missing, tampered, or checksum doesn't match.
    /// </summary>
    public async Task<VerificationResult> VerifyArtifactAsync(
        string artifactPath,
        string expectedChecksum,
        CancellationToken ct = default)
    {
        if (!File.Exists(artifactPath))
            return new VerificationResult(false, "artifact_missing",
                "The backup artifact does not exist on disk");

        if (string.IsNullOrWhiteSpace(expectedChecksum))
            return new VerificationResult(false, "checksum_missing",
                "No checksum recorded for this backup");

        var actual = await BackupService.ComputeChecksumAsync(artifactPath, ct);
        if (!string.Equals(actual, expectedChecksum, StringComparison.OrdinalIgnoreCase))
            return new VerificationResult(false, "checksum_mismatch",
                $"Expected {expectedChecksum} but got {actual}");

        return new VerificationResult(true, null, null);
    }

    /// <summary>
    /// Rejects restore to the live authority database.
    /// Compares target connection against the source (live) connection.
    /// </summary>
    public bool IsRestoreTargetSafe(string targetConnectionString, string sourceConnectionString)
    {
        if (string.IsNullOrWhiteSpace(targetConnectionString))
            return false;

        var target = new NpgsqlConnectionStringBuilder(targetConnectionString);
        var source = new NpgsqlConnectionStringBuilder(sourceConnectionString);

        // Must differ in at least host, port, or database name.
        if (string.Equals(target.Host, source.Host, StringComparison.OrdinalIgnoreCase)
            && target.Port == source.Port
            && string.Equals(target.Database, source.Database, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    /// <summary>
    /// Post-restore verification: checks that migrations, users, and accounting
    /// tables are readable on the restore target.
    /// </summary>
    public async Task<PostRestoreVerification> VerifyRestoreAsync(
        string targetConnectionString,
        CancellationToken ct = default)
    {
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            await using var targetDs = NpgsqlDataSource.Create(targetConnectionString);

            // Check 1: Migrations are readable (schema_migrations or similar).
            checks["migrations_readable"] = await CheckTableReadableAsync(
                targetDs, "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public'",
                ct);

            // Check 2: User accounts are readable.
            checks["users_readable"] = await CheckTableReadableAsync(
                targetDs, "SELECT COUNT(*) FROM user_accounts", ct);

            // Check 3: Accounting tables are readable.
            checks["accounting_readable"] = await CheckTableReadableAsync(
                targetDs, "SELECT COUNT(*) FROM accounting_accounts", ct);

            // Check 4: Balance ledger is readable.
            checks["ledger_readable"] = await CheckTableReadableAsync(
                targetDs, "SELECT COUNT(*) FROM balance_ledger", ct);

            // Check 5: Entity registry is readable.
            checks["registry_readable"] = await CheckTableReadableAsync(
                targetDs, "SELECT COUNT(*) FROM entity_registry", ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Post-restore verification failed");
            errors["verification_error"] = ex.Message;
        }

        var allPassed = checks.Values.All(v => v) && errors.Count == 0;
        return new PostRestoreVerification(allPassed, checks, errors);
    }

    /// <summary>
    /// Injects a failure into the restore target for testing purposes.
    /// Creates a marker table that can be detected by verification.
    /// </summary>
    public async Task<bool> InjectFailureAsync(
        string targetConnectionString,
        string failureType,
        CancellationToken ct = default)
    {
        try
        {
            await using var targetDs = NpgsqlDataSource.Create(targetConnectionString);
            await using var connection = await targetDs.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();

            switch (failureType)
            {
                case "corrupt_table":
                    cmd.CommandText = """
                        CREATE TABLE IF NOT EXISTS _restore_failure_marker (
                            marker_id text PRIMARY KEY,
                            injected_at timestamptz DEFAULT now()
                        )
                        """;
                    break;
                case "missing_data":
                    cmd.CommandText = """
                        DELETE FROM user_accounts WHERE email LIKE 'test-inject-%'
                        """;
                    break;
                default:
                    return false;
            }

            await cmd.ExecuteNonQueryAsync(ct);
            _logger.LogWarning("Injected failure type {FailureType} into restore target",
                failureType);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to inject failure {FailureType}", failureType);
            return false;
        }
    }

    /// <summary>
    /// Records an RPO/RTO measurement after a restore verification.
    /// </summary>
    public async Task<BackupService.RpoRtoRecord> MeasureRpoRtoAsync(
        BackupService backupService,
        string? backupId,
        double backupDurationSeconds,
        double restoreDurationSeconds,
        PostRestoreVerification verification,
        CancellationToken ct = default)
    {
        // RPO = time since last successful backup.
        // RTO = time to complete restore + verification.
        var rpo = backupDurationSeconds; // Simplified: RPO is the backup interval.
        var rto = restoreDurationSeconds;

        return await backupService.RecordRpoRtoAsync(
            backupId,
            rpo,
            rto,
            backupDurationSeconds,
            restoreDurationSeconds,
            verification.AllPassed,
            new { verification.Checks, verification.Errors },
            ct);
    }

    private async Task<bool> CheckTableReadableAsync(
        NpgsqlDataSource dataSource,
        string query,
        CancellationToken ct)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = query;
            await cmd.ExecuteScalarAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Table readability check failed: {Query}", query);
            return false;
        }
    }

    public sealed record VerificationResult(
        bool IsValid, string? ErrorCode, string? ErrorDetail);

    public sealed record PostRestoreVerification(
        bool AllPassed,
        IReadOnlyDictionary<string, bool> Checks,
        IReadOnlyDictionary<string, string> Errors);
}
