extern alias providerMock;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ScalaAPI.Provider.Mock.Tests;

public sealed class MockResponsesHttpContractTests :
    IClassFixture<WebApplicationFactory<providerMock::Program>>
{
    private readonly WebApplicationFactory<providerMock::Program> factory;

    public MockResponsesHttpContractTests(
        WebApplicationFactory<providerMock::Program> factory) => this.factory = factory;

    [Fact]
    public async Task CancelReturnsIdempotentCancelledResponseAndUpdatesRetrieval()
    {
        using var client = factory.CreateClient();
        using var create = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "gpt-4o",
            input = "cancel smoke",
            stream = false,
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var responseId = created.RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(responseId));

        using var cancel = await client.PostAsJsonAsync($"/v1/responses/{responseId}/cancel", new { });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        using var canceled = JsonDocument.Parse(await cancel.Content.ReadAsStringAsync());
        Assert.Equal(responseId, canceled.RootElement.GetProperty("id").GetString());
        Assert.Equal("cancelled", canceled.RootElement.GetProperty("status").GetString());

        using var replay = await client.PostAsJsonAsync($"/v1/responses/{responseId}/cancel", new { });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayDocument = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal("cancelled", replayDocument.RootElement.GetProperty("status").GetString());

        using var get = await client.GetAsync($"/v1/responses/{responseId}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        using var retrieved = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.Equal("cancelled", retrieved.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CancelUnknownResponseReturnsNotFound()
    {
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/v1/responses/resp_missing/cancel", new { });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("response_not_found", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task InputItemsReturnsStableListForStoredResponseAndRejectsUnknownId()
    {
        using var client = factory.CreateClient();
        using var create = await client.PostAsJsonAsync("/v1/responses", new
        {
            model = "gpt-4o",
            input = "input items smoke",
            stream = false,
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var responseId = created.RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(responseId));

        using var first = await client.GetAsync($"/v1/responses/{responseId}/input_items");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadAsStringAsync();
        using var firstDocument = JsonDocument.Parse(firstBody);
        var firstRoot = firstDocument.RootElement;
        Assert.Equal("list", firstRoot.GetProperty("object").GetString());
        Assert.False(firstRoot.GetProperty("has_more").GetBoolean());
        Assert.Equal(responseId + "_input_0",
            firstRoot.GetProperty("data")[0].GetProperty("id").GetString());
        Assert.Equal("input items smoke",
            firstRoot.GetProperty("data")[0].GetProperty("content")[0].GetProperty("text").GetString());

        using var replay = await client.GetAsync($"/v1/responses/{responseId}/input_items");
        Assert.Equal(firstBody, await replay.Content.ReadAsStringAsync());

        using var missing = await client.GetAsync("/v1/responses/resp_missing/input_items");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var delete = await client.DeleteAsync($"/v1/responses/{responseId}");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        using var deletedInputItems = await client.GetAsync($"/v1/responses/{responseId}/input_items");
        Assert.Equal(HttpStatusCode.NotFound, deletedInputItems.StatusCode);
    }

    [Fact]
    public async Task CompactReturnsCompactionItemForJsonAndSse()
    {
        using var client = factory.CreateClient();
        var payload = new
        {
            model = "gpt-4o",
            input = new[]
            {
                new { role = "user", content = new[] { new { type = "input_text", text = "compact smoke" } } },
            },
            stream = false,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses/compact")
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add("X-Provider-Request-Id", "compact-json-1");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("response", root.GetProperty("object").GetString());
        Assert.Equal("completed", root.GetProperty("status").GetString());
        Assert.Equal("compaction", root.GetProperty("output")[0].GetProperty("type").GetString());
        Assert.Contains("compact smoke", root.GetProperty("output")[0].GetProperty("encrypted_content").GetString());
        var usage = root.GetProperty("usage");
        Assert.Equal(usage.GetProperty("input_tokens").GetInt32() + usage.GetProperty("output_tokens").GetInt32(),
            usage.GetProperty("total_tokens").GetInt32());

        var streamPayload = new
        {
            model = "gpt-4o",
            input = new[]
            {
                new { role = "user", content = new[] { new { type = "input_text", text = "compact smoke" } } },
            },
            stream = true,
        };
        using var streamRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/responses/compact")
        {
            Content = JsonContent.Create(streamPayload),
        };
        streamRequest.Headers.Add("X-Provider-Request-Id", "compact-stream-1");
        using var streamResponse = await client.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
        Assert.StartsWith("text/event-stream", streamResponse.Content.Headers.ContentType?.MediaType);
        var streamBody = await streamResponse.Content.ReadAsStringAsync();
        Assert.Contains("event: response.output_item.done", streamBody);
        Assert.Contains("\"type\":\"compaction\"", streamBody);
        Assert.Contains("event: response.completed", streamBody);
    }

    [Fact]
    public async Task CompactRejectsInvalidInputAndPropagatesProviderScenarios()
    {
        using var client = factory.CreateClient();
        using var invalid = await client.PostAsJsonAsync("/v1/responses/compact", new
        {
            model = "gpt-4o",
            input = "must be an array",
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var rateLimited = await client.PostAsJsonAsync("/v1/responses/compact?scenario=429", new
        {
            model = "gpt-4o",
            input = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.TooManyRequests, rateLimited.StatusCode);

        using var malformed = await client.PostAsJsonAsync("/v1/responses/compact?scenario=malformed", new
        {
            model = "gpt-4o",
            input = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.OK, malformed.StatusCode);
        Assert.Equal("{not-json", await malformed.Content.ReadAsStringAsync());
    }
}
