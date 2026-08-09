using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ScalaAPI.Provider.Mock;

internal static class MockOAuthAuthorizationCode
{
    private sealed record Grant(string ClientId, string RedirectUri, string CodeChallenge,
        DateTime ExpiresAt);

    private static readonly ConcurrentDictionary<string, Grant> Grants = new();
    private static readonly object Gate = new();

    internal static string Issue(string clientId, string redirectUri, string codeChallenge)
    {
        var code = $"mock-code-{Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant()}";
        Grants[code] = new Grant(clientId, redirectUri, codeChallenge,
            DateTime.UtcNow.AddMinutes(2));
        return code;
    }

    internal static bool Redeem(string code, string clientId, string redirectUri,
        string codeVerifier)
    {
        lock (Gate)
        {
            if (!Grants.TryGetValue(code, out var grant)
                || grant.ExpiresAt <= DateTime.UtcNow
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(grant.ClientId), Encoding.UTF8.GetBytes(clientId))
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(grant.RedirectUri), Encoding.UTF8.GetBytes(redirectUri))
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(grant.CodeChallenge),
                    Encoding.UTF8.GetBytes(Challenge(codeVerifier))))
                return false;

            return Grants.TryRemove(code, out _);
        }
    }

    internal static string Challenge(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .TrimEnd('=');
}
