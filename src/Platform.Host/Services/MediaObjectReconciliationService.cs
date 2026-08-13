using System.Text.Json;

namespace ScalaAPI.Host.Services;

public sealed record MediaOrphanCleanupResult(int Listed, int Protected, int SkippedYoung,
    int Deleted);

// Parent reconciliation is metadata-only. Batch item repair can recopy one
// Provider item, but it never changes the already-settled operation or lease.
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
                if (error is null)
                {
                    await store.RecordObjectVerificationAsync(operation, true, ct: ct);
                    continue;
                }

                // HEAD mismatch detected -- attempt recopy from provider URL
                if (!string.IsNullOrWhiteSpace(operation.OutputUrl))
                {
                    try
                    {
                        var stored = await objectStorage.CopyFromUrlAsync(
                            operation.OutputUrl, operation.OperationId,
                            operation.ContentType, ct);
                        if (await store.RecordOperationRepairAsync(operation, stored, ct))
                        {
                            logger.LogInformation(
                                "Repaired parent operation object {OperationId} after {VerificationError}",
                                operation.OperationId, error);
                            continue;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception recopyEx)
                    {
                        // Provider URL may have expired; mark as failed gracefully
                        var recopyError = JsonSerializer.Serialize(new
                        {
                            type = "object_recopy_failed",
                            message = recopyEx.Message.Length > 500
                                ? recopyEx.Message[..500] : recopyEx.Message,
                        });
                        await store.RecordObjectVerificationAsync(operation, false,
                            recopyError, ct);
                        logger.LogWarning(recopyEx,
                            "Parent operation recopy failed for {OperationId}",
                            operation.OperationId);
                        continue;
                    }
                }

                await store.RecordObjectVerificationAsync(operation, false, error, ct);
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

    public async Task<int> ReconcileItemsOnceAsync(CancellationToken ct = default)
    {
        var items = await store.ClaimItemReconciliationBatchAsync(64, ct);
        foreach (var item in items)
        {
            try
            {
                string? verificationError;
                if (string.IsNullOrWhiteSpace(item.ObjectKey))
                {
                    verificationError = JsonSerializer.Serialize(new
                    {
                        type = "item_object_key_missing",
                        item_index = item.ItemIndex,
                    });
                }
                else
                {
                    var head = await objectStorage.HeadAsync(item.ObjectKey, ct);
                    verificationError = Validate(item, head);
                    if (verificationError is null)
                    {
                        await store.RecordItemVerificationAsync(item, true, ct: ct);
                        continue;
                    }
                }

                var stored = await objectStorage.CopyBatchItemAsync(item.ProviderUrl,
                    item.OperationId, item.ItemIndex, item.CustomId, ct);
                if (await store.RecordItemRepairAsync(item, stored, ct))
                    logger.LogInformation(
                        "Repaired batch item object {OperationId}/{ItemIndex} after {VerificationError}",
                        item.OperationId, item.ItemIndex, verificationError);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var error = JsonSerializer.Serialize(new
                {
                    type = "item_object_reconcile_error",
                    message = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message,
                });
                await store.RecordItemVerificationAsync(item, false, error, ct);
                logger.LogWarning(ex,
                    "Batch item reconciliation failed for {OperationId}/{ItemIndex}",
                    item.OperationId, item.ItemIndex);
            }
        }
        return items.Count;
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
                await ReconcileItemsOnceAsync(stoppingToken);
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

    private static string? Validate(MediaOperationItem item,
        ObjectStorageHeadResult head)
    {
        if (!head.Exists)
            return JsonSerializer.Serialize(new
            {
                type = "item_object_missing",
                item_index = item.ItemIndex,
                object_key = item.ObjectKey,
            });
        if (item.ObjectSize != head.Size)
            return JsonSerializer.Serialize(new
            {
                type = "item_object_size_mismatch",
                item_index = item.ItemIndex,
                expected = item.ObjectSize,
                actual = head.Size,
            });
        if (!string.IsNullOrWhiteSpace(item.ObjectETag)
            && !string.IsNullOrWhiteSpace(head.ETag)
            && !string.Equals(item.ObjectETag, head.ETag,
                StringComparison.OrdinalIgnoreCase))
            return JsonSerializer.Serialize(new
            {
                type = "item_object_etag_mismatch",
                item_index = item.ItemIndex,
                expected = item.ObjectETag,
                actual = head.ETag,
            });
        return null;
    }
}
