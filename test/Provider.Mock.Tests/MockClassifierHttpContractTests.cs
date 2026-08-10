extern alias providerMock;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ScalaAPI.Host.Services;
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

    [Fact]
    public async Task OpenAiModerationAdapterConsumesOfficialShapedFixture()
    {
        using var client = factory.CreateClient();
        var adapter = new OpenAiModerationClassifier(client,
            new OpenAiModerationClientOptions(
                new Uri("http://localhost/v1/moderations"),
                "mock-openai-moderation-key", "omni-moderation-latest",
                TimeSpan.FromMilliseconds(500)));

        var flagged = await adapter.EvaluateAsync("openai",
            "openai-moderation-flag-marker", "policy-rule");
        var clean = await adapter.EvaluateAsync("openai", "clean payload", "policy-rule");

        Assert.Equal(ContentClassifierOutcome.Match, flagged.Outcome);
        Assert.Equal(ContentClassifierOutcome.NoMatch, clean.Outcome);
    }

    [Theory]
    [InlineData("openai-moderation-unavailable", HttpStatusCode.ServiceUnavailable)]
    [InlineData("openai-moderation-malformed", HttpStatusCode.OK)]
    [InlineData("openai-moderation-oversized", HttpStatusCode.OK)]
    public async Task OpenAiModerationFaultFixturesRemainBounded(
        string scenario, HttpStatusCode expectedStatus)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/moderations")
        {
            Content = JsonContent.Create(new
            {
                input = "fixture input",
                model = "omni-moderation-latest",
            }),
        };
        request.Headers.Authorization = new(
            "Bearer", "mock-openai-moderation-key");
        request.Headers.Add("X-Provider-Scenario", scenario);

        using var response = await client.SendAsync(request);

        Assert.Equal(expectedStatus, response.StatusCode);
        if (scenario.Contains("malformed", StringComparison.Ordinal))
            Assert.Contains("{not-json", await response.Content.ReadAsStringAsync());
        if (scenario.Contains("oversized", StringComparison.Ordinal))
            Assert.True((await response.Content.ReadAsByteArrayAsync()).Length > 16 * 1024);
    }
}
