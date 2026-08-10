using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;

namespace ScalaAPI.Host.Services;

public sealed record ObjectStoragePutResult(
    string ObjectKey, string ETag, long Size, string ContentType, string DownloadUrl);

public sealed record BatchItemObjectResult(
    int ItemIndex, string CustomId, string ProviderUrl, string ObjectKey,
    string ETag, long Size, string ContentType, string ObjectStatus,
    string OutputUrl, string Error);

public sealed record ObjectStorageHeadResult(
    bool Exists, string ETag, long Size, string ContentType);

public sealed record ObjectStorageItem(
    string Key, string ETag, long Size, DateTimeOffset? LastModified);

public interface IMediaObjectStorage
{
    Task<ObjectStorageHeadResult> HeadAsync(string objectKey,
        CancellationToken ct = default);

    Task<IReadOnlyList<ObjectStorageItem>> ListAsync(string prefix,
        CancellationToken ct = default);

    Task DeleteAsync(string objectKey, CancellationToken ct = default);
}

// A small S3-compatible client keeps object ownership in MinIO/Garnet-free
// infrastructure without adding a provider-specific SDK to the control plane.
public sealed class ObjectStorageClient : IMediaObjectStorage
{
    private readonly HttpClient _http;
    private readonly ILogger<ObjectStorageClient> _logger;
    private readonly Uri _endpoint;
    private readonly Uri _publicEndpoint;
    private readonly string _bucket;
    private readonly string _accessKey;
    private readonly string _secretKey;
    private readonly string _region;
    private readonly SemaphoreSlim _bucketGate = new(1, 1);
    private volatile bool _bucketReady;

    public ObjectStorageClient(HttpClient http, IConfiguration configuration,
        ILogger<ObjectStorageClient> logger)
    {
        _http = http;
        _logger = logger;
        _endpoint = ParseEndpoint(configuration["ObjectStorage:Endpoint"]);
        _publicEndpoint = ParseEndpoint(configuration["ObjectStorage:PublicEndpoint"]
            ?? configuration["ObjectStorage:Endpoint"]);
        _bucket = configuration["ObjectStorage:Bucket"]?.Trim() is { Length: > 0 } bucket
            ? bucket : "scalaapi-media";
        _accessKey = configuration["ObjectStorage:AccessKey"]?.Trim() ?? "";
        _secretKey = configuration["ObjectStorage:SecretKey"] ?? "";
        _region = configuration["ObjectStorage:Region"]?.Trim() is { Length: > 0 } region
            ? region : "us-east-1";
    }

    public async Task<ObjectStoragePutResult> CopyFromUrlAsync(string sourceUrl,
        string operationId, string contentType, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source)
            || source.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Provider output URL is not an absolute HTTP URL");

        using var response = await _http.GetAsync(source,
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        const int maxBytes = 64 * 1024 * 1024;
        if (bytes.LongLength > maxBytes)
            throw new InvalidOperationException("Provider media output exceeds the object limit");

        var normalizedType = string.IsNullOrWhiteSpace(contentType)
            ? response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream"
            : contentType.Trim();
        var extension = ExtensionFor(normalizedType);
        var key = $"media/{operationId}.{extension}";
        var stored = await PutAsync(key, bytes, normalizedType, ct);
        return stored with { DownloadUrl = PresignGet(key, TimeSpan.FromHours(1)) };
    }

