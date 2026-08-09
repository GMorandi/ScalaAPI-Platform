using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Grains.Tests;

[Collection("Cluster")]
public class AccountCredentialTests(ClusterFixture fixture)
{
    [Fact]
    public async Task CreateProtectsCredentialsAndHydrateDecryptsThem()
    {
        var grain = fixture.Cluster.GrainFactory.GetGrain<IAccountGrain>(9001);
        await grain.Create(new AccountUpsert(
            "secure-account", "openai", "api-key", "https://upstream.example",
            1, 2, 1, 1, true,
            new() { ["Authorization"] = "Bearer upstream-secret" },
            new(), ["model-a"], null, false));

        var hydrated = await grain.Hydrate();

        Assert.Equal("Bearer upstream-secret", hydrated.AuthHeaders["Authorization"]);
    }

    [Fact]
    public async Task OAuthRefreshLeaseSerializesAndAtomicallyRotatesSecrets()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var grain = fixture.Cluster.GrainFactory.GetGrain<IAccountGrain>(9002);
        await grain.Create(new AccountUpsert(
            "oauth-account", "openai", "oauth", "https://upstream.example",
            1, 2, 1, 1, true, new(), new(), ["model-a"], null, false,
            new ProviderOAuthCredential(
                "https://identity.example/token", "client-id", "client-secret",
                "refresh-old", "access-old", now - 1)));

        var acquired = await grain.BeginOAuthRefresh(now, 120, 30);
        var concurrent = await grain.BeginOAuthRefresh(now, 120, 30);

        Assert.Equal("acquired", acquired.Status);
        Assert.NotNull(acquired.LeaseId);
        Assert.Equal("client-secret", acquired.ClientSecret);
        Assert.Equal("refresh-old", acquired.RefreshToken);
        Assert.Equal("in_progress", concurrent.Status);
        Assert.False(await grain.CompleteOAuthRefresh("wrong-lease", "ignored", null,
            now + 3600, "Bearer"));

        Assert.True(await grain.CompleteOAuthRefresh(acquired.LeaseId!, "access-new",
            "refresh-new", now + 3600, "Bearer"));
        var credentials = await grain.Hydrate();
        var projection = await grain.GetProjection();
        var fresh = await grain.BeginOAuthRefresh(now, 120, 30);

        Assert.Equal("Bearer access-new", credentials.AuthHeaders["Authorization"]);
        Assert.Equal("oauth", projection.CredentialStatus);
        Assert.Equal(2, projection.CredentialVersion);
        Assert.Null(projection.CredentialRefreshError);
        Assert.Equal("fresh", fresh.Status);
    }

    [Fact]
    public async Task OAuthRefreshFailurePersistsSafeStatusAndBackoff()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var retryAt = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds();
        var grain = fixture.Cluster.GrainFactory.GetGrain<IAccountGrain>(9003);
        await grain.Create(new AccountUpsert(
            "failing-oauth-account", "gemini", "oauth", "https://upstream.example",
            1, 2, 1, 1, true, new(), new(), ["model-a"], null, false,
            new ProviderOAuthCredential(
                "https://identity.example/token", "client-id", "",
                "refresh-token", "access-token", now - 1)));
        var acquired = await grain.BeginOAuthRefresh(now, 120, 30);

        await grain.FailOAuthRefresh(acquired.LeaseId!, "oauth_token_endpoint_status_401", retryAt);
        var projection = await grain.GetProjection();

        Assert.Equal("refresh_error", projection.CredentialStatus);
        Assert.Equal("oauth_token_endpoint_status_401", projection.CredentialRefreshError);
        Assert.Equal(retryAt, projection.TempUnschedulableUntil);
    }
}
