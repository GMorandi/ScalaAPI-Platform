using Orleans;
using System.Security.Claims;
using System.Text.Json;
using ScalaAPI.Admin.Auth;
using ScalaAPI.Data.Entities;
using SqlSugar;
using ScalaAPI.Admin.Models;
using ScalaAPI.Data.Config;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Admin.Endpoints;

public static class ConfigEndpoints
{
    public static void MapConfigEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/config").RequireAuthorization("AdminOnly");

        group.MapGet("/", async (IClusterClient client) =>
        {
            var grain = client.GetGrain<IConfigGrain>("system");
            return Results.Ok(await grain.GetSnapshot());
        });

        group.MapPut("/", async (ConfigUpdateRequest req, IClusterClient client,
            ISqlSugarClient db, ClaimsPrincipal principal, HttpContext context,
            ConfigRevisionStore revisions, CancellationToken ct) =>
        {
            if (!ConfigValidation.TryNormalize(req, out var key, out var value, out var error))
                return Results.BadRequest(new { error = "invalid_config", message = error });
            var grain = client.GetGrain<IConfigGrain>("system");

            // Look up the previous revision for the chain
            var latestRevision = await revisions.GetLatestRevisionAsync(key, ct);
            var previousRevisionId = latestRevision?.RevisionId;

            // Record the revision before applying
            if (!AuthClaims.TryGetUserId(principal, out var actorId))
                return Results.Unauthorized();
            var revisionId = await revisions.RecordRevisionAsync(
                key, value, previousRevisionId, actorId, null, ct);

            ConfigSnapshot snapshot;
            try
            {
                snapshot = await grain.Update(key, value, req.ExpectedVersion);
            }
            catch (InvalidOperationException ex) when (ex.Message == "config_version_conflict")
            {
                return Results.Conflict(new { error = "config_version_conflict" });
            }

            await db.Insertable(new AuditLogEntity
            {
                UserId = actorId,
                Action = "config.update",
                ResourceType = "runtime_config",
                ResourceId = key,
                Details = JsonSerializer.Serialize(new {
                    value, version = snapshot.Version, revision_id = revisionId }),
                IpAddress = context.Connection.RemoteIpAddress?.ToString(),
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            return Results.Ok(new { snapshot, revision_id = revisionId });
        });

        // List config revisions with status
        group.MapGet("/revisions", async (string? key, int? limit,
            ConfigRevisionStore revisions, CancellationToken ct) =>
        {
            var items = await revisions.ListRevisionsAsync(key, limit ?? 50, ct);
            return Results.Ok(new
            {
                items = items.Select(r => new
                {
                    r.RevisionId,
                    r.ConfigKey,
                    r.ConfigValue,
                    r.PreviousRevisionId,
                    r.ActorUserId,
                    r.ActorReason,
                    r.CreatedAt,
                    r.AppliedAt,
                    r.RolledBackAt,
                    r.Status,
                }),
            });
        });

        // Rollback a revision
        group.MapPost("/revisions/{id:long}/rollback", async (long id,
            ClaimsPrincipal principal, ConfigRevisionStore revisions,
            CancellationToken ct) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out var actorId))
                return Results.Unauthorized();

            var body = await revisions.RollbackAsync(id, actorId, null, ct);
            if (!body)
                return Results.NotFound(new { error = "revision_not_found_or_not_rollbackable" });

            return Results.Ok(new { revision_id = id, status = "rolled_back" });
        });

        // List node observations
        group.MapGet("/nodes", async (ConfigRevisionStore revisions,
            CancellationToken ct) =>
        {
            var observations = await revisions.GetNodeObservationsAsync(ct);
            return Results.Ok(new
            {
                nodes = observations.Select(o => new
                {
                    o.NodeId,
                    o.LastSeenRevision,
                    o.LastSeenAt,
                }),
            });
        });
    }

    private static class ConfigValidation
    {
        public static bool TryNormalize(ConfigUpdateRequest request,
            out string key, out string value, out string error)
        {
            key = request.Key?.Trim() ?? "";
            value = request.Value ?? "";
            error = "";
            try
            {
                ScalaAPI.Grains.Interfaces.ConfigValidation.Validate(key, value);
                return true;
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
