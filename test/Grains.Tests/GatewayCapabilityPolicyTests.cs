using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Grains.Tests;

public class GatewayCapabilityPolicyTests
{
    [Fact]
    public void OpenAiSupportsMediaAndEmbeddingCapabilities()
    {
        Assert.True(GatewayCapabilityPolicy.Supports("openai", "embeddings"));
        Assert.True(GatewayCapabilityPolicy.Supports("openai", "images_async"));
        Assert.True(GatewayCapabilityPolicy.Supports("openai", "videos"));
    }

    [Fact]
    public void UnsupportedProviderCapabilityIsRejected()
    {
        Assert.False(GatewayCapabilityPolicy.Supports("anthropic", "videos"));
        Assert.False(GatewayCapabilityPolicy.Supports("unknown", "chat_completions"));
    }
}
