using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace ScalaAPI.Host.Services;

public sealed record ProviderPriceQuote(
    string Model,
    decimal InputUsdPerMillion,
    decimal OutputUsdPerMillion,
    decimal CacheReadUsdPerMillion,
    decimal CacheWriteUsdPerMillion);

public sealed record ProviderPricingSnapshot(
    string Provider,
    string Version,
    string Checksum,
    DateTimeOffset RetrievedAt,
    IReadOnlyList<ProviderPriceQuote> Quotes);

public sealed class ProviderPricingCatalogClient(HttpClient client)
{
    private const int MaxResponseBytes = 128 * 1024;
    private const decimal MaxRate = 1_000_000m;

    public async Task<ProviderPricingSnapshot> FetchAsync(
        string provider, Uri endpoint, string? apiKey, CancellationToken ct = default)
    {
        var normalizedProvider = NormalizeProvider(provider);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("ScalaAPI-pricing/1");
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new("Bearer", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && ex is HttpRequestException or IOException or OperationCanceledException)
        {
            throw new ProviderPricingException("provider_pricing_unavailable", ex);
        }

        using (response)
        {
            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
                throw new ProviderPricingException("provider_pricing_response_too_large");
            var body = await ReadBoundedAsync(response.Content, ct);
            if (!response.IsSuccessStatusCode)
                throw new ProviderPricingException(
                    $"provider_pricing_status_{(int)response.StatusCode}");
            return Parse(normalizedProvider, body);
        }
    }

    public static ProviderPricingSnapshot Parse(
        string provider, ReadOnlySpan<byte> body, DateTimeOffset? retrievedAt = null)
    {
        var normalizedProvider = NormalizeProvider(provider);
        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
                throw new ProviderPricingException("provider_pricing_data_missing");

            var quotes = new List<ProviderPriceQuote>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("model", out var modelElement)
                    || modelElement.ValueKind != JsonValueKind.String)
                    throw new ProviderPricingException("provider_pricing_model_invalid");
                var model = modelElement.GetString()?.Trim() ?? string.Empty;
                if (model.Length is < 1 or > 200 || !seen.Add(model))
                    throw new ProviderPricingException("provider_pricing_model_duplicate");

                var quote = new ProviderPriceQuote(
                    model,
                    ReadRate(item, "input_usd_per_million"),
                    ReadRate(item, "output_usd_per_million"),
                    ReadRate(item, "cache_read_usd_per_million"),
                    ReadRate(item, "cache_write_usd_per_million"));
                quotes.Add(quote);
            }
            if (quotes.Count == 0)
                throw new ProviderPricingException("provider_pricing_data_empty");

            var canonical = string.Join('\n', quotes
                .OrderBy(q => q.Model, StringComparer.OrdinalIgnoreCase)
                .Select(q => string.Join('\u001f',
                    q.Model,
                    q.InputUsdPerMillion.ToString("0.00000000", CultureInfo.InvariantCulture),
                    q.OutputUsdPerMillion.ToString("0.00000000", CultureInfo.InvariantCulture),
                    q.CacheReadUsdPerMillion.ToString("0.00000000", CultureInfo.InvariantCulture),
                    q.CacheWriteUsdPerMillion.ToString("0.00000000", CultureInfo.InvariantCulture))));
            var checksum = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
            var version = $"provider-{normalizedProvider}-{checksum[..16]}";
            return new(normalizedProvider, version, checksum,
                retrievedAt ?? DateTimeOffset.UtcNow, quotes);
        }
        catch (ProviderPricingException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new ProviderPricingException("provider_pricing_response_invalid", ex);
        }
    }

    private static decimal ReadRate(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var element))
            throw new ProviderPricingException("provider_pricing_rate_missing");
        decimal value;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var number))
            value = number;
        else if (element.ValueKind == JsonValueKind.String
                 && decimal.TryParse(element.GetString(), NumberStyles.Number,
                     CultureInfo.InvariantCulture, out var parsed))
            value = parsed;
        else
            throw new ProviderPricingException("provider_pricing_rate_invalid");
        if (value < 0m || value > MaxRate || decimal.Round(value, 8) != value)
            throw new ProviderPricingException("provider_pricing_rate_out_of_range");
        return value;
    }

    private static string NormalizeProvider(string provider)
    {
        var normalized = provider.Trim().ToLowerInvariant();
        if (normalized.Length is < 1 or > 64
            || !normalized.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            throw new ProviderPricingException("provider_pricing_provider_invalid");
        return normalized;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content,
        CancellationToken ct)
    {
        await using var source = await content.ReadAsStreamAsync(ct);
        using var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0) return destination.ToArray();
            if (destination.Length + read > MaxResponseBytes)
                throw new ProviderPricingException("provider_pricing_response_too_large");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
}

