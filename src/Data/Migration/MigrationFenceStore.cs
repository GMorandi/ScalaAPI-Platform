using Npgsql;

namespace Sub2Api.Data.Migration;

public sealed class MigrationFenceStore(NpgsqlDataSource dataSource)
{
    public sealed record FenceEvent(
        long Id, long FromEpoch, long ToEpoch, string FromPrimary, string FromMode,
        string ToPrimary, string ToMode, string Reason, string UpdatedBy,
        DateTimeOffset TransitionedAt);

    public async Task<MigrationFence> GetAsync(CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT epoch, write_primary, mode, reason, updated_by, updated_at
            FROM migration_fence WHERE id = 1
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("migration_fence is not initialized");
        return new MigrationFence(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5));
    }

    public async Task AssertWritePrimaryAsync(string expectedPrimary, long expectedEpoch,
        CancellationToken ct = default)
    {
        var fence = await GetAsync(ct);
        if (!string.Equals(fence.WritePrimary, expectedPrimary, StringComparison.Ordinal)
            || fence.Epoch != expectedEpoch)
            throw new InvalidOperationException($"migration fence rejected write: expected {expectedPrimary}@{expectedEpoch}, current {fence.WritePrimary}@{fence.Epoch}");
    }

    public async Task<MigrationFence> PromoteAsync(string currentPrimary, string nextPrimary,
        string nextMode, string reason, string updatedBy, CancellationToken ct = default)
    {
        if (nextPrimary is not ("sub2api" or "platform")) throw new ArgumentException("invalid primary", nameof(nextPrimary));
        if (nextMode is not ("legacy_primary" or "target_canary" or "target_primary" or "legacy_read_only"))
            throw new ArgumentException("invalid mode", nameof(nextMode));

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT epoch, write_primary, mode FROM migration_fence WHERE id = 1 FOR UPDATE
            """;
        command.Transaction = transaction;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("migration fence is missing");
        var epoch = reader.GetInt64(0);
        var primary = reader.GetString(1);
        var currentMode = reader.GetString(2);
        await reader.CloseAsync();
        if (!string.Equals(primary, currentPrimary, StringComparison.Ordinal))
            throw new InvalidOperationException($"fence transition rejected: current primary is {primary}");
        ValidateTransition(primary, currentMode, nextPrimary, nextMode);
        if (nextMode == "target_primary")
            await AssertTargetPrimaryReadyAsync(connection, transaction, ct);

        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE migration_fence
            SET epoch = $1, write_primary = $2, mode = $3, reason = $4, updated_by = $5, updated_at = now()
            WHERE id = 1
            """;
        update.Transaction = transaction;
        update.Parameters.AddWithValue(epoch + 1);
        update.Parameters.AddWithValue(nextPrimary);
        update.Parameters.AddWithValue(nextMode);
        update.Parameters.AddWithValue(reason);
        update.Parameters.AddWithValue(updatedBy);
        await update.ExecuteNonQueryAsync(ct);

        await using var audit = connection.CreateCommand();
        audit.Transaction = transaction;
        audit.CommandText = """
            INSERT INTO migration_fence_events(
                from_epoch, to_epoch, from_primary, from_mode,
                to_primary, to_mode, reason, updated_by)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8)
            """;
        audit.Parameters.AddWithValue(epoch);
        audit.Parameters.AddWithValue(epoch + 1);
        audit.Parameters.AddWithValue(primary);
        audit.Parameters.AddWithValue(currentMode);
        audit.Parameters.AddWithValue(nextPrimary);
        audit.Parameters.AddWithValue(nextMode);
        audit.Parameters.AddWithValue(reason);
        audit.Parameters.AddWithValue(updatedBy);
        await audit.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return await GetAsync(ct);
    }

    public async Task<IReadOnlyList<FenceEvent>> GetHistoryAsync(int limit = 100,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        await using var command = dataSource.CreateCommand("""
            SELECT id, from_epoch, to_epoch, from_primary, from_mode,
                   to_primary, to_mode, reason, updated_by, transitioned_at
            FROM migration_fence_events
            ORDER BY transitioned_at DESC, id DESC
            LIMIT $1
            """);
        command.Parameters.AddWithValue(limit);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var events = new List<FenceEvent>();
        while (await reader.ReadAsync(ct))
        {
            events.Add(new FenceEvent(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetString(7), reader.GetString(8),
                reader.GetFieldValue<DateTimeOffset>(9)));
        }
        return events;
    }

    private static async Task AssertTargetPrimaryReadyAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                EXISTS (SELECT 1 FROM cdc_checkpoints WHERE snapshot_completed),
                (SELECT count(*) FROM cdc_inbox
                    WHERE status IN ('pending', 'failed', 'processing')),
                (SELECT count(*) FROM cdc_dead_letters WHERE replayed_at IS NULL)
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("cannot evaluate target-primary readiness");
        var snapshotCompleted = reader.GetBoolean(0);
        var outstanding = reader.GetInt64(1);
        var deadLetters = reader.GetInt64(2);
        if (!snapshotCompleted || outstanding != 0 || deadLetters != 0)
            throw new InvalidOperationException(
                $"target-primary promotion requires completed snapshot and clean CDC: " +
                $"snapshot_completed={snapshotCompleted}, outstanding={outstanding}, dead_letters={deadLetters}");
    }

    public static void ValidateTransition(string currentPrimary, string currentMode,
        string nextPrimary, string nextMode)
    {
        if ((currentMode == "legacy_primary" || currentMode == "legacy_read_only")
            && currentPrimary != "sub2api")
            throw new InvalidOperationException("current legacy mode requires sub2api as primary");
        if ((currentMode == "target_canary" || currentMode == "target_primary")
            && currentPrimary != "platform")
            throw new InvalidOperationException("current target mode requires platform as primary");
        if ((nextMode == "legacy_primary" || nextMode == "legacy_read_only")
            && nextPrimary != "sub2api")
            throw new ArgumentException("legacy modes require sub2api as primary");
        if ((nextMode == "target_canary" || nextMode == "target_primary")
            && nextPrimary != "platform")
            throw new ArgumentException("target modes require platform as primary");
        if (currentPrimary == nextPrimary && currentMode == nextMode)
            throw new InvalidOperationException("fence transition must change mode or primary");

        var legal = (currentMode, nextMode) switch
        {
            ("legacy_primary", "target_canary") => true,
            ("target_canary", "target_primary") => true,
            ("target_canary", "legacy_primary") => true,
            ("target_primary", "legacy_read_only") => true,
            ("legacy_read_only", "target_primary") => true,
            ("legacy_read_only", "legacy_primary") => true,
            _ => false
        };
        if (!legal)
            throw new InvalidOperationException(
                $"illegal fence transition: {currentPrimary}/{currentMode} -> {nextPrimary}/{nextMode}");
    }

}
