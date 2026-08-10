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

    [Fact]
    public async Task StripeCheckoutUsesBasicAuthAndMinorUnitFormFields()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            capturedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""
                {"id":"cs_test_123","url":"https://checkout.stripe.com/c/pay/cs_test_123","payment_intent":"pi_test_123"}
                """);
        });
        var client = CreateStripeClient(handler, "https://api.stripe.test/v1/checkout/sessions");

        var result = await client.CreateCheckoutAsync(
            new PaymentCheckoutRequest(42, 12.34m, "USD", "Credit"));

        Assert.Equal("cs_test_123", result.ProviderOrderId);
        Assert.Equal("pi_test_123", result.ProviderPaymentId);
        Assert.Equal("https://checkout.stripe.com/c/pay/cs_test_123", result.CheckoutUrl);
        Assert.Equal("Basic " + Convert.ToBase64String(
            Encoding.UTF8.GetBytes("sk_test_secret:")),
            captured!.Headers.Authorization?.ToString());
        var form = await new FormUrlEncodedContent(
            ParseForm(capturedBody!)).ReadAsStringAsync();
        Assert.Contains("line_items%5B0%5D%5Bprice_data%5D%5Bunit_amount%5D=1234", form);
        Assert.Contains("line_items%5B0%5D%5Bprice_data%5D%5Bcurrency%5D=usd", form);
        Assert.Contains("metadata%5Border_id%5D=42", form);
        Assert.Contains("client_reference_id=scalaapi-order%3A42", form);
    }

    [Fact]
    public async Task StripeCheckoutRejectsMissingSecretAndInsecureRedirect()
    {
        var missingSecret = CreateStripeClient(new StubHandler(_ => JsonResponse("{}")),
            "https://api.stripe.test/v1/checkout/sessions", secret: "");
        var missingError = await Assert.ThrowsAsync<PaymentProviderException>(() =>
            missingSecret.CreateCheckoutAsync(new PaymentCheckoutRequest(1, 1m, "USD", null)));
        Assert.Equal("payment_provider_not_configured", missingError.Code);

        var insecureRedirect = CreateStripeClient(new StubHandler(_ => JsonResponse("{}")),
            "https://api.stripe.test/v1/checkout/sessions",
            successUrl: "http://localhost/success");
        var redirectError = await Assert.ThrowsAsync<PaymentProviderException>(() =>
            insecureRedirect.CreateCheckoutAsync(new PaymentCheckoutRequest(1, 1m, "USD", null)));
        Assert.Equal("payment_checkout_redirect_invalid", redirectError.Code);
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

    private static StripePaymentProviderClient CreateStripeClient(
        HttpMessageHandler handler,
        string endpoint,
        string secret = "sk_test_secret",
        string successUrl = "https://scalaapi.example/billing/success")
    {
        var http = new HttpClient(handler);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Payments:Providers:stripe:Endpoint"] = endpoint,
                ["Payments:Providers:stripe:SecretKey"] = secret,
                ["Payments:Providers:stripe:SuccessUrl"] = successUrl,
                ["Payments:Providers:stripe:CancelUrl"] = "https://scalaapi.example/billing/cancel",
                ["Payments:Providers:stripe:ProductName"] = "ScalaAPI credit",
                ["Payments:AllowInsecureProviderEndpoints"] = "false",
            }).Build();
        return new StripePaymentProviderClient(http, configuration,
            NullLogger<StripePaymentProviderClient>.Instance);
    }

    private static Dictionary<string, string> ParseForm(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0].Replace("+", " ")),
                pair => Uri.UnescapeDataString(pair.Length > 1
                    ? pair[1].Replace("+", " ") : ""));

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
