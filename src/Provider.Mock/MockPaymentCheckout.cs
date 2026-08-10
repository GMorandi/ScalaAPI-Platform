using System.Security.Cryptography;
using System.Text;

namespace ScalaAPI.Provider.Mock;

public static class MockPaymentCheckout
{
    public static MockPaymentCheckoutResult? Create(
        string merchantReference, decimal amount, string currency, string? description)
    {
        merchantReference = merchantReference.Trim();
        currency = currency.Trim().ToUpperInvariant();
        if (merchantReference.Length is < 1 or > 128
            || amount <= 0m || amount > 1_000_000m || decimal.Round(amount, 2) != amount
            || currency.Length != 3 || currency.Any(ch => ch is < 'A' or > 'Z'))
            return null;

        var digest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(merchantReference))).ToLowerInvariant()[..24];
        var providerOrderId = $"mock_po_{digest}";
        return new MockPaymentCheckoutResult(
            providerOrderId,
            $"http://localhost:8081/checkout/{providerOrderId}",
            description?.Trim() ?? "");
    }
}

public sealed record MockPaymentCheckoutResult(
    string ProviderOrderId, string CheckoutUrl, string Description);
