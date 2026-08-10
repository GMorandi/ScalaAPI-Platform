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
    string ObjectETag, long ObjectSize, string ObjectStatus, string ObjectError,
    DateTime? ObjectVerifiedAt, int ObjectReconcileAttempts, DateTime? ObjectNextCheckAt,
    DateTime? RetentionUntil);

public sealed record MediaCreateResult(MediaOperation Operation, bool Created, bool Conflict);

public sealed record MediaOperationItem(
    long ItemId, string OperationId, int ItemIndex, string CustomId,
    string ProviderUrl, string ObjectKey, string ObjectETag, long ObjectSize,
    string ContentType, string ObjectStatus, string OutputUrl, string Error,
    DateTime? RetentionUntil, DateTime? ObjectVerifiedAt,
    int ObjectReconcileAttempts, DateTime? ObjectNextCheckAt);

public sealed record MediaOperationItemWrite(
    int ItemIndex, string CustomId, string ProviderUrl, string ObjectKey,
    string ObjectETag, long ObjectSize, string ContentType, string ObjectStatus,
    string OutputUrl, string Error, DateTime? RetentionUntil);

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
        COALESCE(object_error::text, ''), object_verified_at,
        object_reconcile_attempts, object_next_check_at, retention_until
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
        COALESCE(operation.object_error::text, ''), operation.object_verified_at,
        operation.object_reconcile_attempts, operation.object_next_check_at,
        operation.retention_until
        """;
    private const string ItemProjection = """
        item.item_id, item.operation_id, item.item_index, item.custom_id,
        item.provider_url, item.object_key, item.object_etag, item.object_size,
        item.content_type, item.object_status, item.output_url,
        COALESCE(item.error::text, ''), item.retention_until,
        item.object_verified_at, item.object_reconcile_attempts,
        item.object_next_check_at
        """;

    public async Task<MediaCreateResult> CreateOrGetAsync(long apiKeyId, long accountId,
        string requestId, string leaseToken, string operationType,
        string? idempotencyKey, string requestFingerprint, string provider,
        DateTime expiresAt, DateTime? retentionUntil = null,
        CancellationToken ct = default)
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
                expires_at, retention_until, next_poll_at)
            VALUES ($1, $2, $3, $4, 'pending', $5, $6, $7, $8, $9, $10, $11, now())
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
        command.Parameters.AddWithValue(retentionUntil.HasValue
            ? (object)retentionUntil.Value : DBNull.Value);

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

    public async Task<IReadOnlyList<MediaOperation>> ListBatchesAsync(long apiKeyId,
        int limit = 100, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand($"""
            SELECT {Projection}
            FROM media_operations
            WHERE api_key_id = $1 AND operation_type = 'images_batch_create'
            ORDER BY created_at DESC, operation_id DESC
            LIMIT $2
            """);
        command.Parameters.AddWithValue(apiKeyId);
        command.Parameters.AddWithValue(Math.Clamp(limit, 1, 100));
        var result = new List<MediaOperation>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(Read(reader));
        return result;
    }

    public async Task<IReadOnlyList<MediaOperation>> ListBatchesMissingItemsAsync(
        int limit, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand($"""
            SELECT {Projection}
            FROM media_operations operation
            WHERE operation.operation_type = 'images_batch_create'
              AND operation.status = 'succeeded'
              AND jsonb_typeof(operation.output_metadata -> 'data') = 'array'
              AND jsonb_array_length(operation.output_metadata -> 'data') > 0
              AND NOT EXISTS (
                  SELECT 1 FROM media_operation_items item
                  WHERE item.operation_id = operation.operation_id)
            ORDER BY operation.updated_at, operation.operation_id
            LIMIT $1
            """);
        command.Parameters.AddWithValue(Math.Clamp(limit, 1, 100));
        var result = new List<MediaOperation>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(Read(reader));
        return result;
    }

    public async Task<IReadOnlySet<string>> ListReferencedObjectKeysAsync(
        CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT object_key
            FROM media_operations
            WHERE object_key <> ''
            UNION
            SELECT object_key
            FROM media_operation_items
            WHERE object_key <> ''
            """);
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));
        return result;
    }

    public async Task<IReadOnlyList<MediaOperationItem>> ListItemsAsync(
        long apiKeyId, string operationId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand($"""
            SELECT {ItemProjection}
            FROM media_operation_items item
            JOIN media_operations operation ON operation.operation_id = item.operation_id
            WHERE operation.api_key_id = $1 AND item.operation_id = $2
            ORDER BY item.item_index
            """);
        command.Parameters.AddWithValue(apiKeyId);
        command.Parameters.AddWithValue(operationId);
        var result = new List<MediaOperationItem>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadItem(reader));
        return result;
    }

    public async Task<IReadOnlyList<string>> ListItemObjectKeysAsync(
        string operationId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT object_key
            FROM media_operation_items
            WHERE operation_id = $1 AND object_key <> ''
            ORDER BY item_index
            """);
        command.Parameters.AddWithValue(operationId);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetString(0));
        return result;
    }

    public async Task<bool> ReplaceItemsAsync(long apiKeyId, string operationId,
        IReadOnlyList<MediaOperationItemWrite> items, CancellationToken ct = default)
    {
        if (items.Count > 200) throw new ArgumentOutOfRangeException(nameof(items));

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        await using var lockCommand = connection.CreateCommand();
        lockCommand.Transaction = transaction;
        lockCommand.CommandText = """
            SELECT 1
            FROM media_operations
            WHERE api_key_id = $1 AND operation_id = $2 AND status = 'succeeded'
            FOR UPDATE
            """;
        lockCommand.Parameters.AddWithValue(apiKeyId);
        lockCommand.Parameters.AddWithValue(operationId);
        if (await lockCommand.ExecuteScalarAsync(ct) is null)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM media_operation_items WHERE operation_id = $1";
        delete.Parameters.AddWithValue(operationId);
        await delete.ExecuteNonQueryAsync(ct);

        foreach (var item in items.OrderBy(item => item.ItemIndex))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO media_operation_items(
                    operation_id, item_index, custom_id, provider_url, object_key,
                    object_etag, object_size, content_type, object_status, output_url,
                    error, retention_until, updated_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10,
                        NULLIF($11, '')::jsonb, $12, now())
                """;
            insert.Parameters.AddWithValue(operationId);
            insert.Parameters.AddWithValue(item.ItemIndex);
            insert.Parameters.AddWithValue(item.CustomId);
            insert.Parameters.AddWithValue(item.ProviderUrl);
            insert.Parameters.AddWithValue(item.ObjectKey);
            insert.Parameters.AddWithValue(item.ObjectETag);
            insert.Parameters.AddWithValue(item.ObjectSize);
            insert.Parameters.AddWithValue(item.ContentType);
            insert.Parameters.AddWithValue(item.ObjectStatus);
            insert.Parameters.AddWithValue(item.OutputUrl);
            insert.Parameters.AddWithValue(item.Error);
            insert.Parameters.AddWithValue(item.RetentionUntil.HasValue
                ? (object)item.RetentionUntil.Value : DBNull.Value);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> MarkItemsDeletedAsync(string operationId,
        CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE media_operation_items
            SET object_key = '', object_etag = '', object_size = 0,
                object_status = 'deleted', output_url = '', error = NULL,
                object_verified_at = now(), object_next_check_at = NULL,
                updated_at = now()
            WHERE operation_id = $1 AND object_status <> 'deleted'
            """);
        command.Parameters.AddWithValue(operationId);
        return await command.ExecuteNonQueryAsync(ct) > 0;
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
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE media_operations
            SET output_metadata = NULL, output_url = '', content_type = '',
                object_key = '', object_etag = '', object_size = 0,
                object_status = 'deleted', object_error = NULL, updated_at = now()
            WHERE api_key_id = $1 AND operation_id = $2
              AND status IN ('succeeded', 'failed', 'canceled', 'expired')
            RETURNING {Projection}
            """;
        command.Parameters.AddWithValue(apiKeyId);
        command.Parameters.AddWithValue(operationId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            await reader.DisposeAsync();
            await transaction.RollbackAsync(ct);
            return null;
        }
        var operation = Read(reader);
        await reader.DisposeAsync();
        await ClearItemsAsync(connection, transaction, operationId, ct);
        await transaction.CommitAsync(ct);
        return operation;
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

    public async Task<IReadOnlyList<MediaOperation>> ClaimObjectReconciliationBatchAsync(
        int limit, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            WITH due AS (
                SELECT operation_id
                FROM media_operations
                WHERE status = 'succeeded'
                  AND object_status IN ('stored', 'failed')
                  AND object_key <> ''
                  AND (object_next_check_at IS NULL OR object_next_check_at <= now())
                ORDER BY COALESCE(object_next_check_at, updated_at), updated_at
                FOR UPDATE SKIP LOCKED
                LIMIT $1
            )
            UPDATE media_operations AS operation
            SET object_reconcile_attempts = operation.object_reconcile_attempts + 1,
                object_next_check_at = now() + interval '5 minutes',
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

    public async Task<MediaOperation?> RecordObjectVerificationAsync(
        MediaOperation operation, bool valid, string? error = null,
        CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand($"""
            UPDATE media_operations
            SET object_status = CASE WHEN $3 THEN 'stored' ELSE 'failed' END,
                object_error = CASE WHEN $3 THEN NULL ELSE $4::jsonb END,
                object_verified_at = now(),
                object_next_check_at = now() + CASE WHEN $3
                    THEN interval '1 hour' ELSE interval '5 minutes' END,
                updated_at = now()
            WHERE api_key_id = $1 AND operation_id = $2
              AND status = 'succeeded' AND object_key = $5
            RETURNING {Projection}
            """);
        command.Parameters.AddWithValue(operation.ApiKeyId);
        command.Parameters.AddWithValue(operation.OperationId);
        command.Parameters.AddWithValue(valid);
        command.Parameters.AddWithValue((object?)error ?? "{}");
        command.Parameters.AddWithValue(operation.ObjectKey);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<MediaOperationItem>> ClaimItemReconciliationBatchAsync(
        int limit, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            WITH due AS (
                SELECT item.item_id
                FROM media_operation_items item
                JOIN media_operations operation
                  ON operation.operation_id = item.operation_id
                WHERE operation.status = 'succeeded'
                  AND item.object_status IN ('pending', 'stored', 'failed')
                  AND (item.retention_until IS NULL OR item.retention_until > now())
                  AND (item.object_next_check_at IS NULL
                       OR item.object_next_check_at <= now())
                ORDER BY COALESCE(item.object_next_check_at, item.updated_at),
                         item.updated_at, item.item_id
                FOR UPDATE OF item SKIP LOCKED
                LIMIT $1
            )
            UPDATE media_operation_items AS item
            SET object_reconcile_attempts = item.object_reconcile_attempts + 1,
                object_next_check_at = now() + interval '5 minutes',
                updated_at = now()
            FROM due
            WHERE item.item_id = due.item_id
            RETURNING {ItemProjection}
            """;
        command.Parameters.AddWithValue(Math.Clamp(limit, 1, 100));
        var result = new List<MediaOperationItem>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadItem(reader));
        await reader.DisposeAsync();
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<bool> RecordItemVerificationAsync(MediaOperationItem item,
        bool valid, string? error = null, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE media_operation_items
            SET object_status = CASE WHEN $4 THEN 'stored' ELSE 'failed' END,
                error = CASE WHEN $4 THEN NULL ELSE $5::jsonb END,
                object_verified_at = now(),
                object_next_check_at = now() + CASE WHEN $4
                    THEN interval '1 hour' ELSE interval '5 minutes' END,
                updated_at = now()
            WHERE item_id = $1 AND operation_id = $2
              AND object_status = $7
              AND object_reconcile_attempts = $3
              AND object_key = $6
              AND object_next_check_at = $8
            """);
        command.Parameters.AddWithValue(item.ItemId);
        command.Parameters.AddWithValue(item.OperationId);
        command.Parameters.AddWithValue(item.ObjectReconcileAttempts);
        command.Parameters.AddWithValue(valid);
        command.Parameters.AddWithValue((object?)error ?? "{}");
        command.Parameters.AddWithValue(item.ObjectKey);
        command.Parameters.AddWithValue(item.ObjectStatus);
        command.Parameters.AddWithValue(item.ObjectNextCheckAt
            ?? throw new InvalidOperationException("Item reconciliation claim has no deadline"));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> RecordItemRepairAsync(MediaOperationItem item,
        ObjectStoragePutResult stored, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE media_operation_items
            SET object_key = $4, object_etag = $5, object_size = $6,
                content_type = $7, object_status = 'stored', output_url = $8,
                error = NULL, object_verified_at = now(),
                object_next_check_at = now() + interval '1 hour',
                updated_at = now()
            WHERE item_id = $1 AND operation_id = $2
              AND object_status = $9
              AND object_reconcile_attempts = $3
              AND object_next_check_at = $10
            """);
        command.Parameters.AddWithValue(item.ItemId);
        command.Parameters.AddWithValue(item.OperationId);
        command.Parameters.AddWithValue(item.ObjectReconcileAttempts);
        command.Parameters.AddWithValue(stored.ObjectKey);
        command.Parameters.AddWithValue(stored.ETag);
        command.Parameters.AddWithValue(stored.Size);
        command.Parameters.AddWithValue(stored.ContentType);
        command.Parameters.AddWithValue(stored.DownloadUrl);
        command.Parameters.AddWithValue(item.ObjectStatus);
        command.Parameters.AddWithValue(item.ObjectNextCheckAt
            ?? throw new InvalidOperationException("Item reconciliation claim has no deadline"));
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<IReadOnlyList<MediaOperation>> ClaimExpiredOutputBatchAsync(
        int limit, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            WITH due AS (
                SELECT operation_id
                FROM media_operations
                WHERE status IN ('succeeded', 'failed', 'canceled', 'expired')
                  AND object_status IN ('stored', 'failed')
                  AND object_key <> ''
                  AND retention_until IS NOT NULL
                  AND retention_until <= now()
                  AND (object_next_check_at IS NULL OR object_next_check_at <= now())
                ORDER BY retention_until, updated_at
                FOR UPDATE SKIP LOCKED
                LIMIT $1
            )
            UPDATE media_operations AS operation
            SET object_status = 'pending',
                object_error = jsonb_build_object('type', 'retention_pending'),
                object_next_check_at = now() + interval '5 minutes',
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

    public async Task<bool> ClearExpiredOutputAsync(MediaOperation operation,
        CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE media_operations
            SET output_metadata = NULL, output_url = '', content_type = '',
                object_key = '', object_etag = '', object_size = 0,
                object_status = 'deleted', object_error = NULL,
                object_verified_at = now(), object_next_check_at = NULL,
                updated_at = now()
            WHERE operation_id = $1 AND status IN ('succeeded', 'failed', 'canceled', 'expired')
              AND object_status = 'pending' AND object_key = $2
              AND retention_until IS NOT NULL AND retention_until <= now()
            """;
        command.Parameters.AddWithValue(operation.OperationId);
        command.Parameters.AddWithValue(operation.ObjectKey);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }
        await ClearItemsAsync(connection, transaction, operation.OperationId, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> RecordExpiredOutputFailureAsync(MediaOperation operation,
        string error, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE media_operations
            SET object_status = 'failed', object_error = $3::jsonb,
                object_next_check_at = now() + interval '5 minutes', updated_at = now()
            WHERE operation_id = $1 AND status IN ('succeeded', 'failed', 'canceled', 'expired')
              AND object_status = 'pending' AND object_key = $2
            """);
        command.Parameters.AddWithValue(operation.OperationId);
        command.Parameters.AddWithValue(operation.ObjectKey);
        command.Parameters.AddWithValue(error);
        return await command.ExecuteNonQueryAsync(ct) == 1;
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
        reader.GetString(22), reader.GetInt64(23), reader.GetString(24), reader.GetString(25),
        reader.IsDBNull(26) ? null : reader.GetDateTime(26), reader.GetInt32(27),
        reader.IsDBNull(28) ? null : reader.GetDateTime(28),
        reader.IsDBNull(29) ? null : reader.GetDateTime(29));

    private static MediaOperationItem ReadItem(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt64(7),
        reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetDateTime(12),
        reader.IsDBNull(13) ? null : reader.GetDateTime(13), reader.GetInt32(14),
        reader.IsDBNull(15) ? null : reader.GetDateTime(15));

    private static async Task ClearItemsAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string operationId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE media_operation_items
            SET object_key = '', object_etag = '', object_size = 0,
                object_status = 'deleted', output_url = '', error = NULL,
                object_verified_at = now(), object_next_check_at = NULL,
                updated_at = now()
            WHERE operation_id = $1 AND object_status <> 'deleted'
            """;
        command.Parameters.AddWithValue(operationId);
        await command.ExecuteNonQueryAsync(ct);
    }
}
