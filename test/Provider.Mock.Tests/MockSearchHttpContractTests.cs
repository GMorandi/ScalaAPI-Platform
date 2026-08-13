extern alias providerMock;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ScalaAPI.Provider.Mock.Tests;

public sealed class MockSearchHttpContractTests :
    IClassFixture<WebApplicationFactory<providerMock::Program>>
{
    private readonly WebApplicationFactory<providerMock::Program> factory;

    public MockSearchHttpContractTests(
        WebApplicationFactory<providerMock::Program> factory) => this.factory = factory;

    [Fact]
    public async Task SuccessReturnsSearchResultsWithQueryCount()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "mock-openai-key");
        using var response = await client.PostAsJsonAsync("/alpha/search", new
        {
            query = "test search",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("id", out var id));
        Assert.StartsWith("search-", id.GetString());
        Assert.Equal("test search", root.GetProperty("query").GetString());
        Assert.Equal(1, root.GetProperty("query_count").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        var results = root.GetProperty("results");
        Assert.Equal(JsonValueKind.Array, results.ValueKind);
        Assert.Equal(2, results.GetArrayLength());
        var first = results[0];
        Assert.Equal("web", first.GetProperty("source").GetString());
        Assert.False(string.IsNullOrEmpty(first.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrEmpty(first.GetProperty("url").GetString()));
        Assert.False(string.IsNullOrEmpty(first.GetProperty("snippet").GetString()));
    }

    [Fact]
    public async Task EmptyScenarioReturnsEmptyResultsArray()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "mock-openai-key");
        var request = new HttpRequestMessage(HttpMethod.Post, "/alpha/search")
        {
            Content = JsonContent.Create(new { query = "test" }),
        };
        request.Headers.Add("X-Mock-Scenario", "empty");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public async Task RateLimitedScenarioReturns429WithRetryAfter()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "mock-openai-key");
        var request = new HttpRequestMessage(HttpMethod.Post, "/alpha/search")
        {
            Content = JsonContent.Create(new { query = "test" }),
        };
        request.Headers.Add("X-Mock-Scenario", "rate_limited");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Retry-After", out var values));
        Assert.Contains("5", values);
    }

    [Fact]
    public async Task ServerErrorScenarioReturns500()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "mock-openai-key");
        var request = new HttpRequestMessage(HttpMethod.Post, "/alpha/search")
        {
            Content = JsonContent.Create(new { query = "test" }),
        };
        request.Headers.Add("X-Mock-Scenario", "server_error");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task PartialScenarioReturnsTruncatedTrue()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "mock-openai-key");
        var request = new HttpRequestMessage(HttpMethod.Post, "/alpha/search")
        {
            Content = JsonContent.Create(new { query = "test" }),
        };
        request.Headers.Add("X-Mock-Scenario", "partial");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task MissingAuthReturns401()
    {
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/alpha/search", new { query = "test" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MissingQueryReturns400()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "mock-openai-key");
        using var response = await client.PostAsJsonAsync("/alpha/search", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task QueryExceedingLimitReturns400()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "mock-openai-key");
        using var response = await client.PostAsJsonAsync("/alpha/search", new
        {
            query = new string('x', 1001),
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task InvalidDomainTypeReturns400()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "mock-openai-key");
        using var response = await client.PostAsync("/alpha/search",
            new StringContent("{\"query\":\"test\",\"domain\":123}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
