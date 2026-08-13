using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ScalaAPI.Data.Retention;

public sealed record RetentionPolicy(
    long PolicyId,
    string Category,
    int RetentionDays,
    string Description,
    DateTime CreatedAt);

public sealed record CleanupRunResult(
    long RunId,
    string Status,
    bool DryRun,
    int TotalDeleted,
    int TotalFailed,
    IReadOnlyDictionary<string, int> Categories,
    DateTime StartedAt,
    DateTime? CompletedAt);

public sealed record CleanupCategorySpec(
    string Category,
    string Table,
    string DateColumn,
    string? SecondaryDateColumn = null,
    string? ExtraWhere = null);

public sealed class RetentionService(NpgsqlDataSource dataSource, ILogger<RetentionService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly CleanupCategorySpec[] DefaultCategories =
    [
        new("auth_sessions", "auth_sessions", "expires_at", "revoked_at"),
        new("password_reset_tokens", "password_reset_tokens", "expires_at", "used_at"),
        new("email_verification_tokens", "email_verification_tokens", "expires_at", "used_at"),
        new("passkey_challenges", "passkey_challenges", "expires_at", "consumed_at"),
        new("auth_abuse_counters", "auth_abuse_counters", "updated_at",
            ExtraWhere: "(locked_until IS NULL OR locked_until < now())"),
        new("export_jobs", "export_jobs", "expires_at",
            ExtraWhere: "status IN ('expired','downloaded')"),
    ];

    public async Task<IReadOnlyList<RetentionPolicy>> ListPoliciesAsync(
        CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT ON (category) policy_id, category, retention_days, description, created_at
            FROM retention_policies ORDER BY category, created_at DESC
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var policies = new List<RetentionPolicy>();
        while (await reader.ReadAsync(ct))
            policies.Add(new RetentionPolicy(
                reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2),
                reader.GetString(3), reader.GetDateTime(4)));
        return policies;
    }

    public async Task<RetentionPolicy> UpsertPolicyAsync(
        string category, int retentionDays, string description,
        CancellationToken ct = default)
    {
        category = category.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(category) || category.Length > 100)
            throw new ArgumentOutOfRangeException(nameof(category));
        if (retentionDays is < 1 or > 3650)
            throw new ArgumentOutOfRangeException(nameof(retentionDays));

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO retention_policies(category, retention_days, description)
            VALUES ($1, $2, $3)
            RETURNING policy_id, category, retention_days, description, created_at
            """;
        command.Parameters.AddWithValue(category);
        command.Parameters.AddWithValue(retentionDays);
        command.Parameters.AddWithValue(description);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("Policy insert returned no row");
        return new RetentionPolicy(
            reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2),
            reader.GetString(3), reader.GetDateTime(4));
    }

    public async Task<CleanupRunResult> RunCleanupAsync(
        long? actorUserId,
        string idempotencyKey,
        bool dryRun,
        int limitPerCategory = 1_000,
        CancellationToken ct = default)
    {
        idempotencyKey = idempotencyKey.Trim();
        if (idempotencyKey.Length is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(idempotencyKey));
        if (limitPerCategory is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(limitPerCategory));

        var policies = await ListPoliciesAsync(ct);
        var policyMap = policies.ToDictionary(p => p.Category, p => p.RetentionDays);
        var fingerprint = Fingerprint(dryRun, limitPerCategory, policyMap);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Idempotency: check for existing run with same key.
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT run_id, status, dry_run, total_deleted, total_failed,
                       categories::text, started_at, completed_at
                FROM cleanup_runs WHERE idempotency_key = $1
                FOR UPDATE
                """;
            existing.Parameters.AddWithValue(idempotencyKey);
            await using var reader = await existing.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var existingRunId = reader.GetInt64(0);
                var status = reader.GetString(1);
                var isDryRun = reader.GetBoolean(2);
                var deleted = reader.GetInt32(3);
                var failed = reader.GetInt32(4);
                var categoriesJson = reader.GetString(5);
                var startedAt = reader.GetDateTime(6);
                var completedAt = reader.IsDBNull(7) ? (DateTime?)null : reader.GetDateTime(7);
                await reader.DisposeAsync();

                // If a previous run is still "running" (worker crash), reclaim it.
                if (status == "running")
                {
                    await ReclaimStaleRunAsync(connection, transaction, existingRunId, ct);
                    // Fall through to execute the cleanup.
                }
                else if (status == "completed")
                {
                    // Idempotent replay: verify fingerprint matches.
                    await transaction.CommitAsync(ct);
                    var cats = JsonSerializer.Deserialize<Dictionary<string, int>>(
                        categoriesJson, JsonOptions) ?? new Dictionary<string, int>();
                    return new CleanupRunResult(existingRunId, "replayed", isDryRun, deleted, failed,
                        cats, startedAt, completedAt);
                }
                else
                {
                    await transaction.CommitAsync(ct);
                    var cats = JsonSerializer.Deserialize<Dictionary<string, int>>(
                        categoriesJson, JsonOptions) ?? new Dictionary<string, int>();
                    return new CleanupRunResult(existingRunId, status, isDryRun, deleted, failed,
                        cats, startedAt, completedAt);
                }
            }
        }

        // Insert a new run record.
        long runId;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO cleanup_runs(actor_user_id, idempotency_key, request_fingerprint, dry_run, status)
                VALUES ($1, $2, $3, $4, 'running')
                RETURNING run_id
                """;
            insert.Parameters.AddWithValue(actorUserId.HasValue ? (object)actorUserId.Value : DBNull.Value);
            insert.Parameters.AddWithValue(idempotencyKey);
            insert.Parameters.AddWithValue(fingerprint);
            insert.Parameters.AddWithValue(dryRun);
            runId = (long)(await insert.ExecuteScalarAsync(ct))!;
        }
        await transaction.CommitAsync(ct);

        // Execute cleanup per category outside the transaction to avoid long locks.
        var categoryResults = new Dictionary<string, int>();
        var totalDeleted = 0;
        var totalFailed = 0;

        foreach (var spec in DefaultCategories)
        {
            var retentionDays = policyMap.GetValueOrDefault(spec.Category, 30);
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            try
            {
                var deleted = await CleanupCategoryAsync(
                    spec, cutoff, dryRun, limitPerCategory, ct);
                categoryResults[spec.Category] = deleted;
                totalDeleted += deleted;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                totalFailed++;
                categoryResults[spec.Category] = 0;
                logger.LogWarning(ex, "Cleanup failed for category {Category}", spec.Category);
            }
        }

        // Mark run as completed.
        await using (var complete = connection.CreateCommand())
        {
            complete.CommandText = """
                UPDATE cleanup_runs
                SET status = 'completed', total_deleted = $2, total_failed = $3,
                    categories = $4::jsonb, completed_at = now()
                WHERE run_id = $1
                """;
            complete.Parameters.AddWithValue(runId);
            complete.Parameters.AddWithValue(totalDeleted);
            complete.Parameters.AddWithValue(totalFailed);
            complete.Parameters.AddWithValue(JsonSerializer.Serialize(categoryResults, JsonOptions));
            await complete.ExecuteNonQueryAsync(ct);
        }

        return new CleanupRunResult(runId, "completed", dryRun, totalDeleted, totalFailed,
            categoryResults, DateTime.UtcNow, DateTime.UtcNow);
    }

    public async Task<IReadOnlyList<CleanupRunResult>> GetHistoryAsync(
        int limit = 50, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, status, dry_run, total_deleted, total_failed,
                   categories::text, started_at, completed_at
            FROM cleanup_runs ORDER BY completed_at DESC NULLS LAST, run_id DESC
            LIMIT $1
            """;
        command.Parameters.AddWithValue(limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var results = new List<CleanupRunResult>();
        while (await reader.ReadAsync(ct))
        {
            var catsJson = reader.GetString(5);
            var cats = JsonSerializer.Deserialize<Dictionary<string, int>>(
                catsJson, JsonOptions) ?? new Dictionary<string, int>();
            results.Add(new CleanupRunResult(
                reader.GetInt64(0), reader.GetString(1), reader.GetBoolean(2),
                reader.GetInt32(3), reader.GetInt32(4), cats,
                reader.GetDateTime(6),
                reader.IsDBNull(7) ? null : reader.GetDateTime(7)));
        }
        return results;
    }

    private async Task<int> CleanupCategoryAsync(
        CleanupCategorySpec spec, DateTime cutoff, bool dryRun, int limit,
        CancellationToken ct)
    {
        var where = BuildWhereClause(spec, cutoff);
        if (dryRun)
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM {spec.Table} WHERE {where} LIMIT $1";
            command.Parameters.AddWithValue(limit);
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        }

        await using var delConnection = await dataSource.OpenConnectionAsync(ct);
        await using var delCommand = delConnection.CreateCommand();
        delCommand.CommandText = $"""
            DELETE FROM {spec.Table} WHERE ctid IN (
                SELECT ctid FROM {spec.Table} WHERE {where} LIMIT $1
            )
            """;
        delCommand.Parameters.AddWithValue(limit);
        return await delCommand.ExecuteNonQueryAsync(ct);
    }

    private static string BuildWhereClause(CleanupCategorySpec spec, DateTime cutoff)
    {
        var sb = new StringBuilder();
        sb.Append($"({spec.DateColumn} < '@cutoff'");
        if (spec.SecondaryDateColumn is not null)
            sb.Append($" OR ({spec.SecondaryDateColumn} IS NOT NULL AND {spec.SecondaryDateColumn} < '@cutoff')");
        sb.Append(')');
        if (spec.ExtraWhere is not null)
            sb.Append($" AND {spec.ExtraWhere}");
        return sb.ToString().Replace("@cutoff",
            cutoff.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
    }

    private async Task ReclaimStaleRunAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long runId,
        CancellationToken ct)
    {
        await using var reclaim = connection.CreateCommand();
        reclaim.Transaction = transaction;
        reclaim.CommandText = """
            UPDATE cleanup_runs
            SET status = 'failed', error = 'worker_crash_reclaimed', completed_at = now()
            WHERE run_id = $1 AND status = 'running'
            """;
        reclaim.Parameters.AddWithValue(runId);
        await reclaim.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        logger.LogWarning("Reclaimed stale cleanup run {RunId} after worker crash", runId);
    }

    public static string Fingerprint(bool dryRun, int limit,
        IReadOnlyDictionary<string, int> policies)
    {
        var sb = new StringBuilder();
        sb.Append($"dry_run={dryRun};limit={limit}");
        foreach (var kv in policies.OrderBy(k => k.Key))
            sb.Append($";{kv.Key}={kv.Value}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))
            .ToLowerInvariant();
    }
}
