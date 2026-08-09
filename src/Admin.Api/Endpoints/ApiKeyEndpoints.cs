using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Security.Claims;
using Orleans;
using ScalaAPI.Admin.Auth;
using ScalaAPI.Admin.Data;
using ScalaAPI.Admin.Models;
using SqlSugar;
using ScalaAPI.Data.Entities;
using ScalaAPI.Grains.Interfaces;

namespace ScalaAPI.Admin.Endpoints;

public static class ApiKeyEndpoints
{
    public static void MapApiKeyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/apikeys").RequireAuthorization("AdminOnly");

        group.MapGet("/", async (IClusterClient client, ListingRepository repo, int page = 0, int size = 20) =>
        {
            var ids = await repo.GetStringGrainIds("apiKey", page, size);
            var total = await repo.CountGrains("apiKey");
            var items = new List<object>();
            foreach (var id in ids)
            {
                var grain = client.GetGrain<IApiKeyGrain>(id);
                var version = await grain.GetVersion();
                items.Add(new { hash = id, version });
            }
            return Results.Ok(new PagedResponse<object>(items, total, page, size));
        });

        group.MapPost("/", async (ApiKeyCreateRequest req, IClusterClient client,
            ListingRepository repo, ISqlSugarClient db, ClaimsPrincipal principal,
            ApiKeyAuditStore audit) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out var actorId)) return Results.Unauthorized();
            string[] scopes;
            try { scopes = ApiKeyScopes.Normalize(req.Scopes); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            if (!IsFutureExpiry(req.ExpiresAt))
                return Results.BadRequest(new { error = "expires_at must be in the future" });
            var user = await db.Queryable<UserAccountEntity>()
                .Where(x => x.Id == req.UserId && x.Status == "active").FirstAsync();
            if (user is null) return Results.BadRequest(new { error = "User not found" });
            var plainKey = $"sk-{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";
            var hash = HashKey(plainKey);
            var allocator = client.GetGrain<IIdAllocatorGrain>("apiKey");
            var id = await allocator.Next();

            var grain = client.GetGrain<IApiKeyGrain>(hash);
            await grain.Create(new ApiKeyUpsert(
                req.UserId, req.GroupId, req.Quota, req.ExpiresAt,
                req.IpWhitelist, req.IpBlacklist,
                req.RateLimit5h, req.RateLimit1d, req.RateLimit7d, scopes), id);
            var entity = new UserApiKeyEntity
            {
                UserEmail = user.Email,
                KeyHash = hash,
                KeyPrefix = plainKey[..12],
                ApiKeyId = id,
                Status = "active",
                CreatedAt = DateTime.UtcNow,
                Scopes = JsonSerializer.Serialize(scopes),
                ExpiresAtMs = req.ExpiresAt,
            };
            await db.Insertable(entity).ExecuteCommandAsync();
            await repo.RegisterString("apiKey", hash, id);
            await audit.RecordAsync(id, req.UserId, actorId, "created", scopes, req.ExpiresAt);

            return Results.Created($"/admin/apikeys/{hash}", new ApiKeyCreateResponse(plainKey, id));
        });

        group.MapPut("/{hash}", async (string hash, ApiKeyCreateRequest req, IClusterClient client,
            ISqlSugarClient db, ClaimsPrincipal principal, ApiKeyAuditStore audit) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out var actorId)) return Results.Unauthorized();
            string[] scopes;
            try { scopes = ApiKeyScopes.Normalize(req.Scopes); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            if (!IsFutureExpiry(req.ExpiresAt))
                return Results.BadRequest(new { error = "expires_at must be in the future" });
            var grain = client.GetGrain<IApiKeyGrain>(hash);
            var entity = await db.Queryable<UserApiKeyEntity>().Where(x => x.KeyHash == hash).FirstAsync();
            if (entity is null) return Results.NotFound();
            await grain.Update(new ApiKeyUpsert(
                req.UserId, req.GroupId, req.Quota, req.ExpiresAt,
                req.IpWhitelist, req.IpBlacklist,
                req.RateLimit5h, req.RateLimit1d, req.RateLimit7d, scopes));
            await db.Ado.ExecuteCommandAsync(
                "UPDATE user_api_keys SET scopes = @scopes::jsonb, expires_at_ms = @expires WHERE key_hash = @hash",
                new SugarParameter("@scopes", JsonSerializer.Serialize(scopes)),
                new SugarParameter("@expires", req.ExpiresAt),
                new SugarParameter("@hash", hash));
            await audit.RecordAsync(entity.ApiKeyId, req.UserId, actorId, "updated", scopes, req.ExpiresAt,
                reason: "admin policy update");
            return Results.NoContent();
        });

        group.MapDelete("/{hash}", async (string hash, IClusterClient client,
            ListingRepository repo, ISqlSugarClient db, ClaimsPrincipal principal,
            ApiKeyAuditStore audit) =>
        {
            if (!AuthClaims.TryGetUserId(principal, out var actorId)) return Results.Unauthorized();
            var grain = client.GetGrain<IApiKeyGrain>(hash);
            var config = await grain.GetConfig();
            var entity = await db.Queryable<UserApiKeyEntity>().Where(x => x.KeyHash == hash).FirstAsync();
            if (entity is null) return Results.NotFound();
            await grain.Revoke();
            await db.Updateable<UserApiKeyEntity>().SetColumns(x => x.Status == "revoked")
                .Where(x => x.KeyHash == hash).ExecuteCommandAsync();
            await repo.Unregister("apiKey", hash);
            await audit.RecordAsync(entity.ApiKeyId, config.UserId, actorId, "revoked",
                config.Scopes, config.ExpiresAt, reason: "admin revoke");
            return Results.NoContent();
        });
    }

    private static string HashKey(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool IsFutureExpiry(long? expiresAt) =>
        !expiresAt.HasValue || expiresAt.Value > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
