using Xunit;

namespace ScalaAPI.Provider.Mock.Tests;

public sealed class MockOAuthAuthorizationCodeTests
{
    [Fact]
    public void AuthorizationCodeBindsRedirectAndPkceVerifierAndIsSingleUse()
    {
        const string verifier = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~";
        const string redirect = "http://localhost:3000/oauth/callback";
        var code = MockOAuthAuthorizationCode.Issue(
            "mock-client", redirect, MockOAuthAuthorizationCode.Challenge(verifier));

        Assert.True(MockOAuthAuthorizationCode.Redeem(code, "mock-client", redirect, verifier));
        Assert.False(MockOAuthAuthorizationCode.Redeem(code, "mock-client", redirect, verifier));
    }

    [Fact]
    public void AuthorizationCodeRejectsWrongBinding()
    {
        const string verifier = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~";
        var code = MockOAuthAuthorizationCode.Issue(
            "mock-client", "http://localhost:3000/oauth/callback",
            MockOAuthAuthorizationCode.Challenge(verifier));

        Assert.False(MockOAuthAuthorizationCode.Redeem(
            code, "mock-client", "http://localhost:3001/oauth/callback", verifier));
        Assert.False(MockOAuthAuthorizationCode.Redeem(
            code, "mock-client", "http://localhost:3000/oauth/callback", verifier + "x"));
        Assert.True(MockOAuthAuthorizationCode.Redeem(
            code, "mock-client", "http://localhost:3000/oauth/callback", verifier));
    }
}
