using System.Net.Http.Headers;
using System.Text.Json;

namespace ScalaAPI.Admin.Payments;

public sealed record PaymentCheckoutRequest(
    long OrderId, decimal Amount, string Currency, string? Description);

public sealed record PaymentCheckoutResult(
    string ProviderOrderId, string CheckoutUrl);

public sealed class PaymentProviderException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

// The first native payment adapter is deliberately small and provider-owned.
// It validates the boundary before the order can expose a checkout URL.
public sealed class MockPaymentProviderClient(
    HttpClient http,
    IConfiguration configuration,
    ILogger<MockPaymentProviderClient> logger)
{
    public async Task<PaymentCheckoutResult> CreateCheckoutAsync(
        PaymentCheckoutRequest request, CancellationToken ct = default)
    {
        var endpointValue = configuration["Payments:Providers:mock:Endpoint"]?.Trim();
        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
            throw new PaymentProviderException("payment_provider_not_configured");

        var allowInsecure = bool.TryParse(
            configuration["Payments:AllowInsecureProviderEndpoints"], out var configured)
            && configured;
        if (endpoint.Scheme == Uri.UriSchemeHttp && !allowInsecure)
            throw new PaymentProviderException("payment_provider_https_required");
        if (request.OrderId <= 0 || request.Amount <= 0m || request.Amount > 1_000_000m
            || decimal.Round(request.Amount, 2) != request.Amount
            || request.Currency.Length != 3
            || request.Currency.Any(ch => ch is < 'A' or > 'Z'))
            throw new PaymentProviderException("payment_checkout_request_invalid");

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                merchant_reference = $"scalaapi-order:{request.OrderId}",
                amount = request.Amount,
                currency = request.Currency,
                description = request.Description ?? "",
            }),
        };
        var apiKey = configuration["Payments:Providers:mock:ApiKey"]?.Trim();
        if (!string.IsNullOrWhiteSpace(apiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PaymentProviderException("payment_provider_timeout");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Payment provider checkout request failed");
            throw new PaymentProviderException("payment_provider_unavailable");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new PaymentProviderException("payment_provider_rejected");
            if (response.Content.Headers.ContentLength is > 32 * 1024)
                throw new PaymentProviderException("payment_provider_response_too_large");

            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.Length > 32 * 1024)
                throw new PaymentProviderException("payment_provider_response_too_large");
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                var providerOrderId = ReadRequiredString(root, "provider_order_id", 128);
                var checkoutUrl = ReadRequiredString(root, "checkout_url", 2048);
                if (!Uri.TryCreate(checkoutUrl, UriKind.Absolute, out var checkout)
                    || checkout.Scheme is not ("http" or "https"))
                    throw new PaymentProviderException("payment_provider_response_invalid");
                if (checkout.Scheme == Uri.UriSchemeHttp && !allowInsecure)
                    throw new PaymentProviderException("payment_provider_response_https_required");
                return new PaymentCheckoutResult(providerOrderId, checkoutUrl);
            }
            catch (JsonException)
            {
                throw new PaymentProviderException("payment_provider_response_invalid");
            }
        }
    }

    private static string ReadRequiredString(JsonElement root, string name, int maxLength)
    {
        if (!root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
            throw new PaymentProviderException("payment_provider_response_invalid");
        var text = value.GetString()?.Trim() ?? "";
        if (text.Length < 1 || text.Length > maxLength)
            throw new PaymentProviderException("payment_provider_response_invalid");
        return text;
    }
}
