using System.Text.Json;
using Npgsql;
using ScalaAPI.Admin.Auth;
using ScalaAPI.Host.Services;

namespace ScalaAPI.Admin.Endpoints;

/// <summary>
/// Admin endpoints for OPS metrics samples and summary.
/// Supports filtering by metric_name, time range, and aggregated summary with p95/error budgets.
/// </summary>
public static class OpsMetricsEndpoints
{
    public static void MapOpsMetricsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/ops-metrics").RequireAuthorization("AdminOnly");

        // List samples (filterable by metric_name, time range)
        group.MapGet("/samples", async (
            OpsMetricsSampleStore store,
            string? metricName, DateTime? from, DateTime? to, int limit,
            CancellationToken ct) =>
        {
            var samples = await store.ListSamplesAsync(metricName, from, to, limit, ct);
            return Results.Ok(new { items = samples });
        });

        // Aggregated summary (p95, error budget, etc.)
        group.MapGet("/summary", async (
            OpsMetricsSampleStore store,
            string? metricName, DateTime? from, DateTime? to,
            decimal errorBudgetTarget,
            CancellationToken ct) =>
        {
            errorBudgetTarget = Math.Clamp(errorBudgetTarget, 0.1m, 100m);
            var summary = await store.GetSummaryAsync(metricName, from, to, errorBudgetTarget, ct);
            return Results.Ok(new { items = summary });
        });
    }
}
