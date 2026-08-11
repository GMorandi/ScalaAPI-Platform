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
        AddAnthropicAuth(request);
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
        AddAnthropicAuth(malformed);
        malformed.Headers.Add("X-Provider-Scenario", "malformed");
        using var malformedResponse = await client.SendAsync(
            malformed, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, malformedResponse.StatusCode);
        Assert.Equal("text/event-stream",
            malformedResponse.Content.Headers.ContentType?.MediaType);
        Assert.Contains("{not-json", await malformedResponse.Content.ReadAsStringAsync());

        using var wrongMediaType = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(new
            {
                model = "claude-3-5-sonnet",
                max_tokens = 16,
                stream = true,
                messages = new[] { new { role = "user", content = "fault" } },
            }),
        };
        AddAnthropicAuth(wrongMediaType);
        wrongMediaType.Headers.Add("X-Provider-Scenario", "invalid_content_type");
        using var wrongMediaTypeResponse = await client.SendAsync(
            wrongMediaType, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, wrongMediaTypeResponse.StatusCode);
        Assert.Equal("application/json",
            wrongMediaTypeResponse.Content.Headers.ContentType?.MediaType);

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
        AddAnthropicAuth(usage);
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
        AddGeminiAuth(request);
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
        AddGeminiAuth(malformed);
        malformed.Headers.Add("X-Provider-Scenario", "malformed");
        using var malformedResponse = await client.SendAsync(
            malformed, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, malformedResponse.StatusCode);
        Assert.Equal("text/event-stream",
            malformedResponse.Content.Headers.ContentType?.MediaType);
        Assert.Contains("data: {not-json", await malformedResponse.Content.ReadAsStringAsync());

        using var wrongMediaType = new HttpRequestMessage(
            HttpMethod.Post, "/v1beta/models/gemini-2.0-flash:streamGenerateContent?alt=sse")
        {
            Content = JsonContent.Create(new
            {
                contents = new[] { new { role = "user", parts = new[] { new { text = "fault" } } } },
            }),
        };
        AddGeminiAuth(wrongMediaType);
        wrongMediaType.Headers.Add("X-Provider-Scenario", "invalid_content_type");
        using var wrongMediaTypeResponse = await client.SendAsync(
            wrongMediaType, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, wrongMediaTypeResponse.StatusCode);
        Assert.Equal("application/json",
            wrongMediaTypeResponse.Content.Headers.ContentType?.MediaType);

        using var usage = new HttpRequestMessage(
            HttpMethod.Post, "/v1beta/models/gemini-2.0-flash:streamGenerateContent?alt=sse")
        {
            Content = JsonContent.Create(new
            {
                contents = new[] { new { role = "user", parts = new[] { new { text = "fault" } } } },
            }),
        };
        AddGeminiAuth(usage);
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

    [Theory]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    public async Task NativeStreamingClientDisconnectCancelsProviderRequest(string provider)
    {
        using var client = factory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        var requestId = $"native-cancel-{provider}-{Guid.NewGuid():N}";
        var path = provider == "anthropic"
            ? "/v1/messages"
            : "/v1beta/models/gemini-2.0-flash:streamGenerateContent?alt=sse";
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = provider == "anthropic"
                ? JsonContent.Create(new
                {
                    model = "claude-3-5-sonnet",
                    max_tokens = 16,
                    stream = true,
                    messages = new[] { new { role = "user", content = "cancel" } },
                })
                : JsonContent.Create(new
                {
                    contents = new[]
                    {
                        new { role = "user", parts = new[] { new { text = "cancel" } } }
                    },
                }),
        };
        if (provider == "anthropic") AddAnthropicAuth(request);
        else AddGeminiAuth(request);
        request.Headers.Add("X-Provider-Scenario", "client_disconnect");
        request.Headers.Add("X-Provider-Request-Id", requestId);

        var response = await client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using (var stream = await response.Content.ReadAsStreamAsync())
        {
            var buffer = new byte[512];
            Assert.True(await stream.ReadAsync(buffer) > 0);
        }
        response.Dispose();

        var observed = false;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var state = await client.GetFromJsonAsync<JsonElement>(
                $"/__test/cancellations/{requestId}");
            observed = state.GetProperty(provider).GetInt32() == 1;
            if (observed) break;
            await Task.Delay(50);
        }
        Assert.True(observed, $"{provider} request cancellation was not observed");
    }

    private static void AddAnthropicAuth(HttpRequestMessage request)
    {
        request.Headers.Add("x-api-key", "scalaapi-mock-key");
        request.Headers.Add("anthropic-version", "2023-06-01");
    }

    private static void AddGeminiAuth(HttpRequestMessage request) =>
        request.Headers.Add("x-goog-api-key", "scalaapi-mock-key");
}
