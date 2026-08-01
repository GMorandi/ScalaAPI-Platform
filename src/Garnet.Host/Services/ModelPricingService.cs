using System.Collections.Concurrent;

namespace Sub2Api.Host.Services;

public record ModelPrice(decimal InputPerMillion, decimal OutputPerMillion,
    decimal CacheCreatePerMillion = 0, decimal CacheReadPerMillion = 0);

public class ModelPricingService
{
    private readonly ConcurrentDictionary<string, ModelPrice> _prices = new();

    public ModelPricingService()
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
    }

    public ModelPrice GetPrice(string model)
    {
        if (_prices.TryGetValue(model, out var exact))
            return exact;

        foreach (var (prefix, price) in _prices)
        {
            if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return price;
        }

        return new ModelPrice(3m, 15m);
    }

    public void SetPrice(string modelPrefix, ModelPrice price)
    {
        _prices[modelPrefix] = price;
    }
}
