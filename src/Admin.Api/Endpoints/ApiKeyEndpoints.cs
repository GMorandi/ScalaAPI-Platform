using System.Security.Cryptography;
using System.Text;
using Orleans;
using Sub2Api.Admin.Data;
using Sub2Api.Admin.Models;
using Sub2Api.Grains.Interfaces;

namespace Sub2Api.Admin.Endpoints;

public static class ApiKeyEndpoints
{
    public static void MapApiKeyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/apikeys").RequireAuthorization();

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

        group.MapPost("/", async (ApiKeyCreateRequest req, IClusterClient client) =>
        {
            var plainKey = $"sk-{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";
            var hash = HashKey(plainKey);

            var grain = client.GetGrain<IApiKeyGrain>(hash);
            await grain.Create(new ApiKeyUpsert(
                req.UserId, req.GroupId, req.Quota, req.ExpiresAt,
                req.IpWhitelist, req.IpBlacklist,
                req.RateLimit5h, req.RateLimit1d, req.RateLimit7d));

            return Results.Created($"/admin/apikeys/{hash}", new ApiKeyCreateResponse(plainKey, req.UserId));
        });

        group.MapPut("/{hash}", async (string hash, ApiKeyCreateRequest req, IClusterClient client) =>
        {
            var grain = client.GetGrain<IApiKeyGrain>(hash);
            await grain.Update(new ApiKeyUpsert(
                req.UserId, req.GroupId, req.Quota, req.ExpiresAt,
                req.IpWhitelist, req.IpBlacklist,
                req.RateLimit5h, req.RateLimit1d, req.RateLimit7d));
            return Results.NoContent();
        });

        group.MapDelete("/{hash}", async (string hash, IClusterClient client) =>
        {
            var grain = client.GetGrain<IApiKeyGrain>(hash);
            await grain.Revoke();
            return Results.NoContent();
        });
    }

    private static string HashKey(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
