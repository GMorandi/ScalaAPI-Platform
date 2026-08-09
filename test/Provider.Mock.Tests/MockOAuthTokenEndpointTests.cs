using Xunit;

namespace ScalaAPI.Provider.Mock.Tests;

public sealed class MockOAuthTokenEndpointTests
{
    [Fact]
    public void RotatesVersionedRefreshTokenDeterministically()
    {
        var result = MockOAuthTokenEndpoint.Resolve(
            "refresh_token", "mock-client", "mock-secret", "mock-refresh-v7");

        Assert.Equal(MockOAuthOutcomeKind.Success, result.Kind);
        Assert.Equal(8, result.Version);
    }

    [Theory]
    [InlineData("password", "mock-client", "mock-secret", "mock-refresh-v1", "invalid_client")]
    [InlineData("refresh_token", "wrong-client", "mock-secret", "mock-refresh-v1", "invalid_client")]
    [InlineData("refresh_token", "mock-client", "wrong-secret", "mock-refresh-v1", "invalid_client")]
    [InlineData("refresh_token", "mock-client", "mock-secret", "unknown", "invalid_grant")]
    [InlineData("refresh_token", "mock-client", "mock-secret", "mock-refresh-revoked", "invalid_grant")]
    public void RejectsInvalidGrantWithoutEchoingSecrets(
        string grant, string client, string secret, string refresh, string expected)
    {
        var result = MockOAuthTokenEndpoint.Resolve(grant, client, secret, refresh);

        Assert.Equal(MockOAuthOutcomeKind.Rejected, result.Kind);
        Assert.Equal(expected, result.Error);
        Assert.DoesNotContain(secret, result.Error);
        Assert.DoesNotContain(refresh, result.Error);
    }

    [Theory]
    [InlineData("mock-refresh-timeout", "Timeout")]
    [InlineData("mock-refresh-malformed", "Malformed")]
    [InlineData("mock-refresh-oversized", "Oversized")]
    public void SelectsIndependentFailureProfiles(string refreshToken, string expected)
    {
        var result = MockOAuthTokenEndpoint.Resolve(
            "refresh_token", "mock-client", "mock-secret", refreshToken);

        Assert.Equal(Enum.Parse<MockOAuthOutcomeKind>(expected), result.Kind);
    }

    [Theory]
    [InlineData("Bearer mock-access-v1", false)]
    [InlineData("Bearer mock-access-v2", true)]
    [InlineData("Bearer mock-access-v18", true)]
    [InlineData("Bearer unrelated", false)]
    public void AcceptsOnlyRotatedMockAccessTokens(string header, bool expected)
    {
        Assert.Equal(expected, MockOAuthTokenEndpoint.IsAcceptedAccessHeader(header));
    }
}
