using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using ScalaAPI.Grains.Interfaces;
using ScalaAPI.Host.Services;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class QuotaClientContractTests
{
    private static AccountCredentials TestCreds(string baseUrl) => new(
        Id: 1, Platform: "openai", Type: "api_key", BaseUrl: baseUrl,
        AuthHeaders: new Dictionary<string, string> { ["Authorization"] = "Bearer test" },
        ProxyUrl: null, ProxyUsername: null, ProxyPassword: null,
        TlsFingerprint: false, TlsFingerprintProfileId: null,
        ModelMapping: new Dictionary<string, string>());

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    [InlineData("xai")]
    public async Task SuccessReturnsActiveTier(string platform)
    {
        using var handler = new MockHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"object\":\"list\",\"data\":[]}")
            }));
        using var http = new HttpClient(handler);
        var client = CreateClient(platform, http);
        var creds = TestCreds(handler.BaseAddress!.ToString());

        var result = await client.GetQuotaAsync(creds, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("active", result.Tier);
        Assert.True(result.ExpiresAt > DateTime.UtcNow);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    [InlineData("xai")]
    public async Task RateLimitReturnsNull(string platform)
    {
        using var handler = new MockHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"error\":\"rate limit\"}")
            }));
        using var http = new HttpClient(handler);
        var client = CreateClient(platform, http);
        var creds = TestCreds(handler.BaseAddress!.ToString());

        var result = await client.GetQuotaAsync(creds, CancellationToken.None);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    [InlineData("xai")]
    public async Task TimeoutReturnsNull(string platform)
    {
        using var handler = new MockHandler(async (req, ct) =>
        {
            await Task.Delay(5_000, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(100) };
        var client = CreateClient(platform, http);
        var creds = TestCreds(handler.BaseAddress!.ToString());

        var result = await client.GetQuotaAsync(creds, CancellationToken.None);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    [InlineData("xai")]
    public async Task ServerErrorReturnsNull(string platform)
    {
        using var handler = new MockHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        using var http = new HttpClient(handler);
        var client = CreateClient(platform, http);
        var creds = TestCreds(handler.BaseAddress!.ToString());

        var result = await client.GetQuotaAsync(creds, CancellationToken.None);

        Assert.Null(result);
    }

    private static IProviderQuotaClient CreateClient(string platform, HttpClient http) =>
        platform switch
        {
            "openai" => new OpenAIQuotaClient(http, NullLogger<OpenAIQuotaClient>.Instance),
            "anthropic" => new AnthropicQuotaClient(http, NullLogger<AnthropicQuotaClient>.Instance),
            "gemini" => new GeminiQuotaClient(http, NullLogger<GeminiQuotaClient>.Instance),
            "xai" => new XaiQuotaClient(http, NullLogger<XaiQuotaClient>.Instance),
            _ => throw new ArgumentException($"Unknown platform: {platform}")
        };

    sealed class MockHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        public Uri? BaseAddress { get; } = new Uri("http://localhost:19998");

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = handler(request, cancellationToken);
            return response;
        }
    }
}
