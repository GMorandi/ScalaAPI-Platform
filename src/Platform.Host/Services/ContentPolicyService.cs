using System.Text;
using Npgsql;

namespace ScalaAPI.Host.Services;

public sealed record ContentPolicyMatch(long RuleId, string Pattern, string Action,
    string? Scope);

public sealed record ContentPolicyDecision(bool Allowed, string Code,
    IReadOnlyList<ContentPolicyMatch> Matches)
{
    public static ContentPolicyDecision Passed(IReadOnlyList<ContentPolicyMatch> matches) =>
        new(true, "", matches);

    public static ContentPolicyDecision Blocked(string code,
        IReadOnlyList<ContentPolicyMatch> matches) => new(false, code, matches);
}

/// <summary>
/// Applies the source-owned content rules before a billable lease is created.
/// Rules are deliberately bounded substring policies for the first product
/// contract; provider calls never happen before this decision is durable.
/// </summary>
public sealed class ContentPolicyService(
    NpgsqlDataSource dataSource,
    ILogger<ContentPolicyService> logger)
{
    public const int MaxBodyBytes = 128 * 1024;

    public async Task<ContentPolicyDecision> EvaluateAsync(
        long userId,
        string requestId,
        string endpoint,
        string capability,
        string body,
        CancellationToken ct = default)
    {
        if (Encoding.UTF8.GetByteCount(body) > MaxBodyBytes)
        {
            logger.LogWarning("Content policy rejected oversized request {RequestId}", requestId);
            return ContentPolicyDecision.Blocked("content_policy_payload_too_large", []);
        }

        if (body.Length == 0)
            return ContentPolicyDecision.Passed([]);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var rulesCommand = new NpgsqlCommand("""
            SELECT id, pattern, action_type, scope
            FROM content_audit_rules
            WHERE status = 'active' AND pattern <> ''
            ORDER BY id
            """, connection, transaction);

        var matches = new List<ContentPolicyMatch>();
        await using (var reader = await rulesCommand.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var scope = reader.IsDBNull(3) ? null : reader.GetString(3);
                if (!ScopeMatches(scope, endpoint, capability))
                    continue;

                var pattern = reader.GetString(1);
                if (body.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    var action = string.Equals(reader.GetString(2), "block",
                        StringComparison.OrdinalIgnoreCase) ? "block" : "log";
                    matches.Add(new ContentPolicyMatch(reader.GetInt64(0), pattern, action, scope));
                }
            }
        }

        foreach (var match in matches)
        {
            await using var logCommand = new NpgsqlCommand("""
                INSERT INTO content_audit_logs(
                    user_id, request_id, matched_rule, action, content_snippet)
                VALUES ($1, NULLIF($2, ''), $3, $4, $5)
                """, connection, transaction);
            logCommand.Parameters.AddWithValue(userId);
            logCommand.Parameters.AddWithValue(requestId ?? "");
            logCommand.Parameters.AddWithValue(match.Pattern);
            logCommand.Parameters.AddWithValue(match.Action);
            logCommand.Parameters.AddWithValue(Snippet(body));
            await logCommand.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        var blocked = matches.Any(match => match.Action == "block");
        return blocked
            ? ContentPolicyDecision.Blocked("content_policy_blocked", matches)
            : ContentPolicyDecision.Passed(matches);
    }

    private static bool ScopeMatches(string? scope, string endpoint, string capability)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope == "*") return true;
        return string.Equals(scope, endpoint, StringComparison.OrdinalIgnoreCase)
            || string.Equals(scope, capability, StringComparison.OrdinalIgnoreCase);
    }

    private static string Snippet(string body) =>
        body.Length <= 200 ? body : body[..200];
}
