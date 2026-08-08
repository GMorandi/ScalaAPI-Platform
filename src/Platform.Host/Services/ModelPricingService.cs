using System.Collections.Concurrent;

namespace ScalaAPI.Host.Services;

public record ModelPrice(decimal InputPerMillion, decimal OutputPerMillion,
    decimal CacheCreatePerMillion = 0, decimal CacheReadPerMillion = 0,
    decimal ImageInputPerUnit = 0, decimal ImageOutputPerUnit = 0,
    decimal VideoPerSecond = 0, decimal RealtimePerMinute = 0);

public class ModelPricingService
{
    private readonly ConcurrentDictionary<string, ModelPrice> _prices = new();

    public ModelPricingService(IConfiguration configuration)
    {
        _prices["claude-sonnet-4"] = new(3m, 15m, 3.75m, 0.30m);
        _prices["claude-opus-4"] = new(15m, 75m, 18.75m, 1.50m);
        _prices["claude-haiku"] = new(0.80m, 4m, 1m, 0.08m);
        _prices["gpt-4o"] = new(2.50m, 10m, 1.25m, 0);
        _prices["gpt-4.1"] = new(2m, 8m, 0, 0);
        _prices["gpt-4o-mini"] = new(0.15m, 0.60m, 0, 0);
        _prices["o3"] = new(10m, 40m, 2.50m, 0);
        _prices["o4-mini"] = new(1.10m, 4.40m, 0.275m, 0);
        _prices["gemini-2.5-pro"] = new(1.25m, 10m, 0, 0);
        _prices["gemini-2.5-flash"] = new(0.15m, 0.60m, 0, 0);
        _prices["gemini-2.0-flash"] = new(0.10m, 0.40m, 0, 0);

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
                model.GetValue("RealtimePerMinute", 0m));
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
}
