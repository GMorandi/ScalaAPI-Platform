extern alias providerMock;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ScalaAPI.Provider.Mock.Tests;

public sealed class MockClassifierHttpContractTests :
    IClassFixture<WebApplicationFactory<providerMock::Program>>
{
    private readonly WebApplicationFactory<providerMock::Program> factory;

    public MockClassifierHttpContractTests(
        WebApplicationFactory<providerMock::Program> factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task ClassifierReturnsDeterministicMatchAndNoMatch()
    {
        using var client = factory.CreateClient();
        using var match = await client.PostAsJsonAsync("/v1/classifier/evaluate", new
        {
            content = "normalized marker payload",
            pattern = "marker",
            evaluator_version = "unicode-confusable-v1",
        });
        using var matchDocument = JsonDocument.Parse(await match.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, match.StatusCode);
        Assert.Equal("match", matchDocument.RootElement.GetProperty("outcome").GetString());

        using var noMatch = await client.PostAsJsonAsync("/v1/classifier/evaluate", new
        {
            content = "clean payload",
            pattern = "marker",
            evaluator_version = "unicode-confusable-v1",
        });
        using var noMatchDocument = JsonDocument.Parse(await noMatch.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, noMatch.StatusCode);
        Assert.Equal("no_match", noMatchDocument.RootElement.GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData("external-classifier-outage-marker", HttpStatusCode.ServiceUnavailable)]
    [InlineData("external-classifier-malformed-marker", HttpStatusCode.OK)]
    public async Task ClassifierFaultFixturesAreStable(string pattern, HttpStatusCode status)
    {
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/v1/classifier/evaluate", new
        {
            content = pattern,
            pattern,
            evaluator_version = "unicode-confusable-v1",
        });

        Assert.Equal(status, response.StatusCode);
        if (pattern.Contains("malformed", StringComparison.Ordinal))
            Assert.Contains("{not-json", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ClassifierRejectsUnknownEvaluatorAndOversizedPattern()
    {
        using var client = factory.CreateClient();
        using var unsupported = await client.PostAsJsonAsync("/v1/classifier/evaluate", new
        {
            content = "payload",
            pattern = "marker",
            evaluator_version = "old-evaluator",
        });
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);

        using var oversized = await client.PostAsJsonAsync("/v1/classifier/evaluate", new
        {
            content = "payload",
            pattern = new string('x', 1025),
            evaluator_version = "unicode-confusable-v1",
        });
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
    }
}
