using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using ScalaAPI.Admin.Auth;
using ScalaAPI.Admin.Data;

namespace ScalaAPI.Admin.Endpoints;

public sealed record BackupCreateRequest(string? Kind, int RetentionDays = 14);

public static class BackupEndpoints
{
    public static void MapBackupEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/backups").RequireAuthorization("AdminOnly");

        group.MapGet("/", async (BackupStore backups, int page = 1, int size = 50,
            CancellationToken ct = default) =>
            Results.Ok(new { items = await backups.ListAsync(page, size, ct),
                restore_configured = backups.RestoreConfigured }));

        group.MapGet("/{id}", async (string id, BackupStore backups,
            CancellationToken ct = default) =>
        {
            var job = await backups.GetAsync(id, ct);
            return job is null ? Results.NotFound() : Results.Ok(job);
        });

        group.MapPost("/", async (BackupStore backups, ClaimsPrincipal principal,
            HttpRequest request, BackupCreateRequest body, CancellationToken ct = default) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out var actorId))
                return Results.Unauthorized();
            var result = await backups.CreateAsync(actorId,
                request.Headers["Idempotency-Key"].FirstOrDefault(), body.Kind,
                body.RetentionDays,
                request.HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
            return result.Status switch
            {
                BackupCommandStatus.Created => Results.Created($"/admin/backups/{result.Job!.Id}", result.Job),
                BackupCommandStatus.Replayed => Results.Ok(result.Job),
                BackupCommandStatus.Busy or BackupCommandStatus.Conflict => Results.Conflict(new
                {
                    error = result.ErrorCode,
                    message = result.Error,
                    job = result.Job,
                }),
                _ => Results.BadRequest(new { error = result.ErrorCode, message = result.Error }),
            };
        });

        group.MapPost("/{id}/restore", async (string id, BackupStore backups,
            ClaimsPrincipal principal, HttpRequest request, CancellationToken ct = default) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out var actorId))
                return Results.Unauthorized();
            var result = await backups.RestoreAsync(actorId, id,
                request.Headers["Idempotency-Key"].FirstOrDefault(),
                request.HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
            return result.Status switch
            {
                BackupCommandStatus.Created => Results.Accepted($"/admin/backups/{id}/restore", result.Run),
                BackupCommandStatus.Replayed => Results.Ok(result.Run),
                BackupCommandStatus.NotFound => Results.NotFound(),
                BackupCommandStatus.NotConfigured => Results.Conflict(new { error = result.ErrorCode, message = result.Error }),
                BackupCommandStatus.Busy or BackupCommandStatus.Conflict => Results.Conflict(new
                {
                    error = result.ErrorCode,
                    message = result.Error,
                    run = result.Run,
                }),
                _ => Results.BadRequest(new { error = result.ErrorCode, message = result.Error }),
            };
        });
    }
}
