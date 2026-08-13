using System.Security.Claims;
using ScalaAPI.Admin.Auth;
using ScalaAPI.Data.Exports;

namespace ScalaAPI.Admin.Endpoints;

public static class ExportEndpoints
{
    public static void MapExportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/user/exports").RequireAuthorization("UserOnly");

        // Request a new data export.
        group.MapPost("/", async (
            ClaimsPrincipal principal,
            ExportService exports,
            HttpRequest request,
            int limit = 1_000,
            CancellationToken ct = default) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out var userId))
                return Results.Unauthorized();
            try
            {
                var result = await exports.RequestExportAsync(
                    userId,
                    request.HttpContext.Connection.RemoteIpAddress?.ToString(),
                    limit, ct);
                return result.AlreadyExists
                    ? Results.Ok(new { job_id = result.Job.JobId, status = result.Job.Status, existing = true })
                    : Results.Accepted($"/user/exports/{result.Job.JobId}/status",
                        new { job_id = result.Job.JobId, status = result.Job.Status, existing = false });
            }
            catch (ArgumentOutOfRangeException)
            {
                return Results.BadRequest(new { error = "invalid_export_limit" });
            }
        });

        // Check export job status.
        group.MapGet("/{jobId:long}/status", async (
            long jobId,
            ClaimsPrincipal principal,
            ExportService exports,
            CancellationToken ct) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out var userId))
                return Results.Unauthorized();
            var job = await exports.GetJobForUserAsync(jobId, userId, ct);
            if (job is null) return Results.NotFound(new { error = "export_not_found" });
            return Results.Ok(new
            {
                job_id = job.JobId,
                status = job.Status,
                artifact_size_bytes = job.ArtifactSizeBytes,
                expires_at = job.ExpiresAt,
                download_count = job.DownloadCount,
                max_downloads = job.MaxDownloads,
                error = job.Error,
            });
        });

        // Request a short-lived download token.
        group.MapPost("/{jobId:long}/download-token", async (
            long jobId,
            ClaimsPrincipal principal,
            ExportService exports,
            CancellationToken ct) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out var userId))
                return Results.Unauthorized();
            var token = await exports.IssueDownloadTokenAsync(jobId, userId, ct);
            if (token is null)
                return Results.BadRequest(new { error = "export_not_ready" });
            return Results.Ok(new
            {
                download_token = token,
                expires_in_seconds = (int)ExportService.DownloadTokenLifetime.TotalSeconds,
            });
        });

        // Download the export artifact using a token.
        group.MapGet("/{jobId:long}/download", async (
            long jobId,
            string token,
            ExportService exports,
            CancellationToken ct) =>
        {
            var result = await exports.DownloadAsync(jobId, token, ct);
            if (result is null)
                return Results.BadRequest(new { error = "download_unauthorized_or_expired" });
            return Results.File(result.Content, result.ContentType, result.FileName);
        });
    }
}
