using System.Data;
using Npgsql;

namespace ScalaAPI.Host.Services;

public sealed record MediaOperation(
    string OperationId, string IdempotencyKey, string RequestFingerprint,
    string OperationType, string Status, long ApiKeyId, long AccountId,
    string RequestId, string LeaseToken, string Provider, string UpstreamTaskId,
    int Progress, string OutputMetadata, string OutputUrl, string ContentType,
    string Error, bool CancelRequested, int Attempts, DateTime ExpiresAt,
    DateTime? NextPollAt, DateTime? LastPolledAt, string ObjectKey,
    string ObjectETag, long ObjectSize, string ObjectStatus, string ObjectError);

public sealed record MediaCreateResult(MediaOperation Operation, bool Created, bool Conflict);

// PostgreSQL owns media state. Gateway processes only submit commands and
// render authenticated views; they never keep task state in process memory.
public sealed class MediaOperationStore(
    NpgsqlDataSource dataSource)
{
    private const string Projection = """
        operation_id, idempotency_key, request_fingerprint, operation_type,
        status, api_key_id, account_id, request_id, lease_token, provider,
        upstream_task_id, progress, COALESCE(output_metadata::text, ''),
        output_url, content_type, COALESCE(error::text, ''), cancel_requested,
        attempts, expires_at, next_poll_at, last_polled_at
        , object_key, object_etag, object_size, object_status,
        COALESCE(object_error::text, '')
        """;
    private const string OperationProjection = """
        operation.operation_id, operation.idempotency_key, operation.request_fingerprint,
        operation.operation_type, operation.status, operation.api_key_id,
        operation.account_id, operation.request_id, operation.lease_token,
        operation.provider, operation.upstream_task_id, operation.progress,
        COALESCE(operation.output_metadata::text, ''), operation.output_url,
        operation.content_type, COALESCE(operation.error::text, ''),
        operation.cancel_requested, operation.attempts, operation.expires_at,
        operation.next_poll_at, operation.last_polled_at, operation.object_key,
        operation.object_etag, operation.object_size, operation.object_status,
        COALESCE(operation.object_error::text, '')
        """;

    public async Task<MediaCreateResult> CreateOrGetAsync(long apiKeyId, long accountId,
        string requestId, string leaseToken, string operationType,
        string? idempotencyKey, string requestFingerprint, string provider,
        DateTime expiresAt, CancellationToken ct = default)
    {
        var key = string.IsNullOrWhiteSpace(idempotencyKey) ? requestId : idempotencyKey.Trim();
        var operationId = $"med_{Guid.NewGuid():N}";

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO media_operations (
                operation_id, idempotency_key, request_fingerprint, operation_type,
                status, api_key_id, account_id, request_id, lease_token, provider,
                expires_at, next_poll_at)
            VALUES ($1, $2, $3, $4, 'pending', $5, $6, $7, $8, $9, $10, now())
            ON CONFLICT (api_key_id, idempotency_key) DO NOTHING
            RETURNING {Projection}
            """;
        command.Parameters.AddWithValue(operationId);
        command.Parameters.AddWithValue(key);
        command.Parameters.AddWithValue(requestFingerprint ?? "");
        command.Parameters.AddWithValue(operationType);
        command.Parameters.AddWithValue(apiKeyId);
        command.Parameters.AddWithValue(accountId);
        command.Parameters.AddWithValue(requestId);
        command.Parameters.AddWithValue(leaseToken);
        command.Parameters.AddWithValue(provider ?? "");
        command.Parameters.AddWithValue(expiresAt);

        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                var created = Read(reader);
                await reader.DisposeAsync();
                await transaction.CommitAsync(ct);
                return new MediaCreateResult(created, true, false);
            }
        }

        await using var existingCommand = connection.CreateCommand();
        existingCommand.Transaction = transaction;
        existingCommand.CommandText = $"""
            SELECT {Projection}
            FROM media_operations
            WHERE api_key_id = $1 AND idempotency_key = $2
            FOR UPDATE
            """;
        existingCommand.Parameters.AddWithValue(apiKeyId);
        existingCommand.Parameters.AddWithValue(key);
        await using var existingReader = await existingCommand.ExecuteReaderAsync(ct);
        if (!await existingReader.ReadAsync(ct))
            throw new InvalidOperationException("Media idempotency conflict row disappeared");
        var existing = Read(existingReader);
        var conflict = !string.Equals(existing.RequestFingerprint, requestFingerprint ?? "",
            StringComparison.Ordinal);
        await existingReader.DisposeAsync();
        await transaction.CommitAsync(ct);
        return new MediaCreateResult(existing, false, conflict);
    }

    public async Task<MediaOperation?> GetAsync(long apiKeyId, string operationId,
        CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand($"""
            SELECT {Projection}
            FROM media_operations
            WHERE api_key_id = $1 AND operation_id = $2
            """);
        command.Parameters.AddWithValue(apiKeyId);
        command.Parameters.AddWithValue(operationId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<MediaOperation?> GetByIdempotencyAsync(long apiKeyId,
        string idempotencyKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return null;
        await using var command = dataSource.CreateCommand($"""
            SELECT {Projection}
            FROM media_operations
            WHERE api_key_id = $1 AND idempotency_key = $2
            """);
        command.Parameters.AddWithValue(apiKeyId);
        command.Parameters.AddWithValue(idempotencyKey.Trim());
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<MediaOperation?> UpdateAsync(long apiKeyId, string operationId,
        string status, int progress, string? upstreamTaskId = null,
        string? outputMetadata = null, string? outputUrl = null,
        string? contentType = null, string? error = null,
        string? objectKey = null, string? objectEtag = null, long? objectSize = null,
        string? objectStatus = null, string? objectError = null,
        CancellationToken ct = default)
    {
        var terminal = status is "succeeded" or "failed" or "canceled" or "expired";
        await using var command = dataSource.CreateCommand($"""
            UPDATE media_operations
            SET status = $3,
                progress = CASE WHEN $4 THEN 100 ELSE GREATEST(0, LEAST(100, $5)) END,
                upstream_task_id = COALESCE(NULLIF($6, ''), upstream_task_id),
                output_metadata = COALESCE(NULLIF($7, '')::jsonb, output_metadata),
                output_url = COALESCE(NULLIF($8, ''), output_url),
                content_type = COALESCE(NULLIF($9, ''), content_type),
                error = CASE WHEN $3 = 'succeeded' THEN NULL
                    WHEN NULLIF($10, '') IS NULL THEN error ELSE $10::jsonb END,
                object_key = COALESCE(NULLIF($11, ''), object_key),
                object_etag = COALESCE(NULLIF($12, ''), object_etag),
                object_size = CASE WHEN $13::bigint IS NULL THEN object_size ELSE $13::bigint END,
                object_status = CASE WHEN NULLIF($14, '') IS NULL THEN object_status ELSE $14 END,
                object_error = CASE WHEN $14 = 'stored' THEN NULL
                    WHEN NULLIF($15, '') IS NULL THEN object_error ELSE $15::jsonb END,
                next_poll_at = CASE WHEN $4 THEN NULL ELSE now() + interval '3 seconds' END,
                updated_at = now()
            WHERE api_key_id = $1 AND operation_id = $2
              AND status NOT IN ('succeeded', 'failed', 'canceled', 'expired')
              AND ($3 IN ('running', 'succeeded', 'failed', 'canceled', 'expired'))
            RETURNING {Projection}
            """);
        command.Parameters.AddWithValue(apiKeyId);
        command.Parameters.AddWithValue(operationId);
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue(terminal);
        command.Parameters.AddWithValue(progress);
        command.Parameters.AddWithValue(upstreamTaskId ?? "");
        command.Parameters.AddWithValue(outputMetadata ?? "");
        command.Parameters.AddWithValue(outputUrl ?? "");
        command.Parameters.AddWithValue(contentType ?? "");
        command.Parameters.AddWithValue(error ?? "");
        command.Parameters.AddWithValue(objectKey ?? "");
        command.Parameters.AddWithValue(objectEtag ?? "");
        command.Parameters.AddWithValue(objectSize.HasValue
            ? (object)objectSize.Value : DBNull.Value);
        command.Parameters.AddWithValue(objectStatus ?? "");
        command.Parameters.AddWithValue(objectError ?? "");
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : await GetAsync(apiKeyId, operationId, ct);
    }

    public async Task<MediaOperation?> CancelAsync(long apiKeyId, string operationId,
        CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand($"""
            UPDATE media_operations
            SET cancel_requested = true,
                status = 'canceled', progress = 100, next_poll_at = NULL,
                updated_at = now()
            WHERE api_key_id = $1 AND operation_id = $2
              AND status NOT IN ('succeeded', 'failed', 'canceled', 'expired')
            RETURNING {Projection}
            """);
        command.Parameters.AddWithValue(apiKeyId);
        command.Parameters.AddWithValue(operationId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : await GetAsync(apiKeyId, operationId, ct);
    }

    public async Task<bool> DeleteAsync(long apiKeyId, string operationId,
        CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            DELETE FROM media_operations
            WHERE api_key_id = $1 AND operation_id = $2
              AND status IN ('succeeded', 'failed', 'canceled', 'expired')
            """);
        command.Parameters.AddWithValue(apiKeyId);
        command.Parameters.AddWithValue(operationId);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<MediaOperation?> ClearOutputsAsync(long apiKeyId,
        string operationId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand($"""
            UPDATE media_operations
            SET output_metadata = NULL, output_url = '', content_type = '',
                object_key = '', object_etag = '', object_size = 0,
                object_status = 'deleted', object_error = NULL, updated_at = now()
            WHERE api_key_id = $1 AND operation_id = $2
              AND status IN ('succeeded', 'failed', 'canceled', 'expired')
            RETURNING {Projection}
            """);
        command.Parameters.AddWithValue(apiKeyId);
        command.Parameters.AddWithValue(operationId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<int> ExpireDueAsync(CancellationToken ct = default)
        => (await ExpireDueAndReturnAsync(ct)).Count;

    public async Task<IReadOnlyList<MediaOperation>> ExpireDueAndReturnAsync(
        CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand($"""
            UPDATE media_operations
            SET status = 'expired', progress = 100, next_poll_at = NULL,
                error = COALESCE(error, jsonb_build_object(
                    'type', 'expired', 'message', 'Media operation expired')),
                updated_at = now()
            WHERE status IN ('pending', 'running') AND expires_at <= now()
            RETURNING {Projection}
            """);
        var result = new List<MediaOperation>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(Read(reader));
        return result;
    }

    public async Task<IReadOnlyList<MediaOperation>> ClaimDueAsync(int limit,
        CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            WITH due AS (
                SELECT operation_id
                FROM media_operations
                WHERE status IN ('pending', 'running')
                  AND upstream_task_id IS NOT NULL
                  AND upstream_task_id <> ''
                  AND expires_at > now()
                  AND COALESCE(next_poll_at, now()) <= now()
                ORDER BY COALESCE(next_poll_at, created_at), created_at
                FOR UPDATE SKIP LOCKED
                LIMIT $1
            )
            UPDATE media_operations AS operation
            SET attempts = operation.attempts + 1,
                last_polled_at = now(),
                next_poll_at = now() + make_interval(secs => LEAST(30,
                    GREATEST(3, (operation.attempts + 1) * 2))),
                updated_at = now()
            FROM due
            WHERE operation.operation_id = due.operation_id
            RETURNING {OperationProjection}
            """;
        command.Parameters.AddWithValue(Math.Clamp(limit, 1, 100));
        var result = new List<MediaOperation>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(Read(reader));
        await reader.DisposeAsync();
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<MediaOperation?> RecordPollFailureAsync(MediaOperation operation,
        string error, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand($"""
            UPDATE media_operations
            SET status = CASE WHEN attempts >= 20 THEN 'failed' ELSE status END,
                progress = CASE WHEN attempts >= 20 THEN 100 ELSE progress END,
                error = $3::jsonb,
                next_poll_at = CASE WHEN attempts >= 20 THEN NULL ELSE next_poll_at END,
                updated_at = now()
            WHERE operation_id = $1 AND api_key_id = $2
              AND status IN ('pending', 'running')
            RETURNING {Projection}
            """);
        command.Parameters.AddWithValue(operation.OperationId);
        command.Parameters.AddWithValue(operation.ApiKeyId);
        command.Parameters.AddWithValue(error);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    private static MediaOperation Read(NpgsqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetString(7),
        reader.GetString(8), reader.GetString(9), reader.IsDBNull(10) ? "" : reader.GetString(10),
        reader.GetInt32(11), reader.GetString(12), reader.GetString(13), reader.GetString(14),
        reader.GetString(15), reader.GetBoolean(16), reader.GetInt32(17), reader.GetDateTime(18),
        reader.IsDBNull(19) ? null : reader.GetDateTime(19),
        reader.IsDBNull(20) ? null : reader.GetDateTime(20), reader.GetString(21),
        reader.GetString(22), reader.GetInt64(23), reader.GetString(24), reader.GetString(25));
}
