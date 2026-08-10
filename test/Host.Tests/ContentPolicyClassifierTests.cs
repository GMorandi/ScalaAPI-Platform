using System.Net;
using System.Text;
using ScalaAPI.Host.Services;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class ContentPolicyClassifierTests
{
    private static readonly ContentClassifierClientOptions Options =
        new(new Uri("http://classifier.test/v1/classifier/evaluate"),
            TimeSpan.FromMilliseconds(100));
    private static readonly OpenAiModerationClientOptions OpenAiOptions =
        new(new Uri("https://api.openai.test/v1/moderations"), "sk-test",
            "omni-moderation-latest", TimeSpan.FromMilliseconds(100));

    [Fact]
    public async Task ExternalMatchUsesBoundedSourceOwnedContract()
    {
        string? requestBody = null;
        var classifier = Create(new StubHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, "{\"outcome\":\"match\"}");
        }));

        var result = await classifier.EvaluateAsync("external", "hello marker", "marker");

        Assert.Equal(ContentClassifierOutcome.Match, result.Outcome);
        Assert.Contains("\"content\":\"hello marker\"", requestBody);
        Assert.Contains("\"pattern\":\"marker\"", requestBody);
        Assert.Contains("unicode-confusable-v1", requestBody);
    }

    [Fact]
    public async Task LocalClassifierDoesNotMakeAnHttpCall()
    {
        var calls = 0;
        var classifier = Create(new StubHandler(_ =>
        {
            calls++;
            return Task.FromResult(Json(HttpStatusCode.OK, "{\"outcome\":\"match\"}"));
        }));

        var result = await classifier.EvaluateAsync("local", "hello marker", "marker");

        Assert.Equal(ContentClassifierOutcome.Match, result.Outcome);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "content_policy_classifier_unavailable")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "content_policy_classifier_unavailable")]
    [InlineData(HttpStatusCode.BadRequest, "content_policy_classifier_protocol_error")]
    public async Task StatusFailuresMapToStableCodes(HttpStatusCode status, string code)
    {
        var classifier = Create(new StubHandler(_ =>
            Task.FromResult(Json(status, "{\"error\":\"ignored\"}"))));

        var result = await classifier.EvaluateAsync("external", "body", "pattern");

        Assert.Equal(ContentClassifierOutcome.Unavailable, result.Outcome);
        Assert.Equal(code, result.Code);
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"outcome\":\"unexpected\"}")]
    public async Task InvalidPayloadsFailClosedWithoutProviderDetails(string payload)
    {
        var classifier = Create(new StubHandler(_ =>
            Task.FromResult(Json(HttpStatusCode.OK, payload))));

        var result = await classifier.EvaluateAsync("external", "body", "pattern");

        Assert.Equal(ContentClassifierOutcome.Unavailable, result.Outcome);
        Assert.Equal("content_policy_classifier_protocol_error", result.Code);
        Assert.DoesNotContain("provider", result.Code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandlerTimeoutMapsToUnavailable()
    {
        var classifier = Create(new StubHandler(_ =>
            throw new TaskCanceledException("simulated timeout")));

        var result = await classifier.EvaluateAsync("external", "body", "pattern");

        Assert.Equal(ContentClassifierOutcome.Unavailable, result.Outcome);
        Assert.Equal("content_policy_classifier_unavailable", result.Code);
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedToAFalseDecision()
    {
        using var cancellation = new CancellationTokenSource();
        var classifier = Create(new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Json(HttpStatusCode.OK, "{\"outcome\":\"no_match\"}");
        }));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            classifier.EvaluateAsync("external", "body", "pattern", cancellation.Token));
    }

    [Fact]
    public async Task OpenAiModerationUsesBearerInputAndFlaggedResult()
    {
        string? requestBody = null;
        string? authorization = null;
        var classifier = CreateOpenAi(new StubHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            authorization = request.Headers.Authorization?.ToString();
            return Json(HttpStatusCode.OK, """
                {"id":"modr_test","model":"omni-moderation-latest",
                 "results":[{"flagged":true}]}
                """);
        }));

        var result = await classifier.EvaluateAsync("openai", "content", "rule");

        Assert.Equal(ContentClassifierOutcome.Match, result.Outcome);
        Assert.Equal("Bearer sk-test", authorization);
        Assert.Contains("\"model\":\"omni-moderation-latest\"", requestBody);
        Assert.Contains("\"input\":\"content\"", requestBody);
        Assert.DoesNotContain("rule", requestBody);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "content_policy_classifier_unavailable")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "content_policy_classifier_unavailable")]
    [InlineData(HttpStatusCode.Unauthorized, "content_policy_classifier_protocol_error")]
    public async Task OpenAiModerationStatusFailuresRemainFailClosed(
        HttpStatusCode status, string code)
    {
        var classifier = CreateOpenAi(new StubHandler(_ =>
            Task.FromResult(Json(status, "{\"error\":\"ignored\"}"))));

        var result = await classifier.EvaluateAsync("openai", "body", "pattern");

        Assert.Equal(ContentClassifierOutcome.Unavailable, result.Outcome);
        Assert.Equal(code, result.Code);
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"results\":[]}")]
    [InlineData("{\"results\":[{\"flagged\":\"yes\"}]}")]
    public async Task OpenAiModerationInvalidResponsesFailClosed(string payload)
    {
        var classifier = CreateOpenAi(new StubHandler(_ =>
            Task.FromResult(Json(HttpStatusCode.OK, payload))));

        var result = await classifier.EvaluateAsync("openai", "body", "pattern");

        Assert.Equal(ContentClassifierOutcome.Unavailable, result.Outcome);
        Assert.Equal("content_policy_classifier_protocol_error", result.Code);
    }

    [Fact]
    public async Task OpenAiModerationBoundsInputBeforeProviderContact()
    {
        var calls = 0;
        var classifier = CreateOpenAi(new StubHandler(_ =>
        {
            calls++;
            return Task.FromResult(Json(HttpStatusCode.OK,
                "{\"results\":[{\"flagged\":false}]}"));
        }));

        var result = await classifier.EvaluateAsync("openai",
            new string('x', OpenAiModerationClientOptions.MaxRequestBytes + 1), "pattern");

        Assert.Equal(ContentClassifierOutcome.Unavailable, result.Outcome);
        Assert.Equal("content_policy_classifier_protocol_error", result.Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task OpenAiModerationTimeoutMapsToUnavailable()
    {
        var classifier = CreateOpenAi(new StubHandler(_ =>
            throw new TaskCanceledException("simulated timeout")));

        var result = await classifier.EvaluateAsync("openai", "body", "pattern");

        Assert.Equal(ContentClassifierOutcome.Unavailable, result.Outcome);
        Assert.Equal("content_policy_classifier_unavailable", result.Code);
    }

    [Fact]
    public async Task OpenAiModerationMetricsExposeFixedSafeLabelsAndHistogram()
    {
        var metrics = new OpenAiModerationMetrics();
        var calls = 0;
        var classifier = new OpenAiModerationClassifier(
            new HttpClient(new StubHandler(_ =>
            {
                calls++;
                return Task.FromResult(calls == 1
                    ? Json(HttpStatusCode.OK, "{\"results\":[{\"flagged\":true}]}")
                    : Json(HttpStatusCode.OK, "{not-json"));
            })), OpenAiOptions, metrics);

        await classifier.EvaluateAsync("openai", "safe body", "rule");
        await classifier.EvaluateAsync("openai", "second body", "rule");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            classifier.EvaluateAsync("openai", "cancelled", "rule", cancellation.Token));

        var output = metrics.RenderPrometheus();
        Assert.Contains("platform_content_classifier_requests_total{classifier=\"openai\"} 3", output);
        Assert.Contains("platform_content_classifier_matches_total{classifier=\"openai\"} 1", output);
        Assert.Contains("platform_content_classifier_protocol_errors_total{classifier=\"openai\"} 1", output);
        Assert.Contains("platform_content_classifier_cancellations_total{classifier=\"openai\"} 1", output);
        Assert.Contains("duration_seconds_bucket", output);
        Assert.DoesNotContain("safe body", output);
        Assert.DoesNotContain("rule", output);
        Assert.DoesNotContain("sk-test", output);
    }

    private static HttpContentClassifier Create(HttpMessageHandler handler) =>
        new(new HttpClient(handler), Options);

    private static OpenAiModerationClassifier CreateOpenAi(HttpMessageHandler handler) =>
        new(new HttpClient(handler), OpenAiOptions);

    private static HttpResponseMessage Json(HttpStatusCode status, string payload) =>
        new(status)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler;

        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : this(
            (request, _) => handler(request)) { }

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            this.handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
