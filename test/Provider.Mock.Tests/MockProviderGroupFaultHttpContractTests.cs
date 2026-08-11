extern alias providerMock;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ScalaAPI.Provider.Mock.Tests;

public sealed class MockProviderGroupFaultHttpContractTests :
    IClassFixture<WebApplicationFactory<providerMock::Program>>
{
    private readonly WebApplicationFactory<providerMock::Program> factory;

    public MockProviderGroupFaultHttpContractTests(
        WebApplicationFactory<providerMock::Program> factory) => this.factory = factory;

    [Theory]
    [InlineData("429", HttpStatusCode.TooManyRequests)]
    [InlineData("500", HttpStatusCode.InternalServerError)]
    [InlineData("malformed", HttpStatusCode.OK)]
    public async Task AnthropicFaultHeaderControlsJsonContract(
        string scenario, HttpStatusCode expectedStatus)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(new
            {
                model = "claude-3-5-sonnet",
                max_tokens = 16,
                messages = new[] { new { role = "user", content = "fault" } },
            }),
        };
        request.Headers.Add("X-Provider-Scenario", scenario);

        using var response = await client.SendAsync(request);
        Assert.Equal(expectedStatus, response.StatusCode);
        if (scenario == "malformed")
            Assert.Contains("{not-json", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AnthropicStreamingMalformedAndUsageBeforeEofAreDeterministic()
    {
        using var client = factory.CreateClient();
        using var malformed = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(new
            {
                model = "claude-3-5-sonnet",
                max_tokens = 16,
                stream = true,
                messages = new[] { new { role = "user", content = "fault" } },
            }),
        };
        malformed.Headers.Add("X-Provider-Scenario", "malformed");
        using var malformedResponse = await client.SendAsync(
            malformed, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, malformedResponse.StatusCode);
        Assert.Equal("text/event-stream",
            malformedResponse.Content.Headers.ContentType?.MediaType);
        Assert.Contains("{not-json", await malformedResponse.Content.ReadAsStringAsync());

        using var usage = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(new
            {
                model = "claude-3-5-sonnet",
                max_tokens = 16,
                stream = true,
                messages = new[] { new { role = "user", content = "fault" } },
            }),
        };
        usage.Headers.Add("X-Provider-Scenario", "disconnect_after_usage");
        using var usageResponse = await client.SendAsync(
            usage, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, usageResponse.StatusCode);
        var usageBody = await usageResponse.Content.ReadAsStringAsync();
        Assert.Contains("event: message_delta", usageBody);
        Assert.Contains("output_tokens", usageBody);
        Assert.DoesNotContain("message_stop", usageBody);
    }

    [Theory]
    [InlineData("429", HttpStatusCode.TooManyRequests)]
    [InlineData("500", HttpStatusCode.InternalServerError)]
    [InlineData("malformed", HttpStatusCode.OK)]
    public async Task GeminiFaultHeaderControlsJsonContract(
        string scenario, HttpStatusCode expectedStatus)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/v1beta/models/gemini-2.0-flash:generateContent")
        {
            Content = JsonContent.Create(new
            {
                contents = new[] { new { role = "user", parts = new[] { new { text = "fault" } } } },
            }),
        };
        request.Headers.Add("X-Provider-Scenario", scenario);

        using var response = await client.SendAsync(request);
        Assert.Equal(expectedStatus, response.StatusCode);
        if (scenario == "malformed")
            Assert.Contains("{not-json", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GeminiStreamingFaultsPreserveSseAndUsageEvidence()
    {
        using var client = factory.CreateClient();
        using var malformed = new HttpRequestMessage(
            HttpMethod.Post, "/v1beta/models/gemini-2.0-flash:streamGenerateContent?alt=sse")
        {
            Content = JsonContent.Create(new
            {
                contents = new[] { new { role = "user", parts = new[] { new { text = "fault" } } } },
            }),
        };
        malformed.Headers.Add("X-Provider-Scenario", "malformed");
        using var malformedResponse = await client.SendAsync(
            malformed, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, malformedResponse.StatusCode);
        Assert.Equal("text/event-stream",
            malformedResponse.Content.Headers.ContentType?.MediaType);
        Assert.Contains("data: {not-json", await malformedResponse.Content.ReadAsStringAsync());

        using var usage = new HttpRequestMessage(
            HttpMethod.Post, "/v1beta/models/gemini-2.0-flash:streamGenerateContent?alt=sse")
        {
            Content = JsonContent.Create(new
            {
                contents = new[] { new { role = "user", parts = new[] { new { text = "fault" } } } },
            }),
        };
        usage.Headers.Add("X-Provider-Scenario", "disconnect_after_usage");
        using var usageResponse = await client.SendAsync(
            usage, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, usageResponse.StatusCode);
        var usageBody = await usageResponse.Content.ReadAsStringAsync();
        var usageLine = usageBody.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("data: ", StringComparison.Ordinal));
        using var usageJson = JsonDocument.Parse(usageLine[6..]);
        Assert.True(usageJson.RootElement.GetProperty("usageMetadata")
            .GetProperty("totalTokenCount").GetInt32() > 0);
    }
}