public sealed class ProviderPricingException(string code, Exception? inner = null)
    : Exception(code, inner);

public sealed class ProviderPricingRefreshService(
    NpgsqlDataSource dataSource,
    ProviderPricingCatalogClient client,
    IConfiguration configuration,
    ILogger<ProviderPricingRefreshService> logger)
{
    public async Task<int> RefreshOnceAsync(CancellationToken ct = default)
    {
        var applied = 0;
        foreach (var provider in configuration.GetSection("Pricing:Providers").GetChildren())
        {
            var endpointText = provider["Endpoint"];
            if (string.IsNullOrWhiteSpace(endpointText)) continue;
            if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint)
                || (endpoint.Scheme != Uri.UriSchemeHttp
                    && endpoint.Scheme != Uri.UriSchemeHttps))
            {
                logger.LogWarning("Ignoring invalid pricing endpoint for provider {Provider}",
                    provider.Key);
                continue;
            }
            if (endpoint.Scheme == Uri.UriSchemeHttp
                && !configuration.GetValue("Pricing:AllowInsecureProviderEndpoints", false))
            {
                logger.LogWarning("Ignoring insecure pricing endpoint for provider {Provider}",
                    provider.Key);
                continue;
            }

            try
            {
                var snapshot = await client.FetchAsync(provider.Key, endpoint,
                    provider["ApiKey"], ct);
                applied += await ApplySnapshotAsync(snapshot, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var code = ex is ProviderPricingException ? ex.Message
                    : "provider_pricing_refresh_failed";
                logger.LogWarning("Provider pricing refresh failed for {Provider}: {Code}",
                    provider.Key, code);
            }
        }
        return applied;
    }

    public async Task<int> ApplySnapshotAsync(
        ProviderPricingSnapshot snapshot, CancellationToken ct = default)
    {
        if (snapshot.Quotes.Count == 0)
            throw new ArgumentException("Pricing snapshot must contain quotes", nameof(snapshot));
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var inserted = 0;
        foreach (var quote in snapshot.Quotes)
        {
            var version = VersionFor(snapshot, quote.Model);
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO pricing_versions(
                    version, model, input_usd_per_million, output_usd_per_million,
                    cache_read_usd_per_million, cache_write_usd_per_million,
                    effective_from, source_provider, source_model, source_checksum)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $2, $9)
                ON CONFLICT (version) DO NOTHING
                RETURNING version
                """;
            insert.Parameters.AddWithValue(version);
            insert.Parameters.AddWithValue(quote.Model);
            insert.Parameters.AddWithValue(quote.InputUsdPerMillion);
            insert.Parameters.AddWithValue(quote.OutputUsdPerMillion);
            insert.Parameters.AddWithValue(quote.CacheReadUsdPerMillion);
            insert.Parameters.AddWithValue(quote.CacheWriteUsdPerMillion);
            insert.Parameters.AddWithValue(snapshot.RetrievedAt.UtcDateTime);
            insert.Parameters.AddWithValue(snapshot.Provider);
            insert.Parameters.AddWithValue(snapshot.Checksum);
            var wasInserted = await insert.ExecuteScalarAsync(ct) is not null;
            if (!wasInserted) continue;
            inserted++;

            await using var close = connection.CreateCommand();
            close.Transaction = transaction;
            close.CommandText = """
                UPDATE pricing_versions
                SET effective_until = $1
                WHERE source_provider = $2 AND source_model = $3
                  AND effective_until IS NULL AND version <> $4
                  AND effective_from <= $1
                """;
            close.Parameters.AddWithValue(snapshot.RetrievedAt.UtcDateTime);
            close.Parameters.AddWithValue(snapshot.Provider);
            close.Parameters.AddWithValue(quote.Model);
            close.Parameters.AddWithValue(version);
            await close.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return inserted;
    }

    private static string VersionFor(ProviderPricingSnapshot snapshot, string model)
    {
        var modelHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(model))).ToLowerInvariant()[..12];
        return $"{snapshot.Version}-{modelHash}";
    }
}

public sealed class ProviderPricingRefreshHostedService(
    ProviderPricingRefreshService refresh,
    IConfiguration configuration,
    ILogger<ProviderPricingRefreshHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue("Pricing:RefreshSeconds", 60), 15, 3600));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await refresh.RefreshOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Provider pricing refresh iteration failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
