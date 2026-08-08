namespace ScalaAPI.Grains;

public readonly record struct ApiKeyQuotaState(
    decimal Quota,
    decimal QuotaUsed,
    decimal RateLimit5h,
    decimal RateLimit1d,
    decimal RateLimit7d,
    decimal Usage5h,
    decimal Usage1d,
    decimal Usage7d,
    long Window5hStart,
    long Window1dStart,
    long Window7dStart);

public readonly record struct ApiKeyQuotaDecision(
    ApiKeyQuotaState State,
    string? RejectionReason)
{
    public bool Allowed => RejectionReason is null;
}

/// <summary>
/// Deterministic API-key quota policy. Window counters are spend amounts in USD;
/// zero limits mean unlimited. The absolute quota wins over rolling windows,
/// followed by the shortest window to make rejection precedence stable.
/// </summary>
public static class ApiKeyQuotaPolicy
{
    private const long FiveHoursMs = 5 * 3600 * 1000L;
    private const long OneDayMs = 24 * 3600 * 1000L;
    private const long SevenDaysMs = 7 * 24 * 3600 * 1000L;

    public static ApiKeyQuotaDecision Evaluate(ApiKeyQuotaState state, long nowMs)
    {
        var normalized = Normalize(state, nowMs);

        if (normalized.Quota > 0 && normalized.QuotaUsed >= normalized.Quota)
            return new(normalized, "Quota exhausted");
        if (normalized.RateLimit5h > 0 && normalized.Usage5h >= normalized.RateLimit5h)
            return new(normalized, "Rate limit exceeded (5h)");
        if (normalized.RateLimit1d > 0 && normalized.Usage1d >= normalized.RateLimit1d)
            return new(normalized, "Rate limit exceeded (1d)");
        if (normalized.RateLimit7d > 0 && normalized.Usage7d >= normalized.RateLimit7d)
            return new(normalized, "Rate limit exceeded (7d)");

        return new(normalized, null);
    }

    public static ApiKeyQuotaState Normalize(ApiKeyQuotaState state, long nowMs)
    {
        var normalized = state;
        if (IsExpired(normalized.Window5hStart, nowMs, FiveHoursMs))
        {
            normalized = normalized with { Usage5h = 0, Window5hStart = nowMs };
        }
        if (IsExpired(normalized.Window1dStart, nowMs, OneDayMs))
        {
            normalized = normalized with { Usage1d = 0, Window1dStart = nowMs };
        }
        if (IsExpired(normalized.Window7dStart, nowMs, SevenDaysMs))
        {
            normalized = normalized with { Usage7d = 0, Window7dStart = nowMs };
        }
        return normalized;
    }

    private static bool IsExpired(long startMs, long nowMs, long durationMs) =>
        startMs <= 0 || nowMs < startMs || nowMs - startMs >= durationMs;
}
