using ScalaAPI.Data.Content;

namespace ScalaAPI.Host.Services;

public enum ContentClassifierOutcome
{
    NoMatch,
    Match,
    Unavailable,
}

public sealed record ContentClassifierResult(ContentClassifierOutcome Outcome, string Code)
{
    public static ContentClassifierResult NoMatch() =>
        new(ContentClassifierOutcome.NoMatch, "");

    public static ContentClassifierResult Match() =>
        new(ContentClassifierOutcome.Match, "content_policy_match");

    public static ContentClassifierResult Unavailable(string code) =>
        new(ContentClassifierOutcome.Unavailable, code);
}

/// <summary>
/// Native classifier boundary. Local matching is deterministic; an external
/// classifier is intentionally fail-closed until a configured adapter is
/// registered, so an outage can never turn policy enforcement into fail-open.
/// </summary>
public interface IContentClassifier
{
    Task<ContentClassifierResult> EvaluateAsync(
        string classifier,
        string normalizedContent,
        string normalizedPattern,
        CancellationToken ct = default);
}

public sealed class DefaultContentClassifier : IContentClassifier
{
    public Task<ContentClassifierResult> EvaluateAsync(
        string classifier,
        string normalizedContent,
        string normalizedPattern,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.Equals(classifier, "local", StringComparison.Ordinal))
        {
            return Task.FromResult(normalizedContent.Contains(
                normalizedPattern, StringComparison.Ordinal)
                ? ContentClassifierResult.Match()
                : ContentClassifierResult.NoMatch());
        }

        if (string.Equals(classifier, "external", StringComparison.Ordinal))
            return Task.FromResult(ContentClassifierResult.Unavailable(
                "content_policy_classifier_unavailable"));

        return Task.FromResult(ContentClassifierResult.Unavailable(
            "content_policy_classifier_unsupported"));
    }
}
