extern alias providerMock;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ScalaAPI.Provider.Mock.Tests;

public sealed class MockProviderNativeAuthenticationTests :
    IClassFixture<WebApplicationFactory<providerMock::Program>>
{
    private readonly WebApplicationFactory<providerMock::Program> factory;

    public MockProviderNativeAuthenticationTests(
        WebApplicationFactory<providerMock::Program> factory) => this.factory = factory;

    [Theory]
    [InlineData(false, "2023-06-01", HttpStatusCode.Unauthorized)]
    [InlineData(true, "2022-01-01", HttpStatusCode.BadRequest)]
    public async Task AnthropicRejectsMissingKeyOrWrongVersionWithoutEchoingSecrets(
        bool includeKey, string version, HttpStatusCode expected)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(new
            {
                model = "claude-3-5-sonnet",
                max_tokens = 8,
                messages = new[] { new { role = "user", content = "auth" } },
            }),
        };
        if (includeKey) request.Headers.Add("x-api-key", "scalaapi-mock-key");
        request.Headers.Add("anthropic-version", version);

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(expected, response.StatusCode);
        Assert.DoesNotContain("scalaapi-mock-key", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeminiRejectsLiteralSemanticHeaderAndDoesNotEchoIt()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/v1beta/models/gemini-2.0-flash:generateContent")
        {
            Content = JsonContent.Create(new
            {
                contents = new[] { new { parts = new[] { new { text = "auth" } } } },
            }),
        };
        request.Headers.TryAddWithoutValidation("api_key", "leaked-semantic-secret");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("UNAUTHENTICATED", body, StringComparison.Ordinal);
        Assert.DoesNotContain("leaked-semantic-secret", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("anthropic", "/v1/messages")]
    [InlineData("gemini", "/v1beta/models/gemini-2.0-flash:generateContent")]
    public async Task NativeProvidersAcceptRotatedOAuthBearer(string provider, string path)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = provider == "anthropic"
                ? JsonContent.Create(new
                {
                    model = "claude-3-5-sonnet",
                    max_tokens = 8,
                    messages = new[] { new { role = "user", content = "oauth" } },
                })
                : JsonContent.Create(new
                {
                    contents = new[] { new { parts = new[] { new { text = "oauth" } } } },
                }),
        };
        request.Headers.Authorization = new("Bearer", "mock-access-v2");
        if (provider == "anthropic")
            request.Headers.Add("anthropic-version", "2023-06-01");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
