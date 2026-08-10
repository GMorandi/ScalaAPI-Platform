using ScalaAPI.Data.Content;
using ScalaAPI.Data.Entities;

namespace ScalaAPI.Admin.Endpoints;

public sealed record ContentAuditRuleRequest(
    string? Pattern,
    string? ActionType,
    string? Scope,
    string? Status,
    string? Stage,
    string? EvaluatorVersion = null,
    string? Classifier = null,
    bool RedactContent = false);

/// <summary>
/// Normalizes the public rule contract before any database write. The
/// classifier names are deliberately explicit so a configured OpenAI adapter
/// cannot be hidden behind the source-owned external protocol.
/// </summary>
public static class ContentPolicyRuleValidator
{
    public static bool TryNormalize(
        ContentAuditRuleRequest request,
        out ContentAuditRuleEntity rule,
        out string error)
    {
        var pattern = request.Pattern?.Trim() ?? "";
        var action = request.ActionType?.Trim().ToLowerInvariant() ?? "";
        var status = request.Status?.Trim().ToLowerInvariant() ?? "";
        var stage = string.IsNullOrWhiteSpace(request.Stage)
            ? "request" : request.Stage.Trim().ToLowerInvariant();
        var scope = string.IsNullOrWhiteSpace(request.Scope) ? null : request.Scope.Trim();
        var evaluatorVersion = string.IsNullOrWhiteSpace(request.EvaluatorVersion)
            ? ContentPolicyEvaluator.Version : request.EvaluatorVersion.Trim();
        var classifier = string.IsNullOrWhiteSpace(request.Classifier)
            ? "local" : request.Classifier.Trim().ToLowerInvariant();
        if (pattern.Length is < 1 or > 512)
            return Invalid(out rule, out error, "pattern_length_invalid");
        if (ContentPolicyEvaluator.Normalize(pattern).Length == 0)
            return Invalid(out rule, out error, "pattern_normalization_empty");
        if (action is not ("log" or "block"))
            return Invalid(out rule, out error, "action_type_invalid");
        if (status is not ("active" or "disabled"))
            return Invalid(out rule, out error, "status_invalid");
        if (stage is not ("request" or "response" or "both"))
            return Invalid(out rule, out error, "stage_invalid");
        if (!ContentPolicyEvaluator.IsSupported(evaluatorVersion))
            return Invalid(out rule, out error, "evaluator_version_invalid");
        if (classifier is not ("local" or "external" or "openai"))
            return Invalid(out rule, out error, "classifier_invalid");
        if (scope is not null && (scope.Length > 128 || scope.Any(char.IsWhiteSpace)))
            return Invalid(out rule, out error, "scope_invalid");

        rule = new ContentAuditRuleEntity
        {
            Pattern = pattern,
            ActionType = action,
            Scope = scope,
            Status = status,
            Stage = stage,
            EvaluatorVersion = evaluatorVersion,
            Classifier = classifier,
            RedactContent = request.RedactContent,
            CreatedAt = DateTime.UtcNow,
        };
        error = "";
        return true;
    }

    private static bool Invalid(out ContentAuditRuleEntity rule, out string error,
        string code)
    {
        rule = new();
        error = code;
        return false;
    }
}
