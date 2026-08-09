namespace ScalaAPI.Provider.Mock;

internal enum MockOAuthOutcomeKind
{
    Success,
    Rejected,
    Timeout,
    Malformed,
    Oversized,
}

internal sealed record MockOAuthOutcome(
    MockOAuthOutcomeKind Kind, int Version = 0, string Error = "");

internal static class MockOAuthTokenEndpoint
{
    internal static MockOAuthOutcome Resolve(
        string grantType, string clientId, string clientSecret, string refreshToken)
    {
        if (!string.Equals(grantType, "refresh_token", StringComparison.Ordinal)
            || !string.Equals(clientId, "mock-client", StringComparison.Ordinal)
            || !string.Equals(clientSecret, "mock-secret", StringComparison.Ordinal))
            return new(MockOAuthOutcomeKind.Rejected, Error: "invalid_client");
        return refreshToken switch
        {
            "mock-refresh-revoked" => new(MockOAuthOutcomeKind.Rejected, Error: "invalid_grant"),
            "mock-refresh-timeout" => new(MockOAuthOutcomeKind.Timeout),
            "mock-refresh-malformed" => new(MockOAuthOutcomeKind.Malformed),
            "mock-refresh-oversized" => new(MockOAuthOutcomeKind.Oversized),
            _ => ResolveVersion(refreshToken),
        };
    }

    private static MockOAuthOutcome ResolveVersion(string refreshToken)
    {
        const string prefix = "mock-refresh-v";
        if (!refreshToken.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(refreshToken[prefix.Length..], out var version)
            || version < 1 || version >= 1000)
            return new(MockOAuthOutcomeKind.Rejected, Error: "invalid_grant");
        return new(MockOAuthOutcomeKind.Success, version + 1);
    }

    internal static bool IsAcceptedAccessHeader(string? authorization)
    {
        const string prefix = "Bearer mock-access-v";
        return authorization is not null
            && authorization.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(authorization[prefix.Length..], out var version)
            && version >= 2 && version < 1000;
    }
}
