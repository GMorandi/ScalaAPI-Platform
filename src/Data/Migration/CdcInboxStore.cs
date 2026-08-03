using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Sub2Api.Data.Migration;

public enum CdcEnqueueResult
{
    Inserted,
    Duplicate,
    IdentityConflict
}

public sealed class CdcInboxStore(NpgsqlDataSource dataSource, ILogger<CdcInboxStore> logger)
{
    public sealed record InboxStatus(string Status, DateTimeOffset NextAttemptAt, int Attempts);

    public async Task<CdcEnqueueResult> EnqueueAsync(ChangeEnvelope envelope, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(envelope, CdcJson.Options);
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO cdc_inbox (
                event_id, epoch, source_lsn, transaction_id, aggregate_type,
                aggregate_id, operation, schema_version, payload_hash, envelope)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10::jsonb)
            ON CONFLICT (event_id) DO NOTHING
            RETURNING event_id
            """;
        command.Parameters.AddWithValue(envelope.EventId);
        command.Parameters.AddWithValue(envelope.Epoch);
        command.Parameters.AddWithValue(envelope.SourceLsn);
        command.Parameters.AddWithValue(envelope.TransactionId);
        command.Parameters.AddWithValue(envelope.AggregateType);
        command.Parameters.AddWithValue(envelope.AggregateId);
        command.Parameters.AddWithValue(envelope.Operation);
        command.Parameters.AddWithValue(envelope.SchemaVersion);
        command.Parameters.AddWithValue(envelope.PayloadHash);
        command.Parameters.AddWithValue(json);
        var inserted = await command.ExecuteScalarAsync(ct);
        if (inserted is not null)
        {
            await using var ack = connection.CreateCommand();
            ack.Transaction = transaction;
            ack.CommandText = """
                INSERT INTO cdc_sync_acks(event_id, epoch, aggregate_type, aggregate_id, status)
                VALUES ($1,$2,$3,$4,'accepted')
                ON CONFLICT (event_id) DO NOTHING
                """;
            ack.Parameters.AddWithValue(envelope.EventId);
            ack.Parameters.AddWithValue(envelope.Epoch);
            ack.Parameters.AddWithValue(envelope.AggregateType);
            ack.Parameters.AddWithValue(envelope.AggregateId);
            await ack.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return CdcEnqueueResult.Inserted;
        }

        await using var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText =
            "SELECT payload_hash FROM cdc_inbox WHERE event_id = $1";
        existing.Parameters.AddWithValue(envelope.EventId);
        var existingHash = (string?)await existing.ExecuteScalarAsync(ct);
        await transaction.RollbackAsync(ct);
        return string.Equals(existingHash, envelope.PayloadHash, StringComparison.OrdinalIgnoreCase)
            ? CdcEnqueueResult.Duplicate
            : CdcEnqueueResult.IdentityConflict;
    }

    public async Task RecordRejectedAsync(string connectorName, string topic, int partition,
        long offset, string message, Exception error, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var reason = error.Message.Length > 2000 ? error.Message[..2000] : error.Message;
        await using var command = dataSource.CreateCommand("""
            INSERT INTO cdc_rejected_messages (
                connector_name, topic, partition_id, offset_value,
                message_sha256, message_bytes, reason)
            VALUES ($1,$2,$3,$4,$5,$6,$7)
            ON CONFLICT (topic, partition_id, offset_value) DO UPDATE SET
                message_sha256 = EXCLUDED.message_sha256,
                message_bytes = EXCLUDED.message_bytes,
                reason = EXCLUDED.reason
            """);
        command.Parameters.AddWithValue(connectorName);
        command.Parameters.AddWithValue(topic);
        command.Parameters.AddWithValue(partition);
        command.Parameters.AddWithValue(offset);
        command.Parameters.AddWithValue(digest);
        command.Parameters.AddWithValue(bytes.Length);
        command.Parameters.AddWithValue(reason);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> TryClaimAsync(string eventId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE cdc_inbox
            SET status = 'processing', attempts = attempts + 1,
                next_attempt_at = now() + interval '5 minutes'
            WHERE event_id = $1
              AND (
                (status IN ('pending', 'failed') AND next_attempt_at <= now())
                OR (status = 'processing' AND next_attempt_at <= now())
              )
            """);
        command.Parameters.AddWithValue(eventId);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<InboxStatus?> GetStatusAsync(string eventId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT status, next_attempt_at, attempts FROM cdc_inbox WHERE event_id = $1");
        command.Parameters.AddWithValue(eventId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new InboxStatus(reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetInt32(2));
    }

    public async Task<bool> ReplayDeadLetterAsync(string eventId, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE cdc_inbox
            SET status = 'pending', attempts = 0, last_error = NULL,
                next_attempt_at = now()
            WHERE event_id = $1 AND status = 'dead_letter'
            """;
        command.Transaction = transaction;
        command.Parameters.AddWithValue(eventId);
        var changed = await command.ExecuteNonQueryAsync(ct) == 1;
        if (changed)
        {
            await using var clear = connection.CreateCommand();
            clear.CommandText = "UPDATE cdc_dead_letters SET replayed_at = now() WHERE event_id = $1";
            clear.Transaction = transaction;
            clear.Parameters.AddWithValue(eventId);
            await clear.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return changed;
    }

    public async Task MarkAppliedAsync(ChangeEnvelope envelope, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var inbox = connection.CreateCommand())
        {
            inbox.Transaction = transaction;
            inbox.CommandText = "UPDATE cdc_inbox SET status = 'applied', applied_at = now(), last_error = NULL WHERE event_id = $1";
            inbox.Parameters.AddWithValue(envelope.EventId);
            await inbox.ExecuteNonQueryAsync(ct);
        }
        await using (var ack = connection.CreateCommand())
        {
            ack.Transaction = transaction;
            ack.CommandText = """
                INSERT INTO cdc_sync_acks(event_id, epoch, aggregate_type, aggregate_id, status)
                VALUES ($1,$2,$3,$4,'applied')
                ON CONFLICT (event_id) DO UPDATE SET status = 'applied', error_code = NULL, acked_at = now()
                """;
            ack.Parameters.AddWithValue(envelope.EventId);
            ack.Parameters.AddWithValue(envelope.Epoch);
            ack.Parameters.AddWithValue(envelope.AggregateType);
            ack.Parameters.AddWithValue(envelope.AggregateId);
            await ack.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public async Task UpdateCheckpointAsync(string connectorName, ChangeEnvelope envelope,
        bool snapshotCompleted, int? partition, long? offset, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand("""
            INSERT INTO cdc_checkpoints(
                connector_name, source_lsn, source_lsn_value, snapshot_completed,
                last_event_id, last_partition, last_offset)
            VALUES ($1,$2,$3,$4,$5,$6,$7)
            ON CONFLICT (connector_name) DO UPDATE SET
                source_lsn = CASE
                    WHEN EXCLUDED.source_lsn_value IS NOT NULL
                     AND (cdc_checkpoints.source_lsn_value IS NULL
                          OR EXCLUDED.source_lsn_value >= cdc_checkpoints.source_lsn_value)
                        THEN EXCLUDED.source_lsn
                    WHEN cdc_checkpoints.source_lsn IS NULL THEN EXCLUDED.source_lsn
                    ELSE cdc_checkpoints.source_lsn
                END,
                source_lsn_value = CASE
                    WHEN EXCLUDED.source_lsn_value IS NOT NULL
                     AND (cdc_checkpoints.source_lsn_value IS NULL
                          OR EXCLUDED.source_lsn_value >= cdc_checkpoints.source_lsn_value)
                        THEN EXCLUDED.source_lsn_value
                    ELSE cdc_checkpoints.source_lsn_value
                END,
                snapshot_completed = cdc_checkpoints.snapshot_completed OR EXCLUDED.snapshot_completed,
                last_event_id = CASE
                    WHEN EXCLUDED.source_lsn_value IS NOT NULL
                     AND (cdc_checkpoints.source_lsn_value IS NULL
                          OR EXCLUDED.source_lsn_value >= cdc_checkpoints.source_lsn_value)
                        THEN EXCLUDED.last_event_id
                    ELSE cdc_checkpoints.last_event_id
                END,
                last_partition = CASE
                    WHEN EXCLUDED.source_lsn_value IS NOT NULL
                     AND (cdc_checkpoints.source_lsn_value IS NULL
                          OR EXCLUDED.source_lsn_value >= cdc_checkpoints.source_lsn_value)
                        THEN EXCLUDED.last_partition
                    ELSE cdc_checkpoints.last_partition
                END,
                last_offset = CASE
                    WHEN EXCLUDED.source_lsn_value IS NOT NULL
                     AND (cdc_checkpoints.source_lsn_value IS NULL
                          OR EXCLUDED.source_lsn_value >= cdc_checkpoints.source_lsn_value)
                        THEN EXCLUDED.last_offset
                    ELSE cdc_checkpoints.last_offset
                END,
                updated_at = now()
            """);
        command.Parameters.AddWithValue(connectorName);
        command.Parameters.AddWithValue(envelope.SourceLsn);
        command.Parameters.AddWithValue(ChangeEnvelope.TryParseLsn(envelope.SourceLsn, out var lsn)
            ? lsn : (object)DBNull.Value);
        command.Parameters.AddWithValue(snapshotCompleted);
        command.Parameters.AddWithValue(envelope.EventId);
        command.Parameters.AddWithValue((object?)partition ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)offset ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> MarkFailedAsync(ChangeEnvelope envelope, Exception error, CancellationToken ct)
    {
        var message = error.Message.Length > 2000 ? error.Message[..2000] : error.Message;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE cdc_inbox
            SET status = CASE WHEN attempts >= 25 THEN 'dead_letter' ELSE 'failed' END,
                last_error = $2,
                next_attempt_at = now() + make_interval(secs => LEAST(300, 1 << LEAST(attempts, 8)))
            WHERE event_id = $1
            """;
        command.Parameters.AddWithValue(envelope.EventId);
        command.Parameters.AddWithValue(message);
        await command.ExecuteNonQueryAsync(ct);

        await using var ack = connection.CreateCommand();
        ack.Transaction = transaction;
        ack.CommandText = """
            INSERT INTO cdc_sync_acks(event_id, epoch, aggregate_type, aggregate_id, status, error_code)
            VALUES ($1,$2,$3,$4,'failed',$5)
            ON CONFLICT (event_id) DO UPDATE SET status = 'failed', error_code = $5, acked_at = now()
            """;
        ack.Parameters.AddWithValue(envelope.EventId);
        ack.Parameters.AddWithValue(envelope.Epoch);
        ack.Parameters.AddWithValue(envelope.AggregateType);
        ack.Parameters.AddWithValue(envelope.AggregateId);
        ack.Parameters.AddWithValue(message);
        await ack.ExecuteNonQueryAsync(ct);

        var attempts = await ReadAttemptsAsync(connection, transaction, envelope.EventId, ct);
        if (attempts >= 25)
        {
            var json = JsonSerializer.Serialize(envelope, CdcJson.Options);
            await using var dead = connection.CreateCommand();
            dead.Transaction = transaction;
            dead.CommandText = """
                INSERT INTO cdc_dead_letters(event_id, envelope, reason, attempts)
                VALUES ($1,$2::jsonb,$3,$4)
                ON CONFLICT (event_id) DO UPDATE SET reason = $3, attempts = $4
                """;
            dead.Parameters.AddWithValue(envelope.EventId);
            dead.Parameters.AddWithValue(json);
            dead.Parameters.AddWithValue(message);
            dead.Parameters.AddWithValue(attempts);
            await dead.ExecuteNonQueryAsync(ct);
            logger.LogError("CDC event {EventId} moved to dead letter after {Attempts} attempts", envelope.EventId, attempts);
        }
        await transaction.CommitAsync(ct);
        return attempts >= 25;
    }

    private static async Task<int> ReadAttemptsAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string eventId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT attempts FROM cdc_inbox WHERE event_id = $1";
        command.Transaction = transaction;
        command.Parameters.AddWithValue(eventId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }
}
