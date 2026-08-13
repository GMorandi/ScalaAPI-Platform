using System.Text.Json;

namespace ScalaAPI.Data.Accounting;

public sealed record ReconciliationResolutionRequest(
    string Action,
    string EvidenceType,
    string Evidence,
    string Reason,
    int InputTokens = 0,
    int OutputTokens = 0,
    int CacheCreateTokens = 0,
    int CacheReadTokens = 0,
    int DurationMs = 0,
    int FirstTokenMs = 0,
    int StatusCode = 200,
    bool Stream = false,
    bool ClientDisconnect = false,
    int InputImageCount = 0,
    int OutputImageCount = 0,
    string ImageSize = "",
    int VideoCount = 0,
    string VideoResolution = "",
    int VideoDurationSeconds = 0,
    int RealtimeDurationMs = 0,
    int RealtimeFrames = 0,
    string DisconnectReason = "",
    string ProviderUsageJson = "",
    int ReasoningTokens = 0,
    string ServiceTier = "",
    string UpstreamEndpoint = "",
    string CancellationReason = "",
    string MediaOperationId = "",
    int ResponseStatusCode = 200,
    string ResponseContentType = "application/json",
    string ResponseBody = "");

public enum ReconciliationResolutionStatus
{
    Applied,
    Duplicate,
    Conflict,
    NotFound,
    Invalid,
}

public sealed record ReconciliationResolutionResult(
    ReconciliationResolutionStatus Status,
    string ErrorCode = "",
    long? ResolutionId = null,
    string LeaseToken = "",
    string Action = "",
    decimal? CostUsd = null)
{
    public bool Accepted => Status is ReconciliationResolutionStatus.Applied
        or ReconciliationResolutionStatus.Duplicate;
}

public static class ReconciliationResolutionFingerprint
{
    public static string Compute(long incidentId, ReconciliationResolutionRequest request,
        long actorUserId = 0)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            incident_id = incidentId,
            actor_user_id = actorUserId,
            action = request.Action.Trim().ToLowerInvariant(),
            evidence_type = request.EvidenceType.Trim().ToLowerInvariant(),
            evidence = request.Evidence.Trim(),
            reason = request.Reason.Trim(),
            usage = request,
        });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
