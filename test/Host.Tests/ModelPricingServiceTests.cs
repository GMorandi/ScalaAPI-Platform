using Microsoft.Extensions.Configuration;
using Sub2Api.Host.Services;
using Xunit;

namespace Sub2Api.Host.Tests;

public class ModelPricingServiceTests
{
    [Fact]
    public void UnknownModelHasNoImplicitFallbackPrice()
    {
        var pricing = Create();

        Assert.False(pricing.TryGetPrice("unknown-model", out _));
    }

    [Fact]
    public void ConfigurationAddsMediaAndRealtimeRates()
    {
        var pricing = Create(new Dictionary<string, string?>
        {
            ["Pricing:Models:gpt-image-1:ImageInputPerUnit"] = "0.02",
            ["Pricing:Models:gpt-image-1:ImageOutputPerUnit"] = "0.08",
            ["Pricing:Models:grok-video:VideoPerSecond"] = "0.15",
            ["Pricing:Models:realtime-custom:RealtimePerMinute"] = "1.25",
        });

        Assert.True(pricing.TryGetPrice("gpt-image-1", out var image));
        Assert.Equal(0.02m, image.ImageInputPerUnit);
        Assert.Equal(0.08m, image.ImageOutputPerUnit);
        Assert.True(pricing.TryGetPrice("grok-video-v2", out var video));
        Assert.Equal(0.15m, video.VideoPerSecond);
        Assert.True(pricing.TryGetPrice("realtime-custom-preview", out var realtime));
        Assert.Equal(1.25m, realtime.RealtimePerMinute);
    }

    [Fact]
    public void LongestConfiguredPrefixWins()
    {
        var pricing = Create(new Dictionary<string, string?>
        {
            ["Pricing:Models:custom:InputPerMillion"] = "1",
            ["Pricing:Models:custom-pro:InputPerMillion"] = "2",
        });

        Assert.True(pricing.TryGetPrice("custom-pro-2026", out var price));
        Assert.Equal(2m, price.InputPerMillion);
    }

    private static ModelPricingService Create(
        Dictionary<string, string?>? values = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values ?? new Dictionary<string, string?>())
            .Build();
        return new ModelPricingService(configuration);
    }
}
