using System.Net;
using System.Text;
using Npgsql;
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

    [Fact]
    public void OpenAiModerationMetricsExposeErrorBudgetAndP95FromFixedBuckets()
    {
        var metrics = new OpenAiModerationMetrics();
        for (var index = 0; index < 18; index++)
            metrics.Record(ContentClassifierResult.NoMatch(), TimeSpan.FromMilliseconds(25));
        metrics.Record(ContentClassifierResult.Unavailable(
            "content_policy_classifier_unavailable"), TimeSpan.FromMilliseconds(250));
        metrics.Record(ContentClassifierResult.Unavailable(
            "content_policy_classifier_protocol_error"), TimeSpan.FromMilliseconds(250));

        var output = metrics.RenderPrometheus(null,
            new OpenAiModerationMetricBudgetOptions(0.05, 0.1, 20));

        Assert.Contains("platform_content_classifier_unavailable_ratio{classifier=\"openai\"} 0.1", output);
        Assert.Contains("platform_content_classifier_duration_seconds_p95{classifier=\"openai\"} 0.25", output);
        Assert.Contains("platform_content_classifier_unavailable_budget_breached{classifier=\"openai\"} 1", output);
        Assert.Contains("platform_content_classifier_p95_budget_breached{classifier=\"openai\"} 1", output);
        Assert.Contains("platform_content_classifier_budget_breached{classifier=\"openai\"} 1", output);
        Assert.DoesNotContain("content_policy", output);
    }

    [Fact]
    public async Task OpenAiModerationMetricStoreAggregatesInstancesAndReplaysSequence()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var firstInstance = Guid.NewGuid();
        var secondInstance = Guid.NewGuid();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new OpenAiModerationMetricStore(dataSource);
        var first = new OpenAiModerationMetricSnapshot(
            firstInstance, 1, 10, 3, 5, 2, 1, 0, 100,
            [0, 2, 3, 5, 0, 0, 0, 0, 0, 0]);
        var second = new OpenAiModerationMetricSnapshot(
            secondInstance, 1, 10, 4, 4, 2, 0, 0, 200,
            [0, 0, 0, 0, 10, 0, 0, 0, 0, 0]);
        try
        {
            var budget = new OpenAiModerationMetricBudgetOptions(0.05, 0.1, 10, 60);
            await store.AppendAndEvaluateAsync(first, budget);
            await store.AppendAndEvaluateAsync(first, budget);
            await store.AppendAndEvaluateAsync(second, budget);

            var totals = await store.ReadTotalsAsync();
            Assert.Equal(20, totals.Requests);
            Assert.Equal(7, totals.Matches);
            Assert.Equal(4, totals.Unavailable);
            Assert.Equal(300, totals.DurationTicks);
            Assert.Equal(20, totals.Buckets.Sum());

            var output = new OpenAiModerationMetrics().RenderPrometheus(totals);
            Assert.Contains("platform_content_classifier_requests_total{classifier=\"openai\"} 20", output);
            Assert.Contains("platform_content_classifier_unavailable_ratio{classifier=\"openai\"} 0.2", output);
            Assert.Contains("platform_content_classifier_duration_seconds_p95{classifier=\"openai\"} 0.25", output);
            await using var alerts = dataSource.CreateCommand("""
                SELECT budget_kind, status, sample_count
                FROM content_classifier_budget_alerts
                WHERE event_key IN ('openai:unavailable_ratio', 'openai:p95_latency')
                ORDER BY budget_kind
                """);
            await using var alertReader = await alerts.ExecuteReaderAsync();
            var alertRows = new List<(string Kind, string Status, long Samples)>();
            while (await alertReader.ReadAsync())
                alertRows.Add((alertReader.GetString(0), alertReader.GetString(1),
                    alertReader.GetInt64(2)));
            Assert.Equal(2, alertRows.Count);
            Assert.All(alertRows, row => Assert.Equal("open", row.Status));
            Assert.All(alertRows, row => Assert.Equal(20, row.Samples));

            await using (var age = dataSource.CreateCommand("""
                UPDATE content_classifier_metric_snapshots
                SET captured_at = now() - interval '1 hour'
                WHERE instance_id = ANY($1)
                """))
            {
                age.Parameters.AddWithValue(new[] { firstInstance, secondInstance });
                await age.ExecuteNonQueryAsync();
            }
            await store.EvaluateCurrentBudgetAsync(budget);
            var windowTotals = await store.ReadWindowTotalsAsync(budget.WindowSeconds);
            Assert.Equal(0, windowTotals.Requests);
            await using var resolved = dataSource.CreateCommand("""
                SELECT status, sample_count
                FROM content_classifier_budget_alerts
                WHERE event_key IN ('openai:unavailable_ratio', 'openai:p95_latency')
                ORDER BY event_key
                """);
            await using var resolvedReader = await resolved.ExecuteReaderAsync();
            var resolvedRows = new List<(string Status, long Samples)>();
            while (await resolvedReader.ReadAsync())
                resolvedRows.Add((resolvedReader.GetString(0), resolvedReader.GetInt64(1)));
            Assert.Equal(2, resolvedRows.Count);
            Assert.All(resolvedRows, row => Assert.Equal("resolved", row.Status));
            Assert.All(resolvedRows, row => Assert.Equal(0, row.Samples));
        }
        finally
        {
            await using var alertCleanup = dataSource.CreateCommand("""
                DELETE FROM content_classifier_budget_alerts
                WHERE event_key IN ('openai:unavailable_ratio', 'openai:p95_latency')
                """);
            await alertCleanup.ExecuteNonQueryAsync();
            await using var cleanup = dataSource.CreateCommand("""
                DELETE FROM content_classifier_metric_snapshots
                WHERE instance_id = ANY($1)
                """);
            cleanup.Parameters.AddWithValue(new[] { firstInstance, secondInstance });
            await cleanup.ExecuteNonQueryAsync();
        }
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
