using System.Security.Claims;
using System.Text.Json;
using ScalaAPI.Admin.Auth;
using ScalaAPI.Admin.Data;

namespace ScalaAPI.Admin.Endpoints;

public sealed record MaintenanceCleanupRequest(
    bool DryRun = false,
    int RetentionDays = 30,
    int Limit = 1_000);

public static class MaintenanceEndpoints
{
    public static void MapMaintenanceEndpoints(this WebApplication app)
    {
        var user = app.MapGroup("/user").RequireAuthorization("UserOnly");
        user.MapGet("/export", async (
            ClaimsPrincipal principal,
            MaintenanceStore maintenance,
            HttpRequest request,
            int limit = 1_000,
            CancellationToken ct = default) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out var userId))
                return Results.Unauthorized();
            try
            {
                var result = await maintenance.ExportUserAsync(userId,
                    request.HttpContext.Connection.RemoteIpAddress?.ToString(), limit, ct);
                if (result is null) return Results.NotFound();
                request.HttpContext.Response.Headers.CacheControl = "no-store";
                return Results.Ok(result);
            }
            catch (ArgumentOutOfRangeException)
            {
                return Results.BadRequest(new { error = "invalid_export_limit" });
            }
        });

        var admin = app.MapGroup("/admin/maintenance").RequireAuthorization("AdminOnly");
        admin.MapPost("/cleanup", async (
            MaintenanceCleanupRequest request,
            ClaimsPrincipal principal,
            MaintenanceStore maintenance,
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
                var fingerprint = MaintenanceStore.Fingerprint(
                    request.DryRun, request.RetentionDays, request.Limit);
                var result = await maintenance.CleanupExpiredAsync(actorId, key, fingerprint,
                    request.DryRun, request.RetentionDays, request.Limit,
                    http.HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
                return result.Status switch
                {
                    MaintenanceOperationStatus.Conflict => Results.Conflict(new { error = result.Error }),
                    _ => Results.Ok(new { replayed = result.Status == MaintenanceOperationStatus.Replayed,
                        result = result.Summary }),
                };
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new { error = "invalid_cleanup_request" });
            }
        });
    }
}
