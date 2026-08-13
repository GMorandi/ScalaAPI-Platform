using Npgsql;

namespace ScalaAPI.Data.Config;

public sealed record ConfigRevision(
    long RevisionId,
    string ConfigKey,
    string ConfigValue,
    long? PreviousRevisionId,
    long? ActorUserId,
    string? ActorReason,
    DateTime CreatedAt,
    DateTime? AppliedAt,
    DateTime? RolledBackAt,
    string Status);

public sealed record ConfigNodeObservation(
    string NodeId,
    long LastSeenRevision,
    DateTime LastSeenAt);

/// <summary>
/// Persists config revision history and per-node observation state.
/// All writes go through PostgreSQL so that multiple processes converge
/// on the same revision sequence without regression.
/// </summary>
public sealed class ConfigRevisionStore(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Records a new pending config revision and returns its id.
    /// </summary>
    public async Task<long> RecordRevisionAsync(
        string key, string value, long? previousRevisionId,
        long? actorUserId, string? reason, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO config_revisions (config_key, config_value, previous_revision_id,
                actor_user_id, actor_reason, status)
            VALUES ($1, $2, $3, $4, $5, 'pending')
            RETURNING revision_id
            """);
        command.Parameters.AddWithValue(key);
        command.Parameters.AddWithValue(value);
        command.Parameters.AddWithValue((object?)previousRevisionId ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)actorUserId ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)reason ?? DBNull.Value);
        var result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result!);
    }

    /// <summary>
    /// Returns the latest revision for a given key (any status).
    /// </summary>
    public async Task<ConfigRevision?> GetLatestRevisionAsync(
        string key, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT revision_id, config_key, config_value, previous_revision_id,
                   actor_user_id, actor_reason, created_at, applied_at,
                   rolled_back_at, status
            FROM config_revisions
            WHERE config_key = $1
            ORDER BY revision_id DESC
            LIMIT 1
            """);
        command.Parameters.AddWithValue(key);
        return await ReadSingleAsync(command, ct);
    }

    /// <summary>
    /// Returns all revisions for a key, newest first.
    /// </summary>
    public async Task<IReadOnlyList<ConfigRevision>> ListRevisionsAsync(
        string? key = null, int limit = 50, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        await using var command = dataSource.CreateCommand("""
            SELECT revision_id, config_key, config_value, previous_revision_id,
                   actor_user_id, actor_reason, created_at, applied_at,
                   rolled_back_at, status
            FROM config_revisions
            WHERE ($1::text IS NULL OR config_key = $1)
            ORDER BY revision_id DESC
            LIMIT $2
            """);
        command.Parameters.AddWithValue((object?)key ?? DBNull.Value);
        command.Parameters.AddWithValue(limit);
        return await ReadManyAsync(command, ct);
    }

    /// <summary>
    /// Marks a pending revision as applied.
    /// </summary>
    public async Task MarkAppliedAsync(long revisionId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE config_revisions
            SET status = 'applied', applied_at = now()
            WHERE revision_id = $1 AND status = 'pending'
            """);
        command.Parameters.AddWithValue(revisionId);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Marks a revision as rolled back. Only applied or pending revisions can be rolled back.
    /// Creates a compensating revision that restores the previous value.
    /// </summary>
    public async Task<bool> RollbackAsync(
        long revisionId, long? actorUserId, string? reason, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Fetch the revision to roll back
        await using var fetchCmd = new NpgsqlCommand("""
            SELECT revision_id, config_key, config_value, previous_revision_id, status
            FROM config_revisions
            WHERE revision_id = $1
            """, connection, transaction);
        fetchCmd.Parameters.AddWithValue(revisionId);
        long targetRevisionId;
        string configKey;
        long? previousRevisionId;
        string status;
        await using (var reader = await fetchCmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
                return false;
            targetRevisionId = reader.GetInt64(0);
            configKey = reader.GetString(1);
            previousRevisionId = reader.IsDBNull(3) ? null : reader.GetInt64(3);
            status = reader.GetString(4);
        }

        if (status is not ("applied" or "pending"))
            return false;

        // Mark the revision as rolled back
        await using var rollbackCmd = new NpgsqlCommand("""
            UPDATE config_revisions
            SET status = 'rolled_back', rolled_back_at = now()
            WHERE revision_id = $1
            """, connection, transaction);
        rollbackCmd.Parameters.AddWithValue(revisionId);
        await rollbackCmd.ExecuteNonQueryAsync(ct);

        // If there is a previous revision, create a compensating revision
        if (previousRevisionId.HasValue)
        {
            await using var prevCmd = new NpgsqlCommand("""
                SELECT config_value FROM config_revisions
                WHERE revision_id = $1
                """, connection, transaction);
            prevCmd.Parameters.AddWithValue(previousRevisionId.Value);
            var previousValue = await prevCmd.ExecuteScalarAsync(ct);
            if (previousValue is string prevValue)
            {
                await using var insertCmd = new NpgsqlCommand("""
                    INSERT INTO config_revisions (config_key, config_value, previous_revision_id,
                        actor_user_id, actor_reason, status)
                    VALUES ($1, $2, $3, $4, $5, 'pending')
                    """, connection, transaction);
                insertCmd.Parameters.AddWithValue(configKey);
                insertCmd.Parameters.AddWithValue(prevValue);
                insertCmd.Parameters.AddWithValue(targetRevisionId);
                insertCmd.Parameters.AddWithValue((object?)actorUserId ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue((object?)(reason ?? $"Rollback of revision {revisionId}") ?? DBNull.Value);
                await insertCmd.ExecuteNonQueryAsync(ct);
            }
        }

        await transaction.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// Records or updates a node observation for a given revision.
    /// </summary>
    public async Task RecordNodeObservationAsync(
        string nodeId, long revisionId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO config_node_observations (node_id, last_seen_revision, last_seen_at)
            VALUES ($1, $2, now())
            ON CONFLICT (node_id) DO UPDATE
            SET last_seen_revision = EXCLUDED.last_seen_revision,
                last_seen_at = now()
            """);
        command.Parameters.AddWithValue(nodeId);
        command.Parameters.AddWithValue(revisionId);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Returns all known node observations.
    /// </summary>
    public async Task<IReadOnlyList<ConfigNodeObservation>> GetNodeObservationsAsync(
        CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT node_id, last_seen_revision, last_seen_at
            FROM config_node_observations
            ORDER BY node_id
            """);
        var results = new List<ConfigNodeObservation>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ConfigNodeObservation(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetDateTime(2)));
        }
        return results;
    }

    /// <summary>
    /// Returns pending revisions that are older than the given revision for a key.
    /// Used for stale-write detection.
    /// </summary>
    public async Task<bool> HasNewerPendingRevisionAsync(
        string key, long revisionId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT COUNT(*) > 0
            FROM config_revisions
            WHERE config_key = $1 AND revision_id > $2 AND status = 'pending'
            """);
        command.Parameters.AddWithValue(key);
        command.Parameters.AddWithValue(revisionId);
        var result = await command.ExecuteScalarAsync(ct);
        return Convert.ToBoolean(result);
    }

    private static async Task<ConfigRevision?> ReadSingleAsync(
        NpgsqlCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return ReadRevision(reader);
    }

    private static async Task<IReadOnlyList<ConfigRevision>> ReadManyAsync(
        NpgsqlCommand command, CancellationToken ct)
    {
        var results = new List<ConfigRevision>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadRevision(reader));
        return results;
    }

    private static ConfigRevision ReadRevision(NpgsqlDataReader reader)
    {
        return new ConfigRevision(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetDateTime(6),
            reader.IsDBNull(7) ? null : reader.GetDateTime(7),
            reader.IsDBNull(8) ? null : reader.GetDateTime(8),
            reader.GetString(9));
    }
}
