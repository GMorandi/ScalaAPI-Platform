using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ScalaAPI.Admin.Payments;
using Xunit;

namespace ScalaAPI.Admin.Tests;

public sealed class PaymentProviderClientTests
{
    [Fact]
    public async Task CreatesCheckoutWithBearerAndBoundedNativePayload()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            capturedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""
                {"provider_order_id":"mock_po_123","checkout_url":"https://pay.example/checkout/mock_po_123"}
                """);
        });
        var client = CreateClient(handler, "https://pay.example/v1/checkout", false);

        var result = await client.CreateCheckoutAsync(
            new PaymentCheckoutRequest(42, 12.34m, "USD", "Credit"));

        Assert.Equal("mock_po_123", result.ProviderOrderId);
        Assert.Equal("https://pay.example/checkout/mock_po_123", result.CheckoutUrl);
        Assert.Equal("Bearer mock-key", captured!.Headers.Authorization?.ToString());
        using var body = JsonDocument.Parse(capturedBody!);
        Assert.Equal("scalaapi-order:42", body.RootElement.GetProperty("merchant_reference").GetString());
        Assert.Equal(12.34m, body.RootElement.GetProperty("amount").GetDecimal());
        Assert.Equal("USD", body.RootElement.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task FailsClosedForInsecureEndpointAndMalformedProviderResponse()
    {
        var insecure = CreateClient(new StubHandler(_ => JsonResponse("{}")),
            "http://pay.example/v1/checkout", false);
        var httpsError = await Assert.ThrowsAsync<PaymentProviderException>(() =>
            insecure.CreateCheckoutAsync(new PaymentCheckoutRequest(1, 1m, "USD", null)));
        Assert.Equal("payment_provider_https_required", httpsError.Code);

        var malformed = CreateClient(new StubHandler(_ => JsonResponse("{not-json")),
            "https://pay.example/v1/checkout", false);
        var responseError = await Assert.ThrowsAsync<PaymentProviderException>(() =>
            malformed.CreateCheckoutAsync(new PaymentCheckoutRequest(1, 1m, "USD", null)));
        Assert.Equal("payment_provider_response_invalid", responseError.Code);
    }

    private static MockPaymentProviderClient CreateClient(
        HttpMessageHandler handler, string endpoint, bool allowInsecure)
    {
        var http = new HttpClient(handler);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Payments:Providers:mock:Endpoint"] = endpoint,
                ["Payments:Providers:mock:ApiKey"] = "mock-key",
                ["Payments:AllowInsecureProviderEndpoints"] = allowInsecure.ToString(),
            }).Build();
        return new MockPaymentProviderClient(http, configuration,
            NullLogger<MockPaymentProviderClient>.Instance);
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
