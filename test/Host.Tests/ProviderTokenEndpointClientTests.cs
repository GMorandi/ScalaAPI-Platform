using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using ScalaAPI.Grains.Interfaces;
using ScalaAPI.Host.Services;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class ProviderTokenEndpointClientTests
{
    [Fact]
    public async Task RefreshUsesFormContractAndReturnsRotatedToken()
    {
        string? submitted = null;
        var handler = new StubHandler(async request =>
        {
            submitted = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"access_token":"access-new","refresh_token":"refresh-new","token_type":"DPoP","expires_in":7200}
                    """, Encoding.UTF8, "application/json"),
            };
        });
        var client = CreateClient(handler);
        var result = await client.RefreshAsync(new ProviderOAuthRefreshLease(
            "acquired", "lease", 1, "https://identity.example/token", "client-id",
            "client-secret", "refresh-old", "scope-a scope-b", null));

        Assert.Equal("access-new", result.AccessToken);
        Assert.Equal("refresh-new", result.RefreshToken);
        Assert.Equal("DPoP", result.TokenType);
        Assert.InRange(result.ExpiresAtUnixSeconds,
            DateTimeOffset.UtcNow.AddMinutes(119).ToUnixTimeSeconds(),
            DateTimeOffset.UtcNow.AddMinutes(121).ToUnixTimeSeconds());
        Assert.Contains("grant_type=refresh_token", submitted);
        Assert.Contains("refresh_token=refresh-old", submitted);
        Assert.Contains("client_id=client-id", submitted);
        Assert.Contains("client_secret=client-secret", submitted);
        Assert.Contains("scope=scope-a+scope-b", submitted);
    }

    [Fact]
    public async Task RejectsInsecureEndpointBeforeSendingSecrets()
    {
        var calls = 0;
        var client = CreateClient(new StubHandler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));

        var error = await Assert.ThrowsAsync<ProviderCredentialsUnavailableException>(() =>
            client.RefreshAsync(new ProviderOAuthRefreshLease(
                "acquired", "lease", 1, "http://identity.example/token", "client-id",
                "client-secret", "refresh-old", null, null)));

        Assert.Equal("oauth_token_endpoint_not_allowed", error.Message);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RejectsProviderErrorsWithoutReturningSensitiveBody()
    {
        var client = CreateClient(new StubHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("refresh_token=secret-value"),
            })));

        var error = await Assert.ThrowsAsync<ProviderCredentialsUnavailableException>(() =>
            client.RefreshAsync(new ProviderOAuthRefreshLease(
                "acquired", "lease", 1, "https://identity.example/token", "client-id",
                "client-secret", "refresh-old", null, null)));

        Assert.Equal("oauth_token_endpoint_status_401", error.Message);
        Assert.DoesNotContain("secret-value", error.ToString());
    }

    [Fact]
    public async Task ClassifiesInvalidGrantAsTerminalWithoutReturningProviderBody()
    {
        var client = CreateClient(new StubHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    "{\"error\":\"invalid_grant\",\"refresh_token\":\"secret-value\"}",
                    Encoding.UTF8, "application/json"),
            })));

        var error = await Assert.ThrowsAsync<ProviderCredentialsUnavailableException>(() =>
            client.RefreshAsync(new ProviderOAuthRefreshLease(
                "acquired", "lease", 1, "https://identity.example/token", "client-id",
                "client-secret", "refresh-old", null, null)));

        Assert.True(error.CredentialRevoked);
        Assert.Equal("oauth_refresh_token_revoked", error.Message);
        Assert.DoesNotContain("secret-value", error.ToString());
    }

    private static ProviderTokenEndpointClient CreateClient(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        return new ProviderTokenEndpointClient(new HttpClient(handler), configuration);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }
}
