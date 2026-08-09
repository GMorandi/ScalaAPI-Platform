using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ScalaAPI.Host.Services;

public sealed record ObjectStoragePutResult(
    string ObjectKey, string ETag, long Size, string ContentType, string DownloadUrl);

public sealed record ObjectStorageHeadResult(
    bool Exists, string ETag, long Size, string ContentType);

public interface IMediaObjectStorage
{
    Task<ObjectStorageHeadResult> HeadAsync(string objectKey,
        CancellationToken ct = default);
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

    private async Task<HttpResponseMessage> SendSignedAsync(HttpMethod method, string path,
        byte[] body, string? contentType, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'");
        var date = now.ToString("yyyyMMdd");
        var payloadHash = Sha256(body);
        var uri = new Uri(_endpoint, path.TrimStart('/'));
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

        var canonicalRequest = $"{method.Method}\n{path}\n\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
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
