using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ScalaAPI.Host.Services;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class MediaObjectReconciliationTests
{
    [Fact]
    public async Task MissingObjectIsRecoverableWithoutChangingSettledOperation()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var leaseToken = $"media-reconcile-lease-{suffix}";
        var operationId = $"med_reconcile_{suffix}";
        var objectKey = $"media/{operationId}.png";
        await InsertSettledMediaAsync(dataSource, leaseToken, operationId, objectKey);
        var storage = new FakeObjectStorage(new(true, "etag-1", 5, "image/png"));
        var service = new MediaObjectReconciliationService(
            new MediaOperationStore(dataSource), storage,
            new ConfigurationBuilder().Build(),
            NullLogger<MediaObjectReconciliationService>.Instance);

        try
        {
            Assert.Equal(1, await service.ReconcileOnceAsync());
            Assert.Equal(0, await service.ReconcileOnceAsync());
            Assert.Equal(("succeeded", "stored"), await ReadStateAsync(dataSource, operationId));

            await MakeDueAsync(dataSource, operationId);
            storage.Head = new(false, "", 0, "");
            Assert.Equal(1, await service.ReconcileOnceAsync());
            Assert.Equal(("succeeded", "failed"), await ReadStateAsync(dataSource, operationId));
            Assert.Contains("object_missing", await ReadErrorAsync(dataSource, operationId));

            await MakeDueAsync(dataSource, operationId);
            storage.Head = new(true, "etag-1", 5, "image/png");
            Assert.Equal(1, await service.ReconcileOnceAsync());
            Assert.Equal(("succeeded", "stored"), await ReadStateAsync(dataSource, operationId));
            Assert.Equal("{}", await ReadErrorAsync(dataSource, operationId));
        }
        finally
        {
            await using var media = dataSource.CreateCommand(
                "DELETE FROM media_operations WHERE operation_id = $1");
            media.Parameters.AddWithValue(operationId);
            await media.ExecuteNonQueryAsync();
            await using var lease = dataSource.CreateCommand(
                "DELETE FROM request_leases WHERE lease_token = $1");
            lease.Parameters.AddWithValue(leaseToken);
            await lease.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task OrphanCleanupProtectsReferencedAndYoungObjects()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var leaseToken = $"media-orphan-lease-{suffix}";
        var operationId = $"med_orphan_{suffix}";
        var referencedKey = $"media/{operationId}.png";
        var orphanKey = $"media/orphan-{suffix}.bin";
        var youngKey = $"media/young-{suffix}.bin";
        await InsertSettledMediaAsync(dataSource, leaseToken, operationId, referencedKey);
        var storage = new FakeObjectStorage(new(true, "etag-1", 5, "image/png"))
        {
            Objects =
            [
                new(referencedKey, "etag-1", 5, DateTimeOffset.UtcNow.AddHours(-2)),
                new(orphanKey, "orphan-etag", 7, DateTimeOffset.UtcNow.AddHours(-2)),
                new(youngKey, "young-etag", 3, DateTimeOffset.UtcNow.AddMinutes(-1)),
            ],
        };
        var service = new MediaObjectReconciliationService(
            new MediaOperationStore(dataSource), storage,
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ObjectStorage:OrphanGraceMinutes"] = "60",
                }).Build(),
            NullLogger<MediaObjectReconciliationService>.Instance);

        try
        {
            var result = await service.ReconcileOrphansOnceAsync();
            Assert.Equal(3, result.Listed);
            Assert.Equal(1, result.Protected);
            Assert.Equal(1, result.SkippedYoung);
            Assert.Equal(1, result.Deleted);
            Assert.Equal([orphanKey], storage.Deleted);
        }
        finally
        {
            await using var media = dataSource.CreateCommand(
                "DELETE FROM media_operations WHERE operation_id = $1");
            media.Parameters.AddWithValue(operationId);
            await media.ExecuteNonQueryAsync();
            await using var lease = dataSource.CreateCommand(
                "DELETE FROM request_leases WHERE lease_token = $1");
            lease.Parameters.AddWithValue(leaseToken);
            await lease.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ExpiredOutputClaimIsRetryableAndClearsMetadataOnce()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var leaseToken = $"media-retention-lease-{suffix}";
        var operationId = $"med_retention_{suffix}";
        var objectKey = $"media/{operationId}.png";
        await InsertSettledMediaAsync(dataSource, leaseToken, operationId, objectKey);
        var store = new MediaOperationStore(dataSource);

        try
        {
            var claimed = await store.ClaimExpiredOutputBatchAsync(8);
            var operation = Assert.Single(claimed);
            Assert.Equal("pending", operation.ObjectStatus);
            Assert.Empty(await store.ClaimExpiredOutputBatchAsync(8));

            Assert.True(await store.RecordExpiredOutputFailureAsync(operation,
                "{\"type\":\"storage_timeout\"}"));
            await using (var due = dataSource.CreateCommand("""
                UPDATE media_operations
                SET object_next_check_at = now() - interval '1 second'
                WHERE operation_id = $1
                """))
            {
                due.Parameters.AddWithValue(operationId);
                await due.ExecuteNonQueryAsync();
            }

            var retry = Assert.Single(await store.ClaimExpiredOutputBatchAsync(8));
            Assert.Equal("pending", retry.ObjectStatus);
            Assert.True(await store.ClearExpiredOutputAsync(retry));
            Assert.False(await store.ClearExpiredOutputAsync(retry));
            await using var state = dataSource.CreateCommand("""
                SELECT object_key, object_status, output_url
                FROM media_operations WHERE operation_id = $1
                """);
            state.Parameters.AddWithValue(operationId);
            await using var reader = await state.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("", reader.GetString(0));
            Assert.Equal("deleted", reader.GetString(1));
            Assert.Equal("", reader.GetString(2));
        }
        finally
        {
            await using var media = dataSource.CreateCommand(
                "DELETE FROM media_operations WHERE operation_id = $1");
            media.Parameters.AddWithValue(operationId);
            await media.ExecuteNonQueryAsync();
            await using var lease = dataSource.CreateCommand(
                "DELETE FROM request_leases WHERE lease_token = $1");
            lease.Parameters.AddWithValue(leaseToken);
            await lease.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task BatchItemsAreOwnerScopedReplaceableAndReferenced()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var leaseToken = $"media-items-lease-{suffix}";
        var operationId = $"med_items_{suffix}";
        var archiveKey = $"media/{operationId}.zip";
        await InsertSettledMediaAsync(dataSource, leaseToken, operationId, archiveKey);
        var store = new MediaOperationStore(dataSource);
        try
        {
            var retention = DateTime.UtcNow.AddDays(1);
            Assert.True(await store.ReplaceItemsAsync(88001, operationId,
            [
                new(0, "first", "https://provider.test/first", $"media/{operationId}/items/first.png",
                    "etag-first", 12, "image/png", "stored", "https://storage.test/first", "", retention),
                new(1, "second", "https://provider.test/second", $"media/{operationId}/items/second.png",
                    "etag-second", 13, "image/png", "stored", "https://storage.test/second", "", retention),
            ]));

            var items = await store.ListItemsAsync(88001, operationId);
            Assert.Equal(2, items.Count);
            Assert.Equal("first", items[0].CustomId);
            Assert.Empty(await store.ListItemsAsync(88099, operationId));
            var references = await store.ListReferencedObjectKeysAsync();
            Assert.Contains(archiveKey, references);
            Assert.Contains(items[0].ObjectKey, references);
            Assert.Contains(items[1].ObjectKey, references);

            Assert.True(await store.ReplaceItemsAsync(88001, operationId,
            [
                new(0, "replacement", "https://provider.test/replacement",
                    $"media/{operationId}/items/replacement.png", "etag-replacement", 14,
                    "image/png", "stored", "https://storage.test/replacement", "", retention),
            ]));
            Assert.Equal("replacement",
                Assert.Single(await store.ListItemsAsync(88001, operationId)).CustomId);
            Assert.True(await store.MarkItemsDeletedAsync(operationId));
            var deleted = Assert.Single(await store.ListItemsAsync(88001, operationId));
            Assert.Equal("deleted", deleted.ObjectStatus);
            Assert.Equal("", deleted.ObjectKey);
            Assert.Equal("", deleted.OutputUrl);
        }
        finally
        {
            await using var media = dataSource.CreateCommand(
                "DELETE FROM media_operations WHERE operation_id = $1");
            media.Parameters.AddWithValue(operationId);
            await media.ExecuteNonQueryAsync();
            await using var lease = dataSource.CreateCommand(
                "DELETE FROM request_leases WHERE lease_token = $1");
            lease.Parameters.AddWithValue(leaseToken);
            await lease.ExecuteNonQueryAsync();
        }
    }

    private static async Task InsertSettledMediaAsync(NpgsqlDataSource dataSource,
        string leaseToken, string operationId, string objectKey)
    {
        await using var lease = dataSource.CreateCommand("""
            INSERT INTO request_leases(
                lease_token, request_id, api_key_hash, api_key_id, user_id,
                account_id, group_id, model, upstream_model, inbound_endpoint,
                rate_multiplier, hold_amount, status, expires_at)
            VALUES ($1, $2, 'media-reconcile', 88001, 88002, 88003, 88004,
                    'gpt-image-1', 'gpt-image-1', 'images', 1, 0, 'completed', now())
            """);
        lease.Parameters.AddWithValue(leaseToken);
        lease.Parameters.AddWithValue($"request-{operationId}");
        await lease.ExecuteNonQueryAsync();

        await using var media = dataSource.CreateCommand("""
            INSERT INTO media_operations(
                operation_id, idempotency_key, request_fingerprint, operation_type,
                status, api_key_id, account_id, request_id, lease_token, provider,
                upstream_task_id, progress, output_url, content_type, expires_at,
                object_key, object_etag, object_size, object_status, object_error,
                object_next_check_at, retention_until)
            VALUES ($1, $2, 'media-fingerprint', 'images_create', 'succeeded',
                    88001, 88003, $3, $4, 'mock', 'task-1', 100,
                    'https://provider.test/output.png', 'image/png', now(),
                    $5, 'etag-1', 5, 'stored', '{}'::jsonb, now(),
                    now() - interval '1 minute')
            """);
        media.Parameters.AddWithValue(operationId);
        media.Parameters.AddWithValue($"idem-{operationId}");
        media.Parameters.AddWithValue($"request-{operationId}");
        media.Parameters.AddWithValue(leaseToken);
        media.Parameters.AddWithValue(objectKey);
        await media.ExecuteNonQueryAsync();
    }

    private static async Task MakeDueAsync(NpgsqlDataSource dataSource, string operationId)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE media_operations SET object_next_check_at = now() WHERE operation_id = $1");
        command.Parameters.AddWithValue(operationId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(string Status, string ObjectStatus)> ReadStateAsync(
        NpgsqlDataSource dataSource, string operationId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT status, object_status FROM media_operations WHERE operation_id = $1");
        command.Parameters.AddWithValue(operationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<string> ReadErrorAsync(
        NpgsqlDataSource dataSource, string operationId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT COALESCE(object_error::text, '{}') FROM media_operations WHERE operation_id = $1");
        command.Parameters.AddWithValue(operationId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private sealed class FakeObjectStorage(ObjectStorageHeadResult initial)
        : IMediaObjectStorage
    {
        public ObjectStorageHeadResult Head { get; set; } = initial;
        public IReadOnlyList<ObjectStorageItem> Objects { get; set; } = [];
        public List<string> Deleted { get; } = [];

        public Task<ObjectStorageHeadResult> HeadAsync(string objectKey,
            CancellationToken ct = default) => Task.FromResult(Head);

        public Task<IReadOnlyList<ObjectStorageItem>> ListAsync(string prefix,
            CancellationToken ct = default) => Task.FromResult(Objects);

        public Task DeleteAsync(string objectKey, CancellationToken ct = default)
        {
            Deleted.Add(objectKey);
            return Task.CompletedTask;
        }
    }
}
