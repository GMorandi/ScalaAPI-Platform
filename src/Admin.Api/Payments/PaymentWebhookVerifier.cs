using System.Security.Cryptography;
using System.Text;

namespace ScalaAPI.Admin.Payments;

public static class PaymentWebhookVerifier
{
    public static string ComputeSignature(string secret, ReadOnlySpan<byte> payload)
    {
        var digest = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    public static bool Verify(string secret, ReadOnlySpan<byte> payload, string? signature)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(signature))
            return false;

        var value = signature.Trim();
        if (value.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
            value = value[7..];

        byte[] supplied;
        try
        {
            supplied = value.Length == 64
                ? Convert.FromHexString(value)
                : Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload);
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }
}
