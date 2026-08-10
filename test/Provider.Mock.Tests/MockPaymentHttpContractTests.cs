extern alias providerMock;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ScalaAPI.Provider.Mock.Tests;

public sealed class MockPaymentHttpContractTests :
    IClassFixture<WebApplicationFactory<providerMock::Program>>
{
    private readonly WebApplicationFactory<providerMock::Program> factory;

    public MockPaymentHttpContractTests(
        WebApplicationFactory<providerMock::Program> factory) => this.factory = factory;

    [Fact]
    public async Task CheckoutIsBearerProtectedAndDeterministicPerMerchantReference()
    {
        using var client = factory.CreateClient();
        using var unauthorized = await client.PostAsJsonAsync("/v1/payments/checkout", new
        {
            merchant_reference = "scalaapi-order:1",
            amount = 12.34m,
            currency = "USD",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "mock-payment-key");
        var first = await client.PostAsJsonAsync("/v1/payments/checkout", new
        {
            merchant_reference = "scalaapi-order:1",
            amount = 12.34m,
            currency = "USD",
            description = "Credit",
        });
        var second = await client.PostAsJsonAsync("/v1/payments/checkout", new
        {
            merchant_reference = "scalaapi-order:1",
            amount = 12.34m,
            currency = "USD",
        });
        var firstText = await first.Content.ReadAsStringAsync();
        Assert.True(first.StatusCode == HttpStatusCode.OK, firstText);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<CheckoutResponse>();
        var secondBody = await second.Content.ReadFromJsonAsync<CheckoutResponse>();
        Assert.Equal(firstBody?.ProviderOrderId, secondBody?.ProviderOrderId);
        Assert.StartsWith("mock_po_", firstBody?.ProviderOrderId);
        Assert.StartsWith("http://localhost:8081/checkout/", firstBody?.CheckoutUrl);
    }

    private sealed record CheckoutResponse(
        [property: JsonPropertyName("provider_order_id")] string ProviderOrderId,
        [property: JsonPropertyName("checkout_url")] string CheckoutUrl);
}
