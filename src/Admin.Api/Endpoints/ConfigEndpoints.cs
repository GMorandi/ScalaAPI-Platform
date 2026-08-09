using Orleans;
using System.Security.Claims;
using System.Text.Json;
using ScalaAPI.Admin.Auth;
using ScalaAPI.Data.Entities;
using SqlSugar;
using ScalaAPI.Admin.Models;
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
            ISqlSugarClient db, ClaimsPrincipal principal, HttpContext context) =>
        {
            if (!ConfigValidation.TryNormalize(req, out var key, out var value, out var error))
                return Results.BadRequest(new { error = "invalid_config", message = error });
            var grain = client.GetGrain<IConfigGrain>("system");
            ConfigSnapshot snapshot;
            try
            {
                snapshot = await grain.Update(key, value, req.ExpectedVersion);
            }
            catch (InvalidOperationException ex) when (ex.Message == "config_version_conflict")
            {
                return Results.Conflict(new { error = "config_version_conflict" });
            }
            if (!AuthClaims.TryGetUserId(principal, out var actorId))
                return Results.Unauthorized();
            await db.Insertable(new AuditLogEntity
            {
                UserId = actorId,
                Action = "config.update",
                ResourceType = "runtime_config",
                ResourceId = key,
                Details = JsonSerializer.Serialize(new { value, version = snapshot.Version }),
                IpAddress = context.Connection.RemoteIpAddress?.ToString(),
                CreatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
            return Results.Ok(snapshot);
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
