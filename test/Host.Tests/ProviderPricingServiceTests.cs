using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ScalaAPI.Host.Services;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class ProviderPricingServiceTests
{
    [Fact]
    public void CatalogParsingIsDeterministicAndRejectsDuplicateModels()
    {
        const string payload = """
            {"data":[
              {"model":"gpt-4o","input_usd_per_million":2.5,"output_usd_per_million":10,"cache_read_usd_per_million":1.25,"cache_write_usd_per_million":0},
              {"model":"claude-sonnet-4","input_usd_per_million":"3.00000000","output_usd_per_million":15,"cache_read_usd_per_million":0.3,"cache_write_usd_per_million":3.75}
            ]}
            """;
        var retrievedAt = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
        var first = ProviderPricingCatalogClient.Parse("Mock.Provider", Encoding.UTF8.GetBytes(payload), retrievedAt);
        var second = ProviderPricingCatalogClient.Parse("mock.provider", Encoding.UTF8.GetBytes(payload), retrievedAt);

        Assert.Equal(first.Checksum, second.Checksum);
        Assert.Equal(first.Version, second.Version);
        Assert.Equal("mock.provider", first.Provider);
        Assert.Equal(2, first.Quotes.Count);
        Assert.Equal(3m, first.Quotes.Single(q => q.Model == "claude-sonnet-4").InputUsdPerMillion);

        const string duplicate = """
            {"data":[
              {"model":"gpt-4o","input_usd_per_million":1,"output_usd_per_million":1,"cache_read_usd_per_million":0,"cache_write_usd_per_million":0},
              {"model":"GPT-4O","input_usd_per_million":1,"output_usd_per_million":1,"cache_read_usd_per_million":0,"cache_write_usd_per_million":0}
            ]}
            """;
        Assert.Throws<ProviderPricingException>(() => ProviderPricingCatalogClient.Parse(
            "mock", Encoding.UTF8.GetBytes(duplicate), retrievedAt));
    }

    [Fact]
    public async Task CatalogClientUsesBearerCredentialAndBoundsProviderResponse()
    {
        var handler = new RecordingHandler("""
            {"data":[{"model":"gpt-4o","input_usd_per_million":2.5,"output_usd_per_million":10,"cache_read_usd_per_million":1.25,"cache_write_usd_per_million":0}]}
            """);
        using var httpClient = new HttpClient(handler);
        var client = new ProviderPricingCatalogClient(httpClient);

        var snapshot = await client.FetchAsync("mock", new Uri("https://pricing.example.test/v1"),
            "secret-token");

        Assert.Equal("Bearer secret-token", handler.Authorization);
        Assert.Equal("mock", snapshot.Provider);
        Assert.Single(snapshot.Quotes);
    }

    [Fact]
    public async Task ApplyingChangedSnapshotKeepsHistoryAndReplaysIdempotently()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("GREENFIELD_SCHEMA_CONNECTION is not set");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var configuration = new ConfigurationBuilder().Build();
        var service = new ProviderPricingRefreshService(dataSource,
            new ProviderPricingCatalogClient(new HttpClient(new RecordingHandler("""{"data":[]}"""))),
            configuration, NullLogger<ProviderPricingRefreshService>.Instance);
        var first = ProviderPricingCatalogClient.Parse("test-provider", Encoding.UTF8.GetBytes("""
            {"data":[
              {"model":"test-model","input_usd_per_million":1,"output_usd_per_million":2,"cache_read_usd_per_million":0,"cache_write_usd_per_million":0},
              {"model":"test-model-2","input_usd_per_million":3,"output_usd_per_million":4,"cache_read_usd_per_million":0,"cache_write_usd_per_million":0}
            ]}
            """), DateTimeOffset.UtcNow.AddMinutes(-2));
        var second = ProviderPricingCatalogClient.Parse("test-provider", Encoding.UTF8.GetBytes("""
            {"data":[
              {"model":"test-model","input_usd_per_million":1.5,"output_usd_per_million":2,"cache_read_usd_per_million":0,"cache_write_usd_per_million":0},
              {"model":"test-model-2","input_usd_per_million":3,"output_usd_per_million":4,"cache_read_usd_per_million":0,"cache_write_usd_per_million":0}
            ]}
            """), DateTimeOffset.UtcNow.AddMinutes(-1));

        try
        {
            Assert.Equal(2, await service.ApplySnapshotAsync(first));
            Assert.Equal(0, await service.ApplySnapshotAsync(first));
            Assert.Equal(2, await service.ApplySnapshotAsync(second));

            await using var command = dataSource.CreateCommand("""
                SELECT count(*), count(*) FILTER (WHERE effective_until IS NULL),
                       count(*) FILTER (WHERE model = 'test-model' AND effective_until IS NULL
                                        AND input_usd_per_million = 1.5)
                FROM pricing_versions WHERE source_provider = 'test-provider'
                """);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(4, reader.GetInt64(0));
            Assert.Equal(2, reader.GetInt64(1));
            Assert.Equal(1, reader.GetInt64(2));
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand(
                "DELETE FROM pricing_versions WHERE source_provider = 'test-provider'");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private sealed class RecordingHandler(string body) : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
