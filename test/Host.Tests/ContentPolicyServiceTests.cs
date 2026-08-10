using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ScalaAPI.Data.Content;
using ScalaAPI.Host.Services;
using Xunit;

namespace ScalaAPI.Host.Tests;

public sealed class ContentPolicyServiceTests
{
    [Fact]
    public void UnicodeEvaluatorMatchesCompatibilityAndConfusableForms()
    {
        Assert.True(ContentPolicyEvaluator.Contains(
            "ＳｅＮѕіtіνｅ content", "sensitive"));
        Assert.Equal("sensitive", ContentPolicyEvaluator.Normalize("ＳeNѕіtіνe"));
        Assert.True(ContentPolicyEvaluator.Contains("cafe\u0301", "café"));
        Assert.DoesNotContain('\u200b', ContentPolicyEvaluator.Normalize("sen\u200bsitive"));
        Assert.True(ContentPolicyEvaluator.IsSupported(ContentPolicyEvaluator.Version));
    }

    [Fact]
    public async Task BlockRuleIsEvaluatedBeforeDispatchAndAudited()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var suffix = Guid.NewGuid().ToString("N");
        var userId = 9_500_000L + Random.Shared.Next(1, 100_000);
        var requestId = $"content-policy:{suffix}";
        var blockPattern = $"blocked-{suffix}";
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        try
        {
            await using (var command = dataSource.CreateCommand("""
                INSERT INTO content_audit_rules(pattern, action_type, scope, status)
                VALUES ($1, 'block', 'chat_completions', 'active')
                """))
            {
                command.Parameters.AddWithValue(blockPattern);
                await command.ExecuteNonQueryAsync();
            }

            var service = new ContentPolicyService(
                dataSource, NullLogger<ContentPolicyService>.Instance);
            var decision = await service.EvaluateAsync(userId, requestId,
                "chat_completions", "chat_completions",
                ContentPolicyStage.Request,
                $"hello {blockPattern} world");

            Assert.False(decision.Allowed);
            Assert.Equal("content_policy_blocked", decision.Code);
            var match = Assert.Single(decision.Matches);
            Assert.Equal("block", match.Action);

            await using var verify = dataSource.CreateCommand("""
                SELECT action, matched_rule, content_snippet
                FROM content_audit_logs
                WHERE request_id = $1
                """);
            verify.Parameters.AddWithValue(requestId);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("block", reader.GetString(0));
            Assert.Equal(blockPattern, reader.GetString(1));
            Assert.Contains(blockPattern, reader.GetString(2));
        }
        finally
        {
            await using (var cleanupLogs = dataSource.CreateCommand(
                "DELETE FROM content_audit_logs WHERE request_id = $1"))
            {
                cleanupLogs.Parameters.AddWithValue(requestId);
                await cleanupLogs.ExecuteNonQueryAsync();
            }
            await using var cleanupRule = dataSource.CreateCommand(
                "DELETE FROM content_audit_rules WHERE pattern = $1");
            cleanupRule.Parameters.AddWithValue(blockPattern);
            await cleanupRule.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ScopeMismatchDoesNotCreateAnAuditOrBlock()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var suffix = Guid.NewGuid().ToString("N");
        var requestId = $"content-policy-scope:{suffix}";
        var pattern = $"scoped-{suffix}";
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        try
        {
            await using (var command = dataSource.CreateCommand("""
                INSERT INTO content_audit_rules(pattern, action_type, scope, status)
                VALUES ($1, 'block', 'messages', 'active')
                """))
            {
                command.Parameters.AddWithValue(pattern);
                await command.ExecuteNonQueryAsync();
            }

            var service = new ContentPolicyService(
                dataSource, NullLogger<ContentPolicyService>.Instance);
            var decision = await service.EvaluateAsync(9_500_001L, requestId,
                "chat_completions", "chat_completions",
                ContentPolicyStage.Request, pattern);

            Assert.True(decision.Allowed);
            Assert.Empty(decision.Matches);
        }
        finally
        {
            await using var cleanup = dataSource.CreateCommand(
                "DELETE FROM content_audit_rules WHERE pattern = $1");
            cleanup.Parameters.AddWithValue(pattern);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task OversizedBodyFailsClosedWithoutAProviderLease()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var service = new ContentPolicyService(
            dataSource, NullLogger<ContentPolicyService>.Instance);
        var decision = await service.EvaluateAsync(1, "oversized", "messages", "messages",
            ContentPolicyStage.Request,
            new string('x', ContentPolicyService.MaxBodyBytes + 1));

        Assert.False(decision.Allowed);
        Assert.Equal("content_policy_payload_too_large", decision.Code);
        Assert.Empty(decision.Matches);
    }

    [Fact]
    public async Task ResponseRuleIsStageScopedAndAuditWriteIsIdempotent()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var suffix = Guid.NewGuid().ToString("N");
        var requestId = $"content-policy-response:{suffix}";
        var pattern = $"response-{suffix}";
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        try
        {
            await using (var command = dataSource.CreateCommand("""
                INSERT INTO content_audit_rules(pattern, action_type, scope, status, stage)
                VALUES ($1, 'block', 'chat_completions', 'active', 'response')
                """))
            {
                command.Parameters.AddWithValue(pattern);
                await command.ExecuteNonQueryAsync();
            }

            var service = new ContentPolicyService(
                dataSource, NullLogger<ContentPolicyService>.Instance);
            var requestDecision = await service.EvaluateAsync(9_500_002L, requestId,
                "chat_completions", "chat_completions",
                ContentPolicyStage.Request, pattern);
            Assert.True(requestDecision.Allowed);

            var first = await service.EvaluateAsync(9_500_002L, requestId,
                "chat_completions", "chat_completions",
                ContentPolicyStage.Response, pattern);
            var replay = await service.EvaluateAsync(9_500_002L, requestId,
                "chat_completions", "chat_completions",
                ContentPolicyStage.Response, pattern);
            Assert.False(first.Allowed);
            Assert.False(replay.Allowed);
            Assert.Equal(first.Matches.Single().RuleId, replay.Matches.Single().RuleId);

            await using var verify = dataSource.CreateCommand("""
                SELECT count(*), min(stage)
                FROM content_audit_logs
                WHERE request_id = $1
                """);
            verify.Parameters.AddWithValue(requestId);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal("response", reader.GetString(1));
        }
        finally
        {
            await using (var cleanupLogs = dataSource.CreateCommand(
                "DELETE FROM content_audit_logs WHERE request_id = $1"))
            {
                cleanupLogs.Parameters.AddWithValue(requestId);
                await cleanupLogs.ExecuteNonQueryAsync();
            }
            await using var cleanupRule = dataSource.CreateCommand(
                "DELETE FROM content_audit_rules WHERE pattern = $1");
            cleanupRule.Parameters.AddWithValue(pattern);
            await cleanupRule.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ExternalClassifierUnavailableFailsClosedAndRedactsAuditContent()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var suffix = Guid.NewGuid().ToString("N");
        var requestId = $"content-policy-external:{suffix}";
        var pattern = $"sensitive-{suffix}";
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        try
        {
            await using (var command = dataSource.CreateCommand("""
                INSERT INTO content_audit_rules(
                    pattern, action_type, scope, status, stage, classifier, redact_content)
                VALUES ($1, 'block', 'chat_completions', 'active', 'response', 'external', true)
                """))
            {
                command.Parameters.AddWithValue(pattern);
                await command.ExecuteNonQueryAsync();
            }

            var service = new ContentPolicyService(
                dataSource, NullLogger<ContentPolicyService>.Instance);
            var decision = await service.EvaluateAsync(9_500_003L, requestId,
                "chat_completions", "chat_completions", ContentPolicyStage.Response,
                $"Provider payload contains {pattern}");

            Assert.False(decision.Allowed);
            Assert.True(decision.Retryable);
            Assert.Equal("content_policy_classifier_unavailable", decision.Code);
            Assert.Equal(ContentPolicyEvaluator.Version, decision.EvaluatorVersion);

            await using var verify = dataSource.CreateCommand("""
                SELECT action, content_snippet, content_redacted, classifier,
                       evaluator_version, policy_revision
                FROM content_audit_logs WHERE request_id = $1
                """);
            verify.Parameters.AddWithValue(requestId);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("block", reader.GetString(0));
            Assert.Equal("[REDACTED]", reader.GetString(1));
            Assert.True(reader.GetBoolean(2));
            Assert.Equal("external", reader.GetString(3));
            Assert.Equal(ContentPolicyEvaluator.Version, reader.GetString(4));
            Assert.True(reader.GetInt64(5) > 0);

            await using var alert = dataSource.CreateCommand("""
                SELECT kind, severity, code, details::text
                FROM content_policy_alert_events WHERE request_id = $1
                """);
            alert.Parameters.AddWithValue(requestId);
            await using var alertReader = await alert.ExecuteReaderAsync();
            Assert.True(await alertReader.ReadAsync());
            Assert.Equal("classifier_unavailable", alertReader.GetString(0));
            Assert.Equal("critical", alertReader.GetString(1));
            Assert.Equal("content_policy_classifier_unavailable", alertReader.GetString(2));
            Assert.DoesNotContain(pattern, alertReader.GetString(3));
        }
        finally
        {
            await using (var cleanupAlerts = dataSource.CreateCommand(
                "DELETE FROM content_policy_alert_events WHERE request_id = $1"))
            {
                cleanupAlerts.Parameters.AddWithValue(requestId);
                await cleanupAlerts.ExecuteNonQueryAsync();
            }
            await using (var cleanupLogs = dataSource.CreateCommand(
                "DELETE FROM content_audit_logs WHERE request_id = $1"))
            {
                cleanupLogs.Parameters.AddWithValue(requestId);
                await cleanupLogs.ExecuteNonQueryAsync();
            }
            await using var cleanupRule = dataSource.CreateCommand(
                "DELETE FROM content_audit_rules WHERE pattern = $1");
            cleanupRule.Parameters.AddWithValue(pattern);
            await cleanupRule.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task OpenAiClassifierRuleCanBePersistedAndEvaluated()
    {
        var connectionString = Environment.GetEnvironmentVariable("GREENFIELD_SCHEMA_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var suffix = Guid.NewGuid().ToString("N");
        var requestId = $"content-policy-openai:{suffix}";
        var pattern = $"openai-{suffix}";
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        try
        {
            await using (var command = dataSource.CreateCommand("""
                INSERT INTO content_audit_rules(
                    pattern, action_type, scope, status, stage, classifier, redact_content)
                VALUES ($1, 'block', 'chat_completions', 'active', 'response', 'openai', true)
                """))
            {
                command.Parameters.AddWithValue(pattern);
                await command.ExecuteNonQueryAsync();
            }

            var service = new ContentPolicyService(
                dataSource,
                NullLogger<ContentPolicyService>.Instance,
                new MatchingClassifier());
            var decision = await service.EvaluateAsync(9_500_004L, requestId,
                "chat_completions", "chat_completions", ContentPolicyStage.Response,
                $"Provider payload {pattern}");

            Assert.False(decision.Allowed);
            Assert.Equal("content_policy_blocked", decision.Code);
            Assert.Equal("openai", Assert.Single(decision.Matches).Classifier);

            await using var verify = dataSource.CreateCommand("""
                SELECT classifier, content_snippet, content_redacted
                FROM content_audit_logs WHERE request_id = $1
                """);
            verify.Parameters.AddWithValue(requestId);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("openai", reader.GetString(0));
            Assert.Equal("[REDACTED]", reader.GetString(1));
            Assert.True(reader.GetBoolean(2));
        }
        finally
        {
            await using (var cleanupAlerts = dataSource.CreateCommand(
                "DELETE FROM content_policy_alert_events WHERE request_id = $1"))
            {
                cleanupAlerts.Parameters.AddWithValue(requestId);
                await cleanupAlerts.ExecuteNonQueryAsync();
            }
            await using (var cleanupLogs = dataSource.CreateCommand(
                "DELETE FROM content_audit_logs WHERE request_id = $1"))
            {
                cleanupLogs.Parameters.AddWithValue(requestId);
                await cleanupLogs.ExecuteNonQueryAsync();
            }
            await using var cleanupRule = dataSource.CreateCommand(
                "DELETE FROM content_audit_rules WHERE pattern = $1");
            cleanupRule.Parameters.AddWithValue(pattern);
            await cleanupRule.ExecuteNonQueryAsync();
        }
    }

    private sealed class MatchingClassifier : IContentClassifier
    {
        public Task<ContentClassifierResult> EvaluateAsync(
            string classifier, string normalizedContent, string normalizedPattern,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(string.Equals(classifier, "openai", StringComparison.Ordinal)
                ? ContentClassifierResult.Match()
                : ContentClassifierResult.NoMatch());
        }
    }
}
