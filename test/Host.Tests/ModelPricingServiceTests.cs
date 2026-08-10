using Microsoft.Extensions.Configuration;
using Npgsql;
using ScalaAPI.Host.Services;
using Xunit;

namespace ScalaAPI.Host.Tests;

public class ModelPricingServiceTests
{
    [Fact]
    public void UnknownModelHasNoImplicitFallbackPrice()
    {
        var pricing = Create();

        Assert.False(pricing.TryGetPrice("unknown-model", out _));
    }

    [Fact]
    public void AnthropicSeedModelUsesTheDefaultPriceAlias()
    {
        var pricing = Create();

        Assert.True(pricing.TryGetPrice("claude-3-5-sonnet", out var price));
        Assert.Equal(3m, price.InputPerMillion);
        Assert.Equal(15m, price.OutputPerMillion);
        Assert.Equal("runtime-v1", price.Version);
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

    [Fact]
    public async Task AdministrativePriceWinsOverLaterProviderQuote()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var model = $"pricing-precedence-{suffix}";
        var adminVersion = $"admin-{suffix}";
        var providerVersion = $"provider-{suffix}";
        try
        {
            await using (var insert = dataSource.CreateCommand("""
                INSERT INTO pricing_versions(
                    version, model, input_usd_per_million, output_usd_per_million,
                    effective_from, source_provider)
                VALUES ($1, $2, 1, 2, now(), 'admin'),
                       ($3, $2, 9, 10, now() + interval '1 second', 'mock')
                """))
            {
                insert.Parameters.AddWithValue(adminVersion);
                insert.Parameters.AddWithValue(model);
                insert.Parameters.AddWithValue(providerVersion);
                await insert.ExecuteNonQueryAsync();
            }

            var pricing = new ModelPricingService(
                new ConfigurationBuilder().Build(), dataSource);
            await pricing.RefreshFromDatabaseAsync();

            Assert.True(pricing.TryGetPrice(model, out var selected));
            Assert.Equal(1m, selected.InputPerMillion);
            Assert.Equal(adminVersion, selected.Version);
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand(
                "DELETE FROM pricing_versions WHERE version = ANY($1)");
            cleanup.Parameters.AddWithValue(new[] { adminVersion, providerVersion });
            await cleanup.ExecuteNonQueryAsync();
        }
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
