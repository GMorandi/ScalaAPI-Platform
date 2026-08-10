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
}
