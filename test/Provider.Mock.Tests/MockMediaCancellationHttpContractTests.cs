extern alias providerMock;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ScalaAPI.Provider.Mock.Tests;

public sealed class MockMediaCancellationHttpContractTests :
    IClassFixture<WebApplicationFactory<providerMock::Program>>
{
    private readonly WebApplicationFactory<providerMock::Program> factory;

    public MockMediaCancellationHttpContractTests(
        WebApplicationFactory<providerMock::Program> factory) => this.factory = factory;

    [Fact]
    public async Task BatchCancellationIsIdempotentAndVisibleToPolling()
    {
        using var client = factory.CreateClient();
        using var create = await client.PostAsJsonAsync("/v1/images/batches", new { model = "mock-image-1" });
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var batchId = created.RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(batchId));

        using var cancel = await client.PostAsync($"/v1/images/batches/{batchId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        using var canceled = JsonDocument.Parse(await cancel.Content.ReadAsStringAsync());
        Assert.Equal("canceled", canceled.RootElement.GetProperty("status").GetString());

        using var replay = await client.PostAsync($"/v1/images/batches/{batchId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        using var poll = await client.GetAsync($"/v1/images/batches/{batchId}");
        Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
        using var polled = JsonDocument.Parse(await poll.Content.ReadAsStringAsync());
        Assert.Equal("canceled", polled.RootElement.GetProperty("status").GetString());
        Assert.Empty(polled.RootElement.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task UnknownBatchCancellationReturnsNotFound()
    {
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/v1/images/batches/missing/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
