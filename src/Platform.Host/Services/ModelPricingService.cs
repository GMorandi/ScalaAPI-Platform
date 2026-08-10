using System.Collections.Concurrent;
using Npgsql;

namespace ScalaAPI.Host.Services;

public record ModelPrice(decimal InputPerMillion, decimal OutputPerMillion,
    decimal CacheCreatePerMillion = 0, decimal CacheReadPerMillion = 0,
    decimal ImageInputPerUnit = 0, decimal ImageOutputPerUnit = 0,
    decimal VideoPerSecond = 0, decimal RealtimePerMinute = 0,
    string Version = "runtime-v1");

public class ModelPricingService
{
    private readonly ConcurrentDictionary<string, ModelPrice> _prices = new();
    private readonly string _defaultVersion;
    private readonly NpgsqlDataSource? _dataSource;

    public ModelPricingService(IConfiguration configuration, NpgsqlDataSource? dataSource = null)
    {
        _dataSource = dataSource;
        _defaultVersion = configuration["Pricing:Version"] ?? "runtime-v1";
        _prices["claude-sonnet-4"] = new(3m, 15m, 3.75m, 0.30m, Version: _defaultVersion);
        _prices["claude-opus-4"] = new(15m, 75m, 18.75m, 1.50m, Version: _defaultVersion);
        _prices["claude-haiku"] = new(0.80m, 4m, 1m, 0.08m, Version: _defaultVersion);
        _prices["gpt-4o"] = new(2.50m, 10m, 1.25m, 0, Version: _defaultVersion);
        _prices["gpt-4.1"] = new(2m, 8m, 0, 0, Version: _defaultVersion);
        _prices["gpt-4o-mini"] = new(0.15m, 0.60m, 0, 0, Version: _defaultVersion);
        _prices["o3"] = new(10m, 40m, 2.50m, 0, Version: _defaultVersion);
        _prices["o4-mini"] = new(1.10m, 4.40m, 0.275m, 0, Version: _defaultVersion);
        _prices["gemini-2.5-pro"] = new(1.25m, 10m, 0, 0, Version: _defaultVersion);
        _prices["gemini-2.5-flash"] = new(0.15m, 0.60m, 0, 0, Version: _defaultVersion);
        _prices["gemini-2.0-flash"] = new(0.10m, 0.40m, 0, 0, Version: _defaultVersion);

        foreach (var model in configuration.GetSection("Pricing:Models").GetChildren())
        {
            if (string.IsNullOrWhiteSpace(model.Key))
                continue;
            _prices[model.Key] = new ModelPrice(
                model.GetValue("InputPerMillion", 0m),
                model.GetValue("OutputPerMillion", 0m),
                model.GetValue("CacheCreatePerMillion", 0m),
                model.GetValue("CacheReadPerMillion", 0m),
                model.GetValue("ImageInputPerUnit", 0m),
                model.GetValue("ImageOutputPerUnit", 0m),
                model.GetValue("VideoPerSecond", 0m),
                model.GetValue("RealtimePerMinute", 0m),
                model.GetValue("Version", _defaultVersion));
        }
    }

    public bool TryGetPrice(string model, out ModelPrice price)
    {
        if (_prices.TryGetValue(model, out var exact))
        {
            price = exact;
            return true;
        }

        foreach (var (prefix, candidate) in _prices
                     .OrderByDescending(entry => entry.Key.Length))
        {
            if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                price = candidate;
                return true;
            }
        }

        price = null!;
        return false;
    }

    public void SetPrice(string modelPrefix, ModelPrice price)
    {
        _prices[modelPrefix] = price;
    }

    public async Task RefreshFromDatabaseAsync(CancellationToken ct = default)
    {
        if (_dataSource is null) return;
        await using var command = _dataSource.CreateCommand("""
            SELECT DISTINCT ON (model) model, version,
                   input_usd_per_million, output_usd_per_million,
                   cache_read_usd_per_million, cache_write_usd_per_million
            FROM pricing_versions
            WHERE effective_from <= now()
              AND (effective_until IS NULL OR effective_until > now())
            ORDER BY model,
                     CASE WHEN source_provider = 'admin' THEN 0 ELSE 1 END,
                     effective_from DESC, version DESC
            """);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var model = reader.GetString(0);
            _prices[model] = new ModelPrice(
                reader.GetDecimal(2), reader.GetDecimal(3),
                CacheCreatePerMillion: reader.GetDecimal(5),
                CacheReadPerMillion: reader.GetDecimal(4),
                Version: reader.GetString(1));
        }
    }
}
