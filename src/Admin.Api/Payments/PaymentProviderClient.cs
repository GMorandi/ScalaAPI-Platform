using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ScalaAPI.Admin.Payments;

public sealed record PaymentCheckoutRequest(
    long OrderId, decimal Amount, string Currency, string? Description);

public sealed record PaymentCheckoutResult(
    string ProviderOrderId, string CheckoutUrl, string? ProviderPaymentId = null);

public interface IPaymentProviderClient
{
    string Provider { get; }

    Task<PaymentCheckoutResult> CreateCheckoutAsync(
        PaymentCheckoutRequest request, CancellationToken ct = default);
}

public sealed class PaymentProviderException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

// Provider-owned adapters validate their boundary before an order can expose a
// checkout URL. The mock adapter remains available for deterministic local runs.
public sealed class MockPaymentProviderClient(
    HttpClient http,
    IConfiguration configuration,
    ILogger<MockPaymentProviderClient> logger) : IPaymentProviderClient
{
    public string Provider => "mock";

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

public sealed class StripePaymentProviderClient(
    HttpClient http,
    IConfiguration configuration,
    ILogger<StripePaymentProviderClient> logger) : IPaymentProviderClient
{
    public string Provider => "stripe";

    public async Task<PaymentCheckoutResult> CreateCheckoutAsync(
        PaymentCheckoutRequest request, CancellationToken ct = default)
    {
        var endpointValue = configuration["Payments:Providers:stripe:Endpoint"]?.Trim()
            ?? "https://api.stripe.com/v1/checkout/sessions";
        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
            throw new PaymentProviderException("payment_provider_not_configured");

        var allowInsecure = bool.TryParse(
            configuration["Payments:AllowInsecureProviderEndpoints"], out var configured)
            && configured;
        if (endpoint.Scheme == Uri.UriSchemeHttp && !allowInsecure)
            throw new PaymentProviderException("payment_provider_https_required");

        var secret = configuration["Payments:Providers:stripe:SecretKey"]?.Trim();
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 512)
            throw new PaymentProviderException("payment_provider_not_configured");
        ValidateRequest(request);

        var successUrl = ReadAbsoluteUrl("Payments:Providers:stripe:SuccessUrl",
            "https://example.invalid/payment/success", allowInsecure);
        var cancelUrl = ReadAbsoluteUrl("Payments:Providers:stripe:CancelUrl",
            "https://example.invalid/payment/cancel", allowInsecure);
        var productName = (configuration["Payments:Providers:stripe:ProductName"]
            ?? "ScalaAPI credit").Trim();
        if (productName.Length is < 1 or > 128)
            throw new PaymentProviderException("payment_checkout_request_invalid");

        var minorAmount = checked((long)(request.Amount * 100m));
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["mode"] = "payment",
                ["line_items[0][price_data][currency]"] = request.Currency.ToLowerInvariant(),
                ["line_items[0][price_data][unit_amount]"] = minorAmount.ToString(CultureInfo.InvariantCulture),
                ["line_items[0][price_data][product_data][name]"] = productName,
                ["line_items[0][quantity]"] = "1",
                ["success_url"] = successUrl,
                ["cancel_url"] = cancelUrl,
                ["metadata[order_id]"] = request.OrderId.ToString(CultureInfo.InvariantCulture),
                ["client_reference_id"] = "scalaapi-order:" + request.OrderId.ToString(CultureInfo.InvariantCulture),
            }),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(secret + ":")));

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
            logger.LogWarning(ex, "Stripe checkout request failed");
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
                var providerOrderId = ReadRequiredString(root, "id", 128);
                var checkoutUrl = ReadRequiredString(root, "url", 2048);
                var providerPaymentId = ReadOptionalString(root, "payment_intent", 128);
                if (!Uri.TryCreate(checkoutUrl, UriKind.Absolute, out var checkout)
                    || checkout.Scheme is not ("http" or "https"))
                    throw new PaymentProviderException("payment_provider_response_invalid");
                if (checkout.Scheme == Uri.UriSchemeHttp && !allowInsecure)
                    throw new PaymentProviderException("payment_provider_response_https_required");
                return new PaymentCheckoutResult(providerOrderId, checkoutUrl, providerPaymentId);
            }
            catch (JsonException)
            {
                throw new PaymentProviderException("payment_provider_response_invalid");
            }
        }
    }

    private static void ValidateRequest(PaymentCheckoutRequest request)
    {
        if (request.OrderId <= 0 || request.Amount <= 0m || request.Amount > 1_000_000m
            || decimal.Round(request.Amount, 2) != request.Amount
            || request.Currency.Length != 3
            || request.Currency.Any(ch => ch is < 'A' or > 'Z'))
            throw new PaymentProviderException("payment_checkout_request_invalid");
    }

    private string ReadAbsoluteUrl(string key, string fallback, bool allowInsecure)
    {
        var value = configuration[key]?.Trim();
        if (string.IsNullOrWhiteSpace(value)) value = fallback;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || (uri.Scheme == Uri.UriSchemeHttp && !allowInsecure))
            throw new PaymentProviderException("payment_checkout_redirect_invalid");
        return uri.ToString();
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

    private static string? ReadOptionalString(JsonElement root, string name, int maxLength)
    {
        if (!root.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new PaymentProviderException("payment_provider_response_invalid");
        var text = value.GetString()?.Trim() ?? "";
        if (text.Length < 1 || text.Length > maxLength)
            throw new PaymentProviderException("payment_provider_response_invalid");
        return text;
    }
}

public sealed class PaymentProviderRouter(
    MockPaymentProviderClient mock,
    StripePaymentProviderClient stripe)
{
    public Task<PaymentCheckoutResult> CreateCheckoutAsync(
        string provider, PaymentCheckoutRequest request, CancellationToken ct = default) =>
        provider switch
        {
            "mock" => mock.CreateCheckoutAsync(request, ct),
            "stripe" => stripe.CreateCheckoutAsync(request, ct),
            _ => throw new PaymentProviderException("payment_provider_not_supported"),
        };
}
