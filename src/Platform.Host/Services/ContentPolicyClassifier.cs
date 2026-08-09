using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

public sealed record ContentClassifierClientOptions(Uri Endpoint, TimeSpan Timeout)
{
    public const int MaxRequestBytes = 128 * 1024 + 1024;
    public const int MaxResponseBytes = 8 * 1024;
}

/// <summary>
/// Source-owned HTTP classifier contract. The adapter never forwards provider
/// credentials and treats transport, timeout, status, and schema failures as a
/// deterministic fail-closed result.
/// </summary>
public sealed class HttpContentClassifier(
    HttpClient httpClient,
    ContentClassifierClientOptions options) : IContentClassifier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ContentClassifierResult> EvaluateAsync(
        string classifier,
        string normalizedContent,
        string normalizedPattern,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.Equals(classifier, "local", StringComparison.Ordinal))
        {
            return normalizedContent.Contains(normalizedPattern, StringComparison.Ordinal)
                ? ContentClassifierResult.Match()
                : ContentClassifierResult.NoMatch();
        }

        if (!string.Equals(classifier, "external", StringComparison.Ordinal))
            return ContentClassifierResult.Unavailable("content_policy_classifier_unsupported");

        if (string.IsNullOrWhiteSpace(normalizedPattern)
            || Encoding.UTF8.GetByteCount(normalizedContent) > ContentClassifierClientOptions.MaxRequestBytes
            || Encoding.UTF8.GetByteCount(normalizedPattern) > 1024)
            return ContentClassifierResult.Unavailable("content_policy_classifier_protocol_error");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
            {
                Content = JsonContent.Create(new ClassifierRequest(
                    normalizedContent, normalizedPattern, ContentPolicyEvaluator.Version),
                    options: JsonOptions),
            };
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            if ((int)response.StatusCode == StatusCodes.Status429TooManyRequests
                || (int)response.StatusCode >= 500)
                return ContentClassifierResult.Unavailable(
                    "content_policy_classifier_unavailable");
            if (!response.IsSuccessStatusCode)
                return ContentClassifierResult.Unavailable(
                    "content_policy_classifier_protocol_error");

            var payload = await response.Content.ReadAsByteArrayAsync(ct);
            if (payload.Length is 0 or > ContentClassifierClientOptions.MaxResponseBytes)
                return ContentClassifierResult.Unavailable(
                    "content_policy_classifier_protocol_error");

            var result = JsonSerializer.Deserialize<ClassifierResponse>(payload, JsonOptions);
            return result?.Outcome switch
            {
                "match" => ContentClassifierResult.Match(),
                "no_match" => ContentClassifierResult.NoMatch(),
                "unavailable" => ContentClassifierResult.Unavailable(
                    "content_policy_classifier_unavailable"),
                _ => ContentClassifierResult.Unavailable(
                    "content_policy_classifier_protocol_error"),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ContentClassifierResult.Unavailable(
                "content_policy_classifier_unavailable");
        }
        catch (HttpRequestException)
        {
            return ContentClassifierResult.Unavailable(
                "content_policy_classifier_unavailable");
        }
        catch (JsonException)
        {
            return ContentClassifierResult.Unavailable(
                "content_policy_classifier_protocol_error");
        }
    }

    private sealed record ClassifierRequest(
        [property: JsonPropertyName("content")]
        string Content,
        [property: JsonPropertyName("pattern")]
        string Pattern,
        [property: JsonPropertyName("evaluator_version")]
        string EvaluatorVersion);

    private sealed record ClassifierResponse(
        [property: JsonPropertyName("outcome")] string? Outcome,
        [property: JsonPropertyName("code")] string? Code);
}
