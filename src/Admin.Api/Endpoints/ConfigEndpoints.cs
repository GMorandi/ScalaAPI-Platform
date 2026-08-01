using Orleans;
using Sub2Api.Admin.Models;
using Sub2Api.Grains.Interfaces;

namespace Sub2Api.Admin.Endpoints;

public static class ConfigEndpoints
{
    public static void MapConfigEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/config").RequireAuthorization();

        group.MapGet("/", async (IClusterClient client) =>
        {
            var grain = client.GetGrain<IConfigGrain>("system");
            var settings = await grain.Get();
            return Results.Ok(settings);
        });

        group.MapPut("/", async (ConfigUpdateRequest req, IClusterClient client) =>
        {
            var grain = client.GetGrain<IConfigGrain>("system");
            await grain.Update(req.Key, req.Value);
            return Results.NoContent();
        });
    }
}
