using System.Security.Cryptography;
using System.Text;

namespace ScalaAPI.Provider.Mock;

public static class MockPaymentRefund
{
    public static MockPaymentRefundResult? Create(
        string merchantReference, decimal amount, string currency, string idempotencyKey)
    {
        merchantReference = merchantReference.Trim();
        currency = currency.Trim().ToUpperInvariant();
        idempotencyKey = idempotencyKey.Trim();
        if (merchantReference.Length is < 1 or > 128
            || idempotencyKey.Length is < 1 or > 200
            || amount <= 0m || amount > 1_000_000m || decimal.Round(amount, 2) != amount
            || currency.Length != 3 || currency.Any(ch => ch is < 'A' or > 'Z'))
            return null;

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            merchantReference + "\n" + idempotencyKey))).ToLowerInvariant()[..24];
        return new MockPaymentRefundResult($"mock_rf_{digest}", "succeeded", amount, currency);
    }
}

public sealed record MockPaymentRefundResult(
    string ProviderRefundId, string Status, decimal Amount, string Currency);
