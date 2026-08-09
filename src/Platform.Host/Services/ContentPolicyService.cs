using System.Text;
using ScalaAPI.Data.Content;
using Npgsql;

namespace ScalaAPI.Host.Services;

public enum ContentPolicyStage
{
    Request,
    Response,
}

public sealed record ContentPolicyMatch(long RuleId, string Pattern, string Action,
    string? Scope, ContentPolicyStage Stage, string EvaluatorVersion,
    string Classifier, bool RedactContent);

public sealed record ContentPolicyDecision(bool Allowed, string Code,
    IReadOnlyList<ContentPolicyMatch> Matches, long PolicyRevision = 1,
    string EvaluatorVersion = ContentPolicyEvaluator.Version, bool Retryable = false)
{
    public static ContentPolicyDecision Passed(IReadOnlyList<ContentPolicyMatch> matches) =>
        new(true, "", matches);

    public static ContentPolicyDecision Blocked(string code,
        IReadOnlyList<ContentPolicyMatch> matches, long policyRevision = 1,
        bool retryable = false) => new(false, code, matches, policyRevision,
        ContentPolicyEvaluator.Version, retryable);
}

/// <summary>
/// Applies the source-owned content rules at a defined delivery stage.
/// Rules are deliberately bounded substring policies for the first product
/// contract; each decision is durable before the next delivery boundary.
/// </summary>
public sealed class ContentPolicyService(
    NpgsqlDataSource dataSource,
    ILogger<ContentPolicyService> logger,
    IContentClassifier? classifier = null)
{
    public const int MaxBodyBytes = 128 * 1024;
    private readonly IContentClassifier _classifier = classifier ?? new DefaultContentClassifier();

    public async Task<ContentPolicyDecision> EvaluateAsync(
        long userId,
        string requestId,
        string endpoint,
        string capability,
        ContentPolicyStage stage,
        string body,
        CancellationToken ct = default)
    {
        if (Encoding.UTF8.GetByteCount(body) > MaxBodyBytes)
        {
            logger.LogWarning("Content policy rejected oversized {Stage} content for {RequestId}",
                stage, requestId);
            return ContentPolicyDecision.Blocked("content_policy_payload_too_large", []);
        }

        if (body.Length == 0)
            return ContentPolicyDecision.Passed([]);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var (policyRevision, defaultEvaluatorVersion) = await ReadPolicyStateAsync(
            connection, transaction, ct);
        await using var rulesCommand = new NpgsqlCommand("""
            SELECT id, pattern, action_type, scope, evaluator_version, classifier,
                   redact_content
            FROM content_audit_rules
            WHERE status = 'active' AND pattern <> ''
              AND (stage = $1 OR stage = 'both')
            ORDER BY id
            """, connection, transaction);
        rulesCommand.Parameters.AddWithValue(StageValue(stage));

        var matches = new List<ContentPolicyMatch>();
        var normalizedBody = ContentPolicyEvaluator.Normalize(body);
        var failureCode = "";
        var retryable = false;
        await using (var reader = await rulesCommand.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var scope = reader.IsDBNull(3) ? null : reader.GetString(3);
                if (!ScopeMatches(scope, endpoint, capability))
                    continue;

                var pattern = reader.GetString(1);
                var evaluatorVersion = reader.IsDBNull(4)
                    ? defaultEvaluatorVersion : reader.GetString(4);
                var classifierName = reader.IsDBNull(5)
                    ? "local" : reader.GetString(5);
                var redactContent = !reader.IsDBNull(6) && reader.GetBoolean(6);
                if (!ContentPolicyEvaluator.IsSupported(evaluatorVersion))
                {
                    failureCode = "content_policy_evaluator_unsupported";
                    retryable = true;
                    matches.Add(new ContentPolicyMatch(
                        reader.GetInt64(0), pattern, "block", scope, stage,
                        evaluatorVersion, classifierName, redactContent));
                    continue;
                }

                var classifierResult = await _classifier.EvaluateAsync(
                    classifierName, normalizedBody,
                    ContentPolicyEvaluator.Normalize(pattern), ct);
                if (classifierResult.Outcome == ContentClassifierOutcome.Unavailable)
                {
                    failureCode = classifierResult.Code;
                    retryable = true;
                    matches.Add(new ContentPolicyMatch(
                        reader.GetInt64(0), pattern, "block", scope, stage,
                        evaluatorVersion, classifierName, redactContent));
                    continue;
                }

                if (classifierResult.Outcome != ContentClassifierOutcome.Match)
                    continue;

                var action = string.Equals(reader.GetString(2), "block",
                    StringComparison.OrdinalIgnoreCase) ? "block" : "log";
                matches.Add(new ContentPolicyMatch(
                    reader.GetInt64(0), pattern, action, scope, stage,
                    evaluatorVersion, classifierName, redactContent));
            }
        }

        foreach (var match in matches)
        {
            await using var logCommand = new NpgsqlCommand("""
                INSERT INTO content_audit_logs(
                    user_id, request_id, matched_rule, rule_id, stage, action,
                    content_snippet, evaluator_version, classifier, content_redacted,
                    policy_revision)
                VALUES ($1, NULLIF($2, ''), $3, $4, $5, $6, $7, $8, $9, $10, $11)
                ON CONFLICT (request_id, rule_id, stage)
                    WHERE request_id IS NOT NULL AND rule_id IS NOT NULL
                DO NOTHING
                """, connection, transaction);
            logCommand.Parameters.AddWithValue(userId);
            logCommand.Parameters.AddWithValue(requestId ?? "");
            logCommand.Parameters.AddWithValue(match.Pattern);
            logCommand.Parameters.AddWithValue(match.RuleId);
            logCommand.Parameters.AddWithValue(StageValue(stage));
            logCommand.Parameters.AddWithValue(match.Action);
            logCommand.Parameters.AddWithValue(match.RedactContent
                ? "[REDACTED]" : Snippet(body));
            logCommand.Parameters.AddWithValue(match.EvaluatorVersion);
            logCommand.Parameters.AddWithValue(match.Classifier);
            logCommand.Parameters.AddWithValue(match.RedactContent);
            logCommand.Parameters.AddWithValue(policyRevision);
            await logCommand.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        var blocked = matches.Any(match => match.Action == "block");
        if (failureCode.Length > 0)
            return ContentPolicyDecision.Blocked(failureCode, matches,
                policyRevision, retryable);
        return blocked
            ? ContentPolicyDecision.Blocked("content_policy_blocked", matches, policyRevision)
            : new ContentPolicyDecision(true, "", matches, policyRevision,
                defaultEvaluatorVersion);
    }

    private static async Task<(long Revision, string EvaluatorVersion)> ReadPolicyStateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT revision, evaluator_version
            FROM content_policy_state
            WHERE id = 1
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetInt64(0), reader.GetString(1))
            : (1, ContentPolicyEvaluator.Version);
    }

    private static bool ScopeMatches(string? scope, string endpoint, string capability)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope == "*") return true;
        return string.Equals(scope, endpoint, StringComparison.OrdinalIgnoreCase)
            || string.Equals(scope, capability, StringComparison.OrdinalIgnoreCase);
    }

    private static string Snippet(string body) =>
        body.Length <= 200 ? body : body[..200];

    private static string StageValue(ContentPolicyStage stage) =>
        stage == ContentPolicyStage.Response ? "response" : "request";
}
