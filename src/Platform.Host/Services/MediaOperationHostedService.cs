using System.Net;
using System.Text.Json;
using Orleans;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Host.Services;

public sealed class MediaOperationHostedService(
    MediaOperationStore store,
    RequestLeaseStore leases,
    IClusterClient cluster,
    ObjectStorageClient objectStorage,
    ILogger<MediaOperationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                foreach (var expired in await store.ExpireDueAndReturnAsync(stoppingToken))
                    await leases.AbortAsync(expired.LeaseToken, "media_operation_expired", stoppingToken);

                var due = await store.ClaimDueAsync(16, stoppingToken);
                await Parallel.ForEachAsync(due,
                    new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = stoppingToken },
                    PollAsync);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Media operation polling failed");
            }
        }
    }

    private async ValueTask PollAsync(MediaOperation operation, CancellationToken ct)
    {
        try
        {
            var credentials = await cluster.GetGrain<IAccountGrain>(operation.AccountId).Hydrate();
            var path = PollPath(operation);
            if (string.IsNullOrEmpty(path))
            {
                await FailAsync(operation, "unsupported_media_operation",
                    "No polling adapter is registered for this media operation", ct);
                return;
            }

            using var handler = new HttpClientHandler();
            if (!string.IsNullOrWhiteSpace(credentials.ProxyUrl))
            {
                handler.Proxy = new WebProxy(credentials.ProxyUrl);
                handler.UseProxy = true;
            }
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            using var request = new HttpRequestMessage(HttpMethod.Get,
                credentials.BaseUrl.TrimEnd('/') + path);
            foreach (var (name, value) in credentials.AuthHeaders)
                request.Headers.TryAddWithoutValidation(name, value);

            using var response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var updated = await store.RecordPollFailureAsync(operation,
                    JsonSerializer.Serialize(new
                    {
                        type = "provider_poll_error",
                        status = (int)response.StatusCode,
                        message = body.Length > 4096 ? body[..4096] : body
                    }), ct);
                if (updated?.Status == "failed")
                    await leases.AbortAsync(operation.LeaseToken, "media_poll_exhausted", ct);
                return;
            }

            var parsed = Parse(body, response.Content.Headers.ContentType?.MediaType ?? "");
            if (parsed.Status == "succeeded")
            {
                ObjectStoragePutResult stored;
                try
                {
                    stored = await objectStorage.CopyFromUrlAsync(parsed.OutputUrl,
                        operation.OperationId, parsed.ContentType, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await store.UpdateAsync(operation.ApiKeyId, operation.OperationId,
                        "running", parsed.Progress, operation.UpstreamTaskId, Limited(body),
                        parsed.OutputUrl, parsed.ContentType,
                        JsonSerializer.Serialize(new
                        {
                            type = "object_storage_error",
                            message = ex.Message,
                        }),
                        objectStatus: "failed", objectError: JsonSerializer.Serialize(new
                        {
                            type = "object_storage_error",
                            message = ex.Message,
                        }), ct: ct);
                    logger.LogWarning(ex, "Object storage copy failed for {OperationId}",
                        operation.OperationId);
                    return;
                }

                var imageOperation = operation.OperationType.StartsWith("images_", StringComparison.Ordinal);
                var videoOperation = operation.OperationType.StartsWith("videos_", StringComparison.Ordinal);
                var settlement = await leases.CompleteAsync(new LeaseCompletion(
                    operation.LeaseToken, 0, 0, 0, 0, 0, 0, 200, false, false,
                    OutputImageCount: imageOperation ? Math.Max(1, parsed.OutputCount) : 0,
                    ImageSize: parsed.Size,
                    VideoCount: videoOperation ? 1 : 0,
                    VideoResolution: parsed.Resolution,
                    VideoDurationSeconds: parsed.DurationSeconds,
                    UpstreamEndpoint: path,
                    MediaOperationId: operation.OperationId,
                    PricingVersion: "v1"), ct);
                if (!settlement.Accepted)
                {
                    await store.RecordPollFailureAsync(operation,
                        JsonSerializer.Serialize(new
                        {
                            type = "settlement_error",
                            code = settlement.ErrorCode,
                            retryable = settlement.Retryable
                        }), ct);
                    return;
                }
                await store.UpdateAsync(operation.ApiKeyId,
                    operation.OperationId, parsed.Status, parsed.Progress,
                    operation.UpstreamTaskId, Limited(body), stored.DownloadUrl,
                    stored.ContentType, parsed.Error, objectKey: stored.ObjectKey,
                    objectEtag: stored.ETag, objectSize: stored.Size,
                    objectStatus: "stored", objectError: "", ct: ct);
            }
            else if (parsed.Status is "failed" or "canceled")
            {
                await store.UpdateAsync(operation.ApiKeyId,
                    operation.OperationId, parsed.Status, parsed.Progress,
                    operation.UpstreamTaskId, Limited(body), parsed.OutputUrl,
                    parsed.ContentType, parsed.Error, ct: ct);
                await leases.AbortAsync(operation.LeaseToken,
                    $"media_operation_{parsed.Status}", ct);
            }
            else
            {
                await store.UpdateAsync(operation.ApiKeyId,
                    operation.OperationId, parsed.Status, parsed.Progress,
                    operation.UpstreamTaskId, Limited(body), parsed.OutputUrl,
                    parsed.ContentType, parsed.Error, ct: ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Media poll failed for {OperationId}", operation.OperationId);
            var updated = await store.RecordPollFailureAsync(operation,
                JsonSerializer.Serialize(new { type = "poll_exception", message = ex.Message }), ct);
            if (updated?.Status == "failed")
                await leases.AbortAsync(operation.LeaseToken, "media_poll_exhausted", ct);
        }
    }

    private async Task FailAsync(MediaOperation operation, string type, string message,
        CancellationToken ct)
    {
        await store.UpdateAsync(operation.ApiKeyId, operation.OperationId, "failed", 100,
            error: JsonSerializer.Serialize(new { type, message }), ct: ct);
        await leases.AbortAsync(operation.LeaseToken, type, ct);
    }

    private static string PollPath(MediaOperation operation)
    {
        var id = Uri.EscapeDataString(operation.UpstreamTaskId);
        if (operation.OperationType == "images_batch_create") return $"/v1/images/batches/{id}";
        if (operation.OperationType.StartsWith("images_", StringComparison.Ordinal))
            return $"/v1/images/tasks/{id}";
        if (operation.OperationType.StartsWith("videos_", StringComparison.Ordinal))
            return $"/v1/videos/{id}";
        return "";
    }

    private static PollResult Parse(string body, string contentType)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var status = ReadString(root, "status").ToLowerInvariant() switch
            {
                "succeeded" or "completed" or "done" => "succeeded",
                "failed" or "error" => "failed",
                "canceled" or "cancelled" => "canceled",
                _ => "running"
            };
            var progress = ReadInt(root, "progress");
            if (status != "running") progress = 100;
            var outputUrl = ReadString(root, "output_url");
            if (string.IsNullOrEmpty(outputUrl)) outputUrl = ReadString(root, "url");
            if (string.IsNullOrEmpty(outputUrl) && root.TryGetProperty("data", out var outputData)
                && outputData.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in outputData.EnumerateArray())
                {
                    if (item.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                    {
                        outputUrl = url.GetString() ?? "";
                        break;
                    }
                }
            }
            var count = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? data.GetArrayLength() : 0;
            var outputContentType = ReadString(root, "content_type");
            if (string.IsNullOrWhiteSpace(outputContentType)) outputContentType = contentType;
            return new PollResult(status, progress, outputUrl, outputContentType,
                count, ReadString(root, "size"), ReadString(root, "resolution"),
                ReadInt(root, "duration"), status == "failed" ? Limited(body) : "");
        }
        catch (JsonException)
        {
            return new PollResult("failed", 100, "", contentType, 0, "", "", 0,
                JsonSerializer.Serialize(new { type = "invalid_provider_response", message = "Provider returned invalid JSON" }));
        }
    }

    private static string ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int ReadInt(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
            && value.TryGetInt32(out var number) ? Math.Max(0, number) : 0;

    private static string Limited(string value) =>
        value.Length > 512 * 1024 ? value[..(512 * 1024)] : value;

    private sealed record PollResult(string Status, int Progress, string OutputUrl,
        string ContentType, int OutputCount, string Size, string Resolution,
        int DurationSeconds, string Error);
}
