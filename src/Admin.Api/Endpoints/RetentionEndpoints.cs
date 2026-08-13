using System.Security.Claims;
using ScalaAPI.Admin.Auth;
using ScalaAPI.Data.Retention;

namespace ScalaAPI.Admin.Endpoints;

public sealed record RetentionCleanupRequest(
    bool DryRun = false,
    int LimitPerCategory = 1_000);

public sealed record RetentionPolicyRequest(
    string Category,
    int RetentionDays,
    string Description = "");

public static class RetentionEndpoints
{
    public static void MapRetentionEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/admin/retention").RequireAuthorization("AdminOnly");

        // List all retention policies.
        admin.MapGet("/policies", async (
            RetentionService retention,
            CancellationToken ct) =>
        {
            var policies = await retention.ListPoliciesAsync(ct);
            return Results.Ok(policies.Select(p => new
            {
                policy_id = p.PolicyId,
                category = p.Category,
                retention_days = p.RetentionDays,
                description = p.Description,
                created_at = p.CreatedAt,
            }));
        });

        // Create or update a retention policy.
        admin.MapPost("/policies", async (
            RetentionPolicyRequest request,
            RetentionService retention,
            CancellationToken ct) =>
        {
            try
            {
                var policy = await retention.UpsertPolicyAsync(
                    request.Category, request.RetentionDays, request.Description, ct);
                return Results.Created($"/admin/retention/policies", new
                {
                    policy_id = policy.PolicyId,
                    category = policy.Category,
                    retention_days = policy.RetentionDays,
                    description = policy.Description,
                });
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(new { error = ex.ParamName, message = ex.Message });
            }
        });

        // Trigger a cleanup run (dry-run or applied).
        admin.MapPost("/cleanup", async (
            RetentionCleanupRequest request,
            ClaimsPrincipal principal,
            RetentionService retention,
            HttpRequest http,
            CancellationToken ct) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out var actorId))
                return Results.Unauthorized();
            var key = http.Headers["Idempotency-Key"].ToString().Trim();
            if (key.Length is < 1 or > 200)
                return Results.BadRequest(new { error = "idempotency_key_required" });
            try
            {
                var result = await retention.RunCleanupAsync(
                    actorId, key, request.DryRun, request.LimitPerCategory, ct);
                return Results.Ok(new
                {
                    run_id = result.RunId,
                    status = result.Status,
                    dry_run = result.DryRun,
                    total_deleted = result.TotalDeleted,
                    total_failed = result.TotalFailed,
                    categories = result.Categories,
                    started_at = result.StartedAt,
                    completed_at = result.CompletedAt,
                });
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(new { error = ex.ParamName, message = ex.Message });
            }
        });

        // View cleanup history.
        admin.MapGet("/cleanup/history", async (
            RetentionService retention,
            int limit = 50,
            CancellationToken ct = default) =>
        {
            var history = await retention.GetHistoryAsync(limit, ct);
            return Results.Ok(history.Select(h => new
            {
                run_id = h.RunId,
                status = h.Status,
                dry_run = h.DryRun,
                total_deleted = h.TotalDeleted,
                total_failed = h.TotalFailed,
                categories = h.Categories,
                started_at = h.StartedAt,
                completed_at = h.CompletedAt,
            }));
        });
    }
}
