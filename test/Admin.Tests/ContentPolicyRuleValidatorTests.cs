using ScalaAPI.Admin.Endpoints;
using Xunit;

namespace ScalaAPI.Admin.Tests;

public sealed class ContentPolicyRuleValidatorTests
{
    [Fact]
    public void OpenAiClassifierIsAcceptedAndNormalized()
    {
        var accepted = ContentPolicyRuleValidator.TryNormalize(
            new ContentAuditRuleRequest(
                "  flagged marker  ", "BLOCK", "chat_completions", "ACTIVE",
                "response", Classifier: " OpenAI ", RedactContent: true),
            out var rule, out var error);

        Assert.True(accepted);
        Assert.Equal("", error);
        Assert.Equal("flagged marker", rule.Pattern);
        Assert.Equal("block", rule.ActionType);
        Assert.Equal("active", rule.Status);
        Assert.Equal("response", rule.Stage);
        Assert.Equal("openai", rule.Classifier);
        Assert.True(rule.RedactContent);
    }

    [Fact]
    public void UnknownClassifierIsRejectedBeforeWrite()
    {
        var accepted = ContentPolicyRuleValidator.TryNormalize(
            new ContentAuditRuleRequest("marker", "block", "*", "active", "request",
                Classifier: "provider-x"),
            out _, out var error);

        Assert.False(accepted);
        Assert.Equal("classifier_invalid", error);
    }
}
