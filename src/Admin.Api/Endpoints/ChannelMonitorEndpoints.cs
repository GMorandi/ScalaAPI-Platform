using System.Text.Json;
using Npgsql;
using ScalaAPI.Admin.Auth;
using ScalaAPI.Host.Services;

namespace ScalaAPI.Admin.Endpoints;

/// <summary>
/// Admin endpoints for channel monitor templates, checks, and incidents.
/// Supports filtering, creation, and browsing.
/// </summary>
public static class ChannelMonitorEndpoints
{
    public static void MapChannelMonitorEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/channel-monitors").RequireAuthorization("AdminOnly");

        // List templates
        group.MapGet("/templates", async (ChannelMonitorTemplateStore store, CancellationToken ct) =>
        {
            var templates = await store.ListTemplatesAsync(ct);
            return Results.Ok(new { items = templates });
        });

        // Create or update a template
        group.MapPost("/templates", async (
            ChannelMonitorTemplateStore store,
            ChannelMonitorTemplateRequest req,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.TemplateId) || req.TemplateId.Length > 120)
                return Results.BadRequest(new { error = "template_id is required and bounded" });
            if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 200)
                return Results.BadRequest(new { error = "name is required and bounded" });
            if (string.IsNullOrWhiteSpace(req.CheckType) || req.CheckType.Length > 60)
                return Results.BadRequest(new { error = "check_type is required and bounded" });
            if (req.TimeoutSeconds is < 1 or > 300)
                return Results.BadRequest(new { error = "timeout_seconds must be 1-300" });
            if (req.RetryCount is < 0 or > 10)
                return Results.BadRequest(new { error = "retry_count must be 0-10" });
            if (req.AlertThreshold is < 1 or > 100)
                return Results.BadRequest(new { error = "alert_threshold must be 1-100" });

            var template = await store.CreateTemplateAsync(
                req.TemplateId.Trim(), req.Name.Trim(), req.CheckType.Trim(),
                req.ScheduleCron?.Trim() ?? "*/5 * * * *",
                req.TimeoutSeconds, req.RetryCount, req.AlertThreshold, ct);
            return Results.Ok(new { item = template });
        });

        // List incidents (filterable)
        group.MapGet("/incidents", async (
            ChannelMonitorTemplateStore store,
            string? templateId, bool? openOnly, int limit,
            CancellationToken ct) =>
        {
            var incidents = await store.ListIncidentsAsync(templateId, openOnly, limit, ct);
            return Results.Ok(new { items = incidents });
        });

        // List checks (filterable)
        group.MapGet("/checks", async (
            ChannelMonitorTemplateStore store,
            string? templateId, string? status, int limit,
            CancellationToken ct) =>
        {
            var checks = await store.ListChecksAsync(templateId, status, limit, ct);
            return Results.Ok(new { items = checks });
        });
    }
}

public record ChannelMonitorTemplateRequest(
    string TemplateId,
    string Name,
    string CheckType,
    string? ScheduleCron,
    int TimeoutSeconds,
    int RetryCount,
    int AlertThreshold);
