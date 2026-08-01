using Orleans;
using Sub2Api.Admin.Data;
using Sub2Api.Admin.Models;
using Sub2Api.Grains.Interfaces;

namespace Sub2Api.Admin.Endpoints;

public static class UserEndpoints
{
    public static void MapUserEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/users").RequireAuthorization();

        group.MapGet("/", async (IClusterClient client, ListingRepository repo, int page = 0, int size = 20) =>
        {
            var ids = await repo.GetIntegerGrainIds("user", page, size);
            var total = await repo.CountGrains("user");
            var items = new List<UserProjection>();
            foreach (var id in ids)
            {
                var grain = client.GetGrain<IUserGrain>(id);
                items.Add(await grain.GetAuthProjection());
            }
            return Results.Ok(new PagedResponse<UserProjection>(items, total, page, size));
        });

        group.MapGet("/{id:long}", async (long id, IClusterClient client) =>
        {
            var grain = client.GetGrain<IUserGrain>(id);
            return Results.Ok(await grain.GetAuthProjection());
        });

        group.MapPost("/", async (UserCreateRequest req, IClusterClient client) =>
        {
            var allocator = client.GetGrain<IIdAllocatorGrain>("user");
            var id = await allocator.Next();
            var grain = client.GetGrain<IUserGrain>(id);
            await grain.Create(new UserUpsert(
                req.Role, req.Balance, req.Concurrency, req.RpmLimit, req.AllowedGroups));
            return Results.Created($"/admin/users/{id}", new { id });
        });

        group.MapPut("/{id:long}", async (long id, UserCreateRequest req, IClusterClient client) =>
        {
            var grain = client.GetGrain<IUserGrain>(id);
            await grain.Update(new UserUpsert(
                req.Role, req.Balance, req.Concurrency, req.RpmLimit, req.AllowedGroups));
            return Results.NoContent();
        });

        group.MapPatch("/{id:long}/status", async (long id, StatusRequest req, IClusterClient client) =>
        {
            var grain = client.GetGrain<IUserGrain>(id);
            await grain.SetStatus(req.Status);
            return Results.NoContent();
        });

        group.MapPost("/{id:long}/balance", async (long id, BalanceRequest req, IClusterClient client) =>
        {
            var grain = client.GetGrain<IUserGrain>(id);
            await grain.AdjustBalance(req.Delta);
            return Results.NoContent();
        });

        group.MapDelete("/{id:long}", async (long id, IClusterClient client) =>
        {
            var grain = client.GetGrain<IUserGrain>(id);
            await grain.Delete();
            return Results.NoContent();
        });
    }
}
