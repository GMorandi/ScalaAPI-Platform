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
                object_next_check_at)
            VALUES ($1, $2, 'media-fingerprint', 'images_create', 'succeeded',
                    88001, 88003, $3, $4, 'mock', 'task-1', 100,
                    'https://provider.test/output.png', 'image/png', now(),
                    $5, 'etag-1', 5, 'stored', '{}'::jsonb, now())
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
