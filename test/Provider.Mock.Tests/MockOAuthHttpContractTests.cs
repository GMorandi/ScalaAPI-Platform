extern alias providerMock;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using ScalaAPI.Grains.Interfaces;
using ScalaAPI.Host.Services;
using Xunit;

namespace ScalaAPI.Provider.Mock.Tests;

public sealed class MockOAuthHttpContractTests :
    IClassFixture<WebApplicationFactory<providerMock::Program>>
{
    private readonly WebApplicationFactory<providerMock::Program> factory;

    public MockOAuthHttpContractTests(
        WebApplicationFactory<providerMock::Program> factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task PlatformClientRefreshesAgainstRealMockEndpoint()
    {
        var client = CreateClient();
        var result = await client.RefreshAsync(Lease("mock-refresh-v1"));

        Assert.Equal("mock-access-v2", result.AccessToken);
        Assert.Equal("mock-refresh-v2", result.RefreshToken);
        Assert.Equal("Bearer", result.TokenType);
    }

    [Theory]
    [InlineData("mock-refresh-revoked", "oauth_token_endpoint_status_400")]
    [InlineData("mock-refresh-malformed", "oauth_token_response_invalid")]
    [InlineData("mock-refresh-oversized", "oauth_token_response_too_large")]
    public async Task PlatformClientClassifiesMockFailuresWithoutProviderPayload(
        string refreshToken, string expected)
    {
        var client = CreateClient();

        var error = await Assert.ThrowsAsync<ProviderCredentialsUnavailableException>(
            () => client.RefreshAsync(Lease(refreshToken)));

        Assert.Equal(expected, error.Message);
        Assert.DoesNotContain(refreshToken, error.ToString());
    }

    private ProviderTokenEndpointClient CreateClient()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ProviderCredentials:AllowInsecureTokenEndpoints"] = "true",
            }).Build();
        return new ProviderTokenEndpointClient(factory.CreateClient(), configuration);
    }

    private static ProviderOAuthRefreshLease Lease(string refreshToken) => new(
        "acquired", "lease", 1, "http://localhost/oauth/token", "mock-client",
        "mock-secret", refreshToken, null, null);
}
