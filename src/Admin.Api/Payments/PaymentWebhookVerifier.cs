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

    public static bool VerifyStripe(
        string secret,
        ReadOnlySpan<byte> payload,
        string? signature,
        DateTimeOffset now,
        TimeSpan tolerance)
    {
        if (string.IsNullOrWhiteSpace(secret)
            || string.IsNullOrWhiteSpace(signature)
            || tolerance <= TimeSpan.Zero)
            return false;

        long? timestamp = null;
        var candidates = new List<byte[]>();
        foreach (var component in signature.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = component.IndexOf('=');
            if (separator <= 0 || separator == component.Length - 1)
                continue;
            var key = component[..separator].Trim();
            var value = component[(separator + 1)..].Trim();
            if (key == "t" && long.TryParse(value, out var parsed))
                timestamp = parsed;
            else if (key == "v1" && value.Length == 64)
            {
                try
                {
                    candidates.Add(Convert.FromHexString(value));
                }
                catch (FormatException)
                {
                    // Ignore malformed candidates and require one valid signature.
                }
            }
        }

        var ageSeconds = timestamp is null
            ? double.PositiveInfinity
            : Math.Abs((double)now.ToUnixTimeSeconds() - timestamp.Value);
        if (timestamp is null
            || ageSeconds > tolerance.TotalSeconds
            || candidates.Count == 0)
            return false;

        var signedPayload = Encoding.UTF8.GetBytes(
            timestamp.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "." + Encoding.UTF8.GetString(payload));
        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), signedPayload);
        return candidates.Any(candidate =>
            CryptographicOperations.FixedTimeEquals(expected, candidate));
    }
}
