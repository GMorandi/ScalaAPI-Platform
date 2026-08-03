using Orleans;
using Sub2Api.Admin.Data;
using Sub2Api.Admin.Models;
using Sub2Api.Grains.Interfaces;

namespace Sub2Api.Admin.Endpoints;

public static class GroupEndpoints
{
    public static void MapGroupEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/groups").RequireAuthorization("AdminOnly");

        group.MapGet("/", async (IClusterClient client, ListingRepository repo, int page = 0, int size = 20) =>
        {
            var ids = await repo.GetIntegerGrainIds("group", page, size);
            var total = await repo.CountGrains("group");
            var items = new List<GroupConfig>();
            foreach (var id in ids)
            {
                var grain = client.GetGrain<IGroupGrain>(id);
                items.Add(await grain.GetConfig());
            }
            return Results.Ok(new PagedResponse<GroupConfig>(items, total, page, size));
        });

        group.MapGet("/{id:long}", async (long id, IClusterClient client) =>
        {
            var grain = client.GetGrain<IGroupGrain>(id);
            return Results.Ok(await grain.GetConfig());
        });

        group.MapPost("/", async (GroupCreateRequest req, IClusterClient client) =>
        {
            var allocator = client.GetGrain<IIdAllocatorGrain>("group");
            var id = await allocator.Next();
            var grain = client.GetGrain<IGroupGrain>(id);
            await grain.Create(new GroupUpsert(
                req.Platform, req.RateMultiplier, req.IsExclusive,
                req.DailyLimitUsd, req.ClaudeCodeOnly, req.FallbackGroupId,
                req.ModelRoutingEnabled, req.ModelRouting, req.MemberAccountIds,
                req.RpmLimit, req.PeakMultiplier, req.PeakStartHour, req.PeakEndHour));
            return Results.Created($"/admin/groups/{id}", new { id });
        });

        group.MapPut("/{id:long}", async (long id, GroupCreateRequest req, IClusterClient client) =>
        {
            var grain = client.GetGrain<IGroupGrain>(id);
            await grain.Update(new GroupUpsert(
                req.Platform, req.RateMultiplier, req.IsExclusive,
                req.DailyLimitUsd, req.ClaudeCodeOnly, req.FallbackGroupId,
                req.ModelRoutingEnabled, req.ModelRouting, req.MemberAccountIds,
                req.RpmLimit, req.PeakMultiplier, req.PeakStartHour, req.PeakEndHour));
            return Results.NoContent();
        });

        group.MapPatch("/{id:long}/status", async (long id, StatusRequest req, IClusterClient client) =>
        {
            var grain = client.GetGrain<IGroupGrain>(id);
            await grain.SetStatus(req.Status);
            return Results.NoContent();
        });

        group.MapDelete("/{id:long}", async (long id, IClusterClient client) =>
        {
            var grain = client.GetGrain<IGroupGrain>(id);
            await grain.Delete();
            return Results.NoContent();
        });
    }
}
