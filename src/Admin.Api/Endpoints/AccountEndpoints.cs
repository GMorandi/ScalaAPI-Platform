using Orleans;
using Sub2Api.Admin.Data;
using Sub2Api.Admin.Models;
using Sub2Api.Grains.Interfaces;

namespace Sub2Api.Admin.Endpoints;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/accounts").RequireAuthorization();

        group.MapGet("/", async (IClusterClient client, ListingRepository repo, int page = 0, int size = 20) =>
        {
            var ids = await repo.GetIntegerGrainIds("account", page, size);
            var total = await repo.CountGrains("account");
            var items = new List<AccountProjection>();
            foreach (var id in ids)
            {
                var grain = client.GetGrain<IAccountGrain>(id);
                items.Add(await grain.GetProjection());
            }
            return Results.Ok(new PagedResponse<AccountProjection>(items, total, page, size));
        });

        group.MapGet("/{id:long}", async (long id, IClusterClient client) =>
        {
            var grain = client.GetGrain<IAccountGrain>(id);
            var projection = await grain.GetProjection();
            return Results.Ok(projection);
        });

        group.MapPost("/", async (AccountCreateRequest req, IClusterClient client) =>
        {
            var allocator = client.GetGrain<IIdAllocatorGrain>("account");
            var id = await allocator.Next();
            var grain = client.GetGrain<IAccountGrain>(id);
            await grain.Create(new AccountUpsert(
                req.Name, req.Platform, req.Type, req.BaseUrl,
                req.Priority, req.Concurrency, req.LoadFactor, req.RateMultiplier,
                req.Schedulable, req.Credentials, req.ModelMapping,
                req.SupportedModels, req.ProxyUrl, req.TlsFingerprint));
            return Results.Created($"/admin/accounts/{id}", new { id });
        });

        group.MapPut("/{id:long}", async (long id, AccountCreateRequest req, IClusterClient client) =>
        {
            var grain = client.GetGrain<IAccountGrain>(id);
            await grain.Update(new AccountUpsert(
                req.Name, req.Platform, req.Type, req.BaseUrl,
                req.Priority, req.Concurrency, req.LoadFactor, req.RateMultiplier,
                req.Schedulable, req.Credentials, req.ModelMapping,
                req.SupportedModels, req.ProxyUrl, req.TlsFingerprint));
            return Results.NoContent();
        });

        group.MapPatch("/{id:long}/status", async (long id, StatusRequest req, IClusterClient client) =>
        {
            var grain = client.GetGrain<IAccountGrain>(id);
            await grain.SetStatus(req.Status);
            return Results.NoContent();
        });

        group.MapDelete("/{id:long}", async (long id, IClusterClient client) =>
        {
            var grain = client.GetGrain<IAccountGrain>(id);
            await grain.Delete();
            return Results.NoContent();
        });
    }
}
