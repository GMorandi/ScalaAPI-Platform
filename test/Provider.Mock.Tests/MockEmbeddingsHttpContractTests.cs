extern alias providerMock;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ScalaAPI.Provider.Mock.Tests;

public sealed class MockEmbeddingsHttpContractTests :
    IClassFixture<WebApplicationFactory<providerMock::Program>>
{
    private readonly WebApplicationFactory<providerMock::Program> factory;

    public MockEmbeddingsHttpContractTests(
        WebApplicationFactory<providerMock::Program> factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task EmbeddingsHonorsInputCountDimensionsAndBase64()
    {
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/v1/embeddings", new
        {
            model = "text-embedding-3-small",
            input = new[] { "hello", "world" },
            dimensions = 3,
            encoding_format = "base64",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(2, data.GetArrayLength());
        Assert.All(data.EnumerateArray(), item =>
        {
            Assert.Equal(JsonValueKind.String, item.GetProperty("embedding").ValueKind);
            Assert.Equal(16, item.GetProperty("embedding").GetString()!.Length);
        });
    }

    [Theory]
    [InlineData("429", HttpStatusCode.TooManyRequests)]
    [InlineData("500", HttpStatusCode.InternalServerError)]
    [InlineData("malformed", HttpStatusCode.OK)]
    public async Task EmbeddingProviderFaultsRemainDeterministic(
        string scenario, HttpStatusCode expectedStatus)
    {
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/v1/embeddings", new
        {
            model = "text-embedding-3-small",
            input = "hello",
            mock_scenario = scenario,
        });

        Assert.Equal(expectedStatus, response.StatusCode);
        if (scenario == "malformed")
            Assert.Contains("{not-json", await response.Content.ReadAsStringAsync());
    }
}
