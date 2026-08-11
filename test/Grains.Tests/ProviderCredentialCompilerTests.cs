using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Grains.Tests;

public sealed class ProviderCredentialCompilerTests
{
    [Fact]
    public void AnthropicCompilesNativeKeyVersionAndBetaWithoutSemanticAliases()
    {
        var headers = ProviderCredentialCompiler.CompileStatic("Anthropic", "api_key",
            new Dictionary<string, string>
            {
                ["api_key"] = "ant-secret",
                ["anthropic_beta"] = "prompt-caching-2024-07-31",
                ["provider_scenario"] = "success",
            });

        Assert.Equal("ant-secret", headers["x-api-key"]);
        Assert.Equal("2023-06-01", headers["anthropic-version"]);
        Assert.Equal("prompt-caching-2024-07-31", headers["anthropic-beta"]);
        Assert.Equal("success", headers["X-Provider-Scenario"]);
        Assert.DoesNotContain("api_key", headers.Keys);
    }

    [Fact]
    public void GeminiCompilesHeaderKeyAndNeverPutsItInTheUrl()
    {
        var headers = ProviderCredentialCompiler.CompileStatic("google", "api_key",
            new Dictionary<string, string> { ["api_key"] = "gem-secret" });

        Assert.Equal("gem-secret", headers["x-goog-api-key"]);
        Assert.DoesNotContain("api_key", headers.Keys);
        Assert.DoesNotContain("key", headers.Keys);
    }

    [Fact]
    public void NativeAccountsRequireAStaticKeyAndRejectHeaderCollisions()
    {
        var missing = Assert.Throws<ProviderCredentialContractException>(() =>
            ProviderCredentialCompiler.CompileStatic("gemini", "api_key", new Dictionary<string, string>()));
        Assert.Equal("provider_api_key_missing", missing.Code);

        var collision = Assert.Throws<ProviderCredentialContractException>(() =>
            ProviderCredentialCompiler.CompileStatic("anthropic", "api_key",
                new Dictionary<string, string>
                {
                    ["api_key"] = "one",
                    ["x-api-key"] = "two",
                }));
        Assert.Equal("provider_credential_header_collision", collision.Code);
    }

    [Fact]
    public void OAuthAccountsCanUseRotatingHeaderWithoutRequiringNativeApiKey()
    {
        var headers = ProviderCredentialCompiler.CompileStatic("gemini", "oauth",
            new Dictionary<string, string>());

        Assert.Equal("2023-06-01", ProviderCredentialCompiler.CompileStatic(
            "anthropic", "oauth", new Dictionary<string, string>())["anthropic-version"]);
        Assert.Empty(headers);
    }

    [Fact]
    public void AnthropicRejectsOversizedVersionAndExpandedHeaderSet()
    {
        var version = Assert.Throws<ProviderCredentialContractException>(() =>
            ProviderCredentialCompiler.CompileStatic("anthropic", "api_key",
                new Dictionary<string, string>
                {
                    ["api_key"] = "secret",
                    ["anthropic_version"] = new('v', 33),
                }));
        Assert.Equal("provider_credential_invalid", version.Code);

        var credentials = Enumerable.Range(0, 15)
            .ToDictionary(index => $"x-provider-{index}", index => $"value-{index}");
        credentials["api_key"] = "secret";
        var count = Assert.Throws<ProviderCredentialContractException>(() =>
            ProviderCredentialCompiler.CompileStatic("anthropic", "api_key", credentials));
        Assert.Equal("provider_credentials_too_many", count.Code);
    }
}
