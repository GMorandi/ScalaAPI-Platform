using System.Text.Json;

namespace ScalaAPI.Host.Services;

public sealed record MediaRetentionRunResult(int Claimed, int Deleted, int Failed);

public sealed class MediaRetentionService(
    MediaOperationStore store,
    IMediaObjectStorage objectStorage,
    ILogger<MediaRetentionService> logger)
{
    public async Task<MediaRetentionRunResult> RunOnceAsync(int limit = 16,
        CancellationToken ct = default)
    {
        var claimed = 0;
        var deleted = 0;
        var failed = 0;
        foreach (var operation in await store.ClaimExpiredOutputBatchAsync(limit, ct))
        {
            claimed++;
            try
            {
                await objectStorage.DeleteAsync(operation.ObjectKey, ct);
                foreach (var itemKey in await store.ListItemObjectKeysAsync(
                    operation.OperationId, ct))
                    await objectStorage.DeleteAsync(itemKey, ct);
                if (await store.ClearExpiredOutputAsync(operation, ct)) deleted++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var error = JsonSerializer.Serialize(new
                {
                    type = "media_retention_delete_failed",
                    message = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message,
                });
                if (await store.RecordExpiredOutputFailureAsync(operation, error, ct))
                    failed++;
                logger.LogWarning(ex, "Media retention cleanup failed for {OperationId}",
                    operation.OperationId);
            }
        }
        return new(claimed, deleted, failed);
    }
}