    public async Task DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(objectKey)) return;
        await EnsureBucketAsync(ct);
        using var response = await SendSignedAsync(HttpMethod.Delete, ObjectPath(objectKey),
            [], null, ct);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(
            $"Object storage DELETE failed with {(int)response.StatusCode}: {body[..Math.Min(body.Length, 512)]}");
    }

    public async Task<ObjectStorageHeadResult> HeadAsync(string objectKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
            return new(false, "", 0, "");
        await EnsureBucketAsync(ct);
        using var response = await SendSignedAsync(HttpMethod.Head,
            ObjectPath(objectKey), [], null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new(false, "", 0, "");
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Object storage HEAD failed with {(int)response.StatusCode}: {body[..Math.Min(body.Length, 512)]}");
        }
        return new(
            true,
            response.Headers.ETag?.Tag?.Trim('"') ?? "",
            response.Content.Headers.ContentLength ?? 0,
            response.Content.Headers.ContentType?.MediaType ?? "");
    }

    public async Task<IReadOnlyList<ObjectStorageItem>> ListAsync(string prefix,
        CancellationToken ct = default)
    {
        var normalizedPrefix = prefix?.Trim() ?? "";
        if (normalizedPrefix.Length > 512)
            throw new ArgumentOutOfRangeException(nameof(prefix));

        await EnsureBucketAsync(ct);
        var result = new List<ObjectStorageItem>();
        string? continuation = null;
        do
        {
            var query = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["list-type"] = "2",
                ["max-keys"] = "1000",
                ["prefix"] = normalizedPrefix,
            };
            if (!string.IsNullOrEmpty(continuation))
                query["continuation-token"] = continuation;

            using var response = await SendSignedAsync(HttpMethod.Get,
                ObjectPath(""), query, [], null, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException(
                    $"Object storage LIST failed with {(int)response.StatusCode}: {body[..Math.Min(body.Length, 512)]}");
            }

            var bodyText = await response.Content.ReadAsStringAsync(ct);
            if (bodyText.Length > 8 * 1024 * 1024)
                throw new InvalidOperationException("Object storage LIST response exceeds the object limit");
            var document = XDocument.Parse(bodyText, LoadOptions.PreserveWhitespace);
            var root = document.Root ?? throw new InvalidOperationException(
                "Object storage LIST response has no root element");
            var ns = root.Name.Namespace;
            foreach (var item in root.Elements(ns + "Contents"))
            {
                var key = item.Element(ns + "Key")?.Value ?? "";
                if (string.IsNullOrEmpty(key)) continue;
                var etag = (item.Element(ns + "ETag")?.Value ?? "").Trim('"');
                var size = long.TryParse(item.Element(ns + "Size")?.Value,
                    out var parsedSize) ? parsedSize : 0;
                DateTimeOffset? lastModified = DateTimeOffset.TryParse(
                    item.Element(ns + "LastModified")?.Value,
                    out var parsedLastModified) ? parsedLastModified : null;
                result.Add(new ObjectStorageItem(key, etag, size, lastModified));
            }

            var truncated = string.Equals(
                root.Element(ns + "IsTruncated")?.Value, "true",
                StringComparison.OrdinalIgnoreCase);
            continuation = truncated
                ? root.Element(ns + "NextContinuationToken")?.Value
                : null;
            if (truncated && string.IsNullOrWhiteSpace(continuation))
                throw new InvalidOperationException(
                    "Object storage LIST response is truncated without a continuation token");
        }
        while (continuation is not null);

        return result;
    }

    public async Task<ObjectStoragePutResult> CreateBatchArchiveAsync(
        string metadataJson, string operationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128
            || operationId.Any(ch => !char.IsLetterOrDigit(ch) && ch is not ('_' or '-')))
            throw new InvalidOperationException("Media operation ID is not safe for an archive key");

        using var document = JsonDocument.Parse(metadataJson,
            new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Disallow });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Batch output metadata must be a JSON object");

        var items = root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();
        const int maxItems = 200;
        const long maxArchiveBytes = 512 * 1024 * 1024;
        if (items.Length > maxItems)
            throw new InvalidOperationException("Batch output exceeds the item limit");

        await EnsureBucketAsync(ct);
        await using var archiveStream = new MemoryStream();
        using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestFiles = new List<object>();
            var errors = new List<object>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < items.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var item = items[index];
                var url = item.TryGetProperty("url", out var urlValue)
                    && urlValue.ValueKind == JsonValueKind.String
                    ? urlValue.GetString() ?? "" : "";
                var customId = item.TryGetProperty("custom_id", out var customValue)
                    && customValue.ValueKind == JsonValueKind.String
                    ? customValue.GetString() ?? $"item-{index + 1}" : $"item-{index + 1}";
                if (!Uri.TryCreate(url, UriKind.Absolute, out var source)
                    || source.Scheme is not ("http" or "https"))
                {
                    errors.Add(new { custom_id = customId, error = "invalid_output_url" });
                    continue;
                }

                using var response = await _http.GetAsync(source,
                    HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    errors.Add(new
                    {
                        custom_id = customId,
                        error = "output_fetch_failed",
                        status = (int)response.StatusCode,
                    });
                    continue;
                }

                var remaining = maxArchiveBytes - archiveStream.Length;
                var bytes = await ReadBoundedAsync(response.Content, remaining, ct);
                var contentType = response.Content.Headers.ContentType?.MediaType
                    ?? "application/octet-stream";
                var filename = UniqueArchiveName(customId, index, contentType, usedNames);
                var entry = archive.CreateEntry(filename, CompressionLevel.Fastest);
                await using (var output = entry.Open())
                    await output.WriteAsync(bytes, ct);
                manifestFiles.Add(new { custom_id = customId, filename, size = bytes.LongLength,
                    content_type = contentType });
            }

            WriteArchiveJson(archive, "manifest.json", new { operation_id = operationId,
                files = manifestFiles });
            WriteArchiveJson(archive, "errors.json", errors);
            if (items.Length > 0 && manifestFiles.Count == 0)
                throw new InvalidOperationException("Batch output contains no downloadable items");
        }

        if (archiveStream.Length > maxArchiveBytes)
            throw new InvalidOperationException("Batch archive exceeds the object limit");
        var bytesToStore = archiveStream.ToArray();
        var stored = await PutAsync($"media/{operationId}.zip", bytesToStore,
            "application/zip", ct);
        return stored with
        {
            DownloadUrl = PresignGet(stored.ObjectKey, TimeSpan.FromHours(1)),
        };
    }

    public async Task<IReadOnlyList<BatchItemObjectResult>> CreateBatchItemObjectsAsync(
        string metadataJson, string operationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 128
            || operationId.Any(ch => !char.IsLetterOrDigit(ch) && ch is not ('_' or '-')))
            throw new InvalidOperationException("Media operation ID is not safe for an item key");

        using var document = JsonDocument.Parse(metadataJson,
            new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Disallow });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Batch output metadata must be a JSON object");
        var items = root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();
        if (items.Length > 200)
            throw new InvalidOperationException("Batch output exceeds the item limit");

        await EnsureBucketAsync(ct);
        const long maxItemBytes = 64 * 1024 * 1024;
        var totalBytes = 0L;
        var result = new List<BatchItemObjectResult>(items.Length);
        for (var index = 0; index < items.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var item = items[index];
            var customId = item.TryGetProperty("custom_id", out var customValue)
                && customValue.ValueKind == JsonValueKind.String
                ? customValue.GetString() ?? $"item-{index + 1}" : $"item-{index + 1}";
            customId = NormalizeCustomId(customId, index);
            var providerUrl = item.TryGetProperty("url", out var urlValue)
                && urlValue.ValueKind == JsonValueKind.String
                ? urlValue.GetString() ?? "" : "";
            if (providerUrl.Length > 8192
                || !Uri.TryCreate(providerUrl, UriKind.Absolute, out var source)
                || source.Scheme is not ("http" or "https"))
            {
                result.Add(new(index, customId, providerUrl, "", "", 0,
                    "application/octet-stream", "failed", "",
                    JsonSerializer.Serialize(new { type = "invalid_output_url" })));
                continue;
            }

            using var response = await _http.GetAsync(source,
                HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                result.Add(new(index, customId, providerUrl, "", "", 0,
                    "application/octet-stream", "failed", "",
                    JsonSerializer.Serialize(new
                    {
                        type = "output_fetch_failed",
                        status = (int)response.StatusCode,
                    })));
                continue;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType
                ?? "application/octet-stream";
            var bytes = await ReadBoundedAsync(response.Content, maxItemBytes, ct);
            totalBytes += bytes.LongLength;
            if (totalBytes > 512 * 1024 * 1024)
                throw new InvalidOperationException("Batch item outputs exceed the object limit");

            var key = $"media/{operationId}/items/{index + 1:D4}-{SafeItemName(customId)}.{ExtensionFor(contentType)}";
            var stored = await PutAsync(key, bytes, contentType, ct);
            result.Add(new(index, customId, providerUrl, stored.ObjectKey, stored.ETag,
                stored.Size, stored.ContentType, "stored",
                PresignGet(stored.ObjectKey, TimeSpan.FromHours(1)), ""));
        }

        if (items.Length > 0 && result.All(item => item.ObjectStatus != "stored"))
            throw new InvalidOperationException("Batch output contains no downloadable items");
        return result;
    }

    public string PresignGet(string objectKey, TimeSpan lifetime)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = Math.Clamp((long)lifetime.TotalSeconds, 1, 604800);
        var path = ObjectPath(objectKey);
        var host = HostHeader(_publicEndpoint);
        var date = now.ToString("yyyyMMdd");
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'");
        var credential = $"{_accessKey}/{date}/{_region}/s3/aws4_request";
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256",
            ["X-Amz-Credential"] = credential,
            ["X-Amz-Date"] = amzDate,
            ["X-Amz-Expires"] = expires.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["X-Amz-SignedHeaders"] = "host",
        };
        var canonicalQuery = string.Join('&', values.Select(pair =>
            $"{Encode(pair.Key)}={Encode(pair.Value)}"));
        var canonicalRequest = $"GET\n{path}\n{canonicalQuery}\nhost:{host}\n\nhost\nUNSIGNED-PAYLOAD";
        var scope = $"{date}/{_region}/s3/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Sha256(canonicalRequest)}";
        values["X-Amz-Signature"] = Hex(Hmac(SigningKey(date), stringToSign));

        var builder = new UriBuilder(_publicEndpoint)
        {
            Path = path,
            Query = string.Join('&', values.Select(pair =>
                $"{Encode(pair.Key)}={Encode(pair.Value)}")),
        };
        return builder.Uri.ToString();
    }

    private async Task<ObjectStoragePutResult> PutAsync(string objectKey, byte[] bytes,
        string contentType, CancellationToken ct)
    {
        await EnsureBucketAsync(ct);
        using var response = await SendSignedAsync(HttpMethod.Put, ObjectPath(objectKey),
            bytes, contentType, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Object storage PUT failed with {(int)response.StatusCode}: {body[..Math.Min(body.Length, 512)]}");
        }

        var etag = response.Headers.ETag?.Tag?.Trim('"') ?? Hex(SHA256.HashData(bytes));
        return new ObjectStoragePutResult(objectKey, etag, bytes.LongLength, contentType, "");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, long maxBytes,
        CancellationToken ct)
    {
        await using var input = await content.ReadAsStreamAsync(ct);
        await using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct);
            if (read == 0) break;
            if (output.Length + read > maxBytes)
                throw new InvalidOperationException("Batch output exceeds the object limit");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return output.ToArray();
    }

    private static string UniqueArchiveName(string customId, int index, string contentType,
        ISet<string> usedNames)
    {
        var normalized = new string(customId.Where(ch => char.IsLetterOrDigit(ch)
            || ch is '_' or '-' or '.').ToArray());
        if (string.IsNullOrWhiteSpace(normalized)) normalized = $"item-{index + 1}";
        normalized = normalized.Length > 80 ? normalized[..80] : normalized;
        var extension = ExtensionFor(contentType);
        var candidate = $"{normalized}.{extension}";
        var suffix = 1;
        while (!usedNames.Add(candidate))
            candidate = $"{normalized}-{++suffix}.{extension}";
        return candidate;
    }

    private static string SafeItemName(string customId)
    {
        var normalized = new string(customId.Where(ch => char.IsLetterOrDigit(ch)
            || ch is '_' or '-' or '.').ToArray());
        if (string.IsNullOrWhiteSpace(normalized)) normalized = "item";
        return normalized.Length > 80 ? normalized[..80] : normalized;
    }

    private static string NormalizeCustomId(string customId, int index)
    {
        var normalized = customId.Trim();
        if (string.IsNullOrEmpty(normalized)) normalized = $"item-{index + 1}";
        return normalized.Length > 256 ? normalized[..256] : normalized;
    }

    private static void WriteArchiveJson(ZipArchive archive, string name, object value)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8, leaveOpen: false);
        writer.Write(JsonSerializer.Serialize(value));
    }

    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (_bucketReady) return;
        await _bucketGate.WaitAsync(ct);
        try
        {
            if (_bucketReady) return;
            using var response = await SendSignedAsync(HttpMethod.Put, ObjectPath(""),
                [], null, ct);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
            {
                _bucketReady = true;
                return;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Object storage bucket initialization failed with {(int)response.StatusCode}: {body[..Math.Min(body.Length, 512)]}");
        }
        finally
        {
            _bucketGate.Release();
        }
    }

    private Task<HttpResponseMessage> SendSignedAsync(HttpMethod method, string path,
        byte[] body, string? contentType, CancellationToken ct) =>
        SendSignedAsync(method, path, new Dictionary<string, string>(), body,
            contentType, ct);

    private async Task<HttpResponseMessage> SendSignedAsync(HttpMethod method, string path,
        IReadOnlyDictionary<string, string> query, byte[] body, string? contentType,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'");
        var date = now.ToString("yyyyMMdd");
        var payloadHash = Sha256(body);
        var canonicalQuery = string.Join('&', query.OrderBy(pair => pair.Key,
                StringComparer.Ordinal).ThenBy(pair => pair.Value, StringComparer.Ordinal)
            .Select(pair => $"{Encode(pair.Key)}={Encode(pair.Value)}"));
        var uriBuilder = new UriBuilder(new Uri(_endpoint, path.TrimStart('/')))
        {
            Query = canonicalQuery,
        };
        var uri = uriBuilder.Uri;
        var host = HostHeader(uri);
        var canonicalHeaders = new StringBuilder()
            .Append("host:").Append(host).Append('\n')
            .Append("x-amz-content-sha256:").Append(payloadHash).Append('\n')
            .Append("x-amz-date:").Append(amzDate).Append('\n');
        var signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            canonicalHeaders.Insert(0, $"content-type:{contentType.Trim().ToLowerInvariant()}\n");
            signedHeaders = "content-type;" + signedHeaders;
        }

        var canonicalRequest = $"{method.Method}\n{path}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
        var scope = $"{date}/{_region}/s3/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Sha256(canonicalRequest)}";
        var signature = Hex(Hmac(SigningKey(date), stringToSign));

        var request = new HttpRequestMessage(method, uri);
        request.Headers.Host = host;
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("Authorization",
            $"AWS4-HMAC-SHA256 Credential={_accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
        if (method != HttpMethod.Get)
        {
            var content = new ByteArrayContent(body);
            if (!string.IsNullOrWhiteSpace(contentType)) content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            request.Content = content;
        }

        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private byte[] SigningKey(string date) =>
        Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + _secretKey), date), _region), "s3"),
            "aws4_request");

    private string ObjectPath(string key)
    {
        if (string.IsNullOrEmpty(key)) return $"/{Encode(_bucket)}";
        var segments = key.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Encode);
        return $"/{Encode(_bucket)}/{string.Join('/', segments)}";
    }

    private static Uri ParseEndpoint(string? value)
    {
        if (!Uri.TryCreate(value?.TrimEnd('/'), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("ObjectStorage endpoint must be an absolute HTTP URL");
        return uri;
    }

    private static string HostHeader(Uri uri) => uri.IsDefaultPort
        ? uri.Host : $"{uri.Host}:{uri.Port}";

    private static string ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/png" => "png",
        "image/jpeg" => "jpg",
        "video/mp4" => "mp4",
        "application/json" => "json",
        _ => "bin",
    };

    private static string Encode(string value) => Uri.EscapeDataString(value).Replace("%7E", "~");

    private static byte[] Hmac(byte[] key, string value) =>
        Hmac(key, Encoding.UTF8.GetBytes(value));

    private static byte[] Hmac(byte[] key, byte[] value)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(value);
    }

    private static string Sha256(string value) => Sha256(Encoding.UTF8.GetBytes(value));

    private static string Sha256(byte[] value) => Hex(SHA256.HashData(value));

    private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();
}
