using Npgsql;
using ScalaAPI.Admin.Auth;
using ScalaAPI.Host.Services;

namespace ScalaAPI.Admin.Endpoints;

/// <summary>
/// Admin and user endpoints for passive monitor V2 rollups, watermarks, and privacy config.
/// Admin endpoints show all dimensions; user endpoints are filtered by the authenticated user's ID.
/// </summary>
public static class PassiveMonitorV2Endpoints
{
    public static void MapPassiveMonitorV2Endpoints(this WebApplication app)
    {
        var adminGroup = app.MapGroup("/admin/monitor/v2").RequireAuthorization("AdminOnly");

        // List rollups (filterable by dimension, dimension_value, time range)
        adminGroup.MapGet("/rollups", async (
            PassiveMonitorV2Store store,
            string? dimension, string? dimensionValue,
            DateTime? from, DateTime? to, int limit,
            CancellationToken ct) =>
        {
            var rollups = await store.ListRollupsAsync(dimension, dimensionValue, from, to, limit, ct);
            return Results.Ok(new
            {
                items = rollups,
                source = "monitor_v2_rollups",
                description = "Passive aggregation from settled usage_events. Deduplicated by event ID."
            });
        });

        // List watermarks
        adminGroup.MapGet("/watermarks", async (
            PassiveMonitorV2Store store,
            CancellationToken ct) =>
        {
            var watermarks = await store.ListWatermarksAsync(ct);
            return Results.Ok(new
            {
                items = watermarks,
                source = "monitor_v2_watermarks",
                description = "Monotonic watermark per dimension. Tracks last processed event timestamp."
            });
        });

        // Get privacy config
        adminGroup.MapGet("/privacy", async (
            PassiveMonitorV2Store store,
            string configKey,
            CancellationToken ct) =>
        {
            var config = await store.GetPrivacyConfigAsync(configKey, ct);
            if (config is null)
                return Results.NotFound(new { error = "privacy_config_not_found" });
            return Results.Ok(new
            {
                item = config,
                source = "monitor_v2_privacy_config",
                description = "Privacy defaults control redaction and retention for monitor V2 data."
            });
        });

        // Update privacy config
        adminGroup.MapPut("/privacy", async (
            PassiveMonitorV2Store store,
            MonitorV2PrivacyConfigRequest req,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.ConfigKey) || req.ConfigKey.Length > 120)
                return Results.BadRequest(new { error = "config_key is required and bounded" });
            if (req.RetentionDays is < 1 or > 3650)
                return Results.BadRequest(new { error = "retention_days must be 1-3650" });

            var config = await store.UpsertPrivacyConfigAsync(
                req.ConfigKey.Trim(),
                req.RedactUserIds,
                req.RedactPrompts,
                req.RetentionDays,
                ct);
            return Results.Ok(new
            {
                item = config,
                source = "monitor_v2_privacy_config",
                description = "Updated privacy config. Controls redaction and retention for monitor V2."
            });
        });

        // User-facing endpoints: only show the authenticated user's own data
        var userGroup = app.MapGroup("/user/monitor/v2").RequireAuthorization("UserOnly");

        // User's own rollups (filtered by user_id from auth claims)
        userGroup.MapGet("/rollups", async (
            System.Security.Claims.ClaimsPrincipal principal,
            PassiveMonitorV2Store store,
            DateTime? from, DateTime? to, int limit,
            CancellationToken ct) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out var userId))
                return Results.Unauthorized();

            var rollups = await store.ListUserRollupsAsync(userId, from, to, limit, ct);

            // Apply privacy redaction: if privacy config says redact_user_ids,
            // mask the dimension_value for user-dimension rollups that aren't the caller's own
            var config = await store.GetPrivacyConfigAsync("default", ct);
            var redactUserIds = config?.RedactUserIds ?? true;

            var items = rollups.Select(r =>
            {
                if (redactUserIds && r.Dimension == "user" && r.DimensionValue != userId.ToString())
                {
                    return r with { DimensionValue = "***" };
                }
                return r;
            }).ToList();

            return Results.Ok(new
            {
                items,
                source = "monitor_v2_rollups",
                description = "Your usage rollups aggregated from settled events. Platform/model/error dimensions included for context."
            });
        });
    }
}

public record MonitorV2PrivacyConfigRequest(
    string ConfigKey,
    bool RedactUserIds,
    bool RedactPrompts,
    int RetentionDays);
