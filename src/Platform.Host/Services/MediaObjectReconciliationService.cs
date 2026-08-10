using System.Text.Json;

namespace ScalaAPI.Host.Services;

public sealed record MediaOrphanCleanupResult(int Listed, int Protected, int SkippedYoung,
    int Deleted);

// Reconciliation is deliberately metadata-only. It can mark a stored output
// unavailable, while orphan cleanup deletes only unreferenced, aged media keys.
public sealed class MediaObjectReconciliationService(
    MediaOperationStore store,
    IMediaObjectStorage objectStorage,
    IConfiguration configuration,
    ILogger<MediaObjectReconciliationService> logger) : BackgroundService
{
    private const string MediaPrefix = "media/";

    public async Task<int> ReconcileOnceAsync(CancellationToken ct = default)
    {
        var operations = await store.ClaimObjectReconciliationBatchAsync(32, ct);
        foreach (var operation in operations)
        {
            try
            {
                var head = await objectStorage.HeadAsync(operation.ObjectKey, ct);
                var error = Validate(operation, head);
                await store.RecordObjectVerificationAsync(operation, error is null,
                    error, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var error = JsonSerializer.Serialize(new
                {
                    type = "object_reconcile_error",
                    message = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message,
                });
                await store.RecordObjectVerificationAsync(operation, false, error, ct);
                logger.LogWarning(ex, "Media object reconciliation failed for {OperationId}",
                    operation.OperationId);
            }
        }
        return operations.Count;
    }

    public async Task<MediaOrphanCleanupResult> ReconcileOrphansOnceAsync(
        CancellationToken ct = default)
    {
        var objects = await objectStorage.ListAsync(MediaPrefix, ct);
        var referenced = await store.ListReferencedObjectKeysAsync(ct);
        var grace = TimeSpan.FromMinutes(Math.Clamp(
            configuration.GetValue("ObjectStorage:OrphanGraceMinutes", 60), 1, 7 * 24 * 60));
        var cutoff = DateTimeOffset.UtcNow - grace;
        var protectedCount = 0;
        var skippedYoung = 0;
        var deleted = 0;

        foreach (var item in objects)
        {
            ct.ThrowIfCancellationRequested();
            if (referenced.Contains(item.Key))
            {
                protectedCount++;
                continue;
            }
            if (item.LastModified is null || item.LastModified > cutoff)
            {
                skippedYoung++;
                continue;
            }

            await objectStorage.DeleteAsync(item.Key, ct);
            deleted++;
            logger.LogInformation("Deleted unreferenced media object {ObjectKey}", item.Key);
        }

        return new(objects.Count, protectedCount, skippedYoung, deleted);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ReconcileOnceAsync(stoppingToken);
                await ReconcileOrphansOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Media object reconciliation iteration failed");
            }
        }
    }

    private static string? Validate(MediaOperation operation,
        ObjectStorageHeadResult head)
    {
        if (!head.Exists)
            return JsonSerializer.Serialize(new
            {
                type = "object_missing",
                object_key = operation.ObjectKey,
            });
        if (operation.ObjectSize != head.Size)
            return JsonSerializer.Serialize(new
            {
                type = "object_size_mismatch",
                expected = operation.ObjectSize,
                actual = head.Size,
            });
        if (!string.IsNullOrWhiteSpace(operation.ObjectETag)
            && !string.IsNullOrWhiteSpace(head.ETag)
            && !string.Equals(operation.ObjectETag, head.ETag,
                StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(new
            {
                type = "object_etag_mismatch",
                expected = operation.ObjectETag,
                actual = head.ETag,
            });
        return null;
    }
}
