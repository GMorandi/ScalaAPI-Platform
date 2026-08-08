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
}
