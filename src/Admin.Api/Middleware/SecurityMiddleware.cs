using System.Collections.Concurrent;
using System.Security.Claims;
using ScalaAPI.Admin.Auth;

namespace ScalaAPI.Admin.Middleware;

/// <summary>
/// Security middleware providing CSRF protection, session validation,
/// rate limiting, and step-up auth enforcement.
/// </summary>
public sealed class SecurityMiddleware
{
    private readonly RequestDelegate _next;
    private static readonly ConcurrentDictionary<string, RateBucket> _rateBuckets = new();

    private sealed record RateBucket(int Count, DateTime WindowStart);

    public SecurityMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Rate limiting for mutation endpoints
        if (IsMutationMethod(context.Request.Method))
        {
            var clientKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!CheckRateLimit(clientKey, maxRequests: 120, windowSeconds: 60))
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.Response.WriteAsJsonAsync(new { error = "rate_limit_exceeded" });
                return;
            }
        }

        // CSRF: require Origin or X-Requested-With for state-changing requests
        if (IsMutationMethod(context.Request.Method) && !context.Request.Path.StartsWithSegments("/live") && !context.Request.Path.StartsWithSegments("/ready"))
        {
            var hasOrigin = context.Request.Headers.ContainsKey("Origin");
            var hasXRequested = context.Request.Headers.ContainsKey("X-Requested-With");
            if (!hasOrigin && !hasXRequested)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "csrf_missing_header" });
                return;
            }
        }

        // Step-up auth: check for X-Step-Up header on sensitive endpoints
        if (context.Request.Path.StartsWithSegments("/admin/security/rotate-master-key")
            && context.User.Identity?.IsAuthenticated == true)
        {
            var hasStepUp = context.Request.Headers.ContainsKey("X-Step-Up");
            if (!hasStepUp)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "step_up_auth_required" });
                return;
            }
        }

        await _next(context);
    }

    private static bool IsMutationMethod(string method)
        => method is "POST" or "PUT" or "DELETE" or "PATCH";

    private static bool CheckRateLimit(string key, int maxRequests, int windowSeconds)
    {
        var now = DateTime.UtcNow;
        var bucket = _rateBuckets.AddOrUpdate(key,
            _ => new RateBucket(1, now),
            (_, existing) =>
            {
                if ((now - existing.WindowStart).TotalSeconds > windowSeconds)
                    return new RateBucket(1, now);
                return existing with { Count = existing.Count + 1 };
            });
        return bucket.Count <= maxRequests;
    }
}
