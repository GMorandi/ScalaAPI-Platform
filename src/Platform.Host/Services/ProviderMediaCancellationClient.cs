using System.Net;
using System.Net.Http;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Host.Services;

public sealed class ProviderMediaCancellationClient(
    ILogger<ProviderMediaCancellationClient> logger)
{
    public async Task CancelAsync(AccountCredentials credentials,
        MediaOperation operation, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(operation.UpstreamTaskId))
            return;

        var path = CancellationPath(operation);
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("provider_media_cancel_unsupported");
        if (!Uri.TryCreate(credentials.BaseUrl.TrimEnd('/') + path,
                UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp
                && endpoint.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("provider_media_cancel_endpoint_invalid");

        using var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(credentials.ProxyUrl))
        {
            handler.Proxy = new WebProxy(credentials.ProxyUrl);
            handler.UseProxy = true;
        }
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        foreach (var (name, value) in credentials.AuthHeaders)
            request.Headers.TryAddWithoutValidation(name, value);

        using var response = await client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        logger.LogWarning("Provider media cancellation failed for {OperationId}: {StatusCode} {Body}",
            operation.OperationId, (int)response.StatusCode,
            body.Length > 512 ? body[..512] : body);
        throw new InvalidOperationException(
            $"provider_media_cancel_status_{(int)response.StatusCode}");
    }

    private static string CancellationPath(MediaOperation operation)
    {
        var id = Uri.EscapeDataString(operation.UpstreamTaskId);
        if (operation.OperationType == "images_batch_create")
            return $"/v1/images/batches/{id}/cancel";
        if (operation.OperationType.StartsWith("images_", StringComparison.Ordinal))
            return $"/v1/images/tasks/{id}/cancel";
        if (operation.OperationType.StartsWith("videos_", StringComparison.Ordinal))
            return $"/v1/videos/{id}/cancel";
        return "";
    }
}
