extern alias providerMock;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ScalaAPI.Provider.Mock.Tests;

public sealed class MockCatalogHttpContractTests :
    IClassFixture<WebApplicationFactory<providerMock::Program>>
{
    private readonly WebApplicationFactory<providerMock::Program> factory;

    public MockCatalogHttpContractTests(
        WebApplicationFactory<providerMock::Program> factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task ModelCatalogReturnsDeterministicOpenAiAndGeminiMetadata()
    {
        using var client = factory.CreateClient();
        using var openAi = await client.GetAsync("/v1/models");
        using var openAiDocument = JsonDocument.Parse(await openAi.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, openAi.StatusCode);
        Assert.Equal("list", openAiDocument.RootElement.GetProperty("object").GetString());
        Assert.Contains(openAiDocument.RootElement.GetProperty("data").EnumerateArray(),
            model => model.GetProperty("id").GetString() == "gpt-4o");
        Assert.Contains(openAiDocument.RootElement.GetProperty("data").EnumerateArray(),
            model => model.GetProperty("id").GetString() == "jina-embeddings-v5-text-small");
        Assert.Contains(openAiDocument.RootElement.GetProperty("data").EnumerateArray(),
            model => model.GetProperty("id").GetString() == "gemini-embedding-001");

        using var gemini = await client.GetAsync("/v1beta/models");
        using var geminiDocument = JsonDocument.Parse(await gemini.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, gemini.StatusCode);
        var model = Assert.Single(geminiDocument.RootElement.GetProperty("models").EnumerateArray());
        Assert.StartsWith("models/", model.GetProperty("name").GetString());
        Assert.Contains("generateContent", model.GetProperty("supportedGenerationMethods")
            .EnumerateArray().Select(method => method.GetString()));
    }

    [Fact]
    public async Task PricingCatalogReturnsBoundedDecimalQuotes()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/v1/pricing");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var quotes = document.RootElement.GetProperty("data").EnumerateArray().ToArray();
        Assert.Equal(3, quotes.Length);
        Assert.Contains(quotes, quote => quote.GetProperty("model").GetString() == "gpt-4o");
        Assert.All(quotes, quote =>
        {
            Assert.True(quote.GetProperty("input_usd_per_million").GetDecimal() >= 0m);
            Assert.True(quote.GetProperty("output_usd_per_million").GetDecimal() >= 0m);
        });
    }

    [Fact]
    public async Task CountTokensReturnsPositiveDeterministicUsage()
    {
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/v1/messages/count_tokens", new
        {
            model = "claude-3-5-sonnet",
            messages = new[] { new { role = "user", content = "hello world" } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("input_tokens").GetInt32() > 0);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    public async Task CatalogFaultProfilesRemainDeterministic(string scenario)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync($"/v1/models?mock_scenario={scenario}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        if (scenario == "malformed")
            Assert.Contains("{not-json", payload);
        else
            Assert.Equal(2, JsonDocument.Parse(payload).RootElement.GetProperty("data").GetArrayLength());
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("invalid")]
    public async Task CountTokenFaultProfilesRemainDeterministic(string scenario)
    {
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/v1/messages/count_tokens", new
        {
            model = "claude-3-5-sonnet",
            mock_scenario = scenario,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (scenario == "malformed")
            Assert.Contains("{not-json", await response.Content.ReadAsStringAsync());
        else
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(0, document.RootElement.GetProperty("input_tokens").GetInt32());
        }
    }
}
